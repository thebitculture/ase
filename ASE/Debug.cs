using System.Text;

namespace ASE
{
    /// <summary>
    /// Provides static methods for inspecting and displaying the state of the CPU and memory during debugging sessions.
    /// </summary>
    /// <remarks>The Debug class includes utilities for dumping CPU register values, disassembling
    /// instructions at specific memory addresses, and outputting memory contents in a readable format. These methods
    /// are intended to assist developers in low-level debugging scenarios, such as emulator development or systems
    /// programming, where direct examination of CPU state and memory is required. All methods are static and designed
    /// for use in diagnostic or development environments.</remarks>
    public class Debug
    {
        public static void DumnpRegs()
        {
            for (int t = 0; t < 8; t++)
            {
                uint dn = CPU._moira.D[t];
                Console.Write($"D{t}={dn:X8}");
                if (t < 7)
                    Console.Write(" ");
            }

            Console.Write(Environment.NewLine);

            for (int t = 0; t < 8; t++)
            {
                uint an = CPU._moira.A[t];
                Console.Write($"A{t}={an:X8}");
                if (t < 7)
                    Console.Write(" ");
            }

            Console.Write(Environment.NewLine);

            Console.WriteLine("PC=" + CPU._moira.PC.ToString("X8") + 
                " SR=" + CPU._moira.SR.ToString("X8") +
                " SP=" + CPU._moira.SP.ToString("X8"));
        }

        public static void DisassembleRunningPC()
        {
            Console.Clear();

            Debug.DumnpRegs();
            Debug.DisassembleAt(CPU._moira.PC - 40, 20);
        }

        public static void DisassembleAt(uint addr, int instructions)
        {
            uint offset = 0;

            ConsoleColor precolor = Console.ForegroundColor;

            for (int i = 0; i < instructions; i++)
            {
                var lineAddr = addr + offset;
                var sb = new StringBuilder(250);
                var (disStr, disSize) = CPU._moira.Disassemble(lineAddr, 250);

                string data = "";
                for (uint x = 0 ; x < disSize; x+=2)
                    data += $"{Program._mem.Read16(lineAddr + x):X4} ";

                Console.ForegroundColor = (lineAddr == CPU._moira.PC) ? ConsoleColor.White : ConsoleColor.DarkGray;
                Console.WriteLine($"{lineAddr:X8} {data.PadRight(20)} {disStr}");

                offset += (uint)disSize;
            }

            Console.ForegroundColor = precolor;
        }

        public static void DumpMemory(uint startAddr, uint length)
        {
            Console.WriteLine($"Memory Dump from {startAddr:X8} to {startAddr + length - 1:X8}:");
            for (uint addr = startAddr; addr < startAddr + length; addr += 16)
            {
                StringBuilder line = new StringBuilder();
                line.AppendFormat("{0:X8}: ", addr);
                for (uint offset = 0; offset < 16 && (addr + offset) < (startAddr + length); offset++)
                {
                    byte value = Program._mem.Read8(addr + offset);
                    line.AppendFormat("{0:X2} ", value);
                }
                Console.WriteLine(line.ToString().TrimEnd());
            }
        }
    }
}
