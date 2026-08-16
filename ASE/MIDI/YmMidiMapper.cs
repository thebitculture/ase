/*
 *
 * Real-time YM2149 -> MT-32 voice substitution.
 *
 * The idea (and the maths) descend from Andrew Gower's 1998 "YM3/YM5 to MIDI"
 * converter, which proved that sampling the PSG registers once per VBL is enough
 * to reconstruct the music as MIDI. ASE has something better than a register dump:
 * the live chip — so the same 50 Hz sampling runs in real time, the YM channel is
 * muted, and the built-in MT-32 plays the notes instead.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

namespace ASE
{
    /// <summary>
    /// Substitutes YM2149 voices with MT-32 instruments in real time. Each of the three
    /// PSG channels can be mapped (from the MT-32 toolbox) to one of the module's 128
    /// preset timbres; a mapped channel's tone is muted in the PSG mix and its note —
    /// derived from the channel's 12-bit tone period — is played by the built-in module.
    /// The noise mixer bit can additionally be mapped to the MT-32's rhythm part.
    ///
    /// A channel is left alone (the YM keeps sounding) whenever substitution cannot be
    /// faithful — chiefly digitized sound (digidrums, SID-voice, sync-buzzer), recognised
    /// by the register write rate: a music driver touches a channel's volume once or
    /// twice per frame, sample playback hammers it hundreds of times. Noise is more
    /// nuanced, because tunes use short bursts of it as note attacks: mixed with a
    /// running tone it never sounds (the note substitutes and the module's attack
    /// replaces the burst, whose edge re-articulates repeated notes), a noise-only spell
    /// is gated silent for a few frames, and only noise that lasts — real effect
    /// material — falls back to the YM, or to the rhythm part when the drums mapping is
    /// on.
    ///
    /// Arpeggios — the YM trick of cycling a chord's notes at frame rate to fake
    /// polyphony on a 3-voice chip — would reach the MT-32 as an ugly trill, so they are
    /// detected (a small cyclic set of notes alternating fast in the recent history) and
    /// replaced by the actual chord held on the polyphonic module.
    ///
    /// Threading: <see cref="SetProgram"/>/<see cref="DrumsEnabled"/> are written from
    /// the UI thread (plain atomic stores read by the mapper with at most a frame of
    /// lag); everything else — <see cref="OnFrame"/>, called at the end of every emulated
    /// frame, and <see cref="MuteMask"/>, read by <c>YM2149.GetChannelVolume</c> inside
    /// <c>Sync</c> — runs on the emulation thread. MIDI goes out through
    /// <see cref="MidiManager.SendYmMapped"/>, a no-op when no module is running.
    /// </summary>
    public static class YmMidiMapper
    {
        const int Voices = 3;

        // MIDI notes outside this range are not substituted (the YM keeps the note):
        // out-of-range periods are effect territory, not melody. Slightly wider than
        // makemidi's 31..96 to keep deep basses.
        const int MinNote = 24, MaxNote = 102;

        // Attack velocity is fixed and the channel volume rides CC7 instead: the YM has
        // no per-note accents, only a running volume, and sending it through both
        // velocity and CC7 would attenuate twice.
        const byte NoteOnVelocity = 112;
        const int EnvelopeCc7 = 110;    // envelope-driven volume: treated as "loud"

        // ---- digitized-sound detection ----
        // A music driver writes a channel's volume register 1-2 times per frame;
        // digidrums/SID-voice write it at timer speed (hundreds per frame). Once over
        // the threshold, the channel stays excluded for a hold-off so the substitution
        // does not flap between drum hits.
        const int DigiWritesPerFrame = 6;
        const int DigiHoldFrames = 25;      // half a second at 50 Hz

        // ---- attack-noise gate ----
        // Many YM tunes start each note with a noise burst of a frame or two before the
        // tone settles. On a mapped channel that burst must not leak through the YM while
        // the module takes over, so a noise-only spell is held silent this many frames;
        // only noise that outlives the gate is real material (sea, explosions…) and falls
        // back to the YM. Noise *mixed with* a running tone never sounds at all: the
        // note substitutes anyway and the module's own attack replaces the burst.
        const int NoiseGateFrames = 3;

        // ---- arpeggio (chord) detection over the recent per-frame note history ----
        const int HistoryLen = 10;          // frames examined (200 ms)
        const int MaxChordNotes = 4;
        const int MinChanges = 4;           // note flips within the window
        const int MaxChordSpan = 16;        // semitones between lowest and highest
        const int MinTwoNoteInterval = 3;   // 2-note sets closer than this are vibrato/trills
        const int ChordMissFrames = 5;      // frames the chord survives a broken pattern

        /// <summary>MIDI note for each 12-bit tone period: round(69 + 12·log2(125000 /
        /// (440·P))), or 0 when the period falls outside <see cref="MinNote"/>..<see
        /// cref="MaxNote"/>. Same table makemidi builds; 125000 = 2 MHz / 16.</summary>
        static readonly int[] NoteOfPeriod = BuildNoteTable();

        // Written by the UI thread, read by the emulation thread. int/bool stores are
        // atomic; a frame of staleness is inaudible.
        static readonly int[] _programs = { -1, -1, -1 };   // -1 = not mapped
        static readonly bool[] _programDirty = new bool[Voices];
        static volatile bool _drums;

        /// <summary>Bit n set = PSG channel n is being substituted this frame, so
        /// <c>YM2149.GetChannelVolume</c> must silence it. Emulation thread only (the PSG
        /// mixes inside <c>Sync</c>, on the same thread that runs <see cref="OnFrame"/>).</summary>
        public static int MuteMask { get; private set; }

        sealed class Voice
        {
            public readonly int MidiChannel;            // zero-based; parts 1-3 live on channels 2-4
            public readonly List<int> On = new();       // melodic notes currently sounding
            public readonly int[] History = new int[HistoryLen];
            public int HistPos;
            public bool ChordMode;
            public int ChordMiss;
            public int DigiHold;
            public int LastCc7 = -1;
            public int DrumNote;                        // 0 = no percussion note sounding
            public bool PrevNoise;                      // last frame's noise mixer bit (edge detection)
            public int NoiseGate;                       // frames a noise-only spell has been held silent

            public Voice(int midiChannel) { MidiChannel = midiChannel; }
        }

        static readonly Voice[] _voices = { new(1), new(2), new(3) };
        static readonly int[] _volWrites = new int[Voices];

        static int[] BuildNoteTable()
        {
            var table = new int[4096];

            for (int period = 1; period < 4096; period++)
            {
                double note = 69.0 + 12.0 * Math.Log2(125000.0 / (440.0 * period));
                int rounded = (int)Math.Round(note);
                table[period] = rounded is >= MinNote and <= MaxNote ? rounded : 0;
            }

            return table;    // period 0 (the chip treats it as 1) stays 0: no note
        }

        // ==================== UI thread ====================

        /// <summary>Maps PSG voice <paramref name="voice"/> (0-2) to MT-32 program
        /// <paramref name="program"/> (0-127), or unmaps it with -1. Applied by the next
        /// emulated frame; unmapping releases the voice's notes and unmutes the YM.</summary>
        public static void SetProgram(int voice, int program)
        {
            if (_programs[voice] == program)
                return;

            _programs[voice] = program;
            if (program >= 0)
                _programDirty[voice] = true;
        }

        public static int GetProgram(int voice) => _programs[voice];

        /// <summary>Maps the PSG's noise (any channel with its noise mixer bit on) onto
        /// the MT-32's rhythm part: note 35 + noise period, as makemidi's drum mode.</summary>
        public static bool DrumsEnabled
        {
            get => _drums;
            set => _drums = value;
        }

        // ==================== emulation thread ====================

        /// <summary>Forgets all runtime state without sending anything. Called by
        /// <see cref="MidiManager.Initialize"/> at power-on (the fresh module has no
        /// hanging notes) with the emulation thread stopped; mapped programs are re-sent
        /// on the first frame.</summary>
        public static void OnModulePowerOn()
        {
            foreach (Voice v in _voices)
            {
                v.On.Clear();
                Array.Clear(v.History, 0, HistoryLen);
                v.HistPos = 0;
                v.ChordMode = false;
                v.ChordMiss = 0;
                v.DigiHold = 0;
                v.LastCc7 = -1;
                v.DrumNote = 0;
                v.PrevNoise = false;
                v.NoiseGate = 0;
            }

            for (int i = 0; i < Voices; i++)
                _programDirty[i] = _programs[i] >= 0;

            MuteMask = 0;
        }

        /// <summary>
        /// Samples the PSG and updates the MIDI side. Called once per emulated frame
        /// (VBL, 50 Hz) from <c>ASEMain.EmulatorLoop</c> — the same grid YM register
        /// dumps use, which offline converters proved sufficient.
        /// </summary>
        public static void OnFrame(YM2149 ym)
        {
            // Consume the write counters even when idle, so they never accumulate into
            // a false digi verdict the moment a mapping is switched on.
            ym.ConsumePsgWriteCounts(_volWrites, out int envWrites);

            if (!MidiManager.Mt32Active)
            {
                // No module: nothing can be sounding on it, so just stand down.
                if (MuteMask != 0)
                    OnModulePowerOn();
                return;
            }

            int mixer = ym.ReadRegister(7);
            int mask = 0;

            for (int ch = 0; ch < Voices; ch++)
            {
                Voice v = _voices[ch];

                int volReg = ym.ReadRegister(8 + ch);
                bool envMode = (volReg & 0x10) != 0;
                int vol4 = volReg & 0x0F;

                // Digitized sound on this channel? (volume hammered directly, or the
                // envelope retriggered at audio rate — sync-buzzer — while the channel
                // rides it)
                if (_volWrites[ch] > DigiWritesPerFrame || (envMode && envWrites > DigiWritesPerFrame))
                    v.DigiHold = DigiHoldFrames;
                else if (v.DigiHold > 0)
                    v.DigiHold--;

                bool toneOn = ((mixer >> ch) & 1) == 0;
                bool noiseOn = ((mixer >> (3 + ch)) & 1) == 0;
                bool digi = v.DigiHold > 0;
                bool audible = envMode || vol4 > 0;

                bool noiseEdge = noiseOn && !v.PrevNoise;
                v.PrevNoise = noiseOn;

                // ---- melodic substitution ----

                // Noise mixed with a running tone does NOT exclude the note: many tunes
                // open every note with a 1-2 frame noise burst as its attack, and letting
                // it through would click on the YM before the module takes over. The
                // module's own attack replaces the burst; the burst's edge re-articulates
                // repeated notes of the same pitch (see UpdateMelodic).
                int desired = 0;
                if (_programs[ch] >= 0 && toneOn && audible && !digi)
                {
                    int period = ((ym.ReadRegister(2 * ch + 1) & 0x0F) << 8) | ym.ReadRegister(2 * ch);
                    desired = NoteOfPeriod[period];
                }

                // Program before any note it applies to.
                if (_programDirty[ch] && _programs[ch] >= 0)
                {
                    _programDirty[ch] = false;
                    MidiManager.SendYmMapped((byte)(0xC0 | v.MidiChannel), (byte)_programs[ch], 0);
                }

                // The channel volume rides CC7 (fades in/out reach the module live).
                if (desired != 0 || v.On.Count > 0)
                {
                    int cc7 = envMode ? EnvelopeCc7 : Math.Min(127, vol4 * 8);
                    if (cc7 != v.LastCc7)
                    {
                        v.LastCc7 = cc7;
                        MidiManager.SendYmMapped((byte)(0xB0 | v.MidiChannel), 7, (byte)cc7);
                    }
                }

                UpdateMelodic(v, desired, noiseEdge);

                if (v.On.Count > 0)
                    mask |= 1 << ch;

                // ---- drums: noise -> rhythm part ----

                // A channel whose tone is substituting keeps its attack noise out of the
                // drum kit (desired != 0); noise-only percussion — and every unmapped
                // channel — still lands there.
                bool drumCond = _drums && noiseOn && audible && !digi && desired == 0;
                if (drumCond)
                {
                    mask |= 1 << ch;

                    // Edge-triggered, like makemidi: one hit per volume+noise onset.
                    if (v.DrumNote == 0)
                    {
                        v.DrumNote = 35 + (ym.ReadRegister(6) & 0x1F);
                        int vel = envMode ? EnvelopeCc7 : Math.Min(127, vol4 * 8);
                        MidiManager.SendYmMapped(0x99, (byte)v.DrumNote, (byte)vel);
                    }
                }
                else if (v.DrumNote != 0)
                {
                    MidiManager.SendYmMapped(0x89, (byte)v.DrumNote, 0);
                    v.DrumNote = 0;
                }

                // ---- attack-noise gate ----

                // A noise-only spell on a mapped channel (nothing substituting, no drum
                // claiming it) is held silent for a few frames: if the tone follows, the
                // burst was the note's attack and never sounds; if it outlives the gate,
                // it is real noise material and the YM takes it back.
                if (_programs[ch] >= 0 && noiseOn && desired == 0 && !drumCond && !digi)
                {
                    if (v.NoiseGate < NoiseGateFrames)
                    {
                        v.NoiseGate++;
                        mask |= 1 << ch;
                    }
                }
                else
                    v.NoiseGate = 0;
            }

            MuteMask = mask;
        }

        /// <summary>Advances one channel's melodic state machine: pushes the frame's note
        /// into the history, detects arpeggio patterns (played as held chords) and sends
        /// the note on/off traffic for whatever changed. <paramref name="retrigger"/>
        /// (the attack-noise edge) re-articulates a repeated note of the same pitch,
        /// which would otherwise merge into one long note.</summary>
        static void UpdateMelodic(Voice v, int desired, bool retrigger)
        {
            v.History[v.HistPos] = desired;
            v.HistPos = (v.HistPos + 1) % HistoryLen;   // now points at the oldest entry

            // Silence ends everything at once, a held chord included.
            if (desired == 0)
            {
                AllNotesOff(v);
                v.ChordMode = false;
                v.ChordMiss = 0;
                return;
            }

            Span<int> chord = stackalloc int[MaxChordNotes];
            if (DetectChord(v, chord, out int chordLen))
            {
                v.ChordMode = true;
                v.ChordMiss = 0;

                // Reshape what is sounding into the chord: release members that left,
                // attack the new ones, leave the common ones ringing.
                for (int i = v.On.Count - 1; i >= 0; i--)
                {
                    if (!Contains(chord, chordLen, v.On[i]))
                    {
                        MidiManager.SendYmMapped((byte)(0x80 | v.MidiChannel), (byte)v.On[i], 0);
                        v.On.RemoveAt(i);
                    }
                }

                for (int i = 0; i < chordLen; i++)
                {
                    if (!v.On.Contains(chord[i]))
                    {
                        MidiManager.SendYmMapped((byte)(0x90 | v.MidiChannel), (byte)chord[i], NoteOnVelocity);
                        v.On.Add(chord[i]);
                    }
                }

                return;
            }

            if (v.ChordMode)
            {
                // The pattern wobbles during chord changes; hold the chord a few frames
                // before deciding the arpeggio is really over.
                if (++v.ChordMiss < ChordMissFrames)
                    return;

                v.ChordMode = false;
                v.ChordMiss = 0;
                AllNotesOff(v);
                // Falls through to sound the current note on its own.
            }

            // Same single note still sounding: hold it — unless the attack noise marks
            // a fresh strike of the same pitch, which falls through to the off+on below.
            if (v.On.Count == 1 && v.On[0] == desired && !retrigger)
                return;

            AllNotesOff(v);
            MidiManager.SendYmMapped((byte)(0x90 | v.MidiChannel), (byte)desired, NoteOnVelocity);
            v.On.Add(desired);
        }

        /// <summary>
        /// Whether the channel's recent history is an arpeggio: a small set (2-4) of
        /// notes, each recurring, flipping fast, spanning at most a tenth — i.e. a chord
        /// being cycled at frame rate. Fast scales fail the recurrence test, trills and
        /// vibrato fail the 2-note minimum interval, real melodies fail the flip count.
        /// On success the distinct notes are returned in <paramref name="chord"/>.
        /// </summary>
        static bool DetectChord(Voice v, Span<int> chord, out int chordLen)
        {
            chordLen = 0;
            Span<int> occur = stackalloc int[MaxChordNotes];
            int distinct = 0, changes = 0, prev = -1;
            int min = int.MaxValue, max = int.MinValue;

            for (int i = 0; i < HistoryLen; i++)
            {
                int note = v.History[(v.HistPos + i) % HistoryLen];   // chronological
                if (note == 0)
                    return false;   // a rest breaks the pattern

                if (prev != -1 && note != prev) changes++;
                prev = note;

                int j = 0;
                while (j < distinct && chord[j] != note) j++;
                if (j == distinct)
                {
                    if (distinct == MaxChordNotes)
                        return false;   // too many notes: melody, not chord

                    chord[distinct] = note;
                    occur[distinct] = 0;
                    distinct++;
                }
                occur[j]++;

                if (note < min) min = note;
                if (note > max) max = note;
            }

            if (distinct < 2 || changes < MinChanges || max - min > MaxChordSpan)
                return false;

            if (distinct == 2 && max - min < MinTwoNoteInterval)
                return false;   // adjacent-note alternation is vibrato/trill, keep it one voice

            for (int i = 0; i < distinct; i++)
                if (occur[i] < 2)
                    return false;   // every chord member must recur: a run passing through doesn't

            chordLen = distinct;
            return true;
        }

        static bool Contains(Span<int> set, int len, int value)
        {
            for (int i = 0; i < len; i++)
                if (set[i] == value)
                    return true;
            return false;
        }

        static void AllNotesOff(Voice v)
        {
            foreach (int note in v.On)
                MidiManager.SendYmMapped((byte)(0x80 | v.MidiChannel), (byte)note, 0);

            v.On.Clear();
        }
    }
}
