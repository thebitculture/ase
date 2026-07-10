/*
 * 
 * CPU related methods and classes
 * 
 * Official repository 👉 https://github.com/thebitculture/ase
 * 
 */

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
            _moira = new Moira(
                ASEMain._mem.CpuRead8,
                ASEMain._mem.CpuRead16,
                ASEMain._mem.CpuWrite8,
                ASEMain._mem.CpuWrite16,
                null,
                IrqAck
                );

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
