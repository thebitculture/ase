using Mt32;
using static ASE.Config;

namespace ASE
{
    /// <summary>
    /// The built-in Roland MT-32: Munt (libmt32emu) wrapped as a destination for the
    /// ST's MIDI OUT, plus the audio side. Created by <see cref="MidiManager"/> when the
    /// machine powers on with <c>MidiEmulation = BuiltInMT32</c> — a reset therefore
    /// power-cycles the module too, which is exactly what MT-32 titles expect when they
    /// probe and initialise it during loading.
    ///
    /// Threading, matching the library's contract (see <see cref="Mt32Synth"/>):
    /// MIDI arrives from the emulation thread through the mt32emu queue, and
    /// <see cref="MixInto"/> renders from the SDL audio-callback thread only.
    /// <see cref="MidiManager"/> swaps/disposes this object under the SDL audio-device
    /// lock so a render in flight can never race the teardown.
    /// </summary>
    public sealed class Mt32Backend : IMidiOutput
    {
        readonly Mt32Synth _synth;
        short[] _renderBuf;

        // Gain the last mixed sample actually used. The volume knob is read once per
        // audio buffer, so dragging the slider would otherwise step the gain at every
        // buffer boundary (~23 ms at 44.1 kHz) and click; the mix ramps across the
        // buffer instead. Negative = first buffer, i.e. start already at the target.
        float _gain = -1f;

        public string Description => "built-in MT-32 (Munt)";

        Mt32Backend(Mt32Synth synth)
        {
            _synth = synth;
        }

        /// <summary>
        /// Loads the ROMs and opens the synth at the mixer's sample rate. Throws with a
        /// user-readable message on any failure (missing/unidentified ROMs, missing
        /// native library…) — the caller decides how to report it.
        /// </summary>
        public static Mt32Backend Open(string romDirectory, int sampleRate)
        {
            if (string.IsNullOrWhiteSpace(romDirectory))
                throw new InvalidOperationException("no MT-32 ROM folder is configured (Configuration > MIDI).");

            var synth = Mt32Synth.Open(romDirectory, Mt32AnalogOutputMode.Coarse, partialCount: 32,
                                       targetSampleRate: sampleRate);

            try
            {
                if (synth.SampleRate != sampleRate)
                    ColoredConsole.WriteLine(
                        $"MT-32: the library renders at [[yellow]]{synth.SampleRate} Hz[[/yellow]] instead of the requested {sampleRate} Hz — audio will be pitched.");

                Mt32RomInfo roms = synth.GetRomInfo();
                ColoredConsole.WriteLine(
                    $"MT-32: Munt [[green]]{Mt32Synth.LibraryVersion}[[/green]] ready — " +
                    $"control ROM [[cyan]]{roms.ControlDescription ?? "?"}[[/cyan]], PCM ROM [[cyan]]{roms.PcmDescription ?? "?"}[[/cyan]].",
                    ConfigOptions.DebugModes.Quiet);

                // The traditional hello on the module's LCD.
                synth.ShowLcdMessage($"ASE v{Config.Version} ready");

                return new Mt32Backend(synth);
            }
            catch
            {
                synth.Dispose();
                throw;
            }
        }

        // ==================== IMidiOutput (emulation thread) ====================

        public void ShortMessage(byte status, byte data1, byte data2, int length)
        {
            // Undefined system-common bytes have nothing to say to an MT-32.
            if (status is 0xF4 or 0xF5 or 0xF6)
                return;

            try
            {
                _synth.PlayMsg(status, data1, data2);
            }
            catch (InvalidOperationException)
            {
                // MIDI queue full: the renderer is not draining fast enough. Dropping the
                // message is all a real serial line would do better.
                if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Full)
                    ColoredConsole.WriteLine("[[cyan]]MT-32[[/cyan]] [[red]]MIDI queue full[[/red]], message dropped");
            }
        }

        public void SysEx(byte[] message)
        {
            _synth.PlaySysex(message);
        }

        public void RealTime(byte status)
        {
            // The MT-32 ignores system real-time; feeding them to Munt only earns
            // "unknown message" reports.
        }

        // ==================== Front panel ====================

        /// <summary>
        /// What the module's LCD is showing (its 20 characters), the same text the real
        /// front panel would display. Reached from the UI thread through
        /// <see cref="MidiManager.Mt32DisplayText"/>, which reads it under the audio-device
        /// lock: the display is updated by the renderer, i.e. from the audio callback.
        /// </summary>
        public string DisplayText => _synth.GetDisplayText();

        // ==================== Audio (SDL callback thread) ====================

        /// <summary>
        /// Renders <paramref name="samples"/> frames and folds them, stereo to mono, over
        /// the PSG mix already in <paramref name="buffer"/>, at the level the volume knob
        /// asks for. Rendering is what advances Munt's clock, so pausing the SDL device
        /// (UI dialogs, snapshots) freezes the module together with the rest of the
        /// machine — and the render happens whatever the volume, so muting the module
        /// does not desynchronise it from the machine.
        /// </summary>
        public void MixInto(float[] buffer, int samples)
        {
            if (_renderBuf == null || _renderBuf.Length < samples * 2)
                _renderBuf = new short[samples * 2];

            _synth.Render(_renderBuf, samples);

            // Volume knob (percent) -> per-sample gain: 0.5 folds the stereo pair down to
            // mono and 1/32768 brings the 16-bit render into the mixer's float range.
            // Read live, so the slider works without a reset.
            float target = Math.Clamp(ConfigOptions.RunninConfig.Mt32Volume, 0, MaxVolume)
                           * (0.01f * 0.5f / 32768f);

            if (_gain < 0f)
                _gain = target;

            float step = samples > 0 ? (target - _gain) / samples : 0f;

            for (int i = 0; i < samples; i++)
            {
                _gain += step;
                buffer[i] += (_renderBuf[2 * i] + _renderBuf[2 * i + 1]) * _gain;
            }

            // Land exactly on the target: the accumulated step would drift otherwise.
            _gain = target;
        }

        /// <summary>Top of the volume knob's travel, in percent — the ceiling shared by
        /// the Configuration slider and <c>--mt32-volume</c>. Past roughly 300% loud
        /// material can clip against the output, which is the user's call to make, the
        /// same way it is on a real module's amplifier.</summary>
        public const int MaxVolume = 400;

        public void Dispose()
        {
            _synth.Dispose();
        }
    }
}
