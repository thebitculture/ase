/*
 * YM2149 Sound Chip Emulator for Atari ST
 *  
 * Some parts ported from Hatari emulator by Thomas Huth and others.
 * Adapted to work in C# with SDL2 for audio output and ASE project structure.
 *  
 * Official repository 👉 https://github.com/thebitculture/ase
 *  
 */

using System;
using System.Runtime.InteropServices;

namespace ASE
{
    public class YM2149
    {
        private const int YM_FREQ_INTERNAL = 250000;  // 2 MHz / 8 = 250 kHz (Counter update frequency)

        // Output configuration
        private readonly int _outputSampleRate;

        // Registers
        private byte[] _regs = new byte[16];
        // Register the address latch currently points at. This is a full 8-bit value, NOT a
        // 0-15 one: the YM2149's address register has eight bits and a program is free to load
        // any of them. Everything above 15 selects nothing, and reads and writes of the data
        // register must then do nothing at all. Masking it to 0-15 instead (as this did) turned
        // every such select into register 0 — the channel A period — so a replayer that parks
        // the latch out of range between notes, or one whose register table carries an
        // out-of-range entry as a no-op, silently rewrote the pitch of channel A.
        private int _selectedReg = 0;

        // What a read of $FF8800 answers with. It is not simply _regs[_selectedReg]: writing the
        // data register leaves the value that was written visible here UNMASKED, while selecting
        // a register latches its stored (masked) contents. Murders In Venice depends on it —
        // it writes $10 to register 3, whose top four bits the chip does not keep, and expects to
        // read $10 straight back.
        private byte _readData = 0xFF;

        // Internal counters
        private int _cntA, _perA;
        private int _cntB, _perB;
        private int _cntC, _perC;
        private int _cntNoise, _perNoise;
        private int _cntEnv, _perEnv;

        // Output states (Flip-Flops)
        // We use 0 and 1 to facilitate logic operations, then map to voltage
        private int _outA, _outB, _outC;
        private int _outNoise;

        // Noise generator
        private uint _rng;

        // Envelope (Hatari logic: 32-step blocks)
        private int _envShape;
        private int _envPos;      // Global position in the envelope (0..95)

        // Oversampling / Downsampling variables
        private uint _resamplePos;
        private uint _resampleStep;

        // Nominal resample step, and the audio flow control that trims it (see
        // UpdateAudioFlowControl). _queueDepth mirrors AudioQueue's length as an O(1) counter:
        // ConcurrentQueue.Count walks the segments, and this is read on the synthesis path.
        private readonly uint _resampleStepBase;
        private readonly int _queueTarget;
        private int _queueDepth;

        // Leftover CPU cycles not yet converted to 250 kHz ticks. Sync can now be called with any
        // cycle count (the main loop interleaves it with the CPU in fine slices that are not
        // necessarily multiples of 32), so the remainder must be carried over instead of dropped.
        private int _cycleRemainder;

        // Precalculated tables
        // 16 waveforms * 3 blocks * 32 steps
        private static byte[][] _envWaves;

        // ST non-linear volume curve (5 bits -> 16 bits amplitude)
        private static readonly ushort[] YmVolTable =
        {
            0,  369,  438,  521,  619,  735,  874, 1039,
            1234, 1467, 1744, 2072, 2463, 2927, 3479, 4135,
            4914, 5841, 6942, 8250, 9806,11654,13851,16462,
            19565,23253,27636,32845,39037,46395,55141,65535
        };

        // Volume conversion 4 bits -> 5 bits (ST Hardware)
        private static readonly byte[] Vol4to5 = { 0, 1, 5, 7, 9, 11, 13, 15, 17, 19, 21, 23, 25, 27, 29, 31 };

        // Envelope Shapes
        // 0=Down, 1=Up, 2=StayDown, 3=StayUp
        private const int ENV_GODOWN = 0;
        private const int ENV_GOUP = 1;
        private const int ENV_DOWN = 2;
        private const int ENV_UP = 3;

