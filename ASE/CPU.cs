/*
 * 
 * CPU related methods and classes
 * 
 * Official repository 👉 https://github.com/thebitculture/ase
 * 
 */

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static ASE.Config;

namespace ASE
{
    /// <summary>
    /// Represents a Motorola 68K CPU emulator that manages interrupt requests, simulates bus errors, and initializes
    /// the CPU and related hardware components.
    /// </summary>
    /// <remarks>The CPU class provides core functionality for emulating the behavior of a 68K processor,
    /// including handling interrupt acknowledgments and simulating bus error exceptions. It also coordinates the
    /// initialization of the CPU state and associated subsystems such as memory, MFP, ACIA, WD1772, and YM. This class
    /// is essential for accurate emulation of system-level CPU interactions and exception handling.</remarks>
    public class CPU
    {
        public static Moira _moira;

        /// <summary>
        /// Get interrupt vector based on level
        /// </summary>
        /// <param name="level">Interrupt level</param>
        /// <returns></returns>
        static ushort IrqAck(byte level)
        {
            ushort vec;
            switch (level)
            {
                case 2: // HBL
                    ASEMain._mfp.irqController.ClearHBL();
                    vec = (ushort)(24 + level);
                    break;

                case 4: // VBL
                    ASEMain._mfp.irqController.ClearVBL();
                    vec = (ushort)(24 + level);
                    break;

                case 6: // MFP
                    vec = ASEMain._mfp.GetInterruptVector();
                    break;

                default:
                    vec = (ushort)(24 + level);
                    break;
            }

            return vec;
        }

        // ---- Bus callbacks handed to the native core ----
        //
        // These are [UnmanagedCallersOnly] static methods rather than delegates on purpose. A
        // delegate handed to native code is called through a runtime-generated marshalling stub,
        // and the old wiring stacked a second hop on top of it (native -> stub -> closure ->
        // Memory), so every single bus access paid for two indirections plus the stub. With the
        // 68000 prefetching continuously this is the hottest path in the emulator — roughly two
        // million accesses per emulated second — and the transition cost is most visible on slow
        // hosts (Raspberry Pi 4, low-end PCs).
        //
        // The wrappers exist because the native signature carries the unused 'user' pointer, and
        // because [UnmanagedCallersOnly] methods cannot be called from managed code — keeping the
        // real work in the plain methods leaves the rest of the emulator able to use them.
        //
        // An exception must never escape one of these into native code: it cannot be caught across
        // the boundary and terminates the process. Everything they reach (Memory bus accessors,
        // IrqAck) reports faults through Moira's bus-error mechanism instead of throwing.

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte BusRead8(IntPtr user, uint addr) => ASEMain._mem.CpuRead8(addr);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static ushort BusRead16(IntPtr user, uint addr) => ASEMain._mem.CpuRead16(addr);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void BusWrite8(IntPtr user, uint addr, byte v) => ASEMain._mem.CpuWrite8(addr, v);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void BusWrite16(IntPtr user, uint addr, ushort v) => ASEMain._mem.CpuWrite16(addr, v);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static ushort BusIrqAck(IntPtr user, byte level) => IrqAck(level);

        /// <summary>
        /// Initializes the CPU and all associated hardware components to a known state, preparing the system for
        /// operation.
        /// </summary>
        /// <remarks>Call this method before performing any CPU operations to ensure that memory and all
        /// hardware interfaces are properly set up and reset. This method must be invoked once during application
        /// startup or before reinitializing the emulated system. The emulation thread must NOT be
        /// running (see ASEMain.HardReset). Returns false when the TOS ROM is missing or invalid.</remarks>
        public static bool InitCpu()
        {
            ASEMain._mem = new Memory();

            if (ASEMain._mem.ROM == null)
                return false;

            // Wire the CPU bus through the timing wrappers (CpuRead*/CpuWrite*) so the ST memory
            // wait states are applied once per bus cycle. They fall through to the raw Read*/Write*
            // accessors, which the rest of the emulator keeps using directly (no wait states).
            // 'sync' is left null so Moira keeps its own default; peripherals are advanced from
            // ASEMain.RunCpuUntil instead.
            unsafe
            {
                _moira = new Moira(
                    &BusRead8,
                    &BusRead16,
                    &BusWrite8,
                    &BusWrite16,
                    null,
                    &BusIrqAck
                    );
            }

            ASEMain._mfp = new MFP68901();

            ACIA.Reset();
            WD1772.Reset();
            Blitter.Reset();
            STEDmaSound.Reset();
            VideoTiming.Reset();
            ASEMain._ym.Reset();

            _moira.Reset();
            return true;
        }
    }
}
