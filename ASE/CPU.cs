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

        public class EmulatorBusErrorException : Exception { }

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
                    Program._mfp.irqController.ClearHBL();
                    return (ushort)(24 + level);

                case 4: // VBL
                    Program._mfp.irqController.ClearVBL();
                    return (ushort)(24 + level);

                case 6: // MFP
                    ushort vector = Program._mfp.GetInterruptVector();
                    return vector;
            }

            return (ushort)(24 + level);
        }

        /// <summary>
        /// Triggers a bus error at the specified address and transfers control to the bus error handler, simulating a
        /// 68K bus error exception.
        /// </summary>
        /// <remarks>
        /// It's necessary to pass the TOS > 1.00 boot process correctly since it performs
        /// some invalid memory accesses to detect the blitter presence. See Memory.cs
        /// If the bus error handler is not initialized, the error is ignored in debug mode and a
        /// message is logged. The method prepares the exception frame on the stack before transferring control to the
        /// handler.</remarks>
        /// <param name="faultAddress">The memory address that caused the bus error.</param>
        /// <param name="isRead">A value indicating whether the bus error was caused by a read operation. Specify <see langword="true"/> for
        /// a read access; otherwise, <see langword="false"/> for a write access.</param>
        /// <exception cref="EmulatorBusErrorException">Thrown to indicate that a bus error has occurred and control has been transferred to the bus error handler.</exception>
        public static void TriggerBusError(uint faultAddress, bool isRead)
        {
            // Gets bus error handler address from vector table
            uint handler = Program._mem.Read32(8);
            if (handler == 0)
            {
                if (ConfigOptions.RunninConfig.DebugMode)
                    Console.WriteLine($"Bus Error ignored -> {faultAddress:X8} (Vector 2 not initialized)");
                
                return;
            }

            if(ConfigOptions.RunninConfig.DebugMode)
                Console.WriteLine($"Bus Error! PC={_moira.PC:X6} Addr={faultAddress:X8}");

            // 68K Bus Error Exception Frame

            ushort currentSR = _moira.SR;
            ushort currentPC_High = (ushort)(_moira.PC >> 16);
            ushort currentPC_Low = (ushort)(_moira.PC & 0xFFFF);

            ushort opcode = Program._mem.Read16(_moira.PC);

            _moira.SetSupervisorMode(true);

            // Adjust stack to store exception frame
            uint sp = _moira.SP; // A7 / SSP
            sp -= 14;
            _moira.SP = sp;

            // Word 0: Function Codes / Access info (R/W bit)
            ushort specialStatus = (ushort)(isRead ? 0x0010 : 0x0000);
            BigEndian.Write16(sp + 0, specialStatus);

            // Word 1-2: Access Address
            BigEndian.Write16(sp + 2, (ushort)(faultAddress >> 16));
            BigEndian.Write16(sp + 4, (ushort)(faultAddress & 0xFFFF));

            // Word 3: Instruction Register
            BigEndian.Write16(sp + 6, opcode);

            // Word 4: Status Register
            BigEndian.Write16(sp + 8, currentSR);

            // Word 5-6: Program Counter
            BigEndian.Write16(sp + 10, currentPC_High);
            BigEndian.Write16(sp + 12, currentPC_Low);

            // jump to bus error handler
            _moira.PC = handler;

            throw new EmulatorBusErrorException();
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
            Program._mem = new Memory();

            _moira = new Moira(
                Program._mem.Read8,
                Program._mem.Read16,
                Program._mem.Write8,
                Program._mem.Write16,
                null,
                IrqAck
                );

            Program._mfp = new MFP68901();

            ACIA.Reset();
            WD1772.Reset();
            Program._ym.Reset();

            _moira.Reset();
        }
    }
}