        private static readonly int[,] YmEnvDef = new int[16, 3] {
            { ENV_GODOWN, ENV_DOWN, ENV_DOWN } ,    /* 0 \___ */
            { ENV_GODOWN, ENV_DOWN, ENV_DOWN } ,    /* 1 \___ */
            { ENV_GODOWN, ENV_DOWN, ENV_DOWN } ,    /* 2 \___ */
            { ENV_GODOWN, ENV_DOWN, ENV_DOWN } ,    /* 3 \___ */
            { ENV_GOUP,   ENV_DOWN, ENV_DOWN } ,    /* 4 /___ */
            { ENV_GOUP,   ENV_DOWN, ENV_DOWN } ,    /* 5 /___ */
            { ENV_GOUP,   ENV_DOWN, ENV_DOWN } ,    /* 6 /___ */
            { ENV_GOUP,   ENV_DOWN, ENV_DOWN } ,    /* 7 /___ */
            { ENV_GODOWN, ENV_GODOWN, ENV_GODOWN }, /* 8 \\\\ */
            { ENV_GODOWN, ENV_DOWN, ENV_DOWN } ,    /* 9 \___ */
            { ENV_GODOWN, ENV_GOUP, ENV_GODOWN } ,  /* A \/\/ */
            { ENV_GODOWN, ENV_UP, ENV_UP } ,        /* B \--- */
            { ENV_GOUP,   ENV_GOUP, ENV_GOUP } ,    /* C //// */
            { ENV_GOUP,   ENV_UP, ENV_UP } ,        /* D /--- */
            { ENV_GOUP,   ENV_GODOWN, ENV_GOUP } ,  /* E /\/\ */
            { ENV_GOUP,   ENV_DOWN, ENV_DOWN }      /* F /___ */
        };

        // DC Filter. One per output channel: the two sides no longer carry the same
        // signal, and a shared filter state would leak each channel into the other.
        private float _lastSampleL = 0, _lastOutL = 0;
        private float _lastSampleR = 0, _lastOutR = 0;

        /// <summary>
        /// One output frame: the two interleaved channels the sound card is fed. Queuing the
        /// pair as a unit is what keeps the sides from ever drifting apart — a queue of loose
        /// floats drained an odd number of times would swap left and right for the rest of the
        /// session, and <see cref="UpdateAudioFlowControl"/> does drain it.
        /// </summary>
        public readonly struct StereoSample
        {
            public readonly float Left;
            public readonly float Right;

            public StereoSample(float left, float right)
            {
                Left = left;
                Right = right;
            }
        }

        // Thread-safe circular queue to pass audio to SDL. It counts FRAMES, not floats:
        // so do _queueDepth and _queueTarget, and the callback expands each entry into the
        // two interleaved floats the device expects.
        public System.Collections.Concurrent.ConcurrentQueue<StereoSample> AudioQueue
            = new System.Collections.Concurrent.ConcurrentQueue<StereoSample>();

        static YM2149()
        {
            BuildEnvelopeTables();
        }

        public YM2149(int sampleRate = 44100, double chipClockHz = 2000000.0)
        {
            _outputSampleRate = sampleRate;

            // Calculate resampling step.
            // We use 32-bit fixed point (16.16) for precision without floats in the critical loop
            // Ratio = 250000 / OutputRate
            // Multiplied by 65536 for fixed point.
            long ratio = ((long)YM_FREQ_INTERNAL << 16) / _outputSampleRate;
            _resampleStep = (uint)ratio;
            _resampleStepBase = (uint)ratio;

            // How much audio to keep queued ahead of the device: ~67 ms, three SDL buffers.
            // Deep enough that a frame the host makes us miss does not starve the callback,
            // shallow enough that the sound stays in step with the picture — and a long way
            // under the quarter second the old hard ceiling settled at.
            _queueTarget = Math.Max(3 * 1024, sampleRate / 15);

            Reset();
        }

        private static void BuildEnvelopeTables()
        {
            _envWaves = new byte[16][];
            for (int env = 0; env < 16; env++)
            {
                _envWaves[env] = new byte[32 * 3]; // 3 blocks of 32
                for (int block = 0; block < 3; block++)
                {
                    int vol = 0, inc = 0;
                    switch (YmEnvDef[env, block])
                    {
                        case ENV_GODOWN: vol = 31; inc = -1; break;
                        case ENV_GOUP: vol = 0; inc = 1; break;
                        case ENV_DOWN: vol = 0; inc = 0; break;
                        case ENV_UP: vol = 31; inc = 0; break;
                    }

                    for (int i = 0; i < 32; i++)
                    {
                        _envWaves[env][block * 32 + i] = (byte)vol;
                        vol += inc;
                    }
                }
            }
        }

