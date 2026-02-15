/*
 * YM2149 Sound Chip Emulator for Atari ST
 *  
 * Some parts ported from Hatari emulator by Thomas Huth and others.
 * Adapted to work in C# with SDL2 for audio output and ASE project structure.
 * Original source: hatari/src/sound.c
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
        private readonly float[] _hostBuffer;         // Temporary buffer to deliver to SDL

        // Registers
        private byte[] _regs = new byte[16];
        private int _selectedReg = 0;

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
        private bool _envPhase;   // Not used directly, implicit in envPos

        // Oversampling / Downsampling variables
        private uint _resamplePos;
        private uint _resampleStep;

        // Precalculated tables (Ported from Hatari sound.c)
        // 16 waveforms * 3 blocks * 32 steps
        private static byte[][] _envWaves;

        // ST non-linear volume curve (5 bits -> 16 bits amplitude)
        // Ported from Hatari's 'ymout1c5bit'
        private static readonly ushort[] YmVolTable =
        {
            0,  369,  438,  521,  619,  735,  874, 1039,
            1234, 1467, 1744, 2072, 2463, 2927, 3479, 4135,
            4914, 5841, 6942, 8250, 9806,11654,13851,16462,
            19565,23253,27636,32845,39037,46395,55141,65535
        };

        // Volume conversion 4 bits -> 5 bits (ST Hardware)
        private static readonly byte[] Vol4to5 = { 0, 1, 5, 7, 9, 11, 13, 15, 17, 19, 21, 23, 25, 27, 29, 31 };

        // Envelope Shapes obtained from Hatari (YmEnvDef)
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

        // DC Filter
        private float _lastSample = 0;
        private float _lastOut = 0;

        // Thread-safe circular queue to pass audio to SDL
        public System.Collections.Concurrent.ConcurrentQueue<float> AudioQueue
            = new System.Collections.Concurrent.ConcurrentQueue<float>();

        static YM2149()
        {
            BuildEnvelopeTables();
        }

        public YM2149(int sampleRate = 44100, double chipClockHz = 2000000.0)
        {
            _outputSampleRate = sampleRate;
            _hostBuffer = new float[2048]; // Temporary buffer

            // Calculate resampling step.
            // We use 32-bit fixed point (16.16) for precision without floats in the critical loop
            // Ratio = 250000 / OutputRate
            // Multiplied by 65536 for fixed point.
            long ratio = ((long)YM_FREQ_INTERNAL << 16) / _outputSampleRate;
            _resampleStep = (uint)ratio;

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

            // Clear queue
            while (AudioQueue.TryDequeue(out _)) { }

            // Safe default values
            _regs[7] = 0xFF; // Mixer all off
            UpdatePeriods();
        }

        public void PSGRegisterSelect(byte val)
        {
            _selectedReg = val & 0x0F;
        }

        public void PSGWriteRegister(byte val)
        {
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
                case 13:
                    _envShape = val & 0x0F;
                    _envPos = 0;
                    _cntEnv = 0;
                    break;
                case 14:
                    HandlePortA(val);
                    break;
            }
        }

        public byte PSGRegisterData()
        {
            return _regs[_selectedReg];
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
            // 8MHz / 32 = 250kHz
            // We accumulate the remainder for precision if necessary, but on ST 
            // the ratio is exact (32 CPU cycles = 1 internal YM cycle)

            int ymUpdates = cpuCycles / 32;

            // Simplified "Weighted Average" Resampling Algorithm from Hatari.
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

                    float sample = Mix();

                    // DC Filter (High Pass) to center the wave at 0
                    // alpha = approx 0.995 for 44kHz
                    float outSample = sample - _lastSample + 0.995f * _lastOut;
                    _lastSample = sample;
                    _lastOut = outSample;

                    AudioQueue.Enqueue(outSample);

                    // Protection against infinite latency
                    if (AudioQueue.Count > _outputSampleRate / 4)
                        AudioQueue.TryDequeue(out _);
                }
            }
        }

        // Simulates a cycle at 250kHz (Exact hardware)
        private void StepInternal250k()
        {
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

        private float Mix()
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

            return (YmVolTable[volA] + YmVolTable[volB] + YmVolTable[volC]) / (65535.0f * 3.5f);
        }

        private int GetChannelVolume(int ch, int mixer, int toneOut, int envVol5bit)
        {
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
            int samplesNeeded = len / sizeof(float);
            if (_marshalBuf == null || _marshalBuf.Length < samplesNeeded)
                _marshalBuf = new float[samplesNeeded];

            int read = 0;
            while (read < samplesNeeded)
            {
                if (ASEMain._ym.AudioQueue.TryDequeue(out float s))
                {
                    _marshalBuf[read++] = s;
                }
                else
                {
                    // Underrun: Fill with last value (or silence)
                    // To avoid clicks, repeating the last sample is usually better than abrupt 0
                    _marshalBuf[read++] = ASEMain._ym._lastOut;
                }
            }

            Marshal.Copy(_marshalBuf, 0, stream, samplesNeeded);
        }
    }
}
