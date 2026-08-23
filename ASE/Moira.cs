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
    /// interact with memory and registers through caller-supplied function pointers.
    /// </summary>
    /// <remarks>Moira enables integration of a 68k CPU core into .NET applications by allowing the user to
    /// supply callbacks for memory access, synchronization, and interrupt handling. The class exposes methods for
    /// instruction execution, register manipulation, and disassembly, and supports both single-step and cycle-based
    /// execution. Thread safety is not guaranteed; callers should ensure appropriate synchronization if accessing
    /// instances from multiple threads. The class implements IDisposable and must be disposed to release native
    /// resources.</remarks>
    public sealed unsafe class Moira : IDisposable
    {
        // -------------------- Construction --------------------

        /// <summary>
        /// Creates the native CPU core and wires its bus to the supplied callbacks.
        /// </summary>
        /// <remarks>
        /// The callbacks are raw function pointers, not delegates: they are meant to be
        /// <see cref="System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute"/> static methods (see
        /// <c>CPU.BusRead8</c> and friends), which the native core can call without the marshalling stub a
        /// delegate would need. This is the emulator's hottest path — the 68000 prefetches continuously, so
        /// there are on the order of two million bus accesses per emulated second — and every indirection
        /// removed here is CPU time given back on slow hosts.
        /// <para>
        /// A callback must never let an exception escape into native code: it cannot be caught across the
        /// boundary and takes the process down.
        /// </para>
        /// <paramref name="sync"/> and <paramref name="readIrqUserVector"/> may be null, in which case Moira's
        /// own default behaviour is used.
        /// </remarks>
        public Moira(
            delegate* unmanaged[Cdecl]<IntPtr, uint, byte> read8,
            delegate* unmanaged[Cdecl]<IntPtr, uint, ushort> read16,
            delegate* unmanaged[Cdecl]<IntPtr, uint, byte, void> write8,
            delegate* unmanaged[Cdecl]<IntPtr, uint, ushort, void> write16,
            delegate* unmanaged[Cdecl]<IntPtr, int, void> sync,
            delegate* unmanaged[Cdecl]<IntPtr, byte, ushort> readIrqUserVector)
        {
            if (read8 == null)  throw new ArgumentNullException(nameof(read8));
            if (read16 == null) throw new ArgumentNullException(nameof(read16));
            if (write8 == null) throw new ArgumentNullException(nameof(write8));
            if (write16 == null) throw new ArgumentNullException(nameof(write16));

            var cb = new Callbacks
            {
                user = IntPtr.Zero,
                read8 = read8,
                read16 = read16,
                write8 = write8,
                write16 = write16,
                sync = sync,
                readIrqUserVector = readIrqUserVector
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
        /// Execute until at least the given number of cycles has elapsed. Stops early when a
        /// breakpoint is reached (check <see cref="BreakpointWasHit"/> right after the call).
        /// </summary>
        /// <remarks>This method catches all exceptions thrown by the emulator loop since it executes in a different thread.</remarks>
        const int MaxReportedBusFaults = 5;
        int _busFaults;

        public void RunForCycles(long cycles)
        {
            try
            {
                Native.moira_execute_cycles(_h, cycles);
            }
            catch (Exception ex)
            {
                // The exception was thrown inside one of the bus callbacks and crossed the native
                // boundary. Say WHAT it was: a bare "something failed" here is unusable, and this
                // is the only place the failure is ever seen. Reported in full the first few times
                // (an exception per instruction would otherwise scroll the console away), then
                // counted.
                _busFaults++;
                if (_busFaults <= MaxReportedBusFaults)
                    Console.WriteLine($"Not controlled exception in Moira: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                else if (_busFaults == MaxReportedBusFaults + 1)
                    Console.WriteLine("Not controlled exception in Moira: further occurrences suppressed");
            }
        }

        /// <summary>Execute until the internal clock reaches the target cycle. Stops early when a
        /// breakpoint is reached (check <see cref="BreakpointWasHit"/> right after the call).</summary>
        public void RunUntil(long cycle) => Native.moira_execute_until(_h, cycle);

        // -------------------- Breakpoints --------------------
        // Managed by Moira's built-in debugger. While at least one breakpoint is set the core
        // checks the PC after every instruction (CHECK_BP flag); with none set there is no
        // per-instruction overhead.

        /// <summary>Sets a breakpoint at the given address (idempotent).</summary>
        public void SetBreakpoint(uint addr) => Native.moira_bp_setAt(_h, addr);

        /// <summary>Removes the breakpoint at the given address, if any.</summary>
        public void RemoveBreakpoint(uint addr) => Native.moira_bp_removeAt(_h, addr);

        /// <summary>Returns true if a breakpoint is set at the given address.</summary>
        public bool IsBreakpoint(uint addr) => Native.moira_bp_isSetAt(_h, addr);

        /// <summary>Number of breakpoints currently set.</summary>
        public long BreakpointCount => Native.moira_bp_count(_h);

        /// <summary>Removes every breakpoint.</summary>
        public void RemoveAllBreakpoints() => Native.moira_bp_removeAll(_h);

        /// <summary>
        /// True if the last <see cref="RunForCycles"/>/<see cref="RunUntil"/> call stopped because a
        /// breakpoint was reached. The PC is left AT the guarded instruction, which has not been
        /// executed yet; resuming executes it normally (the check only fires on the NEXT arrival).
        /// </summary>
        public bool BreakpointWasHit => Native.moira_bp_wasHit(_h);

        /// <summary>
        /// Enables or disables supervisor mode for the 68k.
        /// </summary>
        /// <remarks>Supervisor mode may grant elevated access to protected memory.</param>
        public void SetSupervisorMode(bool s) => Native.moira_setSupervisorMode(_h, s);

        public void TriggerBusError(uint ErrorAdress, bool IsWrite)
        {
            if (Config.ConfigOptions.RunninConfig.DebugMode >= Config.ConfigOptions.DebugModes.Information)
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

        // Native interop (internal/private)

        private const string Lib = "moira";

        // Mirrors moira_callbacks in Moira_dotnet.h — field order and types must match it.
        // No GC roots are needed here: the callbacks are pointers to static methods, which
        // (unlike the delegates this used to hold) the collector can never move or reclaim.
        [StructLayout(LayoutKind.Sequential)]
        private struct Callbacks
        {
            public IntPtr user;
            public delegate* unmanaged[Cdecl]<IntPtr, uint, byte> read8;
            public delegate* unmanaged[Cdecl]<IntPtr, uint, ushort> read16;
            public delegate* unmanaged[Cdecl]<IntPtr, uint, byte, void> write8;
            public delegate* unmanaged[Cdecl]<IntPtr, uint, ushort, void> write16;
            public delegate* unmanaged[Cdecl]<IntPtr, int, void> sync;                      // may be null
            public delegate* unmanaged[Cdecl]<IntPtr, byte, ushort> readIrqUserVector;      // may be null
        }

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

            // Breakpoints. The natives return C++ bool (1 byte): without MarshalAs(I1) the
            // default marshaller would read 4 bytes (Win32 BOOL) and pick up garbage.
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void moira_bp_setAt(IntPtr h, uint addr);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void moira_bp_removeAt(IntPtr h, uint addr);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            [return: MarshalAs(UnmanagedType.I1)]
            internal static extern bool moira_bp_isSetAt(IntPtr h, uint addr);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern long moira_bp_count(IntPtr h);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void moira_bp_removeAll(IntPtr h);

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            [return: MarshalAs(UnmanagedType.I1)]
            internal static extern bool moira_bp_wasHit(IntPtr h);

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