        public void Reset()
        {
            Array.Clear(_regs, 0, _regs.Length);
            _cntA = _cntB = _cntC = 0;
            _outA = _outB = _outC = 0;
            _outNoise = 1;
            _rng = 1;

            _cntNoise = 0;
            _cntEnv = 0;
            _envPos = 0;
            _envShape = 0;

            _resamplePos = 0;
            _cycleRemainder = 0;
            _resampleStep = _resampleStepBase;

            // Clear queue
            while (AudioQueue.TryDequeue(out _)) { }
            Volatile.Write(ref _queueDepth, 0);

            // Safe default values
            _regs[7] = 0xFF; // Mixer all off
            UpdatePeriods();
        }

        // ==================== Snapshot ====================

        public void SaveState(Snapshot.Writer w)
        {
            w.Bytes(_regs);
            w.U8((byte)_selectedReg);

            w.I32(_cntA); w.I32(_cntB); w.I32(_cntC);
            w.I32(_cntNoise); w.I32(_cntEnv);

            w.U8((byte)_outA); w.U8((byte)_outB); w.U8((byte)_outC); w.U8((byte)_outNoise);
            w.U32(_rng);
            w.I32(_envShape);
            w.I32(_envPos);
            w.I32(_cycleRemainder);

            // Appended: the read-back latch (see _readData)
            w.U8(_readData);
        }

        public void LoadState(Snapshot.Reader r)
        {
            Array.Copy(r.Bytes(16), _regs, 16);
            _selectedReg = r.U8();

            _cntA = r.I32(); _cntB = r.I32(); _cntC = r.I32();
            _cntNoise = r.I32(); _cntEnv = r.I32();

            _outA = r.U8(); _outB = r.U8(); _outC = r.U8(); _outNoise = r.U8();
            _rng = r.U32();
            _envShape = r.I32();
            _envPos = r.I32();
            _cycleRemainder = r.I32();

            // Older snapshots stop here: derive the latch from the selected register instead.
            _readData = r.Remaining > 0
                ? r.U8()
                : (_selectedReg < NUM_REGS ? _regs[_selectedReg] : (byte)0xFF);

            // The periods derive from the registers; the resampler and DC filter are
            // host state and start clean (along with the audio queue)
            UpdatePeriods();
            while (AudioQueue.TryDequeue(out _)) { }
            Volatile.Write(ref _queueDepth, 0);
            _resampleStep = _resampleStepBase;
        }

        /// <summary>Registers the YM2149 actually implements; the address latch is wider.</summary>
        private const int NUM_REGS = 16;

        public void PSGRegisterSelect(byte val)
        {
            _selectedReg = val;
            _readData = _selectedReg < NUM_REGS ? _regs[_selectedReg] : (byte)0xFF;
        }

        public void PSGWriteRegister(byte val)
        {
            // Nothing is selected: the write goes nowhere (see _selectedReg).
            if (_selectedReg >= NUM_REGS)
                return;

            // The value read back straight after a write is the one written, unmasked.
            _readData = val;

            // The chip has no storage for the unused bits of some registers, and they read back
            // as zero: 4 bits for the coarse tone periods and the envelope shape, 5 for the
            // noise period and the three volumes.
            switch (_selectedReg)
            {
                case 1:
                case 3:
                case 5:
                case 13:
                    val &= 0x0F;
                    break;
                case 6:
                case 8:
                case 9:
                case 10:
                    val &= 0x1F;
                    break;
            }

            _regs[_selectedReg] = val;

            switch (_selectedReg)
            {
                case 0:
                case 1:
                case 2:
                case 3:
                case 4:
                case 5:
                case 6:
                case 11:
                case 12:
                    UpdatePeriods();
                    break;
                case 8:
                case 9:
                case 10:
                    // Write-rate counters for the YM->MT-32 mapper: sample playback
                    // (digidrums, SID-voice) hammers the volume registers at timer speed,
                    // which is how the mapper tells it from music. Consumed per frame.
                    _psgVolWrites[_selectedReg - 8]++;
                    break;
                case 13:
                    _psgEnvWrites++;    // same idea: retriggered at audio rate = sync-buzzer
                    _envShape = val & 0x0F;
                    _envPos = 0;
                    _cntEnv = 0;
                    break;
                case 14:
                    HandlePortA(val);
                    break;
            }
        }

        // ==================== YM->MT-32 mapper hooks (emulation thread) ====================

        // Per-channel volume-register (R8-R10) and envelope-shape (R13) write counters,
        // consumed once per frame by YmMidiMapper.OnFrame.
        private readonly int[] _psgVolWrites = new int[3];
        private int _psgEnvWrites;

