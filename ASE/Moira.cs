/*
 * 
 * Wrapper for Moira 68k CPU emulator.
 * This class handles interaction with the Moira native library,
 * 
 * Official repository 👉 https://github.com/thebitculture/ase
 * 
 */

using System.Runtime.InteropServices;
using System.Text;
using static ASE.CPU;

namespace ASE
{
    /// <summary>
    /// Wrapper for Moira 68k CPU emulator, providing methods to execute instructions, control CPU state, and
    /// interact with memory and registers through user-supplied delegates.
    /// </summary>
    /// <remarks>Moira enables integration of a 68k CPU core into .NET applications by allowing the user to
    /// supply delegates for memory access, synchronization, and interrupt handling. The class exposes methods for
    /// instruction execution, register manipulation, and disassembly, and supports both single-step and cycle-based
    /// execution. Thread safety is not guaranteed; callers should ensure appropriate synchronization if accessing
    /// instances from multiple threads. The class implements IDisposable and must be disposed to release native
    /// resources.</remarks>
    public sealed class Moira : IDisposable
    {
        // -------------------- Public delegates --------------------

        public delegate byte Read8(uint addr);
        public delegate ushort Read16(uint addr);
        public delegate void Write8(uint addr, byte value);
        public delegate void Write16(uint addr, ushort value);
        public delegate void Sync(int cycles);
        public delegate ushort ReadIrqUserVector(byte level);

        // -------------------- Construction --------------------

        public Moira(
            Read8 read8,
            Read16 read16,
            Write8 write8,
            Write16 write16,
            Sync? sync = null,
            ReadIrqUserVector? readIrqUserVector = null)
        {
            ArgumentNullException.ThrowIfNull(read8);
            ArgumentNullException.ThrowIfNull(read16);
            ArgumentNullException.ThrowIfNull(write8);
            ArgumentNullException.ThrowIfNull(write16);

            // Keep managed delegates alive (critical: prevents GC collection)
            _read8 = read8;
            _read16 = read16;
            _write8 = write8;
            _write16 = write16;
            _sync = sync;
            _readIrq = readIrqUserVector;

            // Create unmanaged delegates that match the native signature
            _r8 = (_, addr) => _read8(addr);
            _r16 = (_, addr) => _read16(addr);
            _w8 = (_, addr, v) => _write8(addr, v);
            _w16 = (_, addr, v) => _write16(addr, v);
            _syncNative = _sync is null ? null : new SyncFn((_, cycles) => _sync(cycles));
            _irqNative = _readIrq is null ? null : new ReadIrqUserVectorFn((_, level) => _readIrq(level));

            var cb = new Callbacks
            {
                user = IntPtr.Zero,
                read8 = _r8,
                read16 = _r16,
                write8 = _w8,
                write16 = _w16,
                sync = _syncNative,
                readIrqUserVector = _irqNative
            };

            _h = Native.moira_create(ref cb);
            if (_h == IntPtr.Zero)
                throw new InvalidOperationException("moira_create returned null.");
        }

        // -------------------- Lifetime --------------------

        public void Dispose()
        {
            if (_h != IntPtr.Zero)
            {
                Native.moira_destroy(_h);
                _h = IntPtr.Zero;
            }
            GC.SuppressFinalize(this);
        }

        ~Moira() => Dispose();

        // -------------------- Execution --------------------

        public void Reset() => Native.moira_reset(_h);

        /// <summary>Execute a single instruction.</summary>
        public void Step() => Native.moira_execute(_h);

        /// <summary>
        /// Execute until at least the given number of cycles has elapsed.
        /// </summary>
        /// <remarks>This method catches all exceptions thrown by the emulator loop since it executes in a different thread.</remarks>
        public void RunForCycles(long cycles) 
        {
            try
            {
                Native.moira_execute_cycles(_h, cycles);
            }
            catch
            {
                Console.WriteLine("Not controlled exception in Moira");
            }
        }

