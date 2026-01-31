/*
 * 
 * Functions to emulate the Motorola MFP 68901 chip.
 * 
 * This file is a total mess! It contains code snippets ported directly from the MS-DOS version of ASE,
 * as well as others rewritten based on bits of documentation I found along the way, or using Google AI Studio for help.
 * It needs a complete refactor and cleanup. I also need to review the documentation again, as there are still 
 * incompatibilities with some programs.
 * 
 * http://www.bitsavers.org/components/motorola/68000/MC68901_Multi-Function_Peripheral_Jan84.pdf
 * 
 * This is one of the more important chips in the Atari ST, as it handles timers, interrupts and GPIO, and
 * should be correctly emulated for better compatibility.
 * 
 * Official repository 👉 https://github.com/thebitculture/ase
 * 
 */

namespace ASE
{
    public class MFP68901
    {
        // MFP Registers (addresses at $FFFA00-$FFFA3F)
        public const int MFP_BASE = 0xFFFA00;

        // Interrupt registers
        public byte GPIP = 0x00;    // $FFFA01 - General Purpose I/O
        public byte AER = 0x00;     // $FFFA03 - Active Edge Register
        public byte DDR = 0x00;     // $FFFA05 - Data Direction Register

        public byte IERA = 0x00;    // $FFFA07 - Interrupt Enable Register A
        public byte IERB = 0x00;    // $FFFA09 - Interrupt Enable Register B
        public byte IPRA = 0x00;    // $FFFA0B - Interrupt Pending Register A
        public byte IPRB = 0x00;    // $FFFA0D - Interrupt Pending Register B
        public byte ISRA = 0x00;    // $FFFA0F - Interrupt In-Service Register A
        public byte ISRB = 0x00;    // $FFFA11 - Interrupt In-Service Register B
        public byte IMRA = 0x00;    // $FFFA13 - Interrupt Mask Register A
        public byte IMRB = 0x00;    // $FFFA15 - Interrupt Mask Register B

        public byte VR = 0x00;      // $FFFA17 - Vector Register (vector base)

        // Timer registers
        public byte TACR = 0x00;    // $FFFA19 - Timer A Control
        public byte TBCR = 0x00;    // $FFFA1B - Timer B Control
        public byte TCDCR = 0x00;   // $FFFA1D - Timer C/D Control
        public byte TADR = 0x00;    // $FFFA1F - Timer A Data
        public byte TBDR = 0x00;    // $FFFA21 - Timer B Data
        public byte TCDR = 0x00;    // $FFFA23 - Timer C Data
        public byte TDDR = 0x00;    // $FFFA25 - Timer D Data

        // Internal timer counters
        public int timerACounter = 0;
        public int timerBCounter = 0;
        public int timerCCounter = 0;
        public int timerDCounter = 0;

        public int timerAPredivAcc = 0;
        int timerBPredivAcc = 0;
        int timerCPredivAcc = 0;
        int timerDPredivAcc = 0;

        public static class RegA
        {
            public const byte GPIP7 = 0x80;
            public const byte GPIP6 = 0x40;
            public const byte TimerA = 0x20;
            public const byte RX_Full = 0x10;
            public const byte RX_Error = 0x08;
            public const byte TX_Empty = 0x04;
            public const byte TX_Error = 0x02;
            public const byte TimerB = 0x01;
        }

        public static class RegB
        {
            public const byte FDC = 0x80;       // GPIP 5
            public const byte ACIA = 0x40;      // GPIP 4
            public const byte TimerC = 0x20;
            public const byte TimerD = 0x10;
            public const byte Blitter = 0x08;   // Not used on ASE.. by now
            public const byte GPIP2 = 0x04;
            public const byte GPIP1 = 0x02;
            public const byte GPIP0 = 0x01;
        }

        public InterruptController irqController;

        // In MFP68901
        const int CPU_HZ = 8000000;      // ST
        const int MFP_HZ = 2457600;      // MFP clock

        long mfpAcc = 0; // accumulator in "Hz*cycles"

        public bool SoftwareEOI => (VR & 0x08) != 0; // S bit
        int Reload(byte dr) => dr == 0 ? 256 : dr;

        public MFP68901()
        {
            irqController = new InterruptController();

            Reset();
        }

        public void Reset()
        {
            mfpAcc = 0;

            AER = 0x00;
            GPIP = 0xFF;

            // Vector base by default.
            // The 68k seeks from this pointer to find the interrupt vector
            VR = 0x40;
        }