        /// <summary>Register contents as the mapper samples them; no side effects.</summary>
        public byte ReadRegister(int index) => _regs[index & 0x0F];

        /// <summary>Hands over and resets the write counters accumulated since the last
        /// call. <paramref name="volWrites"/> must hold 3 entries (channels A/B/C).</summary>
        public void ConsumePsgWriteCounts(int[] volWrites, out int envWrites)
        {
            for (int i = 0; i < 3; i++)
            {
                volWrites[i] = _psgVolWrites[i];
                _psgVolWrites[i] = 0;
            }

            envWrites = _psgEnvWrites;
            _psgEnvWrites = 0;
        }

        public byte PSGRegisterData()
        {
            return _readData;
        }

        private void UpdatePeriods()
        {
            _perA = ((_regs[1] & 0x0F) << 8) | _regs[0];
            _perB = ((_regs[3] & 0x0F) << 8) | _regs[2];
            _perC = ((_regs[5] & 0x0F) << 8) | _regs[4];
            _perNoise = _regs[6] & 0x1F;
            _perEnv = (_regs[12] << 8) | _regs[11];
        }

        /// <summary>
        /// Generates samples based on elapsed CPU cycles.
        /// Must be called from the main loop.
        /// </summary>
        /// <param name="cpuCycles">Elapsed CPU cycles (8MHz).</param>
        public void Sync(int cpuCycles)
        {
            // Convert CPU cycles (8MHz) to ticks of our internal clock (250kHz)
            // 8MHz / 32 = 250kHz. The remainder is carried across calls so the conversion stays
            // exact even when Sync is fed cycle counts that are not multiples of 32.

            _cycleRemainder += cpuCycles;
            int ymUpdates = _cycleRemainder / 32;
            _cycleRemainder -= ymUpdates * 32;

            // Generates at 250kHz and accumulate until completing a 44.1kHz sample.

            for (int i = 0; i < ymUpdates; i++)
            {
                StepInternal250k();

                // _resamplePos is a 16.16 counter
                // Advance by the ratio (approx 5.66 250k ticks for every 44.1k tick)
                _resamplePos += 0x10000;

                // If we have accumulated enough 250k ticks to output a sample
                while (_resamplePos >= _resampleStep)
                {
                    _resamplePos -= _resampleStep;

                    // In perfect resampling (weighted average N), we should average 
                    // all intermediate samples. For performance and simplicity in C#,
                    // we take the current sample (Nearest/Last). Since we are downsampling 
                    // from 250k to 44k, aliasing is low. To improve, a 'totalSample' 
                    // accumulator can be implemented and divided at the end.

                    Mix(out float sampleL, out float sampleR);

                    // DC Filter (High Pass) to center the wave at 0
                    // alpha = approx 0.995 for 44kHz
                    float outL = sampleL - _lastSampleL + 0.995f * _lastOutL;
                    _lastSampleL = sampleL;
                    _lastOutL = outL;

                    float outR = sampleR - _lastSampleR + 0.995f * _lastOutR;
                    _lastSampleR = sampleR;
                    _lastOutR = outR;

                    AudioQueue.Enqueue(new StereoSample(outL, outR));
                    Interlocked.Increment(ref _queueDepth);
                }
            }
        }

