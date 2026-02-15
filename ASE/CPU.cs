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
            switch (level)
            {
                case 2: // HBL
                    ASEMain._mfp.irqController.ClearHBL();
                    break;

                case 4: // VBL
                    ASEMain._mfp.irqController.ClearVBL();
                    break;

                case 6: // MFP
                    ushort vector = ASEMain._mfp.GetInterruptVector();
                    return vector;
            }

            return (ushort)(24 + level);
        }

        /// <summary>
        /// Initializes the CPU and all associated hardware components to a known state, preparing the system for
        /// operation.
        /// </summary>
        /// <remarks>Call this method before performing any CPU operations to ensure that memory and all
        /// hardware interfaces are properly set up and reset. This method must be invoked once during application
        /// startup or before reinitializing the emulated system.</remarks>
        public static void InitCpu()
        {
            ASEMain._mem = new Memory();

            if (ASEMain._mem.ROM == null)
                return;

            _moira = new Moira(
                ASEMain._mem.Read8,
                ASEMain._mem.Read16,
                ASEMain._mem.Write8,
                ASEMain._mem.Write16,
                null,
                IrqAck
                );

            ASEMain._mfp = new MFP68901();

            ACIA.Reset();
            WD1772.Reset();
            ASEMain._ym.Reset();

            _moira.Reset();
        }
    }
}
