/*
 *
 * Memory control functions.
 * Acts as GLUE and MMU in the Atari ST.
 * 
 * Some parts inspired in Hatari emulator created by Thomas Huth and others.
 * 
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using static ASE.Config;

namespace ASE
{
    /// <summary>
    /// Acts as GLUE and MMU in the Atari ST. Provides an abstraction for system memory, including RAM, ROM, and memory-mapped I/O ports, enabling read and
    /// write operations across different address spaces.
    /// </summary>
    /// <remarks>The Memory class manages the memory layout and access for an emulated system, supporting
    /// byte, word, and double word operations. It handles address mapping, enforces access restrictions, and
    /// coordinates interactions with hardware components such as the MMU, video, sound, and peripheral controllers.
    /// Special handling is implemented for certain hardware registers and unimplemented regions, with appropriate
    /// warnings or exceptions triggered as needed. This class is central to emulation accuracy and should be used for
    /// all memory access within the system.</remarks>
    public class Memory
    {
        public class STPortAdress
        {
            public const uint ST_MMU = 0xFF8001;           // Memory banks configuration

            public const uint ST_SCRHIGHADDR = 0xFF8201;   // Screen address (high)
            public const uint ST_SCRMIDADDR = 0xFF8203;    // Screen address (mid)
            public const uint ST_SCRLOWADDR = 0xFF820D;    // Screen address (low, this register is STE only)
            public const uint ST_HIVADRPOINT = 0xFF8205;   // Video address pointer (high)
            public const uint ST_MIVADRPOINT = 0xFF8207;   // Video address pointer (mid)
            public const uint ST_LOVADRPOINT = 0xFF8209;   // Video address pointer (low)
            public const uint ST_TVHz = 0xFF820A;          // Hz
            public const uint ST_LINEWIDTH = 0xFF820F;     // Line width / extra words per scanline (STE only)
            public const uint ST_PALLETE = 0xFF8240;       // Palette
            public const uint ST_RES = 0xFF8260;           // Screen resolution
            public const uint ST_HSCROLL = 0xFF8265;       // Horizontal fine scroll (STE only)
            public const uint ST_HSCROLL_NP = 0xFF8264;    // Same latch, no prefetch cycle (STE only)

            public const uint ST_PSGREADSELECT = 0xFF8800; // PSG/YM Read data/Register select
            public const uint ST_PSGWRITEDATA = 0xFF8802;  // PSG/YM Write data
            public const uint ST_PSGEND = 0xFF88FF;        // last shadow of the PSG block

            public const uint ST_ACIACMD = 0xFFFC00;       // Keyboard ACIA control
            public const uint ST_ACIADATA = 0xFFFC02;      // Keyboard ACIA data
            public const uint ST_MIDICMD = 0xFFFC04;       // MIDI ACIA control
            public const uint ST_MIDIDATA = 0xFFFC06;      // MIDI ACIA data
        }

        public int TosSize = 192 * 1024;
        public int RamSize = 1 * 1024 * 1024;
        public uint TosBase = 0xFC0000;
        public const uint PortsBase = 0xFF8000;

        // Cartridge port ($FA0000-$FBFFFF). Reads are served from GemdosHD.CartRom (the
        // synthetic cartridge that hooks GEMDOS for the host-folder hard drive); with no
        // cartridge the region floats at 0xFF like an empty port — no bus error, matching
        // the real machine. Writes are ignored (it is ROM).
        public const uint CartBase = 0xFA0000;
        public const uint CartEnd = 0xFC0000;   // exclusive

        // The GLUE decodes the whole 0x000000-0x3FFFFF region as RAM. Addresses inside this
        // region but beyond the configured MMU banks read as 0 (void), they never bus error.
        const uint RamRegionEnd = 0x400000;

        // The top page of the I/O area ($FFFF00-$FFFFFF) decodes to no device on the ST/STE,
        // so reads bus-error on real hardware. Some copy protections (e.g. Ocean's Batman the
        // Movie loader) deliberately fault there and feed the exception back into their
        // decryptor; returning data instead of faulting breaks them. Writes are ignored, as
        // for other void I/O.
        const uint UnmappedIoTop = 0xFFFF00;

        /// <summary>
        /// Everything between the RAM region and the I/O area that is not ROM or the cartridge
        /// port decodes to nothing on an ST/STE, and the bus times out: $400000-$DFFFFF, the
        /// half of $E00000-$FEFFFF the TOS image does not occupy, $F00000-$F9FFFF and
        /// $FF0000-$FF7FFF. Answering a bus error there rather than 0xFF is what lets software
        /// *probe* for hardware, which is how drivers and protections detect what is fitted:
        /// they install a bus-error handler, touch the address and take the fault as "absent".
        /// The ICD Pro hard disk driver hangs forever without this — it polls the Falcon IDE
        /// status at $FFF00039 waiting for BSY to clear, and 0xFF has BSY permanently set.
        /// </summary>
        bool IsUndecoded(uint addr) =>
            addr >= RamRegionEnd && addr < PortsBase &&
            !(addr >= TosBase && addr < RomWindowEnd) &&
            !(addr >= CartBase && addr < CartEnd);

        /// <summary>
        /// Whether an address in the I/O area ($FF8000-$FFFEFF) is answered by a chip this
        /// machine actually has. Everything else reads as a bus error, exactly as on a real ST:
        /// the address decoder only answers for the chips that are fitted, and software uses
        /// that to ask what machine it is running on.
        /// <para>
        /// This is what tells an STE apart from a Mega STE. EmuTOS probes $FF8E09 (the VME/SCU
        /// bus controller) with a bus-error handler installed and, finding it, sets the _MCH
        /// cookie to $00010010 — Mega STE — and then talks to an SCU and an SCC that are not
        /// there. It probes the same way for the Mega ST clock at $FFFC21, the SCC at
        /// $FF8C84, the HD floppy density register at $FF860F and the Falcon sound registers
        /// at $FF8943. Serving those from the Ports array (which is what an unknown I/O
        /// address used to do) answers every one of those questions with "yes".
        /// </para>
        /// <para>
        /// The granularity is the chip block, not the individual register: inside a block that
        /// exists, an unimplemented register keeps reading from the Ports array as before.
        /// That is deliberately looser than the real decoder (which faults on the gaps too),
        /// and it is where a machine-detection problem would be fixed by narrowing a range.
        /// </para>
        /// </summary>
        static bool IsDecodedIo(uint addr)
        {
            // Memory controller / MMU configuration
            if (addr <= 0xFF8001) return true;

            // Shifter: video base and counter, sync mode
            if (addr >= 0xFF8200 && addr <= 0xFF820B) return true;

            // STE only: video base low byte ($FF820D) and line width ($FF820F). A plain ST has
            // neither, and EmuTOS reads $FF820D to decide whether this is an STE at all.
            if (addr >= 0xFF820C && addr <= 0xFF820F) return IsSTE;

            if (addr >= 0xFF8240 && addr <= 0xFF825F) return true;   // palette
            if (addr >= 0xFF8260 && addr <= 0xFF8261) return true;   // resolution

            // STE only: horizontal fine scroll
            if (addr >= 0xFF8264 && addr <= 0xFF8265) return IsSTE;

            // FDC / DMA. The ST's DMA chip decodes up to $FF860D and no further: $FF860E-$FF860F
            // is the high-density floppy register of the Mega STE and TT, and answering it on an
            // ST claims to be one of those. The STE's MCU does decode the whole block, with no
            // register behind those two bytes: reads float high, writes go nowhere, and neither
            // faults.
            // That distinction is not cosmetic. TOS 2.05 -- the Mega STE's own TOS, which people
            // do run on an STE -- writes the density register unconditionally from its floppy
            // driver ($E0357C `move.w $2(a1),$FF860E.w`, and again at $E030BE and $E0352E), with
            // no bus-error handler installed. Faulting it on an STE ends the boot in two bombs
            // the moment TOS first touches the drive.
            // IMPORTANT! -> I should keep this in mind when I extend the emulation to the Mega STE!!!!
            if (addr >= 0xFF8604 && addr <= 0xFF860D) return true;
            if (addr >= 0xFF860E && addr <= 0xFF860F) return IsSTE;

            // PSG. The ST decodes it across the whole page (A8-A15 are not decoded), which is
            // why software reaches it at $FF8800 and at mirrors like $FF8880.
            if (addr >= 0xFF8800 && addr <= 0xFF88FF) return true;

            // STE DMA sound and joypads, and the blitter: the model check for these belongs to
            // their own handlers, which report it, so the block is "known" here in every model.
            if (addr >= 0xFF8900 && addr <= 0xFF8925) return true;
            if (addr >= 0xFF8A00 && addr <= 0xFF8A3D) return true;
            if (addr >= 0xFF9200 && addr <= 0xFF9223) return true;

            if (addr >= 0xFFFA00 && addr <= 0xFFFA2F) return true;   // MFP 68901
            if (addr >= 0xFFFC00 && addr <= 0xFFFC07) return true;   // ACIAs: keyboard, MIDI

            // Mega ST/STE real-time clock (see IsRtcBlock): decoded in every model, with no chip
            // behind it -- the second block, after the HD density register, that has to answer
            // without being implemented.
            if (IsRtcBlock(addr)) return true;

            // Everything else is a chip this machine does not carry: the SCU/VME controller
            // ($FF8E00-$FF8E0F) and the cache/speed register ($FF8E21) of a Mega STE, its SCC
            // ($FF8C80-$FF8C87), TT and Falcon registers, and the gaps between blocks.
            return false;
        }

        /// <summary>
        /// The Mega ST/STE real-time clock block ($FFFC20-$FFFC3F, an RP5C15 on odd bytes). ASE
        /// emulates no clock in any model, but the block is <b>decoded</b> all the same: on a real
        /// machine it answers whether or not the clock board is fitted, reading open bus when it
        /// is not. Only this block is decoded, not the rest of the page around the ACIAs -- what
        /// the gaps at $FFFC08-$FFFC1F and above $FFFC3F do is untested, so they keep faulting.
        /// <para>
        /// TOS 1.02 -- the Mega's own TOS, also sold as the upgrade for plain STs -- depends on
        /// exactly that. Its clock probe at $FC4C0C writes $09 to $FFFC3B, MOVEPs $0A05 into
        /// $FFFC25/27 and compares what comes back, taking a software fallback when it differs;
        /// the ROM installs no bus-error handler anywhere, so faulting the write sent the
        /// exception to the permanent vector and ended every cold boot in two bombs. TOS 1.04
        /// boots because it never runs that probe.
        /// </para>
        /// <para>
        /// It must not be served out of <see cref="Ports"/> either: echoing the pattern back is
        /// what tells TOS -- and EmuTOS, which probes the same block to decide it is on a Mega --
        /// that a clock IS fitted. Reads float high and writes are dropped, so the comparison
        /// fails and the machine is correctly taken for one without a clock.
        /// </para>
        /// </summary>
        static bool IsRtcBlock(uint addr) => addr >= 0xFFFC20 && addr <= 0xFFFC3F;

        /// <summary>
        /// End of the address window the machine decodes for system ROM (exclusive). A 256KB
        /// TOS sits in a 1MB window at $E00000, so the part beyond the image is decoded but
        /// empty — it reads as open bus (0xFF) instead of faulting, like the real ROM socket
        /// with its top address lines unconnected. A 192KB TOS fills its window exactly.
        /// </summary>
        uint RomWindowEnd => TosBase == 0xE00000 ? 0xF00000 : TosBase + (uint)TosSize;

        /// <summary>
        /// Set while a tool walks memory instead of the CPU running: the debugger's listing and
        /// the listing exporter disassemble through Moira, whose <c>read16Dasm</c> goes through
        /// the normal bus callbacks. Those reads are not bus cycles of the emulated machine, so
        /// they must not schedule a bus error — it would be taken by the CPU the moment the
        /// machine resumes, crashing a program that never did anything wrong. Use
        /// <see cref="ReadWithoutBusErrors"/> rather than setting it by hand.
        /// </summary>
        bool _suppressBusErrors;

        /// <summary>
        /// Runs <paramref name="body"/> with bus errors suppressed (see
        /// <see cref="_suppressBusErrors"/>). For debugger/tool code only: the emulated machine
        /// must always see real faults.
        /// </summary>
        public T ReadWithoutBusErrors<T>(Func<T> body)
        {
            bool previous = _suppressBusErrors;
            _suppressBusErrors = true;
            try { return body(); }
            finally { _suppressBusErrors = previous; }
        }

        /// <summary>Signals a bus error to the CPU, unless a tool is reading (see
        /// <see cref="_suppressBusErrors"/>).</summary>
        void BusError(uint addr, bool isWrite)
        {
            if (!_suppressBusErrors)
                CPU._moira.TriggerBusError(addr, isWrite);
        }

        const uint BANK_128K = 128 * 1024;
        const uint BANK_512K = 512 * 1024;
        const uint BANK_2M = 2048 * 1024;

        // Physical RAM banks (the SIMMs really plugged in the machine)
        uint ramBank0Size;
        uint ramBank1Size;

        // Logical MMU banks, as configured by TOS writing to $FF8001
        uint mmuBank0Size;
        uint mmuBank1Size;
        uint logicalRamEnd;     // mmuBank0Size + mmuBank1Size, capped to 4MB
        bool mmuIdentity;       // true when MMU config matches physical RAM (no translation needed)

        // $FF8001 value that matches the physical RAM configuration
        byte mmuConfigExpected;

        public byte[] RAM;   // 0x000000..0x3FFFFF (logical), physical size depends on config
        public byte[] ROM;
        public byte[] Ports; // I/O adresses, starting at 0xFF8000 (PortsBase)

        public Memory()
        {
            if (File.Exists(ConfigOptions.RunninConfig.TOSPath))
            {
                ROM = File.ReadAllBytes(ConfigOptions.RunninConfig.TOSPath);
                ColoredConsole.WriteLine($"TOS loaded from [[green]]{ConfigOptions.RunninConfig.TOSPath}[[/green]], size: [[yellow]]{ROM.Length}[[/yellow]] bytes.", ConfigOptions.DebugModes.Quiet);
            }
            else
            {
                // ROM stays null: CPU.InitCpu reports the failure and the machine stays off,
                // so the user can pick a TOS in the Configuration window and reset.
                string ErrorMessage = $"TOS file [[red]]{ConfigOptions.RunninConfig.TOSPath}[[/red]] not found.";
                ColoredConsole.WriteLine(ErrorMessage);
                TinyDialogsNet.TinyDialogs.MessageBox("TOS not found", ColoredConsole.Strip(ErrorMessage), TinyDialogsNet.MessageBoxDialogType.Ok, TinyDialogsNet.MessageBoxIconType.Warning, TinyDialogsNet.MessageBoxButton.Ok);
                return;
            }

            if (ROM.Length == 192 * 1024)
            {
                TosSize = 192 * 1024;
                TosBase = 0xFC0000;
            }
            else if (ROM.Length == 256 * 1024)
            {
                TosSize = 256 * 1024;
                TosBase = 0xE00000;
            }
            else
            {
                ColoredConsole.WriteLine($"Error: TOS size [[yellow]]{ROM.Length}[[/yellow]] bytes is unknow.");
                ROM = null;
                return;
            }

            Ports = new byte[(0xffffff - 0xff8000) + 1];

            // $FF8001 MMU memory configuration
            // bits 2-3 bank 0, bits 0-1 bank 1 (00=128KB, 01=512KB, 10=2MB)
            // --------------------------------
            // The physical RAM is made of two banks. TOS detects the size of each bank during
            // cold boot: it writes bank configurations to $FF8001 and then looks for mirrored
            // patterns in RAM (a 512KB SIMM configured as a 2MB bank repeats every 512KB).
            // Translate() implements that mirroring the same way Hatari does, so the TOS
            // memory detection works without patching.

            switch (Config.ConfigOptions.RunninConfig.RAMConfiguration)
            {
                case ConfigOptions.RAMConfigurations.RAM_512KB:
                    ramBank0Size = BANK_512K; ramBank1Size = 0;
                    mmuConfigExpected = 0x04; // 01 00 -> 512KB
                    break;
                case ConfigOptions.RAMConfigurations.RAM_1MB:
                    ramBank0Size = BANK_512K; ramBank1Size = BANK_512K;
                    mmuConfigExpected = 0x05; // 01 01 -> 1MB
                    break;
                case ConfigOptions.RAMConfigurations.RAM_2MB:
                    ramBank0Size = BANK_2M; ramBank1Size = 0;
                    mmuConfigExpected = 0x08; // 10 00 -> 2MB
                    break;
                default:
                case ConfigOptions.RAMConfigurations.RAM_4MB:
                    ramBank0Size = BANK_2M; ramBank1Size = BANK_2M;
                    mmuConfigExpected = 0x0A; // 10 10 -> 4MB
                    break;
            }

            RamSize = (int)(ramBank0Size + ramBank1Size);
            RAM = new byte[RamSize];

            // Boot with the MMU matching the physical RAM. TOS will reprogram it during
            // its memory detection anyway.
            Ports[STPortAdress.ST_MMU - PortsBase] = mmuConfigExpected;
            ApplyMMUConfig(mmuConfigExpected);

            // Resolution (low/high) and PAL sync. TOS overwrites $FF8260 during boot from the
            // monochrome-monitor detect line (MFP GPIP7); this just starts it consistent.
            Ports[0x260] = (byte)(ConfigOptions.RunninConfig.MonochromeMonitor ? 2 : 0);
            Ports[0x20a] = 2;
        }

        /// <summary>
        /// Decodes a value written to $FF8001 into the two logical MMU bank sizes.
        /// On the STF (Ricoh MMU) both banks can have different sizes; on the Mega ST (IMP MMU)
        /// and the STE (GST MCU) the bank 0 setting applies to both banks.
        /// </summary>
        void ApplyMMUConfig(byte value)
        {
            mmuBank0Size = MMUBankSize((value >> 2) & 3);

            if (ConfigOptions.RunninConfig.STModel == ConfigOptions.STModels.ST)
                mmuBank1Size = MMUBankSize(value & 3);
            else
                mmuBank1Size = mmuBank0Size;

            logicalRamEnd = Math.Min(mmuBank0Size + mmuBank1Size, RamRegionEnd);
            mmuIdentity = (mmuBank0Size == ramBank0Size) && (mmuBank1Size == ramBank1Size);
        }

        static uint MMUBankSize(int conf)
        {
            switch (conf)
            {
                case 0: return BANK_128K;
                case 1: return BANK_512K;
                case 2: return BANK_2M;
                default: return 0; // reserved
            }
        }

        /// <summary>
        /// Translates a logical RAM address into a physical offset inside the RAM array, taking
        /// into account the MMU bank configuration ($FF8001) versus the physical bank sizes.
        /// Returns -1 when the address falls inside a bank with no RAM behind it (void region).
        /// </summary>
        long Translate(uint addr)
        {
            uint bankStartPhysical;
            uint ramBankSize;
            uint mmuBankSize;

            if (addr < mmuBank0Size)
            {
                bankStartPhysical = 0;
                ramBankSize = ramBank0Size;
                mmuBankSize = mmuBank0Size;
            }
            else
            {
                bankStartPhysical = ramBank0Size;
                ramBankSize = ramBank1Size;
                mmuBankSize = mmuBank1Size;
            }

            if (ramBankSize == 0)
                return -1;

            uint phys;

            if (ConfigOptions.RunninConfig.STModel == ConfigOptions.STModels.STE)
            {
                // The STE MCU interleaves RAS/CAS lines (A1..A20 -> RAS0 CAS0 RAS1 CAS1...),
                // so the translation reduces to wrapping inside the physical bank size.
                phys = addr;
            }
            else
            {
                // STF/Mega ST MMU: A1-A10 always drive RAS0-RAS9, the CAS lines used depend
                // on the configured bank size, producing the bit shuffling below.
                phys = TranslateSTF(addr, ramBankSize, mmuBankSize);
            }

            phys &= ramBankSize - 1;
            return bankStartPhysical + phys;
        }

        static uint TranslateSTF(uint addr, uint ramBankSize, uint mmuBankSize)
        {
            if (ramBankSize == BANK_2M)
            {
                if (mmuBankSize == BANK_2M)
                    return addr;
                if (mmuBankSize == BANK_512K)
                    return ((addr & 0xffc00) << 1) | (addr & 0x7ff);
                return ((addr & 0x7fe00) << 2) | (addr & 0x7ff);            // MMU bank 128K
            }

            if (ramBankSize == BANK_512K)
            {
                if (mmuBankSize == BANK_2M)
                    return ((addr & 0xff800) >> 1) | (addr & 0x3ff);
                if (mmuBankSize == BANK_512K)
                    return addr;
                return ((addr & 0x3fe00) << 1) | (addr & 0x3ff);            // MMU bank 128K
            }

            // ramBankSize == BANK_128K
            if (mmuBankSize == BANK_2M)
                return ((addr & 0x7f800) >> 2) | (addr & 0x1ff);
            if (mmuBankSize == BANK_512K)
                return ((addr & 0x3fc00) >> 1) | (addr & 0x1ff);
            return addr;                                                    // MMU bank 128K
        }

        static bool IsSTE => ConfigOptions.RunninConfig.STModel == ConfigOptions.STModels.STE;

        static bool HasBlitter =>
            ConfigOptions.RunninConfig.STModel == ConfigOptions.STModels.STE ||
            ConfigOptions.RunninConfig.STModel == ConfigOptions.STModels.Mega;

        /// <summary>
        /// Reads a byte from the specified memory address, supporting access to RAM, ROM, and various I/O ports.
        /// </summary>
        /// <remarks>This method handles address mapping for RAM, ROM, and multiple I/O devices, including
        /// special handling for certain hardware registers. For unimplemented or unsupported addresses, the method
        /// returns 0xFF. Some address ranges may trigger internal hardware exceptions captured by Moira.</remarks>
        /// <param name="addr">The 24-bit memory address from which to read a byte. The address determines whether the value is read from
        /// RAM, ROM, or an I/O port. 32 bit addresses will be trimmed to 24 bits addresses.</param>
        /// <returns>The byte value read from the specified address. Returns 0xFF if the address is invalid or not implemented.</returns>
        //
        // Cycle-exact bus timing (CPU-facing entry points) :
        // Moira's read/write callbacks are wired to these wrappers (see CPU.InitCpu). Each call is
        // exactly one CPU bus cycle, so the ST wait states are applied here — once per access —
        // before the real transfer. The raw Read8/Read16/... below are reused internally (e.g. by
        // BigEndian) without re-applying the wait, so word accesses are not double-counted.
        public byte   CpuRead8 (uint addr)            { ApplyBusWait(addr); return Read8(addr); }
        public ushort CpuRead16(uint addr)            { ApplyBusWait(addr); return Read16(addr); }
        public void   CpuWrite8 (uint addr, byte v)   { ApplyBusWait(addr); Write8(addr, v); }
        public void   CpuWrite16(uint addr, ushort v) { ApplyBusWait(addr); Write16(addr, v); }

        /// <summary>
        /// Reproduces the Atari ST memory-bus wait states for a single CPU bus access, by advancing
        /// the Moira clock. The MMU shares RAM between CPU and shifter on a 2-cycle round-robin, so
        /// every CPU access is forced onto a 4-cycle grid: a misaligned access waits 2 cycles for
        /// its slot (this enforcement happens whether or not video actually needs the slot). ROM is
        /// exempt; the MFP and ACIA add fixed extra waits. No-op unless CycleExactBus is enabled.
        /// </summary>
        void ApplyBusWait(uint addr)
        {
            if (!ConfigOptions.RunninConfig.CycleExactBus) return;

            addr &= 0xFFFFFFu;

            // ROM accesses run without wait states (not on the shared MMU bus).
            if (addr >= TosBase && addr < TosBase + TosSize) return;

            long clock = CPU._moira.Clock;

            // ACIAs ($FFFC00-$FFFC07) are 6800-type (VPA) peripherals: instead of a fixed wait,
            // the 68000 synchronizes the access with the E clock (CPU/10) — it stalls until the
            // next multiple of 10 cycles, then the access adds 6 more cycles (Hatari's model).
            // This wait is self-stabilising: in a fixed-length loop the phase converges, so the
            // access cost becomes constant. Spectrum 512's screen kernel relies on exactly this:
            // its per-line glue reads $FFFC00 and was cycle-calibrated against the E clock.
            if (addr >= 0xFFFC00 && addr <= 0xFFFC07)
            {
                int toE = (int)(10 - clock % 10);
                if (toE == 10) toE = 0;
                CPU._moira.Clock = clock + toE + 6;
                return;
            }

            // Fixed extra waits for slow I/O chips, on top of the bus-slot alignment.
            int extra = 0;
            if (addr >= 0xFFFA00 && addr <= 0xFFFA2F) extra = ConfigOptions.RunninConfig.MfpWaitCycles;        // MFP 68901

            // 4-cycle MMU bus-slot alignment (RAM, shifter/video, sound, generic I/O). The 68000
            // clock is always even, so this contributes 0 or 2 cycles.
            int phase = ConfigOptions.RunninConfig.BusPhase & 3;
            int align = (phase - (int)(clock & 3)) & 3;

            int wait = align + extra;
            if (wait > 0) CPU._moira.Clock = clock + wait;
        }

        /// <summary>
        /// Re-derives the logical MMU bank configuration from the $FF8001 latch in the port
        /// array. Used when restoring a snapshot, after the Ports array has been loaded.
        /// </summary>
        public void RestoreMMUFromPorts()
        {
            ApplyMMUConfig(Ports[STPortAdress.ST_MMU - PortsBase]);
        }

        /// <summary>
        /// Reads a byte for the debugger memory monitor without triggering any bus side effect:
        /// no wait states, no bus errors and no device read handlers (I/O returns the raw latched
        /// byte from the port array). RAM goes through the same MMU translation as CPU accesses.
        /// </summary>
        public byte DebugPeek8(uint addr)
        {
            addr &= 0xFFFFFFu;

            // Vector mirror: the ST remaps the first 8 bytes of RAM to the ROM
            if (addr < 0x08)
                return ROM[(int)addr];

            if (addr < RamRegionEnd)
            {
                if (mmuIdentity)
                    return addr < (uint)RamSize ? RAM[(int)addr] : (byte)0;

                if (addr < logicalRamEnd)
                {
                    long phys = Translate(addr);
                    return phys < 0 ? (byte)0 : RAM[(int)phys];
                }

                return 0;   // void region
            }

            if (addr >= TosBase && addr < TosBase + TosSize)
                return ROM[(int)(addr - TosBase)];

            if (addr >= CartBase && addr < CartEnd)
                return CartRead8(addr);

            if (addr >= PortsBase)
                return Ports[(int)(addr - PortsBase)];

            return 0xFF;    // unmapped (would bus error on real hardware)
        }

        /// <summary>
        /// Writes a byte from the debugger memory monitor. Only the RAM region is writable
        /// (through the same MMU translation as CPU writes); ROM and I/O are left untouched so
        /// editing memory can never trigger device side effects. Returns true if the byte was stored.
        /// </summary>
        public bool DebugPoke8(uint addr, byte v)
        {
            addr &= 0xFFFFFFu;

            if (addr >= RamRegionEnd)
                return false;

            if (mmuIdentity)
            {
                if (addr >= (uint)RamSize)
                    return false;

                RAM[(int)addr] = v;
                return true;
            }

            if (addr < logicalRamEnd)
            {
                long phys = Translate(addr);
                if (phys >= 0)
                {
                    RAM[(int)phys] = v;
                    return true;
                }
            }

            return false;   // void region
        }

        public byte Read8(uint addr)
        {
            addr &= 0xFFFFFFu;  // 24 bits addressing

            // Vector mirror at 0x000000
            // The ST remaps the first 8 bytes of RAM to the ROM
            if (addr < 0x08)
                return ROM[(int)addr];

            // RAM region (GLUE decodes 0x000000-0x3FFFFF as RAM)
            if (addr < RamRegionEnd)
            {
                if (mmuIdentity)
                    return addr < (uint)RamSize ? RAM[(int)addr] : (byte)0;

                if (addr < logicalRamEnd)
                {
                    long phys = Translate(addr);
                    return phys < 0 ? (byte)0 : RAM[(int)phys];
                }

                return 0;   // void region, no bus error
            }

            // ROM
            if (addr >= TosBase && addr < TosBase + TosSize)
                return ROM[(int)(addr - TosBase)];

            // Cartridge port
            if (addr >= CartBase && addr < CartEnd)
                return CartRead8(addr);

            // Nothing decodes here (see IsUndecoded): the bus times out
            if (IsUndecoded(addr))
            {
                BusError(addr, false);
                return 0xFF;
            }

            // I/O
            if (addr >= PortsBase)
            {
                // Unmapped top page: no device decoded here -> bus error.
                if (addr >= UnmappedIoTop)
                {
                    BusError(addr, false);
                    return 0xFF;
                }

                // No chip answers here on this machine (see IsDecodedIo): the bus times out,
                // which is how software finds out which Atari it is running on.
                if (!IsDecodedIo(addr))
                {
                    // At Information and above, because this is the first thing to look at when
                    // a program that used to run starts failing: it names the register it asked
                    // for and who asked. A probe is a handful of these at boot; a flood means a
                    // block is missing from IsDecodedIo.
                    if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Information)
                        ColoredConsole.WriteLine($"I/O read of undecoded [[yellow]]${addr:X6}[[/yellow]] from PC=[[cyan]]${CPU._moira.PC0:X6}[[/cyan]] -> bus error");

                    BusError(addr, false);
                    return 0xFF;
                }

                // STE DMA sound + Microwire ($FF8900-$FF8925)
                if (addr >= 0xFF8900 && addr <= 0xFF8925)
                {
                    if (IsSTE)
                        return STEDmaSound.ReadByte(addr);

                    // On ST/Mega these registers don't exist -> bus error
                    if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Quiet)
                        ColoredConsole.WriteLine("Trying to read STe DMA sound registers on a non STE machine.. bus error!");

                    BusError(addr, false);
                    return 0xFF;
                }

                // STE extended joystick/joypad/lightpen ports ($FF9200-$FF9223)
                if (addr >= 0xFF9200 && addr <= 0xFF9223)
                {
                    if (IsSTE)
                        return 0xFF;    // nothing connected/pressed (inverted logic)

                    BusError(addr, false);
                    return 0xFF;
                }

                // YM2149. See the write path below for why the whole block is decoded and not
                // just $FF8800/$FF8802: only A1 picks between the two halves of the chip, so
                // every fourth address up to $FF88FF is another shadow of the same pair. Only
                // the select half answers a read; the data half and both odd shadows float high.
                if (addr >= STPortAdress.ST_PSGREADSELECT && addr <= STPortAdress.ST_PSGEND)
                    return (addr & 3) == 0 ? ASEMain._ym.PSGRegisterData() : (byte)0xFF;

                // FDC
                // Disk commands are forwarded to WD1772.cs, which handles them
                if (addr >= 0xFF8604 && addr <= 0xFF860D)
                    return WD1772.ReadByte(addr);

                // HD floppy density register ($FF860E-$FF860F). Decoded by the STE's MCU with no
                // register behind it (see IsDecodedIo, which bus-errors it on ST/Mega): it floats
                // high rather than answering out of the Ports latch, so a program cannot read
                // back what it wrote and conclude the drive handles high-density disks.
                if (addr >= 0xFF860E && addr <= 0xFF860F)
                    return 0xFF;

                 // Blitter ($FF8A00-$FF8A3D)
                 // On the real ST (without blitter), accessing these registers causes a bus error.
                 // On the STE/Mega the blitter is present and registers are readable.
                 if (addr >= 0xFF8A00 && addr <= 0xFF8A3D)
                 {
                     if (HasBlitter)
                     {
                         return Blitter.ReadByte(addr);
                     }

                     BusError(addr, false);
                     return 0xFF;
                 }

                // ACIA - Keyboard and Joystick ports
                if (addr == STPortAdress.ST_ACIACMD)
                    return ACIA.ReadStatus();

                if (addr == STPortAdress.ST_ACIADATA)
                    return ACIA.ReadData();

                // MIDI ACIA (second 6850). It shares the MFP GPIP4 interrupt line with the
                // keyboard ACIA (see AciaIrqLine), so shared level-6 handlers read its status
                // FIRST to see which chip requested the interrupt — with nothing received and
                // the transmitter idle it reads as TDRE set, IRQ/RDRF clear, which is what
                // keeps such handlers (Rodland) reading the keyboard data.
                if (addr == STPortAdress.ST_MIDICMD)
                    return MidiAcia.ReadStatus();

                if (addr == STPortAdress.ST_MIDIDATA)
                    return MidiAcia.ReadData();

                // Mega ST/STE clock: decoded, empty socket (see IsRtcBlock)
                if (IsRtcBlock(addr))
                    return 0xFF;

                // Video Address Pointer ($FF8205/07/09): computed live so it advances through
                // the active display and freezes in the borders, as games that poll it expect.
                if (addr == STPortAdress.ST_HIVADRPOINT)
                    return (byte)(VideoTiming.GetCurrentVideoAddress() >> 16);
                if (addr == STPortAdress.ST_MIVADRPOINT)
                    return (byte)(VideoTiming.GetCurrentVideoAddress() >> 8);
                if (addr == STPortAdress.ST_LOVADRPOINT)
                    return (byte)(VideoTiming.GetCurrentVideoAddress());

                // Any other port below MFP registers
                if (addr < MFP68901.MFP_BASE)
                    return Ports[addr - PortsBase];

                // treatment for MFP registers
                if (addr >= MFP68901.MFP_BASE && addr <= MFP68901.MFP_BASE + 0x26)
                {
                    uint offset = addr - MFP68901.MFP_BASE;

                    switch (offset)
                    {
                        case 0x01: return ASEMain._mfp.GPIP;
                        case 0x03: return ASEMain._mfp.AER;
                        case 0x05: return ASEMain._mfp.DDR;
                        case 0x07: return ASEMain._mfp.IERA;
                        case 0x09: return ASEMain._mfp.IERB;
                        case 0x0B: return ASEMain._mfp.IPRA;
                        case 0x0D: return ASEMain._mfp.IPRB;
                        case 0x0F: return ASEMain._mfp.ISRA;
                        case 0x11: return ASEMain._mfp.ISRB;
                        case 0x13: return ASEMain._mfp.IMRA;
                        case 0x15: return ASEMain._mfp.IMRB;
                        case 0x17: return ASEMain._mfp.VR;
                        case 0x19: return ASEMain._mfp.TACR;
                        case 0x1B: return ASEMain._mfp.TBCR;
                        case 0x1D: return ASEMain._mfp.TCDCR;
                        // Timer data registers: projected live to the current CPU clock so tight
                        // poll loops see the counter advance smoothly (see MFP68901.ReadTimerCounter).
                        case 0x1F: return ASEMain._mfp.ReadTimerCounter(0);
                        case 0x21: return ASEMain._mfp.ReadTimerCounter(1);
                        case 0x23: return ASEMain._mfp.ReadTimerCounter(2);
                        case 0x25: return ASEMain._mfp.ReadTimerCounter(3);
                        default:
                            // this should throw a bus error
                            return Ports[addr - PortsBase];
                    }
                }

            }

            return 0xFF;
        }

        /// <summary>
        /// Reads a 16-bit unsigned value from the specified memory address, supporting access to RAM, ROM, and I/O
        /// regions.
        /// </summary>
        /// <remarks>This method handles memory-mapped access to RAM, ROM, and certain I/O ports. If the
        /// address refers to an unimplemented or restricted region, a bus error is triggered and 0xFFFF is returned.
        /// Callers should ensure the address is valid for the intended memory region.</remarks>
        /// <param name="addr">The memory address from which to read the 16-bit value. 32 bit addresses will be trimmed to 24 bits addresses.</param>
        /// <returns>The 16-bit value read from the specified address, or 0xFFFF if the address is invalid or not accessible.</returns>
        public ushort Read16(uint addr)
        {
            addr &= 0xFFFFFFu;  // 24 bits addressing

            // RAM
            if (addr + 1 < RamRegionEnd)
                return BigEndian.Read16(addr);

            // ROM
            if (addr >= TosBase && addr + 1 < TosBase + TosSize)
                return BigEndian.Read16(addr);

            // Cartridge port
            if (addr >= CartBase && addr + 1 < CartEnd)
                return (ushort)((CartRead8(addr) << 8) | CartRead8(addr + 1));

            // Nothing decodes here (see IsUndecoded): the bus times out
            if (IsUndecoded(addr))
            {
                BusError(addr, false);
                return 0xFFFF;
            }

            // I/O
            if (addr >= PortsBase)
            {
                // Unmapped top page: no device decoded here -> bus error.
                if (addr >= UnmappedIoTop)
                {
                    BusError(addr, false);
                    return 0xFFFF;
                }

                // Nothing decoded here on this machine (see IsDecodedIo)
                if (!IsDecodedIo(addr))
                {
                    BusError(addr, false);
                    return 0xFFFF;
                }

                // FDC
                if (addr >= 0xFF8604 && addr <= 0xFF860D)
                    return WD1772.ReadWord(addr);

                // Blitter ($FF8A00-$FF8A3D)
                if (addr >= 0xFF8A00 && addr <= 0xFF8A3C)
                {
                    if (HasBlitter)
                    {
                        return Blitter.ReadWord(addr);
                    }

                    BusError(addr, false);
                    return 0xFFFF;
                }

                // Mega ST/STE clock: decoded, empty socket (see IsRtcBlock)
                if (IsRtcBlock(addr))
                    return 0xFFFF;

                // Any other I/O port is read without special treatment.
                return BigEndian.Read16(addr);
            }

            // out of the bounds of the RAM, ROM or I/O ports, returns waste
            return 0xFFFF;
        }

        /// <summary>
        /// Reads a 32-bit unsigned integer from the specified memory address, supporting RAM, ROM, and I/O regions.
        /// </summary>
        /// <remarks>The method determines the appropriate memory region (RAM, ROM, or I/O) based on the
        /// address and reads the value accordingly. If the address does not correspond to a valid region, a default
        /// value of 0xFFFFFFFF is returned.</remarks>
        /// <param name="addr">The memory address from which to read the 32-bit value. 32 bit addresses will be trimmed to 24 bits addresses.</param>
        /// <returns>A 32-bit unsigned integer containing the value read from the specified address, or 0xFFFFFFFF if the address
        /// is outside valid memory regions.</returns>
        public uint Read32(uint addr)
        {
            addr &= 0xFFFFFFu;  // 24 bits addressing

            // RAM
            if (addr + 3 < RamRegionEnd)
                return BigEndian.Read32(addr);

            // ROM
            if (addr >= TosBase && addr + 3 < TosBase + TosSize)
                return BigEndian.Read32(addr);

            // Cartridge port
            if (addr >= CartBase && addr + 3 < CartEnd)
                return ((uint)CartRead8(addr) << 24) | ((uint)CartRead8(addr + 1) << 16) |
                       ((uint)CartRead8(addr + 2) << 8) | CartRead8(addr + 3);

            // Nothing decodes here (see IsUndecoded): the bus times out
            if (IsUndecoded(addr))
            {
                BusError(addr, false);
                return 0xFFFFFFFF;
            }

            // I/O
            if (addr >= PortsBase)
            {
                // Unmapped top page: no device decoded here -> bus error.
                if (addr >= UnmappedIoTop)
                {
                    BusError(addr, false);
                    return 0xFFFFFFFF;
                }

                // Nothing decoded here on this machine (see IsDecodedIo). Both halves are
                // checked: a long read straddling the end of a block faults on a real bus too.
                if (!IsDecodedIo(addr) || !IsDecodedIo(addr + 2))
                {
                    BusError(addr, false);
                    return 0xFFFFFFFF;
                }

                // Mega ST/STE clock: decoded, empty socket (see IsRtcBlock)
                if (IsRtcBlock(addr))
                    return 0xFFFFFFFF;

                return BigEndian.Read32(addr);
            }

            return 0xFFFFFFFF;
        }

        /// <summary>One byte off the cartridge port: the synthetic GEMDOS cartridge when it is
        /// installed, a floating 0xFF otherwise.</summary>
        static byte CartRead8(uint addr)
        {
            byte[] cart = GemdosHD.CartRom;
            uint offset = addr - CartBase;
            return cart != null && offset < (uint)cart.Length ? cart[offset] : (byte)0xFF;
        }

        /// <summary>
        /// Reads one big-endian word the way the SHIFTER does: straight off the memory bus, with
        /// no side effects whatsoever.
        ///
        /// This must NOT go through Read8. The shifter is a DMA device: it fetches whatever the
        /// video counter points at and never raises a CPU exception — an address that decodes to
        /// nothing simply clocks out floating-bus garbage. Read8, on the other hand, calls
        /// BusError() for undecoded space, which schedules a bus error on the CPU. The renderer
        /// runs outside the CPU's execution context, so doing that from here plants hundreds of
        /// spurious faults per scanline; the emulated machine then takes them all on resume,
        /// piles exception on exception, and Moira ends up throwing DoubleFault across the
        /// P/Invoke boundary, which surfaces as "Not controlled exception in Moira: SEHException".
        /// Reaching it only needs the video counter to point somewhere unpopulated for one frame
        /// — which is exactly what happens between the three byte writes of $FF8209/07/05.
        /// </summary>
        public ushort ReadVideoWord(uint addr)
        {
            addr &= 0xFFFFFEu;

            if (addr < RamRegionEnd)
            {
                if (mmuIdentity)
                    return addr + 1 < (uint)RamSize
                         ? (ushort)((RAM[(int)addr] << 8) | RAM[(int)addr + 1])
                         : (ushort)0;

                if (addr + 1 < logicalRamEnd)
                {
                    long p0 = Translate(addr), p1 = Translate(addr + 1);
                    int hi = p0 >= 0 ? RAM[(int)p0] : 0;
                    int lo = p1 >= 0 ? RAM[(int)p1] : 0;
                    return (ushort)((hi << 8) | lo);
                }

                return 0;   // void region
            }

            if (addr >= TosBase && addr + 1 < TosBase + TosSize)
            {
                int off = (int)(addr - TosBase);
                return (ushort)((ROM[off] << 8) | ROM[off + 1]);
            }

            return 0;       // nothing driving the bus
        }

        /// <summary>True when the whole range lies inside the MMU-configured RAM. The GEMDOS
        /// hard drive validates guest buffers with this before bulk transfers.</summary>
        public bool IsRamArea(uint addr, uint size)
        {
            addr &= 0xFFFFFFu;
            return (ulong)addr + size <= logicalRamEnd;
        }

        /// <summary>
        /// Bulk copy into emulated RAM. With the MMU mapping the identity (the usual case) it
        /// is a single Array.Copy; otherwise it falls back to per-byte writes through the MMU
        /// translation. Non-RAM targets are ignored byte-wise, like any other RAM write.
        /// </summary>
        public void WriteBytes(uint addr, byte[] data, int offset, int count)
        {
            addr &= 0xFFFFFFu;

            if (mmuIdentity && (ulong)addr + (uint)count <= (uint)RamSize)
            {
                Array.Copy(data, offset, RAM, (int)addr, count);
                return;
            }

            for (int i = 0; i < count; i++)
                Write8(addr + (uint)i, data[offset + i]);
        }

        /// <summary>Bulk copy out of emulated memory (same fast path as <see cref="WriteBytes"/>).</summary>
        public void ReadBytes(uint addr, byte[] data, int offset, int count)
        {
            addr &= 0xFFFFFFu;

            if (mmuIdentity && addr >= 0x08 && (ulong)addr + (uint)count <= (uint)RamSize)
            {
                Array.Copy(RAM, (int)addr, data, offset, count);
                return;
            }

            for (int i = 0; i < count; i++)
                data[offset + i] = Read8(addr + (uint)i);
        }

        /// <summary>Bulk fill of emulated RAM (BSS/heap clearing when loading programs).</summary>
        public void FillBytes(uint addr, byte value, int count)
        {
            addr &= 0xFFFFFFu;

            if (mmuIdentity && (ulong)addr + (uint)count <= (uint)RamSize)
            {
                Array.Fill(RAM, value, (int)addr, count);
                return;
            }

            for (int i = 0; i < count; i++)
                Write8(addr + (uint)i, value);
        }

        /// <summary>
        /// Writes an 8-bit value to the specified memory address, handling both standard memory and memory-mapped
        /// device registers as appropriate.
        /// </summary>
        /// <remarks>If the address corresponds to a read-only memory (ROM) region, the write operation is
        /// ignored and a warning is issued. For addresses mapped to hardware devices, this method performs the
        /// appropriate device-specific write operation. Writing to certain unimplemented or restricted regions may
        /// trigger additional warnings or errors.</remarks>
        /// <param name="addr">The 24-bit memory address to which the value will be written. Must be within the valid range for RAM, ROM,
        /// or device-mapped addresses. 32 bit addresses will be trimmed to 24 bits addresses.</param>
        /// <param name="v">The 8-bit value to write to the specified address.</param>
        public void Write8(uint addr, byte v)
        {
            addr &= 0xFFFFFFu;  // 24 bits addressing

            // RAM region
            if (addr < RamRegionEnd)
            {
                if (mmuIdentity)
                {
                    if (addr < (uint)RamSize)
                        RAM[(int)addr] = v;
                    return;
                }

                if (addr < logicalRamEnd)
                {
                    long phys = Translate(addr);
                    if (phys >= 0)
                        RAM[(int)phys] = v;
                }

                return;     // void region, write ignored
            }

            if (addr >= TosBase && addr < TosBase + TosSize)
            {
                ColoredConsole.WriteLine($"Warning: Attempt to write to ROM area -> [[red]]{addr:X8}.b[[/red]]", ConfigOptions.DebugModes.Quiet);
                return;
            }

            // Cartridge port: decoded, but it is ROM — the write is simply dropped
            if (addr >= CartBase && addr < CartEnd)
                return;

            // Nothing decodes here (see IsUndecoded): the bus times out. Probing code writes
            // before it reads (the ICD driver pulses the IDE reset line), so the write has to
            // fault too or the probe never learns the device is missing.
            if (IsUndecoded(addr))
            {
                BusError(addr, true);
                return;
            }

            if (addr >= PortsBase)
            {
                // No chip decodes this address on this machine (see IsDecodedIo): the bus times
                // out on a write exactly as it does on a read. This has to fault, not drop
                // silently: TOS' own Mega ST/STE real-time clock probe (e.g. $FC1F84 in TOS
                // 1.04) *writes* a byte to the chip first and only trusts a follow-up read if
                // that write didn't fault — it uninstalls its temporary bus-error handler right
                // after the write, before the read. Dropping the write silently made that probe
                // believe the clock chip answered, so it moved on to the read with the real
                // (permanent, not-yet-initialised) bus-error vector in place; the fault that
                // arrived one instruction later on the read hit that vector instead of the
                // probe's own, and TOS' boot showed its bus-error bomb screen and hung — on
                // every model, since every real TOS runs this same detection at cold boot.
                if (!IsDecodedIo(addr))
                {
                    if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Information)
                        ColoredConsole.WriteLine($"I/O write of undecoded [[yellow]]${addr:X6}[[/yellow]] from PC=[[cyan]]${CPU._moira.PC0:X6}[[/cyan]] -> bus error");

                    BusError(addr, true);
                    return;
                }

                // MMU banks configuration
                if (addr == STPortAdress.ST_MMU)
                {
                    ApplyMMUConfig(v);
                    Ports[addr - PortsBase] = v;
                    return;
                }

                // Chip de sonido YM2149.
                //
                // The PSG decodes exactly two address lines inside its block: A1 chooses the
                // register-select half ($FF8800) from the data half ($FF8802), and A0 selects a
                // "shadow" of whichever half A1 picked. Nothing above A1 is decoded, so the pair
                // repeats every four bytes all the way to $FF88FF, and the shadows are as real
                // as the base addresses. Recognising only the two exact addresses left the rest
                // of the block falling through to the generic port latch, where the write did
                // nothing at all.
                //
                // That is not a curiosity: it is how the fast replayers program the chip. A
                // MOVEM.L of four registers to $FFFF8800 is eight consecutive word writes
                // ($FF8800, $FF8802, $FF8804 ... $FF880E), which the shadows turn into four
                // select/data pairs — four PSG registers set by one instruction. Wings of Death
                // and Toki (Jochen Hippel's replayer) drive their music entirely through
                // "movem.l d0-d3,$FFFF8800.w": with only the first pair decoded, one register
                // out of four reached the chip, every volume stayed at zero and the games ran in
                // complete silence. MOVEP over the odd shadows is the other common idiom.
                //
                // Semantics from Hatari's psg.c, which documents the same decoding and the same
                // instruction sequences measured on real hardware.
                if (addr >= STPortAdress.ST_PSGREADSELECT && addr <= STPortAdress.ST_PSGEND)
                {
                    // A byte access reaches either half, odd shadows included — a MOVEP is a
                    // series of byte accesses, which is what makes it work over $FF8801/$FF8803.
                    if ((addr & 2) == 0)
                        ASEMain._ym.PSGRegisterSelect(v);
                    else
                        ASEMain._ym.PSGWriteRegister(v);
                    return;
                }

                // Palette registers ($FF8240-$FF825F)
                // The shifter only stores 3 bits per component on the ST (read back as 0x777)
                // and 4 bits per component on the STE (read back as 0xFFF).
                if (addr >= STPortAdress.ST_PALLETE && addr < STPortAdress.ST_PALLETE + 32)
                {
                    if ((addr & 1) == 0)
                        v &= IsSTE ? (byte)0x0F : (byte)0x07;   // high byte: bits 11-8
                    else
                        v &= IsSTE ? (byte)0xFF : (byte)0x77;   // low byte: bits 7-0

                    Ports[addr - PortsBase] = v;

                    // Record the write with its cycle inside the line so the renderer can apply it
                    // at the matching horizontal position (Spectrum 512 / mid-line palette splits).
                    VideoTiming.OnPaletteWrite((int)(addr - STPortAdress.ST_PALLETE), v,
                                               (int)(CPU._moira.Clock - VideoTiming.LineStartClock));
                    return;
                }

                // Sync register ($FF820A) and resolution ($FF8260): record the write together
                // with its exact cycle inside the current scanline so VideoTiming can resolve
                // the border-removal tricks and the live Video Address Pointer.
                if (addr == STPortAdress.ST_TVHz)
                {
                    VideoTiming.OnSyncWrite(v, (int)(CPU._moira.Clock - VideoTiming.LineStartClock));
                    Ports[addr - PortsBase] = v;
                    return;
                }
                if (addr == STPortAdress.ST_RES)
                {
                    VideoTiming.OnResWrite(v, (int)(CPU._moira.Clock - VideoTiming.LineStartClock));
                    Ports[addr - PortsBase] = v;
                    return;
                }

                // STE horizontal fine scroll. $FF8264 and $FF8265 are the same 4-bit latch; only
                // $FF8265 adds the shifter's prefetch cycle. Both slots are kept in sync so a read
                // back (and a snapshot restore, through VideoTiming.RestoreFromPorts) sees it.
                if (addr == STPortAdress.ST_HSCROLL || addr == STPortAdress.ST_HSCROLL_NP)
                {
                    if (IsSTE)
                        VideoTiming.OnHScrollWrite(v, addr == STPortAdress.ST_HSCROLL);
                    v &= 0x0F;                       // 4-bit latch: that is all it reads back as
                    Ports[STPortAdress.ST_HSCROLL - PortsBase] = v;
                    Ports[STPortAdress.ST_HSCROLL_NP - PortsBase] = v;
                    return;
                }

                // STE line width: latched by VideoTiming, because a write landing in the right
                // border only takes effect on the next line (the shifter consumes it when the
                // display turns off).
                if (addr == STPortAdress.ST_LINEWIDTH)
                {
                    if (IsSTE)
                        VideoTiming.OnLineWidthWrite(v);
                    Ports[addr - PortsBase] = v;
                    return;
                }

                // Video address counter ($FF8205/07/09). Writable, unlike the video BASE registers
                // the GLUE only reloads at the top of the frame: this moves the shifter's counter
                // right now, which is how a smooth vertical scroll keeps feeding it mid-screen.
                if (addr == STPortAdress.ST_HIVADRPOINT || addr == STPortAdress.ST_MIVADRPOINT ||
                    addr == STPortAdress.ST_LOVADRPOINT)
                {
                    int shift = addr == STPortAdress.ST_HIVADRPOINT ? 16
                              : addr == STPortAdress.ST_MIVADRPOINT ? 8 : 0;
                    VideoTiming.OnVideoCounterWrite(shift, v);
                    Ports[addr - PortsBase] = v;
                    return;
                }

                // FDC
                if (addr >= 0xFF8604 && addr <= 0xFF860D)
                {
                    WD1772.WriteByte(addr, v);
                    Ports[addr - PortsBase] = v;
                    return;
                }

                // HD floppy density register: decoded on the STE, nothing behind it (see
                // IsDecodedIo). Dropped instead of latched in Ports, so the read path keeps
                // floating high.
                if (addr >= 0xFF860E && addr <= 0xFF860F)
                    return;

                // STE DMA sound + Microwire ($FF8900-$FF8925)
                if (addr >= 0xFF8900 && addr <= 0xFF8925)
                {
                    if (IsSTE)
                    {
                        STEDmaSound.WriteByte(addr, v);
                        return;
                    }

                    // Writes on ST/Mega fall through and just land in the Ports array
                }

                // STE extended joystick/joypad ports ($FF9200-$FF9223): writes are ignored
                if (addr >= 0xFF9200 && addr <= 0xFF9223)
                    return;

                // Mega ST/STE clock: decoded, nothing behind it (see IsRtcBlock). Dropped rather
                // than latched in Ports, or a probe would read its own pattern back and conclude
                // the machine has a clock.
                if (IsRtcBlock(addr))
                    return;

                // ACIA
                if (addr == STPortAdress.ST_ACIACMD)
                {
                    ACIA.WriteControl(v);
                    return;
                }

                if (addr == STPortAdress.ST_ACIADATA)
                {
                    ACIA.HandleCommand(v);
                    return;
                }

                // MIDI ACIA (second 6850): control register, and data bytes leaving through
                // the ST's MIDI OUT (routed by MidiManager to the configured destination).
                if (addr == STPortAdress.ST_MIDICMD)
                {
                    MidiAcia.WriteControl(v);
                    return;
                }

                if (addr == STPortAdress.ST_MIDIDATA)
                {
                    MidiAcia.WriteData(v);
                    return;
                }

                // Blitter ($FF8A00-$FF8A3D)
                if (addr >= 0xFF8A00 && addr <= 0xFF8A3D)
                {
                    if (HasBlitter)
                    {
                        Blitter.WriteByte(addr, v);
                        return;
                    }

                    BusError(addr, true);
                    return;
                }

                uint offset = addr - MFP68901.MFP_BASE;

                // This is a complete mess.. fixme later
                switch (offset)
                {
                    case 0x03: ASEMain._mfp.AER = v; break;
                    case 0x05: ASEMain._mfp.DDR = v; break;
                    case 0x07: // IERA - disabling a channel also clears its pending bit
                        ASEMain._mfp.IPRA &= v;
                        ASEMain._mfp.IERA = v;
                        ASEMain._mfp.UpdateIRQ();
                        break;

                    case 0x09: // IERB - disabling a channel also clears its pending bit
                        ASEMain._mfp.IPRB &= v;
                        ASEMain._mfp.IERB = v;
                        ASEMain._mfp.UpdateIRQ();
                        break;

                    case 0x0B: // IPRA - writing 0 clears the bit, writing 1 has no effect
                        ASEMain._mfp.IPRA &= v;
                        ASEMain._mfp.UpdateIRQ();
                        break;

                    case 0x0D: // IPRB - writing 0 clears the bit, writing 1 has no effect
                        ASEMain._mfp.IPRB &= v;
                        ASEMain._mfp.UpdateIRQ();
                        break;

                    case 0x0F: // ISRA: escribir 0 limpia
                        ASEMain._mfp.ISRA &= v;
                        ASEMain._mfp.UpdateIRQ();
                        break;

                    case 0x11: // ISRB: escribir 0 limpia
                        ASEMain._mfp.ISRB &= v;
                        ASEMain._mfp.UpdateIRQ();
                        break;

                    case 0x13: // IMRA
                        ASEMain._mfp.IMRA = v;
                        ASEMain._mfp.UpdateIRQ();
                        break;

                    case 0x15: // IMRB
                        ASEMain._mfp.IMRB = v;
                        ASEMain._mfp.UpdateIRQ();
                        break;

                    case 0x17: // VR
                        ASEMain._mfp.VR = (byte)(v & 0xF8);
                        if ((ASEMain._mfp.VR & 0x08) == 0)
                        {
                            ASEMain._mfp.ISRA = 0;
                            ASEMain._mfp.ISRB = 0;
                        }
                        break;

                    case 0x19: // TACR
                        {
                            byte old = ASEMain._mfp.TACR;
                            int oldMode = old & 0x0F;

                            ASEMain._mfp.TACR = v;
                            int newMode = v & 0x0F;

                            // Si cambia modo/prescaler, resetea fase del prescaler
                            if (oldMode != newMode)
                                ASEMain._mfp.timerAPredivAcc = 0;

                            // If timer transitions from stopped to running, always reload counter from TDR
                            bool wasOff = (oldMode == 0);
                            bool isOn = (newMode != 0);
                            if (wasOff && isOn)
                                ASEMain._mfp.timerACounter = (ASEMain._mfp.TADR == 0 ? 256 : ASEMain._mfp.TADR);

                            break;
                        }

                    case 0x1B:
                        { // TBCR - always reload counter when transitioning from stopped to running
                            bool wasOff = (ASEMain._mfp.TBCR & 0x07) == 0;
                            ASEMain._mfp.TBCR = v;
                            if (wasOff && (v & 0x07) != 0)
                                ASEMain._mfp.timerBCounter = (ASEMain._mfp.TBDR == 0 ? 256 : ASEMain._mfp.TBDR);
                            break;
                        }

                    case 0x1D: // TCDCR
                        {
                            byte old = ASEMain._mfp.TCDCR;
                            ASEMain._mfp.TCDCR = v;

                            // Timer C: bits 4..6
                            bool cWasOff = (((old >> 4) & 0x07) == 0);
                            bool cIsOn = (((v >> 4) & 0x07) != 0);
                            if (cWasOff && cIsOn)
                                ASEMain._mfp.timerCCounter = (ASEMain._mfp.TCDR == 0) ? 256 : ASEMain._mfp.TCDR;

                            // Timer D: bits 0..2
                            bool dWasOff = ((old & 0x07) == 0);
                            bool dIsOn = ((v & 0x07) != 0);
                            if (dWasOff && dIsOn)
                                ASEMain._mfp.timerDCounter = (ASEMain._mfp.TDDR == 0) ? 256 : ASEMain._mfp.TDDR;

                            break;
                        }

                    case 0x1F: // TADR - only reload counter when timer is stopped
                        ASEMain._mfp.TADR = v;
                        if ((ASEMain._mfp.TACR & 0x0F) == 0)
                            ASEMain._mfp.timerACounter = (v == 0 ? 256 : v);
                        break;

                    case 0x21: // TBDR - only reload counter when timer is stopped
                        ASEMain._mfp.TBDR = v;
                        if ((ASEMain._mfp.TBCR & 0x0F) == 0)
                            ASEMain._mfp.timerBCounter = (v == 0 ? 256 : v);
                        break;

                    case 0x23: // TCDR - only reload counter when timer is stopped
                        ASEMain._mfp.TCDR = v;
                        if (((ASEMain._mfp.TCDCR >> 4) & 0x07) == 0)
                            ASEMain._mfp.timerCCounter = (v == 0 ? 256 : v);
                        break;

                    case 0x25: // TDDR - only reload counter when timer is stopped
                        ASEMain._mfp.TDDR = v;
                        if ((ASEMain._mfp.TCDCR & 0x07) == 0)
                            ASEMain._mfp.timerDCounter = (v == 0 ? 256 : v);
                        break;
                }

                Ports[addr - PortsBase] = v;
            }
        }

        /// <summary>
        /// Writes a 16-bit value to the specified memory address, handling the operation according to the address
        /// range.
        /// </summary>
        /// <remarks>If the address is within the ROM area, the method does not perform the write and
        /// instead logs a warning. Specific hardware port address ranges are handled accordingly.</remarks>
        /// <param name="addr">The 24-bit memory address at which to write the 16-bit value. Must be within a valid writable memory range. 32 bit addresses will be trimmed to 24 bits addresses.</param>
        /// <param name="v">The 16-bit value to write to the specified address.</param>
        public void Write16(uint addr, ushort v)
        {
            addr &= 0xFFFFFFu; // 24 bits addressing

            if (addr + 1 < RamRegionEnd)
            {
                BigEndian.Write16(addr, v);
                return;
            }

            if (addr >= TosBase && addr + 1 < TosBase + TosSize)
            {
                ColoredConsole.WriteLine($"Warning: Attempt to write to ROM area -> [[yellow]]${addr:X8}.w[[/yellow]]", ConfigOptions.DebugModes.Quiet);
                return;
            }

            // Cartridge port: decoded ROM, write dropped
            if (addr >= CartBase && addr + 1 < CartEnd)
                return;

            // Nothing decodes here (see IsUndecoded): the bus times out
            if (IsUndecoded(addr))
            {
                BusError(addr, true);
                return;
            }

            if (addr >= PortsBase)
            {
                // No IsDecodedIo check here on purpose: every other write below ends up in
                // BigEndian.Write16, which is two Write8 calls, and that is where undecoded
                // addresses are resolved — byte by byte, so a word straddling the end of a
                // block still writes the half that does exist and only faults on the half
                // that does not.

                // FDC
                if (addr >= 0xFF8604 && addr <= 0xFF860D)
                {
                    WD1772.WriteWord(addr, v);
                    return;
                }

                // Blitter ($FF8A00-$FF8A3C) - word writes handled atomically
                if (addr >= 0xFF8A00 && addr <= 0xFF8A3C)
                {
                    if (HasBlitter)
                    {
                        Blitter.WriteWord(addr, v);
                        return;
                    }
                }

                // YM2149: a word access is NOT two byte accesses here (see Write8 for the
                // decoding). The chip hangs off the high half of the data bus, so only the high
                // byte reaches it, and the odd address the low byte would land on is the shadow
                // of the very half being written — the real chip cannot take both from one
                // instruction, so that half of the word is dropped rather than written twice.
                // Letting this fall through to BigEndian.Write16 would turn every
                // "move.w #$0700,$FFFF8800" into "select register 7, then select register 0".
                if (addr >= STPortAdress.ST_PSGREADSELECT && addr <= STPortAdress.ST_PSGEND)
                {
                    if ((addr & 2) == 0)
                        ASEMain._ym.PSGRegisterSelect((byte)(v >> 8));
                    else
                        ASEMain._ym.PSGWriteRegister((byte)(v >> 8));
                    return;
                }

                BigEndian.Write16(addr, v);
                return;
            }
        }

        /// <summary>
        /// Writes a 32-bit value to the specified memory address, enforcing address range and access restrictions.
        /// </summary>
        /// <remarks>If the address is within the ROM area, the write operation is ignored and a warning
        /// is logged. Writes to the ports area are permitted without restriction.</remarks>
        /// <param name="addr">The 24-bit memory address at which to write the 32-bit value. Must not exceed the available RAM size. 32 bit addresses will be trimmed to 24 bits addresses.</param>
        /// <param name="v">The 32-bit value to write to the specified memory address.</param>
        public void Write32(uint addr, uint v)
        {
            addr &= 0xFFFFFFu;  // 24 bits addressing

            if (addr + 3 < RamRegionEnd)
            {
                BigEndian.Write32(addr, v);
                return;
            }

            if (addr >= TosBase && addr + 3 < TosBase + TosSize)
            {
                ColoredConsole.WriteLine($"Warning: Attempt to write to ROM area -> [[yellow]]${addr:X8}.l[[/yellow]]", ConfigOptions.DebugModes.Quiet);
                return;
            }

            // Cartridge port: decoded ROM, write dropped
            if (addr >= CartBase && addr + 3 < CartEnd)
                return;

            // Nothing decodes here (see IsUndecoded): the bus times out
            if (IsUndecoded(addr))
            {
                BusError(addr, true);
                return;
            }

            if (addr >= PortsBase)
            {
                // Undecoded addresses are dropped one byte at a time inside Write8, which
                // BigEndian.Write32 goes through (see Write16).
                BigEndian.Write32(addr, v);
                return;
            }
        }
    }
}