        // Simulates a cycle at 250kHz (Exact hardware)
        private void StepInternal250k()
        {
            // The STE DMA sound engine shares the audio pipeline, advance it at the same pace
            if (Config.ConfigOptions.RunninConfig.STModel == Config.ConfigOptions.STModels.STE)
                STEDmaSound.Step250k();

            // -> Tones
            // Period 0 is treated as 1.

            // Channel A
            _cntA++;
            if (_cntA >= (_perA == 0 ? 1 : _perA))
            {
                _cntA = 0;
                _outA ^= 1;
            }

            // Channel B
            _cntB++;
            if (_cntB >= (_perB == 0 ? 1 : _perB))
            {
                _cntB = 0;
                _outB ^= 1;
            }

            // Channel C
            _cntC++;
            if (_cntC >= (_perC == 0 ? 1 : _perC))
            {
                _cntC = 0;
                _outC ^= 1;
            }

            // -> Noise
            // Noise runs at 125kHz (half of 250kHz).
            // We use effective period * 2 to simulate it running at half speed.

            _cntNoise++;
            int effectiveNoisePer = (_perNoise == 0 ? 1 : _perNoise) * 2;

            if (_cntNoise >= effectiveNoisePer)
            {
                _cntNoise = 0;
                // LFSR 17-bit (Poly: bit 17 and 14)
                if ((_rng & 1) != 0)
                {
                    _rng = (_rng >> 1) ^ 0x12000;
                    _outNoise = 1;
                }
                else
                {
                    _rng >>= 1;
                    _outNoise = 0;
                }
            }

            // -> Envelope
            // The envelope frequency is Master / (256 * EP).
            // Since an envelope cycle has 32 steps, each step occurs every
            // (256 * EP) / 32 = 8 * EP Master clock cycles.
            // Our internal clock (StepInternal250k) runs at 250kHz (Master / 8).
            // Therefore, the number of ticks of our clock to advance a step is:
            // (8 * EP) / 8 = EP.

            _cntEnv++;
            int effectiveEnvPer = (_perEnv == 0 ? 1 : _perEnv);

            if (_cntEnv >= effectiveEnvPer)
            {
                _cntEnv = 0;
                _envPos++;

                // Block 0 is attack/initial. Blocks 1 and 2 are the loop (sustain/alternate).
                if (_envPos >= 3 * 32)
                {
                    _envPos -= 2 * 32; // Return to start of block 1
                }
            }
        }

        /// <summary>
        /// Keeps the queue that feeds the audio device near <c>_queueTarget</c>. Called once per
        /// emulated frame.
        /// <para>
        /// The emulator and the sound card run off different clocks and never agree exactly, and
        /// the frame pacer makes it worse: when the host makes the emulator miss its deadline the
        /// pacer catches up by running the next frames with no wait at all, and those frames
        /// produce audio far faster than the device drains it. Without this the queue only ever
        /// grew — and once it hit the old hard ceiling every new sample threw the oldest one away,
        /// which is a dropout every few samples, continuous audible distortion, on top of a
        /// quarter second of lag between picture and sound. It never recovered on its own: a
        /// single hiccup while a game loaded left the sound wrecked for the rest of the session,
        /// which is exactly what a disk-loading title looked like next to a floppy one.
        /// </para>
        /// <para>
        /// The fix is to steer the production rate instead of throwing samples away: the resample
        /// step is trimmed by up to ±0.5% (about eight cents — inaudible) to speed the queue up or
        /// slow it down, so it converges on the target and stays there. A queue that has run far
        /// past the target is cut back in one go: one discontinuity instead of thousands.
        /// </para>
        /// </summary>
        public void UpdateAudioFlowControl()
        {
            int depth = Volatile.Read(ref _queueDepth);

            // Way past the target (a long stall, or a resume after a pause): drop back in one
            // step rather than waiting minutes for a ±0.5% trim to drain it.
            if (depth > _queueTarget * 4)
            {
                while (depth > _queueTarget && AudioQueue.TryDequeue(out _))
                    depth = Interlocked.Decrement(ref _queueDepth);
            }

            // Proportional trim: queue above target -> bigger step -> fewer samples produced.
            double error = (depth - _queueTarget) / (double)_queueTarget;
            double adjust = 1.0 + Math.Clamp(error * 0.02, -0.005, 0.005);

            _resampleStep = (uint)(_resampleStepBase * adjust);
        }

        /// <summary>
        /// Produces one output frame. The PSG itself is mono — a real YM2149 sums its three
        /// channels into a single output pin — so it feeds both sides identically; the stereo
        /// comes from the STE's DMA sound, whose two channels and LMC1992 balance are kept
        /// apart all the way here (see <see cref="STEDmaSound.CurrentSampleLeft"/>).
        /// </summary>
        private void Mix(out float left, out float right)
        {
            // Register 7: Mixer (0 = Enable, 1 = Disable)
            int mixer = _regs[7];

            // Get current envelope volume
            // The _envWaves table already has the 0-31 volume precalculated for the current position
            int envVol5bit = _envWaves[_envShape][_envPos];

            // Mix channels
            int volA = GetChannelVolume(0, mixer, _outA, envVol5bit);
            int volB = GetChannelVolume(1, mixer, _outB, envVol5bit);
            int volC = GetChannelVolume(2, mixer, _outC, envVol5bit);

            // Simple linear sum, converted to float 0..1
            // We use the logarithmic YmVolTable which returns 0..65535
            // Sum and normalize. Theoretical max = 65535 * 3.

            float ymSample = (YmVolTable[volA] + YmVolTable[volB] + YmVolTable[volC]) / (65535.0f * 3.5f);

            // On the STE, mix in the DMA sound output (the LMC1992 mixing setting
            // attenuates or mutes the PSG). This is the only stereo source in the machine.
            if (Config.ConfigOptions.RunninConfig.STModel == Config.ConfigOptions.STModels.STE)
            {
                ymSample *= STEDmaSound.YmMixGain;
                left = ymSample + STEDmaSound.CurrentSampleLeft * 0.6f;
                right = ymSample + STEDmaSound.CurrentSampleRight * 0.6f;
                return;
            }

            left = right = ymSample;
        }