        /// <summary>Execute until the internal clock reaches the target cycle.</summary>
        public void RunUntil(long cycle) => Native.moira_execute_until(_h, cycle);

        /// <summary>
        /// Enables or disables supervisor mode for the 68k.
        /// </summary>
        /// <remarks>Supervisor mode may grant elevated access to protected memory.</param>
        public void SetSupervisorMode(bool s) => Native.moira_setSupervisorMode(_h, s);

        public void TriggerBusError(uint ErrorAdress, bool IsWrite)
        {
            if (Config.ConfigOptions.RunninConfig.DebugMode)
                ColoredConsole.WriteLine($"Moira: Triggering bus error at address [[red]]{ErrorAdress:X}[[/red]] (isWrite=[[magenta]]{IsWrite}[[/magenta]])");

            Native.moira_triggerBusError(_h, ErrorAdress, IsWrite);
        }

        public long Clock
        {
            get => Native.moira_getClock(_h);
            set => Native.moira_setClock(_h, value);
        }

        // Registers (idiomatic)

        public uint PC
        {
            get => Native.moira_getPC(_h);
            set => Native.moira_setPC(_h, value);
        }

        public uint PC0
        {
            get => Native.moira_getPC0(_h);
            set => Native.moira_setPC0(_h, value);
        }

        public ushort IRC
        {
            get => Native.moira_getIRC(_h);
            set => Native.moira_setIRC(_h, value);
        }

        public ushort IRD
        {
            get => Native.moira_getIRD(_h);
            set => Native.moira_setIRD(_h, value);
        }

        public byte CCR
        {
            get => Native.moira_getCCR(_h);
            set => Native.moira_setCCR(_h, value);
        }

        public ushort SR
        {
            get => Native.moira_getSR(_h);
            set => Native.moira_setSR(_h, value);
        }

        public uint SP
        {
            get => Native.moira_getSP(_h);
            set => Native.moira_setSP(_h, value);
        }

        public byte IPL
        {
            get => Native.moira_getIPL(_h);
            set => Native.moira_setIPL(_h, value);
        }

        /// <summary>Data registers D0..D7 (index 0-7).</summary>
        public RegisterBank D => new RegisterBank(this, isAddress: false);

        /// <summary>Address registers A0..A7 (index 0-7).</summary>
        public RegisterBank A => new RegisterBank(this, isAddress: true);

        public readonly struct RegisterBank
        {
            private readonly Moira _cpu;
            private readonly bool _isAddress;

            internal RegisterBank(Moira cpu, bool isAddress)
            {
                _cpu = cpu;
                _isAddress = isAddress;
            }

            public uint this[int index]
            {
                get
                {
                    if ((uint)index > 7) throw new ArgumentOutOfRangeException(nameof(index), "Register index must be 0..7.");
                    return _isAddress ? Native.moira_getA(_cpu._h, index) : Native.moira_getD(_cpu._h, index);
                }
                set
                {
                    if ((uint)index > 7) throw new ArgumentOutOfRangeException(nameof(index), "Register index must be 0..7.");
                    if (_isAddress) Native.moira_setA(_cpu._h, index, value);
                    else Native.moira_setD(_cpu._h, index, value);
                }
            }
        }

        // Disassembler / formatting

        /// <summary>
        /// Disassembles instruction at address and returns the formatted line.
        /// </summary>
        public (string, int) Disassemble(uint addr, int capacity = 256)
        {
            var sb = new StringBuilder(capacity);
            int bytesSize = Native.moira_disassemble(_h, sb, addr);
            return (sb.ToString(), bytesSize);
        }

        public string DisassembleSR(int capacity = 128)
        {
            var sb = new StringBuilder(capacity);
            Native.moira_disassembleSR(_h, sb);
            return sb.ToString();
        }

        public string Dump8(byte value, int capacity = 64)
        {
            var sb = new StringBuilder(capacity);
            Native.moira_dump8(_h, sb, value);
            return sb.ToString();
        }