        public bool HasActiveInterrupts()
        {
            byte activeA = (byte)(IPRA & IERA & IMRA);
            byte activeB = (byte)(IPRB & IERB & IMRB);

            if (SoftwareEOI)
            {
                activeA &= (byte)~ISRA;
                activeB &= (byte)~ISRB;
            }

            return (activeA != 0) || (activeB != 0);
        }

        // Mark an interrupt as pending
        public void SetInterruptPending(byte interruptBit, bool isRegisterB = false)
        {
            if (isRegisterB)
            {
                IPRB |= interruptBit;
            }
            else
            {
                IPRA |= interruptBit;
            }

            UpdateIRQ();
        }

        // Update IRQ 6 state
        public void UpdateIRQ()
        {
            if (HasActiveInterrupts())
            {
                irqController.RaiseMFP();
            }
            else
            {
                irqController.ClearMFP();
            }
        }

        // Get interrupt vector (called from IRQ callback)
        public ushort GetInterruptVector()
        {
            // Find the highest priority interrupt
            byte activeA = (byte)(IPRA & IERA & IMRA & ~ISRA);
            byte activeB = (byte)(IPRB & IERB & IMRB & ~ISRB);

            int bit = -1;
            bool isRegB = false;

            // Priority: IPRA has higher priority than IPRB
            if (activeA != 0)
            {
                // Find the most significant bit (highest priority)
                for (int i = 7; i >= 0; i--)
                {
                    if ((activeA & (1 << i)) != 0)
                    {
                        bit = i;
                        isRegB = false;
                        break;
                    }
                }
            }
            else if (activeB != 0)
            {
                for (int i = 7; i >= 0; i--)
                {
                    if ((activeB & (1 << i)) != 0)
                    {
                        bit = i;
                        isRegB = true;
                        break;
                    }
                }
            }

            if (bit >= 0)
            {
                if (isRegB)
                {
                    if (SoftwareEOI) ISRB |= (byte)(1 << bit);
                    IPRB &= (byte)~(1 << bit);
                }
                else
                {
                    if (SoftwareEOI) ISRA |= (byte)(1 << bit);
                    IPRA &= (byte)~(1 << bit);
                }

                // Calculate the vector
                ushort vectorNumber = (ushort)((VR & 0xF0) | (isRegB ? (bit) : (bit + 8)));

                // Update IRQ after processing
                UpdateIRQ();

                return vectorNumber;
            }

            // Should not reach here
            return 0x18; // Spurious vector
        }

        public void TickTimerA_EventCount()
        {
            if ((TACR & 0x0F) == 0x08) // Event Count
            {
                timerACounter--;
                if (timerACounter <= 0)
                {
                    timerACounter = Reload(TADR);
                    SetInterruptPending(RegA.TimerA, false); // IPRA
                }
            }
        }

        public void TickTimerB_EventCount()
        {
            // Only if in Event Count mode (bit 3 active, mode 8)
            if ((TBCR & 0x0F) == 0x08)
            {
                timerBCounter--;
                if (timerBCounter <= 0)
                {
                    timerBCounter = Reload(TBDR);
                    SetInterruptPending(RegA.TimerB, false); // IPRA
                }
            }
        }

        public void UpdateTimers(int cpuCycles)
        {
            // accumulates "MFP ticks" (rational)
            mfpAcc += (long)cpuCycles * MFP_HZ;

            int mfpTicks = (int)(mfpAcc / CPU_HZ);   // how many real MFP ticks passed
            mfpAcc %= CPU_HZ;

            if (mfpTicks <= 0) return;

            UpdateTimerA(mfpTicks);
            UpdateTimerB(mfpTicks);
            UpdateTimerC(mfpTicks);
            UpdateTimerD(mfpTicks);
        }

        void UpdateTimerA(int mfpTicks)
        {
            int mode = TACR & 0x0F;
            if (mode == 0 || mode > 7) return; // Ignore Stopped or Event Count

            int div = GetTimerAPrescaler();
            if (div == 0) return;

            timerAPredivAcc += mfpTicks;
            int dec = timerAPredivAcc / div;
            timerAPredivAcc %= div;

            if (dec <= 0) return;

            timerACounter -= dec;
            while (timerACounter <= 0)
            {
                timerACounter += Reload(TADR);
                SetInterruptPending(RegA.TimerA, false);
            }
        }

