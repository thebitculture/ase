/*
 * 
 * Functions to emulate the Motorola MFP 68901 chip.
 * 
 * This file is a total mess! It contains code snippets ported directly from the MS-DOS version of ASE,
 * as well as others rewritten based on bits of documentation I found along the way.
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
            public const byte ACIA = 0x40;      // GPIP 4 (Joystick/Kbd)
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
            GPIP = 0xFF; // Inputs por defecto a pull-up
            VR = 0x40;   // Vector base 64 ($40)

            // Limpiar registros de interrupción
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

            // Interrupciones candidatas
            ushort active = (ushort)(pending & enabled & masked);

            if (active == 0) return false;

            int highestActiveBit = GetHighestBitSet(active);
            int highestServiceBit = GetHighestBitSet(service);

            // Solo interrumpimos si la prioridad es mayor que la que se está sirviendo
            if (highestActiveBit > highestServiceBit)
            {
                return true;
            }

            return false;
        }

        // Método auxiliar rápido para obtener el bit más alto
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
                // Solo si el bit correspondiente en IERB está activo
                if ((IERB & interruptBit) != 0)
                {
                    IPRB |= interruptBit;
                    UpdateIRQ();
                }
            }
            else
            {
                // Solo si el bit correspondiente en IERA está activo
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

        // Llamado por la CPU cuando acepta la interrupción (Ciclo IACK)
        public ushort GetInterruptVector()
        {
            ushort pending = (ushort)((IPRA << 8) | IPRB);
            ushort enabled = (ushort)((IERA << 8) | IERB);
            ushort masked = (ushort)((IMRA << 8) | IMRB);
            ushort service = (ushort)((ISRA << 8) | ISRB);

            ushort active = (ushort)(pending & enabled & masked);
            int highestServiceBit = GetHighestBitSet(service);

            // Buscar la ganadora (Mayor que la que está en servicio)
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

        public void SetGPIOBit(int bit, bool active)
        {
            // GPIP bit logic: 0 = Input active (Low), 1 = Inactive (High) usually?
            // Pero en emulación simplificada: active=true -> señal activa.
            // El bit 4 (ACIA) suele ser Active LOW en hardware real.

            bool oldValue = (GPIP & (1 << bit)) != 0;
            bool newValue = active; // Si active es true, ponemos el bit a 1

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

                // (Se podrían añadir el resto de bits si se emularan)
            }
        }


    }
}
