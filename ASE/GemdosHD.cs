/*
 *
 * GEMDOS hard disk emulation: a host folder mounted as an ST hard drive.
 *
 * Rather than emulating a disk, this intercepts GEMDOS (trap #1) at file-system
 * level and serves the calls from the host directory, the way Hatari's GEMDOS HD
 * does (gemdos.c / cart_asm.s, the reference for all the semantics here):
 *
 *  - A synthetic application cartridge lives at $FA0000 (served by Memory.cs from
 *    CartRom). TOS scans the cartridge port on every boot and calls its C-INIT
 *    entry after GEMDOS is initialized but before the disk boot; that entry hooks
 *    the trap #1 vector ($84) to the cartridge's own handler and keeps TOS's
 *    original vector to chain to.
 *  - Where Hatari patches its CPU core with pseudo-opcodes, ASE plants Moira
 *    breakpoints on the cartridge code's hook points (a NOP each). When one is
 *    hit, ASEMain.RunCpuUntil hands the PC to TryHandleBreakpoint instead of
 *    opening the debugger: the call is served in C#, the 68000's condition codes
 *    are set to steer the cartridge code (Z = handled -> RTE, Z clear -> chain to
 *    TOS, V = run the Pexec sequence), and the PC is moved past the hook word —
 *    which also guarantees a pending interrupt can't bounce execution back onto
 *    the breakpoint and run a call twice.
 *  - Pexec modes 0/3 are the elaborate case: the handler pushes a Pexec-5/7
 *    "create basepage" parameter block and lets the cartridge code re-enter TOS
 *    with it; a second hook after that call loads and relocates the PRG from the
 *    host folder into the new basepage, then either returns it (mode 3) or
 *    rewrites the caller's parameters into a Pexec-4/6 "just go" and chains to
 *    TOS again (mode 0).
 *
 * The drive takes the first letter after the ACSI image's partitions (both can
 * be enabled at once); it is never bootable — only the ACSI image is.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using System.Text;
using static ASE.Config;

namespace ASE
{
    public static class GemdosHD
    {
        // ==================== Cartridge layout ====================

        const uint CART_BASE = Memory.CartBase;
        const uint CART_OLDGEMDOS = CART_BASE + 0x24;   // TOS's trap #1 vector, patched at boot
        const uint NEW_GEMDOS = CART_BASE + 0x2A;       // the vector $84 points here
        const uint HOOK_TRAP = CART_BASE + 0x2A;        // nop: every trap #1 lands here first
        const uint HOOK_PEXEC = CART_BASE + 0x42;       // nop: after TOS created the basepage

        // C-INIT entry. TOS jumps here with the flag bits still in the address (see
        // CART_INIT_ENTRY), so the first thing it does is an absolute jump to HOOK_SYSINIT,
        // which lands the PC on a clean 24-bit address.
        const uint CART_INIT_ENTRY = CART_BASE + 0x58;
        const uint HOOK_SYSINIT = CART_BASE + 0x5E;     // nop: cartridge C-INIT, every boot

        /// <summary>The cartridge ROM served by Memory at $FA0000, or null when the GEMDOS
        /// drive is disabled (the port then floats at 0xFF and TOS sees no cartridge).</summary>
        public static byte[] CartRom { get; private set; }

        public static bool Enabled { get; private set; }

        // 68000 condition codes used to steer the cartridge code
        const ushort SR_ZERO = 0x0004;
        const ushort SR_OVERFLOW = 0x0002;
        const ushort SR_SUPER = 0x2000;

        // ==================== GEMDOS constants ====================

        const int GEMDOS_EOK = 0;
        const int GEMDOS_ERROR = -1;
        const int GEMDOS_E_SEEK = -6;
        const int GEMDOS_EWRPRO = -13;
        const int GEMDOS_EFILNF = -33;
        const int GEMDOS_EPTHNF = -34;
        const int GEMDOS_ENHNDL = -35;
        const int GEMDOS_EACCDN = -36;
        const int GEMDOS_ENSMEM = -39;
        const int GEMDOS_EDRIVE = -46;
        const int GEMDOS_ENMFIL = -49;
        const int GEMDOS_ERANGE = -64;
        const int GEMDOS_EINTRN = -65;
        const int GEMDOS_EPLFMT = -66;

        // File attributes
        const int FA_READONLY = 0x01;
        const int FA_HIDDEN = 0x02;
        const int FA_VOLUME = 0x08;
        const int FA_DIR = 0x10;
        const int FA_ARCHIVE = 0x20;
        // Attribute filtering ignores archive & read-only, per the Profibuch
        const int FA_IGNORED = FA_ARCHIVE | FA_READONLY;

        // Our file handles must not collide with TOS's (0-5 std, 6+ TOS internal), and stay < 256
        const int BASE_FILEHANDLE = 64;
        const int MAX_FILE_HANDLES = 64;

        const int BASEPAGE_OFFSET_DTA = 0x20;
        const int BASEPAGE_OFFSET_PARENT = 0x24;
        const uint DTA_MAGIC = 0x12983476;

        // ==================== State ====================

        sealed class FileHandleEntry
        {
            public bool Used;
            public bool ReadOnly;
            public uint Basepage;
            public FileStream Fs;
            public string HostPath;
        }

        sealed class InternalDta
        {
            public bool Used;
            public uint Addr;              // guest DTA address, to detect reuse
            public string HostDir;         // host directory being enumerated
            public List<string> Entries;   // host names matching the mask
            public int Current;
            public int Attrib;
        }

        static readonly FileHandleEntry[] _handles = new FileHandleEntry[MAX_FILE_HANDLES];
        static readonly int[] _forcedHandle = new int[5];       // std handle -> our index, -1 = unforced
        static readonly uint[] _forcedBasepage = new uint[5];
        static readonly InternalDta[] _dtas = new InternalDta[256];
        static int _dtaIndex;

        static string _hostRoot = "";       // host folder, no trailing separator
        static string _currentHostDir = ""; // current directory, WITH trailing separator
        static int _driveNumber = 2;        // 2 = C:
        static int _currentDrive;           // GEMDOS current drive (Dsetdrv tracking), 0 = A:
        static int _tosVersion;
        static uint _actPd;                 // pointer to TOS's "current basepage" variable
        static uint _oldGemdos;             // TOS's own trap #1 handler, chained to
        static bool _booted;                // the cartridge C-INIT ran this boot
        static uint _savedPexecParams;      // caller's Pexec parameter block, between the two stages

        public static char DriveLetter => (char)('A' + _driveNumber);

        // ==================== Power-on ====================

        /// <summary>
        /// Called once per power-on from ASEMain.TurnOn, after the CPU/memory exist. Builds
        /// the cartridge, plants the hook breakpoints and resets all host-side state. With the
        /// option off (or a bad folder) the cartridge is absent and nothing ever triggers.
        /// </summary>
        public static void Initialize()
        {
            CloseAllHandles();
            ClearAllDtas();

            Enabled = false;
            CartRom = null;
            _booted = false;
            _oldGemdos = 0;
            _actPd = 0;
            _currentDrive = 0;
            _savedPexecParams = 0;

            if (!ConfigOptions.RunninConfig.GemdosDriveEnabled)
                return;

            string dir = ConfigOptions.RunninConfig.GemdosDrivePath ?? "";
            if (!Directory.Exists(dir))
            {
                ColoredConsole.WriteLine($"GEMDOS drive: folder [[red]]{dir}[[/red]] not found, drive disabled.");
                return;
            }

            _hostRoot = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            _currentHostDir = _hostRoot + Path.DirectorySeparatorChar;

            // The drive letter comes after the ACSI image's partitions, so both can coexist
            // the way they would on a real chain (driver-mapped partitions first).
            _driveNumber = 2 + (Acsi.EmulationOn ? Acsi.PartitionCount : 0);
            if (_driveNumber > 15)
            {
                ColoredConsole.WriteLine($"GEMDOS drive: no drive letter left after {Acsi.PartitionCount} ACSI partitions, using P:.");
                _driveNumber = 15;
            }

            _tosVersion = (ASEMain._mem.ROM[2] << 8) | ASEMain._mem.ROM[3];

            BuildCartridge();

            CPU._moira.SetBreakpoint(HOOK_TRAP);
            CPU._moira.SetBreakpoint(HOOK_PEXEC);
            CPU._moira.SetBreakpoint(HOOK_SYSINIT);

            Enabled = true;

            ColoredConsole.WriteLine($"GEMDOS drive [[green]]{DriveLetter}:[[/green]] <-> [[green]]{_hostRoot}[[/green]]",
                ConfigOptions.DebugModes.Quiet);
        }

        /// <summary>
        /// The synthetic application cartridge. TOS finds the $ABCDEF42 magic when it scans
        /// the port and calls C-INIT ($08000000 flag: before disk boot, after GEMDOS init) on
        /// every boot — which is what makes the hook survive warm reboots. The 68000 code is
        /// Hatari's cart_asm.s with the pseudo-opcodes replaced by breakpointed NOPs.
        /// </summary>
        static void BuildCartridge()
        {
            var c = new byte[0x68];
            int p;

            void W16(uint addr, ushort v) { p = (int)(addr - CART_BASE); c[p] = (byte)(v >> 8); c[p + 1] = (byte)v; }
            void W32(uint addr, uint v) { p = (int)(addr - CART_BASE); c[p] = (byte)(v >> 24); c[p + 1] = (byte)(v >> 16); c[p + 2] = (byte)(v >> 8); c[p + 3] = (byte)v; }

            uint cartRun = CART_BASE + 0x62;

            // ---- Header ----
            W32(CART_BASE + 0x00, 0xABCDEF42);              // C-FLAG: application cartridge
            W32(CART_BASE + 0x04, 0);                       // C-NEXT
            W32(CART_BASE + 0x08, 0x08000000 | CART_INIT_ENTRY); // C-INIT: before disk boot, after GEMDOS init
            W32(CART_BASE + 0x0C, cartRun);                 // C-RUN
            W16(CART_BASE + 0x10, 0x5800);                  // C-TIME
            W16(CART_BASE + 0x12, 0x3229);                  // C-DATE
            W32(CART_BASE + 0x14, 6);                       // C-BSIZ
            var name = Encoding.ASCII.GetBytes("ASE.TOS");  // C-NAME, 12 bytes zero-padded
            Array.Copy(name, 0, c, 0x18, name.Length);

            W32(CART_OLDGEMDOS, 0);                         // old trap #1 vector, patched at boot
            W16(CART_BASE + 0x28, 0);                       // (spare, keeps new_gemdos at $FA002A)

            // ---- new_gemdos: every trap #1 lands here ----
            W16(HOOK_TRAP, 0x4E71);                         // nop            <- breakpoint (C# sets CCR)
            W16(CART_BASE + 0x2C, 0x690A);                  // bvs.s pexec    (V: run the Pexec sequence)
            W16(CART_BASE + 0x2E, 0x6602);                  // bne.s go_old   (Z clear: chain to TOS)
            W16(CART_BASE + 0x30, 0x4E73);                  // do_rte: rte    (Z: handled, D0 holds the result)
            W16(CART_BASE + 0x32, 0x2F3A);                  // go_old: move.l old_gemdos(pc),-(sp)
            W16(CART_BASE + 0x34, 0xFFF0);                  //   (pc-relative displacement to CART_OLDGEMDOS)
            W16(CART_BASE + 0x36, 0x4E75);                  // rts            (jumps through the old vector)

            // ---- pexec: C# already pushed the Pexec-5/7 parameters onto the stack ----
            W16(CART_BASE + 0x38, 0x4E41);                  // trap #1        (TOS creates the basepage)
            W16(CART_BASE + 0x3A, 0x4FEF);                  // lea 16(sp),sp
            W16(CART_BASE + 0x3C, 0x0010);
            W16(CART_BASE + 0x3E, 0x4A80);                  // tst.l d0
            W16(CART_BASE + 0x40, 0x6BEE);                  // bmi.s do_rte   (TOS error goes back to the caller)
            W16(HOOK_PEXEC, 0x4E71);                        // nop            <- breakpoint (load & relocate)
            W16(CART_BASE + 0x44, 0x69EC);                  // bvs.s go_old   (mode 0: re-enter TOS as Pexec "just go")
            W16(CART_BASE + 0x46, 0x67E8);                  // beq.s do_rte   (mode 3: return the basepage)
            W16(CART_BASE + 0x48, 0x2F00);                  // move.l d0,-(sp)   (load failed: free the basepage)
            W16(CART_BASE + 0x4A, 0x2F08);                  // move.l a0,-(sp)
            W16(CART_BASE + 0x4C, 0x3F3C);                  // move.w #73,-(sp)  (Mfree)
            W16(CART_BASE + 0x4E, 0x0049);
            W16(CART_BASE + 0x50, 0x4E41);                  // trap #1
            W16(CART_BASE + 0x52, 0x5C8F);                  // addq.l #6,sp
            W16(CART_BASE + 0x54, 0x201F);                  // move.l (sp)+,d0
            W16(CART_BASE + 0x56, 0x4E73);                  // rte

            // ---- sys_init: TOS calls this on every boot (cartridge C-INIT) ----
            // TOS loads C-INIT into a register and jumps to it *including the flag byte*
            // ("movea.l $4(a0),a0; jsr (a0)"), so it arrives here with PC = $08FA0058: on real
            // hardware the 24-bit bus drops the top byte, but the emulated PC keeps it and a
            // breakpoint on the plain address would never match. This absolute jump is what
            // normalises the PC before the hook.
            W16(CART_INIT_ENTRY, 0x4EF9);                   // jmp HOOK_SYSINIT.l
            W32(CART_INIT_ENTRY + 2, HOOK_SYSINIT);
            W16(HOOK_SYSINIT, 0x4E71);                      // nop            <- breakpoint (hook $84, set drvbits)
            W16(CART_BASE + 0x60, 0x4E75);                  // rts

            // ---- C-RUN: if the user ever launches the cartridge "program", just exit ----
            W16(cartRun + 0x00, 0x4267);                    // clr.w -(sp)
            W16(cartRun + 0x02, 0x4E41);                    // trap #1 (Pterm0)
            W16(cartRun + 0x04, 0x60FE);                    // bra.s * (never reached)

            CartRom = c;
        }

        // ==================== Hook dispatch ====================

        /// <summary>
        /// True for the addresses whose breakpoints belong to the hard drive. The Debug window
        /// filters them out of everything the user can see or clear: they are wiring, not
        /// breakpoints, and removing one would leave the cartridge running its NOP with
        /// undefined flags — the branch after it would then jump anywhere.
        /// </summary>
        public static bool IsHookAddress(uint addr)
            => Enabled && (addr == HOOK_TRAP || addr == HOOK_PEXEC || addr == HOOK_SYSINIT);

        /// <summary>How many of Moira's breakpoints are ours (0 when the drive is off).</summary>
        public static int HookBreakpointCount => Enabled ? 3 : 0;

        /// <summary>Re-plants the hooks after something cleared every breakpoint.</summary>
        public static void RearmHooks()
        {
            if (!Enabled)
                return;

            CPU._moira.SetBreakpoint(HOOK_TRAP);
            CPU._moira.SetBreakpoint(HOOK_PEXEC);
            CPU._moira.SetBreakpoint(HOOK_SYSINIT);
        }

        /// <summary>
        /// Called by ASEMain.RunCpuUntil when a Moira breakpoint stops the CPU. Returns true
        /// when the PC is one of the GEMDOS hooks (handled here, machine keeps running);
        /// false means a real user breakpoint that must open the debugger. After handling,
        /// the PC is moved past the hook word so a pending interrupt taken on resume cannot
        /// return onto the breakpoint and run the call twice.
        /// </summary>
        public static bool TryHandleBreakpoint(uint pc)
        {
            if (!Enabled)
                return false;

            switch (pc)
            {
                case HOOK_TRAP:
                    Guarded(OnGemdosTrap);
                    SkipHookWord(HOOK_TRAP);
                    return true;

                case HOOK_PEXEC:
                    Guarded(OnPexecBasepageCreated);
                    SkipHookWord(HOOK_PEXEC);
                    return true;

                case HOOK_SYSINIT:
                    Guarded(OnSysInit);
                    SkipHookWord(HOOK_SYSINIT);
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>A fault in the host-side code must never kill the emulation thread: the
        /// call falls through to TOS (CCR: Z clear, V clear) and the error goes to the console.</summary>
        static void Guarded(Action handler)
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                ColoredConsole.WriteLine($"GEMDOS drive: internal error, call passed to TOS: {ex.Message}");
                var m = CPU._moira;
                m.SR = (ushort)(m.SR & ~(SR_ZERO | SR_OVERFLOW));
            }
        }

        /// <summary>Moves the CPU to the instruction after a hook's NOP (same idiom as the
        /// debugger's JumpTo: PC/PC0 plus a prefetch refill).</summary>
        static void SkipHookWord(uint hook)
        {
            var m = CPU._moira;
            uint next = hook + 2;
            m.PC = next;
            m.PC0 = next;
            m.IRD = ASEMain._mem.Read16(next);
            m.IRC = ASEMain._mem.Read16(next + 2);
        }

        // ==================== Boot (cartridge C-INIT) ====================

        /// <summary>
        /// Runs at every TOS boot, after GEMDOS init and before the disk boot: publishes the
        /// drive in _drvbits, locates the "current basepage" OS variable and hooks trap #1.
        /// </summary>
        static void OnSysInit()
        {
            if (_booted)
            {
                // Warm reboot: whatever was open belongs to a world that no longer exists
                CloseAllHandles();
                ClearAllDtas();
                _currentHostDir = _hostRoot + Path.DirectorySeparatorChar;
                _currentDrive = 0;
            }
            _booted = true;

            // Desktop and Dsetdrv learn about the drive from the connected-drives mask
            W32g(0x4C2, R32g(0x4C2) | (1u << _driveNumber));

            // The variable holding the current basepage pointer. TOS 1.00 has no documented
            // hook, so fixed addresses are used (the Spanish ROM differs); later TOSes expose
            // it through the OS header (sysbase + 0x28).
            if (_tosVersion == 0x100)
            {
                int country = ((ASEMain._mem.ROM[28] << 8) | ASEMain._mem.ROM[29]) >> 1;
                _actPd = country == 4 ? 0x873Cu : 0x602Cu;
            }
            else
            {
                uint osAddress = R32g(0x4F2);
                _actPd = R32g(osAddress + 0x28);
            }

            // Chain: keep TOS's trap #1 handler in the cartridge and point $84 at ours
            _oldGemdos = R32g(0x84);
            PatchOldGemdos(_oldGemdos);
            W32g(0x84, NEW_GEMDOS);

            // Reported per power-on, not just traced: this is the line that says TOS actually
            // ran the cartridge and the drive is live, as opposed to merely configured.
            ColoredConsole.WriteLine($"GEMDOS drive [[green]]{DriveLetter}:[[/green]] hooked into TOS " +
                $"(act_pd=${_actPd:X6}, TOS trap #1 at ${_oldGemdos:X6}).", ConfigOptions.DebugModes.Quiet);
        }

        static void PatchOldGemdos(uint vector)
        {
            int o = (int)(CART_OLDGEMDOS - CART_BASE);
            CartRom[o] = (byte)(vector >> 24);
            CartRom[o + 1] = (byte)(vector >> 16);
            CartRom[o + 2] = (byte)(vector >> 8);
            CartRom[o + 3] = (byte)vector;
        }

        // ==================== The trap #1 dispatcher ====================

        static void OnGemdosTrap()
        {
            var m = CPU._moira;
            ushort sr = m.SR;

            if (_oldGemdos == 0)
            {
                // Should be unreachable ($84 only points here after OnSysInit): fail the call
                // rather than chain into address 0.
                ColoredConsole.WriteLine("GEMDOS drive: trap with no chained TOS vector, call rejected.");
                SetD0(-32);     // EINVFN
                m.SR = (ushort)((sr | SR_ZERO) & ~SR_OVERFLOW);
                return;
            }

            // Without the "current basepage" variable we cannot tell whose file handles are
            // whose, so nothing may be intercepted: every call goes to TOS untouched.
            if (_actPd == 0)
            {
                m.SR = (ushort)(sr & ~(SR_ZERO | SR_OVERFLOW));
                return;
            }

            // The exception frame tells whether the caller ran in user or supervisor mode,
            // which decides which stack carries the call parameters (68000: 6-byte frame).
            uint ssp = GetA(7);
            ushort callerSr = R16g(ssp);
            uint parms = (callerSr & SR_SUPER) != 0 ? ssp + 6 : GetUsp();

            ushort call = R16g(parms);
            parms += 2;

            sr &= unchecked((ushort)~SR_OVERFLOW);

            int finished = Dispatch(call, parms);

            if (finished == -1)     // Pexec: run the cartridge's create-basepage sequence
            {
                sr |= SR_OVERFLOW;
                finished = 1;
            }

            if (finished != 0)
            {
                sr |= SR_ZERO;

                // Light the hard disk LED for the calls that actually touched the drive —
                // not for the bookkeeping ones (Pterm, Dsetdrv, Fforce), which never reach
                // the host file system.
                if (TouchesTheDrive(call))
                    ASEMain.SignalHardDiskActivity();
            }
            else
            {
                sr &= unchecked((ushort)~SR_ZERO);
            }

            m.SR = sr;
        }

        /// <summary>
        /// Whether a served call actually reached the host file system, and so should blink the
        /// activity light. Same set Hatari lights its HD LED for: directory and file operations,
        /// but not process/handle bookkeeping.
        /// </summary>
        static bool TouchesTheDrive(ushort call) => call switch
        {
            0x36 or 0x39 or 0x3A or 0x3B or 0x3C or 0x3D or 0x3E or 0x3F or
            0x40 or 0x41 or 0x42 or 0x43 or 0x47 or 0x4B or 0x4E or 0x4F or 0x56 or 0x57 => true,
            _ => false,
        };

        /// <summary>Returns 1 = handled (D0 set), 0 = pass to TOS, -1 = Pexec sequence.</summary>
        static int Dispatch(ushort call, uint parms)
        {
            switch (call)
            {
                case 0x00: return Pterm0();
                case 0x0E: return SetDrv(parms);
                case 0x31: return Ptermres();
                case 0x36: return DFree(parms);
                case 0x39: return DCreate(parms);
                case 0x3A: return DDelete(parms);
                case 0x3B: return DSetPath(parms);
                case 0x3C: return FCreate(parms);
                case 0x3D: return FOpen(parms);
                case 0x3E: return FClose(parms);
                case 0x3F: return FRead(parms);
                case 0x40: return FWrite(parms);
                case 0x41: return FDelete(parms);
                case 0x42: return FSeek(parms);
                case 0x43: return FAttrib(parms);
                case 0x46: return FForce(parms);
                case 0x47: return DGetPath(parms);
                case 0x4B: return Pexec(parms);
                case 0x4C: return Pterm();
                case 0x4E: return FsFirst(parms);
                case 0x4F: return FsNext(true);
                case 0x56: return FRename(parms);
                case 0x57: return FDatime(parms);
                default: return 0;
            }
        }

        // ==================== Guest memory / CPU helpers ====================

        static byte R8g(uint a) => ASEMain._mem.Read8(a);
        static ushort R16g(uint a) => ASEMain._mem.Read16(a);
        static uint R32g(uint a) => ASEMain._mem.Read32(a);
        static void W8g(uint a, byte v) => ASEMain._mem.Write8(a, v);
        static void W16g(uint a, ushort v) => ASEMain._mem.Write16(a, v);
        static void W32g(uint a, uint v) => ASEMain._mem.Write32(a, v);

        // Moira exposes the register banks as a struct returned by value, so an indexer
        // assignment has to go through a local (the struct only holds the Moira reference,
        // which is what the setter writes through).
        static uint GetD(int n) { var d = CPU._moira.D; return d[n]; }
        static void SetD(int n, uint v) { var d = CPU._moira.D; d[n] = v; }
        static uint GetA(int n) { var a = CPU._moira.A; return a[n]; }
        static void SetA(int n, uint v) { var a = CPU._moira.A; a[n] = v; }

        static void SetD0(int v) => SetD(0, unchecked((uint)v));

        /// <summary>Reads the user stack pointer while in supervisor mode: Moira only exposes
        /// the active A7, so the SR's S bit is toggled briefly (same idiom as Snapshot).</summary>
        static uint GetUsp()
        {
            var m = CPU._moira;
            ushort sr = m.SR;
            m.SR = (ushort)(sr & ~SR_SUPER);
            uint usp = m.SP;
            m.SR = sr;
            return usp;
        }

        /// <summary>NUL-terminated guest string (RAM, TOS ROM or cartridge), or null when the
        /// address is invalid or unterminated within a sane bound.</summary>
        static string ReadStringG(uint addr)
        {
            if (addr == 0)
                return null;

            var sb = new StringBuilder(64);
            for (int i = 0; i < 4096; i++)
            {
                byte b = R8g(addr + (uint)i);
                if (b == 0)
                    return sb.ToString();
                sb.Append((char)b);     // Latin-ish byte-to-char, good enough for file names
            }
            return null;
        }

        static uint CurrentBasepage() => R32g(_actPd);

        static void Trace(string msg)
        {
            if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Full)
                ColoredConsole.WriteLine($"[[green]]GEMDOS[[/green]] {msg}");
        }

        // ==================== Drive / path resolution ====================

        /// <summary>Drive number in a GEMDOS filename ("C:\..." -> 2), the current drive when
        /// none is given, 0 for special devices ("CON:", "AUX:", "PRN:").</summary>
        static int FindDriveNumber(string name)
        {
            if (name.Length >= 2 && name[1] == ':')
            {
                char letter = char.ToUpperInvariant(name[0]);
                if (letter >= 'A' && letter <= 'Z')
                    return letter - 'A';
            }
            else if (name.Length == 4 && name[3] == ':')
            {
                return 0;
            }
            return _currentDrive;
        }

        /// <summary>True when the filename addresses our emulated drive.</summary>
        static bool IsOurs(string name) => name != null && FindDriveNumber(name) == _driveNumber;

        static bool IsOurDrive(int drive) => drive == _driveNumber;

        /// <summary>
        /// Maps a GEMDOS path onto the host file system. Components are matched one at a time,
        /// case-insensitively, against what really exists (host names win, so "README.TXT"
        /// finds "readme.txt" on a case-sensitive host); a component with no match is appended
        /// as-is, which is normal for files about to be created. ".." can never climb above
        /// the mounted folder. The final component keeps its wildcards when
        /// <paramref name="keepWildcards"/> (Fsfirst masks).
        /// </summary>
        static string ToHostPath(string gemdosPath, bool keepWildcards = false)
        {
            char sep = Path.DirectorySeparatorChar;
            string rest = gemdosPath;
            string path;

            if (rest.Length >= 2 && rest[1] == ':')
            {
                path = _hostRoot;
                rest = rest.Substring(2);
            }
            else if (rest.StartsWith("\\"))
            {
                path = _hostRoot;
            }
            else
            {
                path = _currentHostDir.TrimEnd(sep);
            }

            var parts = rest.Split('\\', StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < parts.Length; i++)
            {
                string comp = parts[i];
                bool last = i == parts.Length - 1;

                if (comp == ".")
                    continue;

                if (comp == "..")
                {
                    if (path.Length > _hostRoot.Length)
                    {
                        int cut = path.LastIndexOf(sep);
                        if (cut >= _hostRoot.Length)
                            path = path.Substring(0, cut);
                    }
                    continue;
                }

                if (last && keepWildcards && (comp.Contains('*') || comp.Contains('?')))
                {
                    path = path + sep + comp;
                    break;
                }

                path = path + sep + MatchHostEntry(path, comp, isDir: !last);
            }

            return path;
        }

        /// <summary>The real host name for one TOS path component (clipped to 8.3 like TOS
        /// clips it), or the component itself when nothing matches.</summary>
        static string MatchHostEntry(string dir, string component, bool isDir)
        {
            string name = ClipTo83(component);

            string found = FindEntryIgnoreCase(dir, name);
            if (found != null)
                return found;

            // TOS 1.02's file selector appends a '.' to 8-character folder names
            if (isDir && name.Length == 9 && name.EndsWith("."))
            {
                found = FindEntryIgnoreCase(dir, name.Substring(0, 8));
                if (found != null)
                    return found;
            }

            return name;
        }

        static string FindEntryIgnoreCase(string dir, string name)
        {
            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(dir))
                {
                    string entryName = Path.GetFileName(entry);
                    if (string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase))
                        return entryName;
                }
            }
            catch
            {
                // unreadable directory: treated as no match
            }
            return null;
        }

        /// <summary>Clips a name to 8+3 the way TOS does before it ever reaches the drive.</summary>
        static string ClipTo83(string name)
        {
            int dot = name.IndexOf('.');
            if (dot >= 0)
            {
                string stem = name.Substring(0, Math.Min(dot, 8));
                string ext = name.Substring(dot);
                if (ext.Length > 4)
                    ext = ext.Substring(0, 4);
                return stem + ext;
            }
            return name.Length > 8 ? name.Substring(0, 8) : name;
        }

        /// <summary>Host file/dir name -> TOS 8.3 uppercase name for a DTA.</summary>
        static string HostNameToAtari(string name)
        {
            if (name == "." || name == "..")
                return name;

            string stem, ext;
            int dot = name.LastIndexOf('.');
            if (dot > 0)
            {
                stem = name.Substring(0, dot);
                ext = name.Substring(dot + 1);
            }
            else
            {
                stem = name;
                ext = "";
            }

            var sb = new StringBuilder(12);
            foreach (char ch in stem.ToUpperInvariant())
            {
                if (sb.Length >= 8) break;
                sb.Append(ch is >= (char)33 and < (char)127 && ch != '\\' && ch != '?' && ch != '*' && ch != '.' ? ch : '_');
            }
            if (ext.Length > 0)
            {
                sb.Append('.');
                int start = sb.Length;
                foreach (char ch in ext.ToUpperInvariant())
                {
                    if (sb.Length - start >= 3) break;
                    sb.Append(ch is >= (char)33 and < (char)127 && ch != '\\' && ch != '?' && ch != '*' && ch != '.' ? ch : '_');
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// TOS wildcard match ('*' stops at the last dot, '?' one char), ported from Hatari's
        /// fsfirst_match. Root listings hide every dot-name; subdirectories show only "." and
        /// "..".
        /// </summary>
        static bool FsFirstMatch(string pat, string name, bool subdir)
        {
            if (name.StartsWith("."))
            {
                if (!subdir)
                    return false;
                if (name != "." && name != "..")
                    return false;
            }

            int dot = name.LastIndexOf('.');
            if (dot >= 0 && pat == "*")
                return false;               // plain '*' never matches a name with an extension

            int p = 0, n = 0;
            while (n < name.Length)
            {
                if (p < pat.Length && pat[p] == '*')
                {
                    while (n < name.Length && n != dot)
                        n++;
                    p++;
                }
                else if (p < pat.Length && pat[p] == '?')
                {
                    n++;
                    p++;
                }
                else if (p < pat.Length && char.ToUpperInvariant(pat[p]) == char.ToUpperInvariant(name[n]))
                {
                    p++;
                    n++;
                }
                else
                {
                    return false;
                }
            }

            // name consumed; the pattern may still hold matched-by-emptiness parts
            while (p < pat.Length && pat[p] == '*')
                p++;
            if (p + 1 < pat.Length && pat[p] == '.' && pat[p + 1] == '*')
                p += 2;
            while (p < pat.Length && pat[p] == '*')
                p++;

            return p == pat.Length;
        }

        static int HostAttributes(string hostPath)
        {
            var attr = File.GetAttributes(hostPath);
            int a = 0;
            if ((attr & FileAttributes.Directory) != 0) a |= FA_DIR;
            if ((attr & FileAttributes.ReadOnly) != 0) a |= FA_READONLY;
            if ((attr & FileAttributes.Hidden) != 0) a |= FA_HIDDEN;
            return a;
        }

        static void DateTimeToTos(DateTime t, out ushort timeword, out ushort dateword)
        {
            int year = Math.Max(t.Year - 1980, 0);
            timeword = (ushort)((t.Second >> 1) | (t.Minute << 5) | (t.Hour << 11));
            dateword = (ushort)(t.Day | (t.Month << 5) | (year << 9));
        }

        // ==================== File handles ====================

        static void CloseAllHandles()
        {
            for (int i = 0; i < _handles.Length; i++)
            {
                try { _handles[i]?.Fs?.Dispose(); } catch { }
                _handles[i] = null;
            }
            for (int i = 0; i < _forcedHandle.Length; i++)
            {
                _forcedHandle[i] = -1;
                _forcedBasepage[i] = 0;
            }
        }

        static int FindFreeHandle()
        {
            for (int i = 0; i < _handles.Length; i++)
                if (_handles[i] == null || !_handles[i].Used)
                    return i;
            return -1;
        }

        static bool BasepageMatches(uint checkBase)
        {
            int maxParents = 12;
            uint basepage = CurrentBasepage();
            while (maxParents-- > 0 && ASEMain._mem.IsRamArea(basepage, 0x100) && basepage != 0)
            {
                if (basepage == checkBase)
                    return true;
                basepage = R32g(basepage + BASEPAGE_OFFSET_PARENT);
            }
            return false;
        }

        /// <summary>Internal index for a TOS handle (direct or Fforce-aliased), -1 when it is
        /// not one of ours (TOS's own handles pass through).</summary>
        static int GetValidHandle(int handle)
        {
            bool forced = false;

            if (handle >= 0 && handle < _forcedHandle.Length && _forcedHandle[handle] != -1)
            {
                if (BasepageMatches(_forcedBasepage[handle]))
                {
                    forced = true;
                    handle = _forcedHandle[handle];
                }
                else
                {
                    // stale redirection from a program that already terminated
                    _forcedHandle[handle] = -1;
                    _forcedBasepage[handle] = 0;
                    return -1;
                }
            }
            else
            {
                handle -= BASE_FILEHANDLE;
            }

            if (handle >= 0 && handle < _handles.Length && _handles[handle] != null && _handles[handle].Used)
            {
                if (!forced && _handles[handle].Basepage != CurrentBasepage())
                    Trace($"handle {handle} used from another program's basepage");
                return handle;
            }

            return -1;
        }

        // ==================== The GEMDOS calls ====================

        static int Pterm0()
        {
            TerminateClose();
            return 0;
        }

        static int Pterm()
        {
            TerminateClose();
            return 0;
        }

        static int Ptermres()
        {
            TerminateClose();
            return 0;
        }

        /// <summary>Implicit close/unforce of everything the dying program left open.</summary>
        static void TerminateClose()
        {
            uint current = CurrentBasepage();

            for (int i = 0; i < _handles.Length; i++)
            {
                if (_handles[i] != null && _handles[i].Used && _handles[i].Basepage == current)
                {
                    try { _handles[i].Fs?.Dispose(); } catch { }
                    _handles[i] = null;
                }
            }
            for (int i = 0; i < _forcedHandle.Length; i++)
            {
                if (_forcedBasepage[i] == current)
                {
                    _forcedHandle[i] = -1;
                    _forcedBasepage[i] = 0;
                }
            }
        }

        static int SetDrv(uint parms)
        {
            // Only tracked; TOS keeps the call (the drive is in _drvbits, so it accepts it)
            _currentDrive = R16g(parms);
            return 0;
        }

        static int DFree(uint parms)
        {
            uint address = R32g(parms);
            int drive = R16g(parms + 4);

            drive = drive == 0 ? _currentDrive : drive - 1;
            if (!IsOurDrive(drive))
                return 0;

            if (!ASEMain._mem.IsRamArea(address, 16))
            {
                SetD0(GEMDOS_ERANGE);
                return 1;
            }

            ulong totalKb, freeKb;
            try
            {
                var di = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_hostRoot)));
                totalKb = (ulong)(di.TotalSize / 1024);
                freeKb = (ulong)(di.AvailableFreeSpace / 1024);
            }
            catch
            {
                totalKb = 32 * 1024;    // fake 32MB drive, 16MB free
                freeKb = 16 * 1024;
            }

            // Cap to what each TOS generation can represent (1KB clusters)
            ulong tosMax = _tosVersion >= 0x400 ? 1024u * 1024 : _tosVersion >= 0x106 ? 512u * 1024 : 256u * 1024;
            if (totalKb > tosMax) totalKb = tosMax;
            if (totalKb == 0) totalKb = tosMax;
            if (freeKb > totalKb) freeKb = totalKb;

            W32g(address, (uint)freeKb);        // free clusters
            W32g(address + 4, (uint)totalKb);   // total clusters
            W32g(address + 8, 512);             // bytes per sector
            W32g(address + 12, 2);              // sectors per cluster (1KB clusters)

            SetD0(GEMDOS_EOK);
            return 1;
        }

        static int DCreate(uint parms)
        {
            string name = ReadStringG(R32g(parms));
            if (string.IsNullOrEmpty(name) || !IsOurs(name))
                return 0;

            Trace($"Dcreate(\"{name}\")");

            try
            {
                string host = ToHostPath(name);
                if (Directory.Exists(host) || File.Exists(host))
                {
                    SetD0(GEMDOS_EACCDN);
                }
                else
                {
                    Directory.CreateDirectory(host);
                    SetD0(GEMDOS_EOK);
                }
            }
            catch (DirectoryNotFoundException) { SetD0(GEMDOS_EPTHNF); }
            catch (UnauthorizedAccessException) { SetD0(GEMDOS_EACCDN); }
            catch { SetD0(GEMDOS_ERROR); }
            return 1;
        }

        static int DDelete(uint parms)
        {
            string name = ReadStringG(R32g(parms));
            if (string.IsNullOrEmpty(name) || !IsOurs(name))
                return 0;

            Trace($"Ddelete(\"{name}\")");

            try
            {
                Directory.Delete(ToHostPath(name), recursive: false);
                SetD0(GEMDOS_EOK);
            }
            catch (DirectoryNotFoundException) { SetD0(GEMDOS_EPTHNF); }
            catch (UnauthorizedAccessException) { SetD0(GEMDOS_EACCDN); }
            catch (IOException) { SetD0(GEMDOS_EACCDN); }   // not empty
            catch { SetD0(GEMDOS_ERROR); }
            return 1;
        }

        static int DSetPath(uint parms)
        {
            string name = ReadStringG(R32g(parms));
            if (name == null)
            {
                SetD0(GEMDOS_EPTHNF);
                return 1;
            }
            if (!IsOurs(name))
                return 0;

            Trace($"Dsetpath(\"{name}\")");

            if (name.Length == 0)
            {
                SetD0(GEMDOS_EOK);
                return 1;
            }

            string host = ToHostPath(name);

            if (!Directory.Exists(host))
            {
                SetD0(GEMDOS_EPTHNF);
                return 1;
            }

            string full = Path.GetFullPath(host).TrimEnd(Path.DirectorySeparatorChar);
            if (!full.Equals(_hostRoot, StringComparison.OrdinalIgnoreCase) &&
                !full.StartsWith(_hostRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                SetD0(GEMDOS_EPTHNF);   // '..' shenanigans below the mounted folder
                return 1;
            }

            _currentHostDir = full + Path.DirectorySeparatorChar;
            SetD0(GEMDOS_EOK);
            return 1;
        }

        static int DGetPath(uint parms)
        {
            uint address = R32g(parms);
            int drive = R16g(parms + 4);

            drive = drive == 0 ? _currentDrive : drive - 1;
            if (!IsOurDrive(drive))
                return 0;

            string rel = Path.GetFullPath(_currentHostDir).Substring(_hostRoot.Length)
                             .TrimEnd(Path.DirectorySeparatorChar)
                             .Replace(Path.DirectorySeparatorChar, '\\');

            if (!ASEMain._mem.IsRamArea(address, (uint)rel.Length + 1))
            {
                SetD0(GEMDOS_ERANGE);
                return 1;
            }

            for (int i = 0; i < rel.Length; i++)
                W8g(address + (uint)i, (byte)rel[i]);
            W8g(address + (uint)rel.Length, 0);

            Trace($"Dgetpath -> \"{rel}\"");
            SetD0(GEMDOS_EOK);
            return 1;
        }

        static int FCreate(uint parms)
        {
            string name = ReadStringG(R32g(parms));
            int mode = R16g(parms + 4);

            if (string.IsNullOrEmpty(name) || !IsOurs(name))
                return 0;

            Trace($"Fcreate(\"{name}\", ${mode:X2})");

            if ((mode & ~FA_IGNORED) == FA_VOLUME)
            {
                SetD0(GEMDOS_EFILNF);   // volume label creation is not supported
                return 1;
            }

            int index = FindFreeHandle();
            if (index == -1)
            {
                SetD0(GEMDOS_ENHNDL);
                return 1;
            }

            string host = ToHostPath(name);

            try
            {
                if (Directory.Exists(host))
                {
                    SetD0(GEMDOS_EACCDN);   // can't truncate a directory
                    return 1;
                }

                // An existing file is truncated, the way TOS's Fcreate works
                var fs = new FileStream(host, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);

                if ((mode & FA_READONLY) != 0)
                {
                    try { File.SetAttributes(host, File.GetAttributes(host) | FileAttributes.ReadOnly); } catch { }
                }

                _handles[index] = new FileHandleEntry
                {
                    Used = true,
                    ReadOnly = (mode & FA_READONLY) != 0,
                    Basepage = CurrentBasepage(),
                    Fs = fs,
                    HostPath = host,
                };

                SetD0(index + BASE_FILEHANDLE);
            }
            catch (DirectoryNotFoundException) { SetD0(GEMDOS_EPTHNF); }
            catch (UnauthorizedAccessException) { SetD0(GEMDOS_EACCDN); }
            catch { SetD0(GEMDOS_EFILNF); }
            return 1;
        }

        static int FOpen(uint parms)
        {
            string name = ReadStringG(R32g(parms));
            int mode = R16g(parms + 4) & 3;

            if (string.IsNullOrEmpty(name) || !IsOurs(name))
                return 0;

            Trace($"Fopen(\"{name}\", {mode})");

            int index = FindFreeHandle();
            if (index == -1)
            {
                SetD0(GEMDOS_ENHNDL);
                return 1;
            }

            string host = ToHostPath(name);

            try
            {
                // Every TOS lets Fread/Fwrite through whatever the Fopen mode asked, so the
                // only distinction that matters is a file the host won't let us write.
                FileStream fs;
                bool readOnly;
                try
                {
                    fs = new FileStream(host, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                    readOnly = mode == 0;
                }
                catch (UnauthorizedAccessException)
                {
                    fs = new FileStream(host, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    readOnly = true;
                }

                _handles[index] = new FileHandleEntry
                {
                    Used = true,
                    ReadOnly = readOnly,
                    Basepage = CurrentBasepage(),
                    Fs = fs,
                    HostPath = host,
                };

                SetD0(index + BASE_FILEHANDLE);
            }
            catch (FileNotFoundException) { SetD0(GEMDOS_EFILNF); }
            catch (DirectoryNotFoundException) { SetD0(GEMDOS_EPTHNF); }
            catch (UnauthorizedAccessException) { SetD0(GEMDOS_EACCDN); }
            catch { SetD0(GEMDOS_EFILNF); }
            return 1;
        }

        static int FClose(uint parms)
        {
            int handle = R16g(parms);
            int index = GetValidHandle(handle);
            if (index < 0)
                return 0;

            Trace($"Fclose({handle})");

            try { _handles[index].Fs?.Dispose(); } catch { }
            _handles[index] = null;

            for (int i = 0; i < _forcedHandle.Length; i++)
            {
                if (_forcedHandle[i] == index)
                {
                    _forcedHandle[i] = -1;
                    _forcedBasepage[i] = 0;
                }
            }

            SetD0(GEMDOS_EOK);
            return 1;
        }

        static int FRead(uint parms)
        {
            int handle = R16g(parms);
            uint size = R32g(parms + 2);
            uint addr = R32g(parms + 6);

            int index = GetValidHandle(handle);
            if (index < 0)
                return 0;

            // Old TOS treats the size as signed
            if (_tosVersion < 0x400 && (size & 0x80000000) != 0)
            {
                SetD0(-1);
                return 1;
            }

            var fs = _handles[index].Fs;
            long left = fs.Length - fs.Position;

            if (size == 0 || left <= 0)
            {
                SetD0(0);
                return 1;
            }

            if (size > (ulong)left)
                size = (uint)left;

            if (!ASEMain._mem.IsRamArea(addr, size))
            {
                SetD0(GEMDOS_ERANGE);
                return 1;
            }

            try
            {
                byte[] data = new byte[size];
                int read = fs.Read(data, 0, (int)size);
                ASEMain._mem.WriteBytes(addr, data, 0, read);
                SetD0(read);
            }
            catch (Exception ex)
            {
                ColoredConsole.WriteLine($"GEMDOS drive: read error on '{_handles[index].HostPath}': {ex.Message}", ConfigOptions.DebugModes.Quiet);
                SetD0(GEMDOS_ERROR);
            }
            return 1;
        }

        static int FWrite(uint parms)
        {
            int handle = R16g(parms);
            uint size = R32g(parms + 2);
            uint addr = R32g(parms + 6);

            int index = GetValidHandle(handle);
            if (index < 0)
                return 0;

            if (size != 0 && !ASEMain._mem.IsRamArea(addr, size) && !IsTosRomArea(addr, size))
            {
                SetD0(GEMDOS_ERANGE);
                return 1;
            }

            try
            {
                byte[] data = new byte[size];
                ASEMain._mem.ReadBytes(addr, data, 0, (int)size);
                var fs = _handles[index].Fs;
                fs.Write(data, 0, (int)size);
                fs.Flush();
                SetD0((int)size);
            }
            catch (Exception ex)
            {
                ColoredConsole.WriteLine($"GEMDOS drive: write error on '{_handles[index].HostPath}': {ex.Message}", ConfigOptions.DebugModes.Quiet);
                SetD0(GEMDOS_EACCDN);
            }
            return 1;
        }

        static bool IsTosRomArea(uint addr, uint size)
        {
            var mem = ASEMain._mem;
            return addr >= mem.TosBase && addr + size <= mem.TosBase + (uint)mem.TosSize;
        }

        static int FDelete(uint parms)
        {
            string name = ReadStringG(R32g(parms));
            if (string.IsNullOrEmpty(name) || !IsOurs(name))
                return 0;

            Trace($"Fdelete(\"{name}\")");

            try
            {
                string host = ToHostPath(name);
                if (!File.Exists(host))
                {
                    SetD0(GEMDOS_EFILNF);
                }
                else
                {
                    File.Delete(host);
                    SetD0(GEMDOS_EOK);
                }
            }
            catch (UnauthorizedAccessException) { SetD0(GEMDOS_EACCDN); }
            catch { SetD0(GEMDOS_ERROR); }
            return 1;
        }

        static int FSeek(uint parms)
        {
            int offset = (int)R32g(parms);
            int handle = R16g(parms + 4);
            int mode = R16g(parms + 6);

            int index = GetValidHandle(handle);
            if (index < 0)
                return 0;

            var fs = _handles[index].Fs;
            long dest = mode switch
            {
                0 => offset,
                1 => fs.Position + offset,
                2 => fs.Length + offset,
                _ => -1,
            };

            if (dest < 0 || dest > fs.Length)
            {
                SetD0(GEMDOS_ERANGE);
                return 1;
            }

            fs.Position = dest;
            SetD0((int)dest);
            return 1;
        }

        static int FAttrib(uint parms)
        {
            string name = ReadStringG(R32g(parms));
            int rwFlag = R16g(parms + 4);
            int attrib = R16g(parms + 6);

            if (string.IsNullOrEmpty(name) || !IsOurs(name))
                return 0;

            Trace($"Fattrib(\"{name}\", {rwFlag}, ${attrib:X2})");

            string host = ToHostPath(name);

            if ((attrib & ~FA_IGNORED) == FA_VOLUME || !(File.Exists(host) || Directory.Exists(host)))
            {
                SetD0(GEMDOS_EFILNF);
                return 1;
            }

            int current = HostAttributes(host);

            if (rwFlag == 0)
            {
                SetD0(current);
                return 1;
            }

            // Setting: the directory bit must agree with what the entry is
            if ((attrib & FA_DIR) != 0 && (current & FA_DIR) == 0)
            {
                SetD0(GEMDOS_EPTHNF);
                return 1;
            }
            if ((attrib & FA_DIR) == 0 && (current & FA_DIR) != 0)
            {
                SetD0(GEMDOS_EFILNF);
                return 1;
            }

            try
            {
                var hostAttr = File.GetAttributes(host);
                if ((attrib & FA_READONLY) != 0)
                    hostAttr |= FileAttributes.ReadOnly;
                else
                    hostAttr &= ~FileAttributes.ReadOnly;
                File.SetAttributes(host, hostAttr);
                SetD0(attrib);
            }
            catch { SetD0(GEMDOS_EACCDN); }
            return 1;
        }

        static int FForce(uint parms)
        {
            int std = R16g(parms);
            int own = R16g(parms + 2);

            if (std > own)
                (std, own) = (own, std);

            int index = GetValidHandle(own);
            if (index < 0)
                return 0;

            if (std < 0 || std >= _forcedHandle.Length)
            {
                Trace($"Fforce of non-standard handle {std} ignored");
                return 0;
            }

            _forcedBasepage[std] = CurrentBasepage();
            _forcedHandle[std] = index;

            SetD0(GEMDOS_EOK);
            return 1;
        }

        static int FRename(uint parms)
        {
            string oldName = ReadStringG(R32g(parms + 2));
            string newName = ReadStringG(R32g(parms + 6));

            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName))
                return 0;
            if (!(IsOurs(oldName) && IsOurs(newName)))
                return 0;

            Trace($"Frename(\"{oldName}\", \"{newName}\")");

            string oldHost = ToHostPath(oldName);
            string newHost = ToHostPath(newName);

            try
            {
                bool oldIsDir = Directory.Exists(oldHost);
                if (!oldIsDir && !File.Exists(oldHost))
                {
                    SetD0(GEMDOS_EFILNF);
                }
                else if (File.Exists(newHost) || Directory.Exists(newHost))
                {
                    SetD0(GEMDOS_EACCDN);   // TOS only renames onto a free name
                }
                else
                {
                    if (oldIsDir)
                        Directory.Move(oldHost, newHost);
                    else
                        File.Move(oldHost, newHost);
                    SetD0(GEMDOS_EOK);
                }
            }
            catch (UnauthorizedAccessException) { SetD0(GEMDOS_EACCDN); }
            catch { SetD0(GEMDOS_ERROR); }
            return 1;
        }

        static int FDatime(uint parms)
        {
            uint buffer = R32g(parms);
            int handle = R16g(parms + 4);
            int flag = R16g(parms + 6);

            int index = GetValidHandle(handle);
            if (index < 0)
                return 0;

            string host = _handles[index].HostPath;

            if (flag == 1)
            {
                ushort timeword = R16g(buffer);
                ushort dateword = R16g(buffer + 2);
                try
                {
                    var t = new DateTime(
                        1980 + ((dateword >> 9) & 0x7F),
                        Math.Clamp((dateword >> 5) & 0x0F, 1, 12),
                        Math.Clamp(dateword & 0x1F, 1, 31),
                        Math.Clamp((timeword >> 11) & 0x1F, 0, 23),
                        Math.Clamp((timeword >> 5) & 0x3F, 0, 59),
                        Math.Clamp((timeword & 0x1F) << 1, 0, 59));
                    _handles[index].Fs.Flush();
                    File.SetLastWriteTime(host, t);
                    SetD0(GEMDOS_EOK);
                }
                catch { SetD0(GEMDOS_EACCDN); }
                return 1;
            }

            if (!ASEMain._mem.IsRamArea(buffer, 4))
            {
                SetD0(GEMDOS_ERANGE);
                return 1;
            }

            try
            {
                DateTimeToTos(File.GetLastWriteTime(host), out ushort timeword, out ushort dateword);
                W16g(buffer, timeword);
                W16g(buffer + 2, dateword);
                SetD0(GEMDOS_EOK);
            }
            catch { SetD0(GEMDOS_ERROR); }
            return 1;
        }

        // ==================== Fsfirst / Fsnext ====================

        static void ClearAllDtas()
        {
            for (int i = 0; i < _dtas.Length; i++)
                _dtas[i] = null;
            _dtaIndex = 0;
        }

        static int FsFirst(uint parms)
        {
            uint nameAddr = R32g(parms);
            int attrib = R16g(parms + 4);

            string mask = ReadStringG(nameAddr);
            if (mask == null)
                return 0;

            if (!IsOurs(mask))
                return 0;

            Trace($"Fsfirst(\"{mask}\", ${attrib:X2})");

            uint dtaAddr = R32g(CurrentBasepage() + BASEPAGE_OFFSET_DTA);
            if (!ASEMain._mem.IsRamArea(dtaAddr, 44))
            {
                SetD0(GEMDOS_EINTRN);
                return 1;
            }

            // Reuse the slot when the program iterates the same DTA again
            int useIdx;
            if (R32g(dtaAddr + 2) == DTA_MAGIC)
            {
                useIdx = R16g(dtaAddr);
                if (useIdx >= _dtas.Length || _dtas[useIdx] == null || _dtas[useIdx].Addr != dtaAddr)
                    useIdx = _dtaIndex;
            }
            else
            {
                W32g(dtaAddr + 2, DTA_MAGIC);
                useIdx = _dtaIndex;
            }

            W16g(dtaAddr, (ushort)useIdx);

            var dta = new InternalDta { Used = true, Addr = dtaAddr, Attrib = attrib };
            _dtas[useIdx] = dta;

            // A volume-label-only query answers with a synthetic label, like Hatari
            if ((attrib & ~FA_IGNORED) == FA_VOLUME)
            {
                WriteDtaEntry(dtaAddr, "ASE_HD", FA_VOLUME, 0, 0x21, 0);    // 1980-01-01
                SetD0(GEMDOS_EOK);
                return 1;
            }

            string hostPattern = ToHostPath(mask, keepWildcards: true);
            string hostDir = Path.GetDirectoryName(hostPattern);
            string pattern = Path.GetFileName(hostPattern);

            if (string.IsNullOrEmpty(hostDir) || !Directory.Exists(hostDir))
            {
                SetD0(GEMDOS_EPTHNF);
                return 1;
            }

            bool isRoot = string.Equals(Path.GetFullPath(hostDir).TrimEnd(Path.DirectorySeparatorChar),
                                        _hostRoot, StringComparison.OrdinalIgnoreCase);

            dta.HostDir = hostDir;
            dta.Entries = new List<string>();
            dta.Current = 0;

            if (!isRoot)
            {
                if (FsFirstMatch(pattern, ".", subdir: true)) dta.Entries.Add(".");
                if (FsFirstMatch(pattern, "..", subdir: true)) dta.Entries.Add("..");
            }

            try
            {
                var names = new List<string>();
                foreach (string entry in Directory.EnumerateFileSystemEntries(hostDir))
                    names.Add(Path.GetFileName(entry));
                names.Sort(StringComparer.OrdinalIgnoreCase);

                foreach (string n in names)
                    if (FsFirstMatch(pattern, n, subdir: !isRoot))
                        dta.Entries.Add(n);
            }
            catch
            {
                SetD0(GEMDOS_EPTHNF);
                return 1;
            }

            if (dta.Entries.Count == 0)
            {
                SetD0(GEMDOS_EFILNF);
                return 1;
            }

            FsNext(false);      // deliver the first match

            if (useIdx == _dtaIndex && ++_dtaIndex >= _dtas.Length)
                _dtaIndex = 0;

            return 1;
        }

        static int FsNext(bool fromTrap)
        {
            if (fromTrap)
                Trace("Fsnext()");

            uint dtaAddr = R32g(CurrentBasepage() + BASEPAGE_OFFSET_DTA);
            if (!ASEMain._mem.IsRamArea(dtaAddr, 44))
            {
                SetD0(GEMDOS_EINTRN);
                return 1;
            }

            if (R32g(dtaAddr + 2) != DTA_MAGIC)
                return 0;       // a TOS DTA, not ours

            int index = R16g(dtaAddr);
            if (index >= _dtas.Length || _dtas[index] == null || !_dtas[index].Used)
            {
                SetD0(GEMDOS_ENMFIL);
                return 1;
            }

            var dta = _dtas[index];

            if ((dta.Attrib & ~FA_IGNORED) == FA_VOLUME)
            {
                SetD0(GEMDOS_ENMFIL);   // the label was already delivered by Fsfirst
                return 1;
            }

            while (true)
            {
                if (dta.Current >= dta.Entries.Count)
                {
                    if (_tosVersion < 0x400)
                        W8g(dtaAddr + 30, 0);   // old TOS zeroes the name on no-more-files
                    SetD0(GEMDOS_ENMFIL);
                    return 1;
                }

                string hostName = dta.Entries[dta.Current++];
                string hostPath = hostName == "." ? dta.HostDir
                                : hostName == ".." ? (Path.GetDirectoryName(dta.HostDir) ?? dta.HostDir)
                                : Path.Combine(dta.HostDir, hostName);

                int attr;
                long size;
                DateTime mtime;
                try
                {
                    attr = HostAttributes(hostPath);
                    if ((attr & FA_DIR) != 0)
                    {
                        size = 0;
                        mtime = Directory.GetLastWriteTime(hostPath);
                    }
                    else
                    {
                        var fi = new FileInfo(hostPath);
                        size = fi.Length;
                        mtime = fi.LastWriteTime;
                    }
                }
                catch
                {
                    continue;   // vanished between the scan and now: skip it
                }

                // Attribute filter, as the Profibuch describes it
                int attrMask = dta.Attrib | FA_IGNORED;
                if (attr != 0 && (attrMask & attr) == 0)
                    continue;

                DateTimeToTos(mtime, out ushort timeword, out ushort dateword);
                WriteDtaEntry(dtaAddr, HostNameToAtari(hostName), attr, timeword, dateword, (uint)size);

                SetD0(GEMDOS_EOK);
                return 1;
            }
        }

        /// <summary>The public DTA fields: attrib @21, time @22, date @24, size @26, name @30
        /// (14 bytes, zero padded).</summary>
        static void WriteDtaEntry(uint dtaAddr, string name, int attr, ushort timeword, ushort dateword, uint size)
        {
            W8g(dtaAddr + 21, (byte)attr);
            W16g(dtaAddr + 22, timeword);
            W16g(dtaAddr + 24, dateword);
            W32g(dtaAddr + 26, size);

            for (int i = 0; i < 14; i++)
                W8g(dtaAddr + 30 + (uint)i, (byte)(i < name.Length ? name[i] : 0));
        }

        // ==================== Pexec ====================

        /// <summary>
        /// Pexec stage 1 (modes 0 and 3 on our drive): validates the PRG and pushes a
        /// "create basepage" parameter block for the cartridge code to feed back into TOS.
        /// Returns -1 to make the dispatcher raise the overflow flag (the bvs into that code).
        /// </summary>
        static int Pexec(uint parms)
        {
            int mode = R16g(parms);
            uint prgname = R32g(parms + 2);
            uint cmdline = R32g(parms + 6);
            uint envstring = R32g(parms + 10);

            if (mode != 0 && mode != 3)
                return 0;   // only the "load" modes are intercepted

            string name = ReadStringG(prgname);
            if (name == null || !IsOurs(name))
                return 0;

            Trace($"Pexec({mode}, \"{name}\")");

            string host = ToHostPath(name);
            byte[] header = new byte[28];

            try
            {
                using var fs = new FileStream(host, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Read(header, 0, 28) != 28)
                {
                    SetD0(GEMDOS_EPLFMT);
                    return 1;
                }
            }
            catch
            {
                SetD0(GEMDOS_EFILNF);
                return 1;
            }

            if (header[0] != 0x60 || header[1] != 0x1A ||
                (header[2] & 0x80) != 0 || (header[6] & 0x80) != 0 || (header[10] & 0x80) != 0)
            {
                SetD0(GEMDOS_EPLFMT);
                return 1;
            }

            // Stack a Pexec(5|7, prgflags, cmdline, env) block for the cartridge's trap #1.
            // TOS >= 2 understands mode 7 (create basepage honouring the PRG flags).
            uint sp = GetA(7) - 16;
            SetA(7, sp);

            W16g(sp, 0x4B);
            W16g(sp + 2, (ushort)(_tosVersion >= 0x200 ? 7 : 5));
            W32g(sp + 4, (uint)((header[22] << 24) | (header[23] << 16) | (header[24] << 8) | header[25]));
            W32g(sp + 8, cmdline);
            W32g(sp + 12, envstring);

            _savedPexecParams = parms;      // the caller's block, starting at the mode word

            return -1;
        }

        /// <summary>
        /// Pexec stage 2: TOS created the basepage (D0). Load and relocate the PRG into it;
        /// mode 0 then rewrites the caller's parameters into a Pexec "just go" and chains to
        /// TOS (overflow flag), mode 3 returns the basepage (zero flag), and a load failure
        /// falls through to the cartridge's Mfree cleanup (neither flag).
        /// </summary>
        static void OnPexecBasepageCreated()
        {
            var m = CPU._moira;
            ushort sr = (ushort)(m.SR & ~(SR_OVERFLOW | SR_ZERO));

            uint parms = _savedPexecParams;
            int mode = R16g(parms);
            uint prgname = R32g(parms + 2);

            string name = ReadStringG(prgname);
            int errcode;

            if (name != null && IsOurs(name))
                errcode = LoadAndReloc(ToHostPath(name), GetD(0));
            else
                errcode = GEMDOS_EDRIVE;

            if (errcode != 0)
            {
                SetA(0, GetD(0));       // basepage, for the cartridge's Mfree cleanup
                SetD0(errcode);
            }
            else if (mode == 0)
            {
                // Rewrite the caller's block into Pexec(4|6, ..., basepage, ...) and chain to
                // TOS with the original exception frame: its RTE starts the program.
                W16g(parms, (ushort)(_tosVersion >= 0x104 ? 6 : 4));
                W32g(parms + 6, GetD(0));
                sr |= SR_OVERFLOW;
            }
            else
            {
                sr |= SR_ZERO;          // mode 3: hand the basepage back to the caller
            }

            m.SR = sr;
        }

        /// <summary>
        /// Loads a PRG from the host into the TPA of the freshly created basepage and applies
        /// its relocation table; fills in the basepage text/data/bss fields and clears
        /// BSS (plus the heap, unless the fastload flag is set). Returns 0 or a GEMDOS error.
        /// </summary>
        static int LoadAndReloc(string hostPath, uint basepage)
        {
            byte[] prg;
            try
            {
                prg = File.ReadAllBytes(hostPath);
            }
            catch
            {
                return GEMDOS_EFILNF;
            }

            // Loading a program is the longest access of all; keep the light on through it
            ASEMain.SignalHardDiskActivity();

            if (prg.Length < 30 || prg[0] != 0x60 || prg[1] != 0x1A)
                return GEMDOS_EPLFMT;

            uint textLen = Be32(prg, 2);
            uint dataLen = Be32(prg, 6);
            uint bssLen = Be32(prg, 10);
            uint symLen = Be32(prg, 14);

            uint memtop = R32g(0x436);
            uint textAddr = basepage + 0x100;

            if (textAddr + textLen + dataLen + bssLen > memtop)
                return GEMDOS_ENSMEM;

            if ((ulong)28 + textLen + dataLen > (ulong)prg.Length)
                return GEMDOS_EPLFMT;

            var mem = ASEMain._mem;

            if (!mem.IsRamArea(textAddr, textLen + dataLen + bssLen))
                return GEMDOS_EPLFMT;

            mem.WriteBytes(textAddr, prg, 28, (int)(textLen + dataLen));
            mem.FillBytes(textAddr + textLen + dataLen, 0, (int)bssLen);

            // Basepage text/data/bss fields (TOS filled the memory bounds when creating it)
            W32g(basepage + 8, textAddr);                       // p_tbase
            W32g(basepage + 12, textLen);                       // p_tlen
            W32g(basepage + 16, textAddr + textLen);            // p_dbase
            W32g(basepage + 20, dataLen);                       // p_dlen
            W32g(basepage + 24, textAddr + textLen + dataLen);  // p_bbase
            W32g(basepage + 28, bssLen);                        // p_blen

            // Without the fastload flag the whole heap up to p_hitpa is cleared too
            if ((prg[25] & 1) == 0)
            {
                uint heapStart = textAddr + textLen + dataLen + bssLen;
                uint hitpa = R32g(basepage + 4);
                if (hitpa > heapStart && mem.IsRamArea(heapStart, hitpa - heapStart))
                    mem.FillBytes(heapStart, 0, (int)(hitpa - heapStart));
            }

            // ABSFLAG set: no relocation information
            if (((prg[26] << 8) | prg[27]) != 0)
                return 0;

            long relIdx = 28 + textLen + dataLen;
            if (relIdx > prg.Length - 4)
                return GEMDOS_EPLFMT;
            if (relIdx + symLen <= prg.Length - 4)
                relIdx += symLen;   // an oversized symbol table is ignored, like original TOS

            uint relOff = Be32(prg, (int)relIdx);
            if (relOff == 0)
                return 0;

            uint cur = textAddr + relOff;
            W32g(cur, R32g(cur) + textAddr);
            relIdx += 4;

            while (relIdx < prg.Length && prg[relIdx] != 0)
            {
                if (prg[relIdx] == 1)
                {
                    relOff += 254;
                    relIdx++;
                    continue;
                }
                relOff += prg[relIdx];
                cur = textAddr + relOff;
                W32g(cur, R32g(cur) + textAddr);
                relIdx++;
            }

            return 0;
        }

        static uint Be32(byte[] b, int i) =>
            ((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3];

        // ==================== Snapshot ====================

        /// <summary>
        /// The little state that must survive a snapshot: the trap #1 chain (the RAM image
        /// carries $84 pointing into the cartridge, so the cartridge must keep knowing where
        /// TOS's handler was) and the boot bookkeeping. Host-side file handles and directory
        /// scans are NOT saved: they reset, like unplugging and replugging the drive.
        /// </summary>
        public static void SaveState(Snapshot.Writer w)
        {
            w.Bool(Enabled);
            w.Bool(_booted);
            w.U8((byte)_driveNumber);
            w.U32(_oldGemdos);
            w.U32(_actPd);
            w.U16((ushort)_currentDrive);
        }

        public static void LoadState(Snapshot.Reader r)
        {
            bool wasEnabled = r.Bool();
            bool booted = r.Bool();
            byte driveNumber = r.U8();
            uint oldGemdos = r.U32();
            uint actPd = r.U32();
            ushort currentDrive = r.U16();

            if (!wasEnabled)
            {
                // The drive is on now but the snapshot was taken without it: TOS never ran the
                // cartridge's boot hook in that machine, so nothing points at us. It appears
                // on the next reset.
                if (Enabled)
                    ColoredConsole.WriteLine($"Snapshot predates the GEMDOS drive; {DriveLetter}: appears after a reset.");
                return;
            }

            if (!Enabled)
            {
                // The snapshot's RAM has $84 hooked into a cartridge that is not there now:
                // un-hook it so trap #1 still reaches TOS. A program chained on top of the
                // hook would break, but the alternative is a certain crash.
                if (R32g(0x84) == NEW_GEMDOS && oldGemdos != 0)
                    W32g(0x84, oldGemdos);
                ColoredConsole.WriteLine("Snapshot used a GEMDOS drive that is not enabled now; its files are gone.");
                return;
            }

            _booted = booted;
            _oldGemdos = oldGemdos;
            _actPd = actPd;
            _currentDrive = currentDrive;
            _driveNumber = driveNumber;
            PatchOldGemdos(_oldGemdos);

            // Open files and directory scans do not survive; current dir resets to the root
            CloseAllHandles();
            ClearAllDtas();
            _currentHostDir = _hostRoot + Path.DirectorySeparatorChar;
        }
    }
}