        public string Dump16(ushort value, int capacity = 64)
        {
            var sb = new StringBuilder(capacity);
            Native.moira_dump16(_h, sb, value);
            return sb.ToString();
        }

        public string Dump24(uint value, int capacity = 64)
        {
            var sb = new StringBuilder(capacity);
            Native.moira_dump24(_h, sb, value);
            return sb.ToString();
        }

        public string Dump32(uint value, int capacity = 64)
        {
            var sb = new StringBuilder(capacity);
            Native.moira_dump32(_h, sb, value);
            return sb.ToString();
        }

        // Private state

        private IntPtr _h;

        // Keep original managed delegates (user passed) alive
        private readonly Read8 _read8;
        private readonly Read16 _read16;
        private readonly Write8 _write8;
        private readonly Write16 _write16;
        private readonly Sync? _sync;
        private readonly ReadIrqUserVector? _readIrq;

        // Keep unmanaged delegates alive
        private readonly Read8Fn _r8;
        private readonly Read16Fn _r16;
        private readonly Write8Fn _w8;
        private readonly Write16Fn _w16;
        private readonly SyncFn? _syncNative;
        private readonly ReadIrqUserVectorFn? _irqNative;

        // Native interop (internal/private)

        private const string Lib = "moira";

        [StructLayout(LayoutKind.Sequential)]
        private struct Callbacks
        {
            public IntPtr user;
            public Read8Fn read8;
            public Read16Fn read16;
            public Write8Fn write8;
            public Write16Fn write16;
            public SyncFn? sync;
            public ReadIrqUserVectorFn? readIrqUserVector;
        }

        // Native callback signatures (match your current DLL)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte Read8Fn(IntPtr user, uint addr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ushort Read16Fn(IntPtr user, uint addr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void Write8Fn(IntPtr user, uint addr, byte v);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void Write16Fn(IntPtr user, uint addr, ushort v);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SyncFn(IntPtr user, int cycles);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ushort ReadIrqUserVectorFn(IntPtr user, byte level);

        private static class Native
        {
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern IntPtr moira_create(ref Callbacks cb);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void moira_destroy(IntPtr h);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void moira_reset(IntPtr h);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void moira_execute(IntPtr h);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void moira_execute_cycles(IntPtr h, long cycles);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void moira_execute_until(IntPtr h, long cycle);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void moira_setSupervisorMode(IntPtr h, bool s);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void moira_triggerBusError(IntPtr h, uint adress, bool iswrite);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern long moira_getClock(IntPtr h);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void moira_setClock(IntPtr h, long v);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint moira_getD(IntPtr h, int n);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void moira_setD(IntPtr h, int n, uint v);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint moira_getA(IntPtr h, int n);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void moira_setA(IntPtr h, int n, uint v);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint moira_getPC(IntPtr h);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void moira_setPC(IntPtr h, uint v);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint moira_getPC0(IntPtr h);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void moira_setPC0(IntPtr h, uint v);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern ushort moira_getIRC(IntPtr h);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void moira_setIRC(IntPtr h, ushort v);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern ushort moira_getIRD(IntPtr h);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void moira_setIRD(IntPtr h, ushort v);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern byte moira_getCCR(IntPtr h);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void moira_setCCR(IntPtr h, byte v);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern ushort moira_getSR(IntPtr h);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void moira_setSR(IntPtr h, ushort v);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint moira_getSP(IntPtr h);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void moira_setSP(IntPtr h, uint v);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern byte moira_getIPL(IntPtr h);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void moira_setIPL(IntPtr h, byte v);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern int moira_disassemble(IntPtr h, StringBuilder str, uint addr);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern void moira_disassembleSR(IntPtr h, StringBuilder str);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern void moira_dump8(IntPtr h, StringBuilder str, byte v);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern void moira_dump16(IntPtr h, StringBuilder str, ushort v);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern void moira_dump24(IntPtr h, StringBuilder str, uint v);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern void moira_dump32(IntPtr h, StringBuilder str, uint v);
        }
    }
}
