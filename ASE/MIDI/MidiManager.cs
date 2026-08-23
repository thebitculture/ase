using static ASE.Config;

namespace ASE
{
    /// <summary>
    /// Wires the emulated MIDI ACIA to whatever <see cref="ConfigOptions.MidiEmulation"/>
    /// says its DIN sockets are plugged into: nothing, the host's MIDI ports
    /// (<see cref="HostMidiOutput"/>/<see cref="HostMidiInput"/>) or the built-in MT-32
    /// (<see cref="Mt32Backend"/>). (Re)built on every machine power-on
    /// (<see cref="ASEMain.TurnOn"/>), which is also when a mode or ROM-folder change in
    /// the Configuration window takes effect — its OK forces the reset.
    ///
    /// Threading: <see cref="Initialize"/>/<see cref="Shutdown"/> run with the emulation
    /// thread stopped; <see cref="TransmitByte"/> runs on the emulation thread;
    /// <see cref="MixAudio"/> on the SDL audio-callback thread. The MT-32 reference is
    /// swapped under the SDL audio-device lock so a render in flight never races the
    /// teardown.
    /// </summary>
    public static class MidiManager
    {
        static IMidiOutput _output;             // where the ST's MIDI OUT goes (null = unplugged)
        static IDisposable _input;              // host MIDI IN capture (System mode only)
        static volatile Mt32Backend _mt32;      // set when _output is the built-in module
        static MidiStreamParser _parser;
        static bool _outputFaulted;             // one console report per session, not one per byte

        /// <summary>
        /// What the machine was actually wired up with the last time it was powered on —
        /// <see cref="ConfigOptions.MIDIEmulationOptions.None"/> until the first
        /// <see cref="Initialize"/>. This is deliberately not
        /// <c>ConfigOptions.RunninConfig.MidiEmulation</c>: the Configuration window's combo
        /// writes into the running config live (so Cancel has something to restore), so the
        /// config can say "System" for as long as that dialog is open even though the ST is
        /// still talking to the MT-32. A committed mode change always goes through a reset —
        /// its OK refuses to apply otherwise — hence through this field. The MT-32 toolbox
        /// keys off it to decide whether it still has a module to be a front panel for.
        /// </summary>
        public static ConfigOptions.MIDIEmulationOptions Mode { get; private set; }

        /// <summary>
        /// Builds the session's MIDI wiring from the running configuration. Called from
        /// <see cref="ASEMain.TurnOn"/> — i.e. at every power-on and hard reset — with
        /// the emulation thread not running. Any previous wiring is torn down first, so
        /// a reset also power-cycles the MT-32 module, as its titles expect.
        /// </summary>
        public static void Initialize()
        {
            Shutdown();
            _outputFaulted = false;

            var cfg = ConfigOptions.RunninConfig;
            Mode = cfg.MidiEmulation;

            switch (cfg.MidiEmulation)
            {
                case ConfigOptions.MIDIEmulationOptions.None:
                    break;

                case ConfigOptions.MIDIEmulationOptions.System:
                    if (!string.IsNullOrEmpty(cfg.MidiOutDevice))
                        _output = HostMidiOutput.Open(cfg.MidiOutDevice);

                    if (!string.IsNullOrEmpty(cfg.MidiInDevice))
                        _input = HostMidiInput.Open(cfg.MidiInDevice, MidiAcia.Receive);

                    if (_output == null && _input == null)
                        ColoredConsole.WriteLine("MIDI: system mode with no host port connected.", ConfigOptions.DebugModes.Quiet);
                    break;

                case ConfigOptions.MIDIEmulationOptions.BuiltInMT32:
                    try
                    {
                        Mt32Backend mt32 = Mt32Backend.Open(cfg.MT32Rompath, cfg.SampleRate);

                        _output = mt32;
                        ASEMain.WithAudioLocked(() => _mt32 = mt32);
                    }
                    catch (DllNotFoundException)
                    {
                        ColoredConsole.WriteLine(
                            "MIDI: the [[red]]Munt library (mt32emu)[[/red]] could not be loaded — MT-32 emulation disabled.");
                    }
                    catch (Exception ex)
                    {
                        // Missing/unidentified ROMs, unreadable folder... Mt32Backend and
                        // Mt32Synth throw with user-readable messages.
                        ColoredConsole.WriteLine($"MIDI: MT-32 emulation disabled — [[red]]{ex.Message}[[/red]]");
                    }
                    break;
            }

            _parser = _output != null ? new MidiStreamParser(_output) : null;

            // The YM->MT-32 mapper starts every power-on clean: the fresh module has no
            // hanging notes, and the mapped programs are re-sent on the first frame.
            YmMidiMapper.OnModulePowerOn();
        }

