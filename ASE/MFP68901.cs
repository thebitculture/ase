/*
 * 
 * Functions to emulate the Motorola MFP 68901 chip.
 * 
 * This is one of the more important chips in the Atari ST, as it handles timers, interrupts and GPIO, and
 * should be correctly emulated for better compatibility.
 * 
 * References:
 * http://www.bitsavers.org/components/motorola/68000/MC68901_Multi-Function_Peripheral_Jan84.pdf
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
            public const byte ACIA = 0x40;      // GPIP 4 (Joystick/Kbd)
            public const byte TimerC = 0x20;
            public const byte TimerD = 0x10;
            public const byte Blitter = 0x08;
            public const byte GPIP2 = 0x04;
            public const byte GPIP1 = 0x02;
            public const byte GPIP0 = 0x01;
        }

        public InterruptController irqController;

        // In MFP68901
        const int CPU_HZ = 8000000;      // ST
        const int MFP_HZ = 2457600;      // MFP clock

        long mfpAcc = 0; // accumulator in Hz*cycles
        long _lastUpdateClock = 0; // CPU clock through which the timers have been advanced (for live counter reads)

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
            _lastUpdateClock = 0;
            AER = 0x00;
            GPIP = 0xFF; // Inputs default to pull-up
            VR = 0x40;   // Vector base 64 ($40)

            // Clear interrupt registers
            IERA = IERB = 0;
            IPRA = IPRB = 0;
            ISRA = ISRB = 0;
            IMRA = IMRB = 0;

            irqController.ClearMFP();
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

        public bool CheckPendingInterrupts()
        {
            ushort pending = (ushort)((IPRA << 8) | IPRB);
            ushort enabled = (ushort)((IERA << 8) | IERB);
            ushort masked = (ushort)((IMRA << 8) | IMRB);
            ushort service = (ushort)((ISRA << 8) | ISRB);

            // Candidate interrupts
            ushort active = (ushort)(pending & enabled & masked);

            if (active == 0) return false;

            int highestActiveBit = GetHighestBitSet(active);
            int highestServiceBit = GetHighestBitSet(service);

            // Only interrupt if the priority is higher than the one currently in service
            if (highestActiveBit > highestServiceBit)
            {
                return true;
            }

            return false;
        }

        // Fast helper to get the highest set bit
        private int GetHighestBitSet(ushort v)
        {
            if (v == 0) return -1;
            int bit = 15;
            ushort mask = 0x8000;
            while ((v & mask) == 0)
            {
                mask >>= 1;
                bit--;
            }
            return bit;
        }

        // Mark an interrupt as pending
        public void SetInterruptPending(byte interruptBit, bool isRegisterB = false)
        {
            if (isRegisterB)
            {
                // Only if the corresponding bit in IERB is enabled
                if ((IERB & interruptBit) != 0)
                {
                    IPRB |= interruptBit;
                    UpdateIRQ();
                }
            }
            else
            {
                // Only if the corresponding bit in IERA is enabled
                if ((IERA & interruptBit) != 0)
                {
                    IPRA |= interruptBit;
                    UpdateIRQ();
                }
            }
        }

        public void UpdateIRQ()
        {
            if (CheckPendingInterrupts())
                irqController.RaiseMFP();
            else
                irqController.ClearMFP();
        }

        // Called by the CPU when it acknowledges the interrupt (IACK cycle)
        public ushort GetInterruptVector()
        {
            ushort pending = (ushort)((IPRA << 8) | IPRB);
            ushort enabled = (ushort)((IERA << 8) | IERB);
            ushort masked = (ushort)((IMRA << 8) | IMRB);
            ushort service = (ushort)((ISRA << 8) | ISRB);

            ushort active = (ushort)(pending & enabled & masked);
            int highestServiceBit = GetHighestBitSet(service);

            // Find the winner (higher than the one currently in service)
            int bit = -1;
            for (int i = 15; i > highestServiceBit; i--)
            {
                if ((active & (1 << i)) != 0)
                {
                    bit = i;
                    break;
                }
            }

            if (bit != -1)
            {
                ushort bitMask = (ushort)(1 << bit);

                // Limpiar Pendiente
                if (bit >= 8) IPRA &= (byte)~(bitMask >> 8);
                else IPRB &= (byte)~bitMask;

                // Gestionar In-Service
                if (SoftwareEOI)
                {
                    if (bit >= 8) ISRA |= (byte)(bitMask >> 8);
                    else ISRB |= (byte)bitMask;
                }

                UpdateIRQ();

                // Vector = Base + Canal (0-15)
                return (ushort)((VR & 0xF0) | bit);
            }

            // Spurious Interrupt
            return 0x18;
        }

        public void TickTimerA_EventCount()
        {
            if ((TACR & 0x0F) == 0x08)
            {
                timerACounter--;
                if (timerACounter <= 0)
                {
                    timerACounter = Reload(TADR);
                    SetInterruptPending(RegA.TimerA, false);
                }
            }
        }

        public void TickTimerB_EventCount()
        {
            if ((TBCR & 0x0F) == 0x08)
            {
                timerBCounter--;
                if (timerBCounter <= 0)
                {
                    timerBCounter = Reload(TBDR);
                    SetInterruptPending(RegA.TimerB, false);
                }
            }
        }

        public void UpdateTimers(int cpuCycles)
        {
            // The timers are now accurate up to the current CPU clock; remember it so timer
            // data-register reads can be projected forward to the exact moment of the read.
            _lastUpdateClock = CPU._moira.Clock;

            mfpAcc += (long)cpuCycles * MFP_HZ;
            int mfpTicks = (int)(mfpAcc / CPU_HZ);
            mfpAcc %= CPU_HZ;

            if (mfpTicks <= 0)
                return;

            UpdateTimerA(mfpTicks);
            UpdateTimerB(mfpTicks);
            UpdateTimerC(mfpTicks);
            UpdateTimerD(mfpTicks);
        }

        void UpdateTimerA(int mfpTicks)
        {
            int mode = TACR & 0x0F;
            if (mode == 0 || mode > 7) return;

            int div = GetPrescaler(mode);
            timerAPredivAcc += mfpTicks;
            int dec = timerAPredivAcc / div;
            timerAPredivAcc %= div;

            if (dec > 0)
            {
                timerACounter -= dec;
                while (timerACounter <= 0)
                {
                    timerACounter += Reload(TADR);
                    SetInterruptPending(RegA.TimerA, false);
                }
            }
        }

        void UpdateTimerB(int mfpTicks)
        {
            int mode = TBCR & 0x0F;
            if (mode == 0 || mode > 7) return;

            int div = GetPrescaler(mode);
            timerBPredivAcc += mfpTicks;
            int dec = timerBPredivAcc / div;
            timerBPredivAcc %= div;

            if (dec > 0)
            {
                timerBCounter -= dec;
                while (timerBCounter <= 0)
                {
                    timerBCounter += Reload(TBDR);
                    SetInterruptPending(RegA.TimerB, false);
                }
            }
        }

        void UpdateTimerC(int mfpTicks)
        {
            int mode = (TCDCR >> 4) & 0x07;
            if (mode == 0) { timerCPredivAcc = 0; return; }

            int div = GetPrescaler(mode);
            timerCPredivAcc += mfpTicks;
            int dec = timerCPredivAcc / div;
            timerCPredivAcc %= div;

            if (dec > 0)
            {
                timerCCounter -= dec;
                while (timerCCounter <= 0)
                {
                    timerCCounter += Reload(TCDR);
                    SetInterruptPending(RegB.TimerC, true);
                }
            }
        }

        void UpdateTimerD(int mfpTicks)
        {
            int mode = TCDCR & 0x07;
            if (mode == 0) { timerDPredivAcc = 0; return; }

            int div = GetPrescaler(mode);
            timerDPredivAcc += mfpTicks;
            int dec = timerDPredivAcc / div;
            timerDPredivAcc %= div;

            if (dec > 0)
            {
                timerDCounter -= dec;
                while (timerDCounter <= 0)
                {
                    timerDCounter += Reload(TDDR);
                    SetInterruptPending(RegB.TimerD, true);
                }
            }
        }

        private int GetPrescaler(int mode)
        {
            switch (mode)
            {
                case 1: return 4;
                case 2: return 10;
                case 3: return 16;
                case 4: return 50;
                case 5: return 64;
                case 6: return 100;
                case 7: return 200;
                default: return 1;
            }
        }

        /// <summary>
        /// Returns a timer's data register (its down-counter) projected to the *current* CPU
        /// clock. The counters are only stepped at the slice boundaries (see
        /// <c>ASEMain.RunCpuSliced</c>), so a tight loop polling $FFFA1F/21/23/25 for an exact
        /// value would otherwise read a frozen value and could miss the value it waits for.
        /// This advances the latched counter by the cycles elapsed since the last update without
        /// mutating any state — exactly how the live Video Address Pointer is computed on read.
        /// </summary>
        /// <param name="timer">0 = Timer A, 1 = Timer B, 2 = Timer C, 3 = Timer D.</param>
        public byte ReadTimerCounter(int timer)
        {
            int mode, counter, prediv;
            byte dr;
            switch (timer)
            {
                case 0:  mode = TACR & 0x0F;         counter = timerACounter; prediv = timerAPredivAcc; dr = TADR; break;
                case 1:  mode = TBCR & 0x0F;         counter = timerBCounter; prediv = timerBPredivAcc; dr = TBDR; break;
                case 2:  mode = (TCDCR >> 4) & 0x07; counter = timerCCounter; prediv = timerCPredivAcc; dr = TCDR; break;
                default: mode = TCDCR & 0x07;        counter = timerDCounter; prediv = timerDPredivAcc; dr = TDDR; break;
            }

            // Only delay mode (1..7) free-runs from the MFP clock. Stopped (0), event-count (8)
            // and pulse-extension (9..15) modes are not driven from here, so return the latched
            // value unchanged.
            if (mode < 1 || mode > 7)
                return (byte)counter;

            long projDelta = CPU._moira.Clock - _lastUpdateClock;
            if (projDelta > 0)
            {
                int div = GetPrescaler(mode);
                long projAcc = mfpAcc + projDelta * MFP_HZ;
                int projTicks = (int)(projAcc / CPU_HZ);
                int dec = (prediv + projTicks) / div;
                if (dec > 0)
                {
                    counter -= dec;
                    int reload = Reload(dr);
                    while (counter <= 0) counter += reload;
                }
            }

            return (byte)counter;
        }

        // Snapshot

        public void SaveState(Snapshot.Writer w)
        {
            w.U8(GPIP); w.U8(AER); w.U8(DDR);
            w.U8(IERA); w.U8(IERB);
            w.U8(IPRA); w.U8(IPRB);
            w.U8(ISRA); w.U8(ISRB);
            w.U8(IMRA); w.U8(IMRB);
            w.U8(VR);
            w.U8(TACR); w.U8(TBCR); w.U8(TCDCR);
            w.U8(TADR); w.U8(TBDR); w.U8(TCDR); w.U8(TDDR);

            w.I32(timerACounter); w.I32(timerBCounter); w.I32(timerCCounter); w.I32(timerDCounter);
            w.I32(timerAPredivAcc); w.I32(timerBPredivAcc); w.I32(timerCPredivAcc); w.I32(timerDPredivAcc);

            w.I64(mfpAcc);
            w.I64(_lastUpdateClock);

            irqController.SaveState(w);
        }

        public void LoadState(Snapshot.Reader r)
        {
            GPIP = r.U8(); AER = r.U8(); DDR = r.U8();
            IERA = r.U8(); IERB = r.U8();
            IPRA = r.U8(); IPRB = r.U8();
            ISRA = r.U8(); ISRB = r.U8();
            IMRA = r.U8(); IMRB = r.U8();
            VR = r.U8();
            TACR = r.U8(); TBCR = r.U8(); TCDCR = r.U8();
            TADR = r.U8(); TBDR = r.U8(); TCDR = r.U8(); TDDR = r.U8();

            timerACounter = r.I32(); timerBCounter = r.I32(); timerCCounter = r.I32(); timerDCounter = r.I32();
            timerAPredivAcc = r.I32(); timerBPredivAcc = r.I32(); timerCPredivAcc = r.I32(); timerDPredivAcc = r.I32();

            mfpAcc = r.I64();
            _lastUpdateClock = r.I64();

            irqController.LoadState(r);
        }

        public void SetGPIOBit(int bit, bool active)
        {
            // GPIP bit logic: 0 = Input active (Low), 1 = Inactive (High) usually?
            // But in this simplified emulation: active=true -> signal asserted.
            // Bit 4 (ACIA) is usually active LOW on real hardware.

            bool oldValue = (GPIP & (1 << bit)) != 0;
            bool newValue = active; // If active is true, set the bit to 1

            if (newValue) GPIP |= (byte)(1 << bit);
            else GPIP &= (byte)~(1 << bit);

            // AER: 1 = Rising edge (0->1), 0 = Falling edge (1->0)
            bool triggerOnRising = (AER & (1 << bit)) != 0;
            bool interruptTriggered = false;

            if (triggerOnRising)
            {
                if (!oldValue && newValue) interruptTriggered = true;
            }
            else
            {
                if (oldValue && !newValue) interruptTriggered = true;
            }

            if (interruptTriggered)
            {
                // Bit 4 = ACIA (RegB Bit 6)
                if (bit == 4) SetInterruptPending(RegB.ACIA, true);
                // Bit 5 = FDC (RegB Bit 7)
                else if (bit == 5) SetInterruptPending(RegB.FDC, true);
                // Bit 6 = RS232 ring indicator (RegA Bit 6)
                else if (bit == 6) SetInterruptPending(RegA.GPIP6, false);
                // Bit 7 = Monochrome detect / STE DMA sound XSINT (RegA Bit 7)
                else if (bit == 7) SetInterruptPending(RegA.GPIP7, false);

                // (The remaining bits could be added if they were emulated)
            }
        }


    }
}
