/*
 * 
 * Memory control functions.
 * Acts as GLUE and MMU in the Atari ST.
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
            public const uint ST_MMU = PortsBase;          // Memory banks configuration

            public const uint ST_SCRHIGHADDR = 0xFF8201;   // Screen address (high)
            public const uint ST_SCRMIDADDR = 0xFF8203;    // Screen address (mid)
            public const uint ST_SCRLOWADDR = 0xFF8203;    // Screen address (low, this register is for the STE)
            public const uint ST_HIVADRPOINT = 0xFF8205;   // Video address pointer (high)
            public const uint ST_MIVADRPOINT = 0xFF8207;   // Video address pointer (mid)
            public const uint ST_LOVADRPOINT = 0xFF8209;   // Video address pointer (low)
            public const uint ST_TVHz = 0xFF820A;          // Hz
            public const uint ST_PALLETE = 0xFF8240;       // Palette
            public const uint ST_RES = 0xFF8260;           // Screen resolution

            public const uint ST_PSGREADSELECT = 0xFF8800; // PSG/YM Read data/Register select
            public const uint ST_PSGWRITEDATA = 0xFF8802;  // PSG/YM Write data

            public const uint ST_ACIACMD = 0xFFFC00;       // Keyboard ACIA control
            public const uint ST_ACIADATA = 0xFFFC02;      // Keyboard ACIA data
        }

        public int TosSize = 192 * 1024;
        public int RamSize = 1 * 1024 * 1024;
        public uint TosBase = 0xFC0000;
        public const uint PortsBase = 0xFF8000;

        byte MMUConfig = 0;

        public byte[] RAM;   // 0x000000..0x0FFFFF
        public byte[] ROM;
        public byte[] Ports; // I/O adresses, starting at 0xFF8000 (PortsBase)

        public Memory()
        {
            if (File.Exists(ConfigOptions.RunninConfig.TOSPath))
            {
                ROM = File.ReadAllBytes(ConfigOptions.RunninConfig.TOSPath);
                ColoredConsole.WriteLine($"TOS loaded from [[green]]{ConfigOptions.RunninConfig.TOSPath}[[/green]], size: [[yellow]]{ROM.Length}[[/yellow]] bytes.");
            }
            else
            {
                string ErrorMessage = $"TOS file [[red]]{ConfigOptions.RunninConfig.TOSPath}[[/red]] not found.";
                ColoredConsole.WriteLine(ErrorMessage);
                ASEMain.Shutdown();
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
                ColoredConsole.WriteLine($"Error: TOS size [[]]{ROM.Length}[[/yellow]] bytes is unknow.");
                ASEMain.Shutdown();
                return;
            }

            Ports = new byte[(0xffffff - 0xff8000) + 1];

            // $FF8001 MMU memory configuration
            // bits 0-1 bank 0, bits 2-3 bank 1
            // --------------------------------
            // This is not working as expected, memory is not propertly detected on TOS.
            // I have to study how it works in more detail and maybe implement a more complete MMU emulation,
            // but for now I just set the value that corresponds to the selected RAM size and hope for the best.

            switch (Config.ConfigOptions.RunninConfig.RAMConfiguration)
            { 
                case ConfigOptions.RAMConfigurations.RAM_512KB:
                    MMUConfig = 4; // 01 00 -> 512KB
                    RamSize = 512 * 1024;
                    RAM = new byte[RamSize];
                    break;
                case ConfigOptions.RAMConfigurations.RAM_1MB:
                    MMUConfig = 5; // 01 01 -> 1MB
                    RamSize = 1024 * 1024;
                    RAM = new byte[RamSize];
                    break;
                case ConfigOptions.RAMConfigurations.RAM_2MB:
                    MMUConfig = 8; // 10 00 -> 2MB
                    RamSize = 2048 * 1024;
                    RAM = new byte[RamSize];
                    break;
                case ConfigOptions.RAMConfigurations.RAM_4MB:
                    MMUConfig = 10; // 10 10 -> 4MB
                    RamSize = 4096 * 1024;
                    RAM = new byte[RamSize];
                    break;
            }

            // Color, PAL
            Ports[0x260] = 0;
            Ports[0x20a] = 2;
        }

        /// <summary>
        /// Reads a byte from the specified memory address, supporting access to RAM, ROM, and various I/O ports.
        /// </summary>
        /// <remarks>This method handles address mapping for RAM, ROM, and multiple I/O devices, including
        /// special handling for certain hardware registers. For unimplemented or unsupported addresses, the method
        /// returns 0xFF. Some address ranges may trigger internal hardware exceptions captured by Moira.</remarks>
        /// <param name="addr">The 24-bit memory address from which to read a byte. The address determines whether the value is read from
        /// RAM, ROM, or an I/O port. 32 bit addresses will be trimmed to 24 bits addresses.</param>
        /// <returns>The byte value read from the specified address. Returns 0xFF if the address is invalid or not implemented.</returns>
        public byte Read8(uint addr)
        {
            addr &= 0xFFFFFFu;  // 24 bits addressing

            // Vector mirror at 0x000000
            // The ST remaps the first 8 bytes of RAM to the ROM
            if (addr < 0x08)
                return ROM[(int)addr];

            if (addr < RamSize)
                return RAM[(int)addr];

            // ROM
            if (addr >= TosBase && addr < TosBase + TosSize)
                return ROM[(int)(addr - TosBase)];

            // I/O
            if (addr >= PortsBase)
            {
                // STe only registers, not implemented yet
                if ( 
                    (addr >= 0xFF8900 && addr <= 0xFF8924) ||   // DMA sound registers
                    (addr >= 0xFF9200 && addr <= 0xFF9222)      // Extended Joystick/Lightpen Ports
                    )
                {
                    if (ConfigOptions.RunninConfig.DebugMode)
                        ColoredConsole.WriteLine("Trying to read STe not implemented registers.. ignored!");

                    CPU._moira.TriggerBusError(addr, false);
                    return 0xFF;
                }

                // YM2149
                if (addr == STPortAdress.ST_PSGREADSELECT)
                    return ASEMain._ym.PSGRegisterData();
                if (addr == STPortAdress.ST_PSGWRITEDATA)
                    return 0xFF;
                
                // FDC
                if (addr >= 0xFF8604 && addr <= 0xFF860D)
                    return WD1772.ReadByte(addr);

                 // Blitter:
                 // TOS tries to detect the blitter by writing to its registers and expecting a bus error if it is not present.
                if (addr >= 0xFF8A00 && addr <= 0xFF8A3C)
                {
                    if (ConfigOptions.RunninConfig.DebugMode)
                        ColoredConsole.WriteLine($"Trying to read a byte from blitter at [[red]]${addr:X8}[[/red]], but it's not emulated yet.");

                    CPU._moira.TriggerBusError(addr, false);
                    return 0xFF;
                }

                // ACIA - Keyboard and Joystick ports
                if (addr == STPortAdress.ST_ACIACMD)
                    return ACIA.ReadStatus();

                if (addr == STPortAdress.ST_ACIADATA)
                    return ACIA.ReadData();

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
                        case 0x1F: return (byte)ASEMain._mfp.timerACounter;
                        case 0x21: return (byte)ASEMain._mfp.timerBCounter;
                        case 0x23: return (byte)ASEMain._mfp.timerCCounter;
                        case 0x25: return (byte)ASEMain._mfp.timerDCounter;
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
            if (addr + 1 < RamSize)
                return BigEndian.Read16(addr);

            // ROM
            if (addr >= TosBase && addr + 1 < TosBase + TosSize)
                return BigEndian.Read16(addr);

            // I/O
            if (addr >= PortsBase)
            {
                // FDC
                if (addr >= 0xFF8604 && addr <= 0xFF860D)
                    return WD1772.ReadWord(addr);

                // See comment at Read8 about blitter emulation
                if (addr >= 0xFF8A00 && addr <= 0xFF8A3C)
                {
                    if (ConfigOptions.RunninConfig.DebugMode)
                        ColoredConsole.WriteLine($"Trying to read a word from blitter at [[red]]${addr:X8}[[/red]], but it's not emulated yet.");

                    CPU._moira.TriggerBusError(addr, false);
                    return 0xFFFF; // dummy return
                }

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
            if (addr + 3 < RamSize)
                return BigEndian.Read32(addr);

            // ROM
            if (addr >= TosBase && addr + 3 < TosBase + TosSize)
                return BigEndian.Read32(addr);

            // I/O
            if (addr >= PortsBase)
            {
                return BigEndian.Read32(addr);
            }

            return 0xFFFFFFFF;
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

            // RAM
            if (addr < RamSize)
            {
                RAM[addr] = v;
                return;
            }

            if (addr >= TosBase && addr < TosBase + TosSize)
            {
                ColoredConsole.WriteLine($"Warning: Attempt to write to ROM area -> [[red]]{addr:X8}.b[[/red]]");
                return;
            }

            if (addr >= PortsBase)
            {
                // Chip de sonido YM2149
                if (addr == STPortAdress.ST_PSGREADSELECT)
                {
                    ASEMain._ym.PSGRegisterSelect(v);
                    return;
                }
                if (addr == STPortAdress.ST_PSGWRITEDATA)
                {
                    ASEMain._ym.PSGWriteRegister(v);
                    return;
                }

                // FDC
                if (addr >= 0xFF8604 && addr <= 0xFF860D)
                {
                    WD1772.WriteByte(addr, v);
                    Ports[addr - PortsBase] = v;
                    return;
                }

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

                // See comment at Read8 about blitter emulation
                if (addr >= 0xFF8A00 && addr <= 0xFF8A3C)
                {
                    ColoredConsole.WriteLine($"Trying to write a byte to blitter at [[red]]${addr:X8}[[/red]], but it's not emulated yet.");
                    CPU._moira.TriggerBusError(addr, true);
                    return;
                }

                uint offset = addr - MFP68901.MFP_BASE;

                // This is a complete mess.. fixme later
                switch (offset)
                {
                    case 0x03: ASEMain._mfp.AER = v; break;
                    case 0x05: ASEMain._mfp.DDR = v; break;
                    case 0x07: // IERA
                        ASEMain._mfp.IERA = v;
                        ASEMain._mfp.UpdateIRQ();
                        break;

                    case 0x09: // IERB
                        ASEMain._mfp.IERB = v;
                        ASEMain._mfp.UpdateIRQ();
                        break;

                    case 0x0B: // IPRA
                        ASEMain._mfp.IPRA &= (byte)~v; // Escribir 1 limpia el bit
                        ASEMain._mfp.UpdateIRQ();
                        break;

                    case 0x0D: // IPRB
                        ASEMain._mfp.IPRB &= (byte)~v;
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

                            // Si estaba apagado y lo encienden (delay 1..7 o event count 8)
                            bool wasOff = (oldMode == 0);
                            bool isOn = (newMode != 0);
                            if (wasOff && isOn && ASEMain._mfp.timerACounter == 0)
                                ASEMain._mfp.timerACounter = (ASEMain._mfp.TADR == 0 ? 256 : ASEMain._mfp.TADR);

                            break;
                        }

                    case 0x1B:
                        { // TBCR
                            bool wasOff = (ASEMain._mfp.TBCR & 0x07) == 0;
                            ASEMain._mfp.TBCR = v;
                            if (wasOff && (v & 0x07) != 0 && ASEMain._mfp.timerBCounter == 0)
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

                    case 0x1F: // TADR
                        ASEMain._mfp.TADR = v;
                        ASEMain._mfp.timerACounter = (v == 0 ? 256 : v);
                        break;

                    case 0x21: // TBDR
                        ASEMain._mfp.TBDR = v;
                        ASEMain._mfp.timerBCounter = (v == 0 ? 256 : v);
                        break;

                    case 0x23: // TCDR
                        ASEMain._mfp.TCDR = v;
                        ASEMain._mfp.timerCCounter = (v == 0 ? 256 : v);
                        break;

                    case 0x25: // TDDR
                        ASEMain._mfp.TDDR = v;
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

            if (addr + 1 < RamSize)
            {
                BigEndian.Write16(addr, v);
                return;
            }

            if (addr >= TosBase && addr + 1 < TosBase + TosSize)
            {
                ColoredConsole.WriteLine($"Warning: Attempt to write to ROM area -> [[yellow]]${addr:X8}.w[[/yellow]]");
                return;
            }

            if (addr >= PortsBase)
            {
                // FDC
                if (addr >= 0xFF8604 && addr <= 0xFF860D)
                {
                    WD1772.WriteWord(addr, v);
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

            if (addr + 3 < RamSize)
            {
                BigEndian.Write32(addr, v);
                return;
            }

            if (addr >= TosBase && addr + 3 < TosBase + TosSize)
            {
                ColoredConsole.WriteLine($"Warning: Attempt to write to ROM area -> [[yellow]]${addr:X8}.l[[/yellow]]");
                return;
            }

            if (addr >= PortsBase)
            {
                BigEndian.Write32(addr, v);
                return;
            }
        }
    }
}
