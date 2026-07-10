/*
 *
 * Atari STE DMA sound (GST Shifter DMA audio) + Microwire/LMC1992 emulation.
 *
 * Registers mapped at $FF8900-$FF8925:
 *
 *   $FF8901 (byte) : Sound DMA control (bit 0 = play, bit 1 = loop)
 *   $FF8903/05/07  : Frame start address (hi/mid/lo)
 *   $FF8909/0B/0D  : Frame address counter (hi/mid/lo, read only)
 *   $FF890F/11/13  : Frame end address (hi/mid/lo)
 *   $FF8921 (byte) : Sound mode (bit 7 = mono, bits 0-1 = sample rate)
 *   $FF8922 (word) : Microwire data register
 *   $FF8924 (word) : Microwire mask register
 *
 * The XSINT signal (high while a frame is playing) is wired to MFP GPIP7 and
 * to the Timer A event input, so programs can get an interrupt at end of frame.
 *
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

namespace ASE
{
    public static class STEDmaSound
    {
        // Sample rates selected by bits 0-1 of the sound mode register
        static readonly int[] SampleRates = { 6258, 12517, 25033, 50066 };

        // Control / mode registers
        static byte control;            // $FF8901: bit 0 = play, bit 1 = loop
        static byte soundMode;          // $FF8921: bit 7 = mono, bits 0-1 = rate

        // Frame start/end as last written by the CPU (latched into the counter on frame start)
        static uint frameStartReg;
        static uint frameEndReg;

        // Active frame
        static bool playing;
        static uint frameCounter;       // current DMA address
        static uint frameEnd;           // latched end address
        static double phase;            // fractional sample accumulator (250 kHz steps)

        // Latest decoded sample, exposed to the YM2149 mixer
        static float sampleLeft;
        static float sampleRight;

        // XSINT line state (high = playing); wired to MFP GPIP7 / Timer A
        static bool xsint;

        // Microwire / LMC1992
        static ushort mwData;
        static ushort mwMask;
        static int mwCyclesRemaining;   // a full transfer takes 16 shifts x 8 cycles = 128 cycles

        static int lmcMixing = 1;       // 01 = DMA sound + YM2149
        static float lmcMasterGain = 1.0f;
        static float lmcLeftGain = 1.0f;
        static float lmcRightGain = 1.0f;

        public static void Reset()
        {
            control = 0;
            soundMode = 0;
            frameStartReg = 0;
            frameEndReg = 0;
            playing = false;
            frameCounter = 0;
            frameEnd = 0;
            phase = 0;
            sampleLeft = 0;
            sampleRight = 0;
            mwCyclesRemaining = 0;

            // The Microwire/LMC1992 has no reset signal on the real hardware, but starting
            // from a sane audible state avoids muted sound on cold boot.
            mwData = 0;
            mwMask = 0;
            lmcMixing = 1;
            lmcMasterGain = 1.0f;
            lmcLeftGain = 1.0f;
            lmcRightGain = 1.0f;

            SetXsint(false);
        }

        // ==================== Snapshot ====================

        public static void SaveState(Snapshot.Writer w)
        {
            w.U8(control);
            w.U8(soundMode);
            w.Bool(playing);
            w.Bool(xsint);
            w.U32(frameStartReg);
            w.U32(frameEndReg);
            w.U32(frameCounter);
            w.U32(frameEnd);
            w.F64(phase);
            w.F32(sampleLeft);
            w.F32(sampleRight);
            w.U16(mwData);
            w.U16(mwMask);
            w.I32(mwCyclesRemaining);
            w.I32(lmcMixing);
            w.F32(lmcMasterGain);
            w.F32(lmcLeftGain);
            w.F32(lmcRightGain);
        }

        public static void LoadState(Snapshot.Reader r)
        {
            control = r.U8();
            soundMode = r.U8();
            playing = r.Bool();
            // xsint se restaura directamente, sin pasar por SetXsint: el estado del MFP
            // (GPIP7, Timer A) viene ya restaurado de su propia sección
            xsint = r.Bool();
            frameStartReg = r.U32();
            frameEndReg = r.U32();
            frameCounter = r.U32();
            frameEnd = r.U32();
            phase = r.F64();
            sampleLeft = r.F32();
            sampleRight = r.F32();
            mwData = r.U16();
            mwMask = r.U16();
            mwCyclesRemaining = r.I32();
            lmcMixing = r.I32();
            lmcMasterGain = r.F32();
            lmcLeftGain = r.F32();
            lmcRightGain = r.F32();
        }

        /// <summary>
        /// Mono mix of the current DMA sample (-1..1), already scaled by the LMC1992 volumes.
        /// Consumed by the YM2149 mixer when generating output samples.
        /// </summary>
        public static float CurrentSample
        {
            get
            {
                if (!playing)
                    return 0f;

                return (sampleLeft * lmcLeftGain + sampleRight * lmcRightGain) * 0.5f * lmcMasterGain;
            }
        }

        /// <summary>
        /// Gain to apply to the YM2149 output according to the LMC1992 mixing setting:
        /// 0 = DMA + YM2149 attenuated -12dB, 1 = DMA + YM2149, 2 = DMA only (YM muted),
        /// 3 = reserved (treated as 0, like Hatari does).
        /// </summary>
        public static float YmMixGain => lmcMixing switch
        {
            1 => 1.0f,
            2 => 0.0f,
            _ => 0.25f,     // -12dB
        };

        public static byte ReadByte(uint addr)
        {
            switch (addr)
            {
                case 0xFF8900: return 0;
                case 0xFF8901: return (byte)(control & 0x03);

                case 0xFF8903: return (byte)((frameStartReg >> 16) & 0x3F);
                case 0xFF8905: return (byte)(frameStartReg >> 8);
                case 0xFF8907: return (byte)(frameStartReg & 0xFE);

                case 0xFF8909: return (byte)((frameCounter >> 16) & 0x3F);
                case 0xFF890B: return (byte)(frameCounter >> 8);
                case 0xFF890D: return (byte)(frameCounter & 0xFE);

                case 0xFF890F: return (byte)((frameEndReg >> 16) & 0x3F);
                case 0xFF8911: return (byte)(frameEndReg >> 8);
                case 0xFF8913: return (byte)(frameEndReg & 0xFE);

                case 0xFF8920: return 0;
                case 0xFF8921: return soundMode;

                case 0xFF8922: return (byte)(MwVisibleData() >> 8);
                case 0xFF8923: return (byte)(MwVisibleData() & 0xFF);
                case 0xFF8924: return (byte)(MwVisibleMask() >> 8);
                case 0xFF8925: return (byte)(MwVisibleMask() & 0xFF);

                default:
                    return 0xFF;    // unused addresses inside the region, no bus error
            }
        }

        public static void WriteByte(uint addr, byte v)
        {
            switch (addr)
            {
                case 0xFF8901:
                    SetControl(v);
                    break;

                case 0xFF8903: frameStartReg = (frameStartReg & 0x00FFFF) | (((uint)v & 0x3F) << 16); break;
                case 0xFF8905: frameStartReg = (frameStartReg & 0x3F00FF) | ((uint)v << 8); break;
                case 0xFF8907: frameStartReg = (frameStartReg & 0x3FFF00) | ((uint)v & 0xFE); break;

                case 0xFF890F: frameEndReg = (frameEndReg & 0x00FFFF) | (((uint)v & 0x3F) << 16); break;
                case 0xFF8911: frameEndReg = (frameEndReg & 0x3F00FF) | ((uint)v << 8); break;
                case 0xFF8913: frameEndReg = (frameEndReg & 0x3FFF00) | ((uint)v & 0xFE); break;

                case 0xFF8921:
                    soundMode = (byte)(v & 0x83);
                    break;

                // Microwire data: a write to the low byte starts the transfer
                // (a 68000 word write stores high byte first, then low byte)
                case 0xFF8922:
                    if (mwCyclesRemaining <= 0)
                        mwData = (ushort)((mwData & 0x00FF) | (v << 8));
                    break;
                case 0xFF8923:
                    if (mwCyclesRemaining <= 0)
                    {
                        mwData = (ushort)((mwData & 0xFF00) | v);
                        mwCyclesRemaining = 16 * 8;     // 16 shifts, 8 CPU cycles each
                    }
                    break;

                // Microwire mask: only updated when no transfer is in progress
                case 0xFF8924:
                    if (mwCyclesRemaining <= 0)
                        mwMask = (ushort)((mwMask & 0x00FF) | (v << 8));
                    break;
                case 0xFF8925:
                    if (mwCyclesRemaining <= 0)
                        mwMask = (ushort)((mwMask & 0xFF00) | v);
                    break;
            }
        }

        static void SetControl(byte v)
        {
            byte old = control;
            control = (byte)(v & 0x03);

            if ((old & 0x01) == 0 && (control & 0x01) != 0)
            {
                // play 0 -> 1: start a new frame
                phase = 0;
                StartNewFrame();
            }
            else if ((old & 0x01) != 0 && (control & 0x01) == 0)
            {
                // play 1 -> 0: stop immediately
                playing = false;
                SetXsint(false);
            }
        }

        static void StartNewFrame()
        {
            frameCounter = frameStartReg;
            frameEnd = frameEndReg;

            // As verified on real STE: if start == end and repeat is off, DMA sound is
            // turned off immediately and no end-of-frame interrupt is generated.
            if (frameCounter == frameEnd && (control & 0x02) == 0)
            {
                control &= 0xFE;
                playing = false;
                SetXsint(false);
                return;
            }

            playing = true;
            SetXsint(true);
        }

        /// <summary>
        /// Advances the DMA sound engine by one 250 kHz step. Called from the YM2149
        /// synthesis loop so DMA samples line up with the PSG output.
        /// </summary>
        public static void Step250k()
        {
            if (!playing)
                return;

            phase += SampleRates[soundMode & 0x03] / 250000.0;

            while (phase >= 1.0 && playing)
            {
                phase -= 1.0;
                FetchNextSample();
            }
        }

        static void FetchNextSample()
        {
            bool mono = (soundMode & 0x80) != 0;

            if (mono)
            {
                sampleLeft = sampleRight = (sbyte)ASEMain._mem.Read8(frameCounter) / 128.0f;
                frameCounter += 1;
            }
            else
            {
                sampleLeft = (sbyte)ASEMain._mem.Read8(frameCounter) / 128.0f;
                sampleRight = (sbyte)ASEMain._mem.Read8(frameCounter + 1) / 128.0f;
                frameCounter += 2;
            }

            if (frameCounter >= frameEnd)
            {
                // End of frame: lower XSINT (triggers GPIP7/Timer A) and loop or stop
                SetXsint(false);

                if ((control & 0x02) != 0)
                    StartNewFrame();
                else
                {
                    control &= 0xFE;
                    playing = false;
                }
            }
        }

        static void SetXsint(bool high)
        {
            if (xsint == high)
                return;

            xsint = high;

            if (ASEMain._mfp == null)
                return;

            // GPIP7 = monochrome detect (1 on a colour system) XOR XSINT
            ASEMain._mfp.SetGPIOBit(7, !high);

            // Timer A event count input is also driven by XSINT (counts end of frames)
            if (!high)
                ASEMain._mfp.TickTimerA_EventCount();
        }

        /// <summary>
        /// Advances the Microwire transfer by the given amount of CPU cycles.
        /// Called from the emulation main loop.
        /// </summary>
        public static void Tick(int cpuCycles)
        {
            if (mwCyclesRemaining <= 0)
                return;

            mwCyclesRemaining -= cpuCycles;

            if (mwCyclesRemaining <= 0)
            {
                mwCyclesRemaining = 0;
                DecodeMicrowireCommand();
                mwData = 0;     // fully shifted out, reads back as 0 (the mask is back to its original value)
            }
        }

        // While a transfer is in progress the data register is shifted left and the mask
        // register is rotated left, 1 step every 8 cycles. After the 16 steps the mask is
        // back to its original value and the data register reads 0.
        static int MwShiftsDone()
        {
            if (mwCyclesRemaining <= 0)
                return 0;       // idle: registers show their written values (data ends up 0 after a transfer, but TOS only polls the mask)

            return 16 - ((mwCyclesRemaining + 7) / 8);
        }

        static ushort MwVisibleData()
        {
            int shifts = MwShiftsDone();
            return shifts == 0 ? mwData : (ushort)(mwData << shifts);
        }

        static ushort MwVisibleMask()
        {
            int shifts = MwShiftsDone();
            if (shifts == 0)
                return mwMask;

            return (ushort)((mwMask << shifts) | (mwMask >> (16 - shifts)));
        }

        /// <summary>
        /// Decodes the LMC1992 command once the 16 shifts of a transfer are done.
        /// The command starts at the first '1' bit of the mask and uses the data bits
        /// where the mask is '1'. Valid commands are at least 11 bits and start with
        /// the chip address '10'.
        /// </summary>
        static void DecodeMicrowireCommand()
        {
            int cmd = 0;
            int cmdLen = 0;

            for (int i = 15; i >= 0; i--)
            {
                if ((mwMask & (1 << i)) == 0)
                    continue;

                // Start of command found: collect bits while mask bits are '1'
                cmd = 0;
                cmdLen = 0;
                do
                {
                    cmd <<= 1;
                    cmdLen++;
                    if ((mwData & (1 << i)) != 0)
                        cmd |= 1;
                    i--;
                }
                while (i >= 0 && (mwMask & (1 << i)) != 0);

                if (cmdLen >= 11 && ((cmd >> (cmdLen - 2)) & 0x03) == 0x02)
                    break;      // valid command found

                cmd = 0;
                cmdLen = 0;
            }

            if (cmdLen < 11)
                return;

            switch ((cmd >> 6) & 0x07)
            {
                case 0: // Mixing
                    lmcMixing = cmd & 0x03;
                    break;

                case 1: // Bass (not filtered in this emulator)
                case 2: // Treble (not filtered in this emulator)
                    break;

                case 3: // Master volume: -80 dB (0) .. 0 dB (>= 40), 2 dB per step
                    lmcMasterGain = DbToGain(((cmd & 0x3F) - 40) * 2);
                    break;

                case 4: // Right channel volume: -40 dB (0) .. 0 dB (>= 20), 2 dB per step
                    lmcRightGain = DbToGain(((cmd & 0x1F) - 20) * 2);
                    break;

                case 5: // Left channel volume
                    lmcLeftGain = DbToGain(((cmd & 0x1F) - 20) * 2);
                    break;
            }
        }

        static float DbToGain(int db)
        {
            if (db >= 0)
                return 1.0f;

            return (float)Math.Pow(10.0, db / 20.0);
        }
    }
}