        private int GetChannelVolume(int ch, int mixer, int toneOut, int envVol5bit)
        {
            // A channel the YM->MT-32 mapper is substituting this frame is silenced here:
            // the built-in module is playing its note instead. The mask is recomputed
            // every frame with the mapper's exclusions (noise, digitized sound), so an
            // excluded channel falls back to the YM automatically. Same thread as the
            // mapper (both run inside the emulation loop), no synchronisation needed.
            if ((YmMidiMapper.MuteMask & (1 << ch)) != 0)
                return 0;

            // Mixer: Bit ch = Tone Disable (1), Bit ch+3 = Noise Disable (1)
            bool toneOn = ((mixer >> ch) & 1) == 0;
            bool noiseOn = ((mixer >> (3 + ch)) & 1) == 0;

            // Logic output (AND of active components)
            // If disabled, high level (1) is assumed in the YM logical mix
            int output = 1;
            if (toneOn) output &= toneOut;
            if (noiseOn) output &= _outNoise; // LFSR bit 0 or 1

            if (output == 0) return 0; // Silence

            // Determine base volume
            int regVol = _regs[8 + ch];

            // If bit 4 (M) is set, use envelope
            if ((regVol & 0x10) != 0)
            {
                return envVol5bit; // Already 0-31
            }
            else
            {
                // Fixed volume 4 bits -> Convert to 5 bits
                return Vol4to5[regVol & 0x0F];
            }
        }

        // *** Floppy Drive Interface (Port A) ***
        private void HandlePortA(byte val)
        {
            int side = (val & 0x01) != 0 ? 0 : 1;
            int drive = -1;
            if ((val & 0x02) == 0) drive = 0;
            else if ((val & 0x04) == 0) drive = 1;
            WD1772.SetDriveAndSide(drive, side);
        }

        // *** SDL Callback ***
        private static float[] _marshalBuf;

        public static void AudioCallback(IntPtr userdata, IntPtr stream, int len)
        {
            // The device is opened as interleaved stereo, so the buffer holds two floats per
            // frame. Everything downstream of the queue counts frames; only the marshalling
            // to SDL counts floats.
            int floatsNeeded = len / sizeof(float);
            int frames = floatsNeeded / 2;

            if (_marshalBuf == null || _marshalBuf.Length < floatsNeeded)
                _marshalBuf = new float[floatsNeeded];

            // Snapshot the instance: the audio thread can run before TurnOn() has created the
            // chip, and HardReset() replaces it mid-flight — never read ASEMain._ym twice.
            var ym = ASEMain._ym;
            if (ym == null)
            {
                Array.Clear(_marshalBuf, 0, floatsNeeded);
                Marshal.Copy(_marshalBuf, 0, stream, floatsNeeded);
                return;
            }

            for (int i = 0; i < frames; i++)
            {
                if (ym.AudioQueue.TryDequeue(out StereoSample s))
                {
                    Interlocked.Decrement(ref ym._queueDepth);
                    _marshalBuf[2 * i] = s.Left;
                    _marshalBuf[2 * i + 1] = s.Right;
                }
                else
                {
                    // Underrun: Fill with last value (or silence)
                    // To avoid clicks, repeating the last sample is usually better than abrupt 0
                    _marshalBuf[2 * i] = ym._lastOutL;
                    _marshalBuf[2 * i + 1] = ym._lastOutR;
                }
            }

            // Fold in the built-in MT-32's output (a no-op unless the module is active).
            // Rendering here, clocked by the audio device itself, is what keeps Munt in
            // step with the stream and frozen while the device is paused.
            MidiManager.MixAudio(_marshalBuf, frames);

            Marshal.Copy(_marshalBuf, 0, stream, floatsNeeded);
        }
    }
}