        /// <summary>Whether the built-in MT-32 is running — i.e. whether the YM->MT-32
        /// mapper has anywhere to send. Reading the volatile field is enough: the backend
        /// is only swapped with the emulation thread stopped.</summary>
        public static bool Mt32Active => _mt32 != null;

        /// <summary>
        /// A short message from the YM->MT-32 mapper straight to the built-in module,
        /// bypassing the ACIA/parser path (this traffic never existed on the ST's MIDI
        /// wire). Emulation thread; a no-op when no module is running, and never throws —
        /// it is called from the frame loop.
        /// </summary>
        public static void SendYmMapped(byte status, byte data1, byte data2)
        {
            var mt32 = _mt32;
            if (mt32 == null)
                return;

            try { mt32.ShortMessage(status, data1, data2, 3); }
            catch { /* a failing module must not take the frame loop down */ }
        }

        /// <summary>Tears the wiring down (host ports closed, MT-32 disposed). Safe to
        /// call twice; also the first step of <see cref="Initialize"/>.</summary>
        public static void Shutdown()
        {
            // Unhook the audio mix first, under the device lock: once this returns, no
            // callback can be inside Render, so disposing the synth below is safe.
            if (_mt32 != null)
                ASEMain.WithAudioLocked(() => _mt32 = null);

            _parser = null;

            try { _output?.Dispose(); } catch { /* a failing driver must not block the teardown */ }
            _output = null;

            try { _input?.Dispose(); } catch { /* same */ }
            _input = null;
        }

        /// <summary>
        /// A byte the MIDI ACIA put on the wire (emulation thread). Never throws: the
        /// call originates inside the CPU's bus-write callback, where an escaped
        /// exception would kill the process.
        /// </summary>
        public static void TransmitByte(byte b)
        {
            var parser = _parser;
            if (parser == null)
                return;

            try
            {
                parser.Feed(b);
            }
            catch (Exception ex)
            {
                if (!_outputFaulted)
                {
                    _outputFaulted = true;
                    ColoredConsole.WriteLine(
                        $"MIDI: output to [[cyan]]{_output?.Description}[[/cyan]] failed ([[red]]{ex.GetType().Name}[[/red]]: {ex.Message}); further errors muted.");
                }
            }
        }

        /// <summary>
        /// What the built-in MT-32's LCD is showing, or <c>null</c> when no module is
        /// running (any other MIDI mode, missing ROMs, Munt not loadable). Polled by the
        /// MT-32 toolbox from the UI thread: the read happens under the audio-device lock,
        /// the same one the backend is swapped and disposed under, so it can never race a
        /// render or a teardown in flight.
        /// </summary>
        public static string Mt32DisplayText()
        {
            if (_mt32 == null)
                return null;

            string text = null;

            ASEMain.WithAudioLocked(() =>
            {
                // Re-read the field inside the lock: Shutdown may have cleared it since
                // the check above.
                try { text = _mt32?.DisplayText; }
                catch { /* a faulted module has nothing to show; the LCD falls back */ }
            });

            return text;
        }

        /// <summary>
        /// Mixes the built-in MT-32's output over the PSG mix (SDL audio-callback thread).
        /// A no-op unless the module is active. Never throws — an exception leaving the SDL
        /// callback would kill the process.
        /// </summary>
        /// <param name="buffer">Interleaved stereo output buffer, two floats per frame.</param>
        /// <param name="frames">Number of frames to render, i.e. half the buffer's floats.</param>
        public static void MixAudio(float[] buffer, int frames)
        {
            var mt32 = _mt32;
            if (mt32 == null)
                return;

            try
            {
                mt32.MixInto(buffer, frames);
            }
            catch
            {
                // Rendering must never take the audio thread down; the next Initialize
                // or Shutdown will clean up.
            }
        }
    }
}