        void UpdateTimerB(int mfpTicks)
        {
            int mode = TBCR & 0x0F;
            if (mode == 0 || mode > 7) return; // stopped or event count

            int div = GetTimerBPrescaler();   // 4,10,16,50,64,100,200...
            if (div == 0) return;

            timerBPredivAcc += mfpTicks;
            int dec = timerBPredivAcc / div;
            timerBPredivAcc %= div;
            if (dec <= 0) return;

            timerBCounter -= dec;
            while (timerBCounter <= 0)
            {
                timerBCounter += Reload(TBDR);
                SetInterruptPending(RegA.TimerB, false);
            }
        }

        void UpdateTimerC(int mfpTicks)
        {
            // MC68901: bits 6..4 = Timer C control
            int mode = (TCDCR >> 4) & 0x07;
            if (mode == 0) { timerCPredivAcc = 0; return; }

            int div = GetTimerCPrescaler();

            timerCPredivAcc += mfpTicks;
            int dec = timerCPredivAcc / div;
            timerCPredivAcc %= div;
            if (dec <= 0) return;

            timerCCounter -= dec;
            while (timerCCounter <= 0)
            {
                timerCCounter += Reload(TCDR);
                SetInterruptPending(RegB.TimerC, true);
            }
        }

        void UpdateTimerD(int mfpTicks)
        {
            // MC68901: bits 2..0 = Timer D control
            int mode = TCDCR & 0x07;
            if (mode == 0)
            {
                timerDPredivAcc = 0;
                return;
            }

            int div = GetTimerDPrescaler();

            timerDPredivAcc += mfpTicks;
            int dec = timerDPredivAcc / div;
            timerDPredivAcc %= div;
            if (dec <= 0) return;

            timerDCounter -= dec;
            while (timerDCounter <= 0)
            {
                timerDCounter += Reload(TDDR);
                SetInterruptPending(RegB.TimerD, true);
            }
        }

        private int GetTimerAPrescaler()
        {
            int mode = TACR & 0x0F;
            return mode switch
            {
                0 => 0,      // Stopped
                1 => 4,      // Delay mode, prescaler = 4
                2 => 10,     // Delay mode, prescaler = 10
                3 => 16,     // Delay mode, prescaler = 16
                4 => 50,     // Delay mode, prescaler = 50
                5 => 64,     // Delay mode, prescaler = 64
                6 => 100,    // Delay mode, prescaler = 100
                7 => 200,    // Delay mode, prescaler = 200
                _ => 0
            };
        }

        private int GetTimerBPrescaler()
        {
            int mode = TBCR & 0x0F;
            return mode switch
            {
                0 => 0,      // Stopped
                1 => 4,      // Delay mode, prescaler = 4
                2 => 10,     // Delay mode, prescaler = 10
                3 => 16,     // Delay mode, prescaler = 16
                4 => 50,     // Delay mode, prescaler = 50
                5 => 64,     // Delay mode, prescaler = 64
                6 => 100,    // Delay mode, prescaler = 100
                7 => 200,    // Delay mode, prescaler = 200
                _ => 0
            };
        }

        private int GetTimerCPrescaler()
        {
            int mode = (TCDCR >> 4) & 0x07;
            return mode switch
            {
                0 => 0,
                1 => 4,
                2 => 10,
                3 => 16,
                4 => 50,
                5 => 64,
                6 => 100,
                7 => 200,
                _ => 1
            };
        }

        private int GetTimerDPrescaler()
        {
            int mode = TCDCR & 0x07;
            return mode switch
            {
                0 => 0,
                1 => 4,
                2 => 10,
                3 => 16,
                4 => 50,
                5 => 64,
                6 => 100,
                7 => 200,
                _ => 1
            };
        }

        public void SetGPIOBit(int bit, bool active)
        {
            // Save previous state to detect edges
            bool oldValue = (GPIP & (1 << bit)) != 0;
            bool newValue = active;

            // Update the data register (GPIP) so polling works
            if (newValue)
                GPIP |= (byte)(1 << bit);
            else
                GPIP &= (byte)~(1 << bit);

            bool triggerOnRising = (AER & (1 << bit)) != 0;

            bool interruptTriggered = false;

            if (triggerOnRising)
            {
                // Detect rising edge (0 -> 1)
                if (!oldValue && newValue) interruptTriggered = true;
            }
            else
            {
                // Detect falling edge (1 -> 0)
                if (oldValue && !newValue) interruptTriggered = true;
            }

            if (interruptTriggered)
            {
                if (bit == 4) // ACIA IRQ -> GPIP4
                {
                    SetInterruptPending(RegB.ACIA, true);
                    TickTimerA_EventCount();
                }
                else if (bit == 5) // FDC/HDC
                {
                    SetInterruptPending(RegB.FDC, true);
                }
            }
        }
    }
}
