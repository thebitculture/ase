/*
 * 
 * ASE Main loop
 * 
 * Official repository 👉 https://github.com/thebitculture/ase
 * 
 */

using SDL2;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using static ASE.Config;
using static ASE.Video;
using static SDL2.SDL;

namespace ASE
{
    public static class ASEMain
    {
        // The texture handed to OpenGL is BUFFER_WIDTH x BUFFER_HEIGHT (display + borders).
        // ScreenHeight is the *visual* height: each ST scanline is shown at double height, so the window aspect ratio and GLControl's
        public const int ScreenWidth = VideoTiming.BUFFER_WIDTH;
        public const int ScreenHeight = VideoTiming.BUFFER_HEIGHT * 2;
        public const int ScreenViewSize = VideoTiming.BUFFER_WIDTH * VideoTiming.BUFFER_HEIGHT;

        public static Memory _mem;
        public static MFP68901 _mfp;
        public static YM2149 _ym;

        static SDL.SDL_AudioCallback _audiocallback;
        static uint _audiodev;
        static nint GamepadController;
        const int GamepadDeadzone = 8000;

        // SDL input plumbing. SDL_PumpEvents may only run on the thread that initialised the
        // video subsystem (the UI thread), so a dispatcher timer keeps the queue fed at Input
        // priority — this is what polls the gamepad everywhere and translates keyboard/mouse on
        // macOS/Linux (on Windows those enter SDL's queue straight from the subclassed window
        // procedure). The emulation thread then CONSUMES the queue with SDL_PeepEvents, which
        // only takes the queue lock and never pumps, so it is thread-safe off the video thread.
        // Handling used to happen once per emulated frame via InvokeAsync at Background
        // priority, which the continuously-rendering GL control starved for several frames at a
        // time.
        static DispatcherTimer _sdlPumpTimer;
        const int MaxEventsPerDrain = 64;
        static readonly SDL_Event[] _drainBuffer = new SDL_Event[MaxEventsPerDrain];

        // The CPU is advanced in short slices of this many cycles so the MFP timers — and
        // therefore the interrupt level fed to the 68000 — are re-evaluated many times per
        // scanline instead of only twice. Running the whole ~448-cycle active part in one go
        // meant a timer that came due early in the chunk was not delivered until the end of it;
        // code that busy-waits on the 200 Hz Timer C ($114) or polls a timer data register
        // could miss the transition entirely and hang. Configurable via Config.CpuSyncSliceCycles
        // (default 64 ≈ a couple of instructions); clamped to a sane range so a bad config value
        // can never stall or degenerate the loop.
        static int CpuSliceCycles
        {
            get
            {
                int s = ConfigOptions.RunninConfig.CpuSyncSliceCycles;
                if (s < 1) s = 1;
                if (s > VideoTiming.CYCLES_PER_LINE) s = VideoTiming.CYCLES_PER_LINE;
                return s;
            }
        }

        public static FloppyImage driveA = new FloppyImage();
        public static FloppyImage driveB = new FloppyImage();

        // Double buffer: the emulation thread renders into _renderBuffer (no contention); at the
        // end of each frame it is copied into ScreenBuffer under _syncLock, which the GL thread
        // reads through SnapshotScreen. This prevents the GL thread from ever sampling a
        // half-drawn frame (which showed up as flickering scanlines).
        public static uint[] ScreenBuffer = new uint[ScreenViewSize];
        static readonly uint[] _renderBuffer = new uint[ScreenViewSize];
        public static bool IsMouseCaptured = false;
        public static MainWindow MainWindow;

        static readonly object _syncLock = new object();
        static Thread _thread;
        static bool _isRunning;
        public static bool IsPaused = false;

        // Freeze rendezvous used by RunWhilePaused: _freezeRequest asks the emulation thread to
        // park at the next frame boundary and _frozen is its acknowledgment. Unlike IsPaused the
        // parked loop does not drain SDL events either — HandleEvents mutates ACIA/IKBD state,
        // which must not change under a snapshot writer.
        static volatile bool _freezeRequest;
        static volatile bool _frozen;

        static public event Action OnFrameComplete;

        static public void Init(MainWindow mainWindow)
        {
            MainWindow = mainWindow;

            // Search for gamepads

            for (int i = 0; i < SDL.SDL_NumJoysticks(); i++)
            {
                if (SDL.SDL_IsGameController(i) == SDL.SDL_bool.SDL_TRUE)
                {
                    GamepadController = SDL.SDL_GameControllerOpen(i);
                    SDL_GameControllerOpen(i);
                    ColoredConsole.WriteLine("[[green]]Gamepad found![[/green]]", ConfigOptions.DebugModes.Quiet);
                    break;
                }
            }

            // Init audio output

            var want = new SDL.SDL_AudioSpec
            {
                freq = ConfigOptions.RunninConfig.SampleRate,
                format = SDL.AUDIO_F32SYS,
                channels = 1,               // Mono
                samples = 1024,             // Buffer size
                callback = YM2149.AudioCallback
            };

            _audiocallback = want.callback; // avoid GC

            SDL.SDL_AudioSpec have;
            _audiodev = SDL.SDL_OpenAudioDevice(null, 0, ref want, out have, 0);
            
            if (_audiodev == 0)
            {
                Console.WriteLine("SDL_OpenAudioDevice error: " + SDL.SDL_GetError());
                return;
            }

            if (have.freq != ConfigOptions.RunninConfig.SampleRate)
            {
                ColoredConsole.WriteLine($"Warning: Sample rate [[yellow]]{ConfigOptions.RunninConfig.SampleRate}[[/yellow]] not supported, got [[green]]{have.freq}[[/green]] instead.", ConfigOptions.DebugModes.Quiet);
                ConfigOptions.RunninConfig.SampleRate = have.freq;
            }

            // Init runs on the UI thread (MainWindow.OnOpened), the SDL video thread.
            _sdlPumpTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(4), DispatcherPriority.Input,
                                                (_, _) => SDL_PumpEvents());
            _sdlPumpTimer.Start();

            // Arranque desde snapshot si se pidió por línea de comandos (--snapshot=<path>);
            // la máquina se enciende directamente con el estado restaurado, sin arrancar TOS.
            string startupSnapshot = Config.StartupSnapshot;
            Config.StartupSnapshot = "";    // sólo aplica al primer encendido, no a los resets

            if (string.IsNullOrEmpty(startupSnapshot))
            {
                TurnOn();
            }
            else
            {
                Snapshot.SnapshotFile snap = null;
                string error = null;

                try
                {
                    snap = Snapshot.ReadFile(startupSnapshot);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                // Con un archivo inválido (o si aplicar falla a medias, con fallback interno)
                // se sigue con un arranque limpio: el emulador siempre queda encendido.
                if (snap != null && StartWithSnapshot(snap, out error))
                    ColoredConsole.WriteLine($"Snapshot restored from [[green]]{startupSnapshot}[[/green]]", ConfigOptions.DebugModes.Quiet);
                else
                {
                    ColoredConsole.WriteLine($"Could not restore startup snapshot [[red]]{startupSnapshot}[[/red]]: {error}");

                    if (snap == null)
                        TurnOn();
                }
            }

            // Unpause only after TurnOn() has created _ym: the moment the device is unpaused
            // the SDL audio thread starts firing YM2149.AudioCallback, which reads ASEMain._ym.
            SDL.SDL_PauseAudioDevice(_audiodev, 0); // Turn on sound
        }

        public static void EmulatorLoop()
        {
            //  Speed control variables
            const double frame = 1.0 / 50.0; // 1 ST pal frame -> 0.02 s
            var sw = Stopwatch.StartNew();
            double next = 0.0;
            var last = Stopwatch.StartNew();

            while (_isRunning)
            {
                // Freeze rendezvous (snapshot/screenshot): park at the frame boundary until the
                // writer finishes, so no machine state mutates under it.
                if (_freezeRequest)
                {
                    _frozen = true;
                    while (_freezeRequest && _isRunning)
                        Thread.Sleep(1);
                    _frozen = false;
                    continue;
                }

                uint frameStart = SDL_GetTicks();

                bool isSTE = ConfigOptions.RunninConfig.STModel == ConfigOptions.STModels.STE;

                uint baseHigh = _mem.Read8(Memory.STPortAdress.ST_SCRHIGHADDR);
                uint baseMid = _mem.Read8(Memory.STPortAdress.ST_SCRMIDADDR);
                uint baseLow = isSTE ? (uint)(_mem.Read8(Memory.STPortAdress.ST_SCRLOWADDR) & 0xFE) : 0;  // low byte is STE only
                uint videoBase = (baseHigh << 16) | (baseMid << 8) | baseLow;

                // Reload the shifter address from the video base for the new frame. From here on
                // the Video Address Pointer is computed live by VideoTiming (read back through
                // $FF8205/07/09), so it no longer has to be written into the port array.
                VideoTiming.StartFrame(videoBase);

                if (!IsPaused)
                {
                    /*
                     * The PAL Atari ST has 313 scanlines per frame. Each lasts 512 CPU cycles at
                     * 8 MHz (64 us): 448 active + 64 H-Blank. Sync ($FF820A) and resolution
                     * ($FF8260) writes are timestamped by their CPU clock as the chunks run, so
                     * VideoTiming can resolve the Display Enable window — and therefore the
                     * top/bottom/left/right borders and the live Video Address Pointer — per line.
                     */
                    // Absolute clock at the start of the frame. Every line's run target is taken
                    // from this base (lineBase + N*512), so RunForCycles' per-instruction overshoot
                    // is absorbed line to line instead of accumulating — the video boundaries stay
                    // locked to 512 cycles/line, which Spectrum 512's screen-wide cycle-counted
                    // palette routine relies on (otherwise its colours drift down the picture).
                    long frameClockBase = CPU._moira.Clock;

                    for (int scanline = 0; scanline < 313; scanline++)
                    {
                        // Input first: consume whatever the UI thread has pumped into SDL's
                        // queue and feed it to the IKBD at scanline granularity, so a key or
                        // gamepad change lands mid-frame like on a real ST instead of waiting
                        // for the next frame boundary.
                        DrainSdlEvents();

                        long lineBase = frameClockBase + (long)scanline * VideoTiming.CYCLES_PER_LINE;
                        VideoTiming.StartLine(scanline, lineBase);

                        int ElapsedCycles = 0;

                        // Correr la CPU hasta que cae la señal Display Enable (DE).
                        // En 50Hz, DE cae en el ciclo 376. Aquí es donde DEBEN saltar el Timer B y el HBL.
                        int cyclesToDEStop = 376;
                        RunCpuUntil(lineBase + cyclesToDEStop);
                        ElapsedCycles += cyclesToDEStop;

                        // Disparar Timer B exactamente al caer Display Enable (como el hardware real).
                        // Comprobamos si es una línea visible (en PAL estándar va de la 63 a la 262).
                        bool isVisibleLine = (scanline >= 63 && scanline < 263);
                        if (isVisibleLine)
                        {
                            _mfp.TickTimerA_EventCount();
                            _mfp.TickTimerB_EventCount();
                        }

                        // Disparar HBL justo al inicio del H-Blank
                        _mfp.irqController.RaiseHBL();

                        // Correr el resto de la scanline (los 136 ciclos restantes del H-Blank / Borde derecho)
                        int cyclesToLineEnd = VideoTiming.CYCLES_PER_LINE - cyclesToDEStop;
                        RunCpuUntil(lineBase + VideoTiming.CYCLES_PER_LINE);
                        ElapsedCycles += cyclesToLineEnd;

                        // Sincronizar el resto de periféricos (el WD1772 se sincroniza por
                        // slice dentro de RunCpuUntil: la entrega DMA de los discos STX
                        // necesita granularidad de pocos ciclos)
                        ACIA.Sync(ElapsedCycles);

                        // Resolver la línea (bordes abiertos, etc.)
                        VideoTiming.LineInfo line = VideoTiming.ResolveLine();

                        if (line.Visible)
                            AtariStRenderer.BlitLineWithBorders(_renderBuffer, line);
                    }

                    // Publish the finished frame to the GL thread atomically.
                    lock (_syncLock)
                        Array.Copy(_renderBuffer, ScreenBuffer, ScreenViewSize);

                    // Vsync completed
                    _mfp.irqController.RaiseVBL();

                    // For future use: recording, etc.
                    OnFrameComplete?.Invoke();

                    MainWindow.RefreshDriveLed();

                    next += frame;

                    if (!ConfigOptions.RunninConfig.MaxSpeed)
                    {
                        // Dynamic wait loop. It combines longer waits using SDL_Delay, which do not block the process,
                        // and then performs a fine-grained adjustment during the last few microseconds using SpinWait,
                        // which does block but is more precise. The longer waits allow SDL to keep collecting events in
                        // the meantime, making the emulator feel smoother.
                        while (true)
                        {
                            double now = (double)sw.ElapsedTicks / Stopwatch.Frequency;
                            double remaining = next - now;
                            if (remaining <= 0) break;

                            if (remaining > 0.002) // > 2 ms, delay - sleep thread
                                SDL_Delay(1);
                            else
                                Thread.SpinWait(10); // < 2 ms, active wait
                        }

                        // Check if we are late
                        double late = (double)sw.ElapsedTicks / Stopwatch.Frequency - next;
                        if (late > 0.1)
                            next = (double)sw.ElapsedTicks / Stopwatch.Frequency;
                    }
                }
                else
                {
                    // Paused: keep consuming input (F12 capture toggle, gamepad hot-plug)
                    // without busy-spinning a whole core.
                    DrainSdlEvents();
                    SDL_Delay(5);
                }
            }
        }

        /// <summary>
        /// Runs the CPU until the emulated clock reaches the ABSOLUTE <paramref name="targetClock"/>,
        /// split into short slices (<see cref="CpuSliceCycles"/>). After each slice the MFP timers,
        /// the PSG and the STE sound are advanced by the cycles actually executed, so:
        ///   * the interrupt level handed to the 68000 tracks the timers throughout the scanline
        ///     (not just at the chunk boundaries) — timer interrupts, e.g. the 200 Hz Timer C, are
        ///     taken promptly; and
        ///   * a digi sample a timer interrupt writes to the PSG is captured within one slice.
        ///
        /// The target is ABSOLUTE on purpose: RunForCycles overshoots by up to one instruction, so
        /// taking each line's target from a fixed per-frame base (rather than "current clock + n")
        /// makes every line absorb the previous line's overshoot instead of accumulating it. The
        /// video line boundaries therefore stay locked to 512 cycles/line. Cycle-counted raster
        /// code (Spectrum 512 rewrites the palette across the whole screen from a single top-of-
        /// frame sync) depends on this: with accumulating overshoot the palette drifted a little
        /// further off on every line, which showed up as horizontal stripes worsening downwards.
        /// </summary>

        static void RunCpuUntil(long targetClock)
        {
            bool isSTE = ConfigOptions.RunninConfig.STModel == ConfigOptions.STModels.STE;

            while (CPU._moira.Clock < targetClock)
            {
                long before = CPU._moira.Clock;
                long want = targetClock - before;
                if (want > CpuSliceCycles) want = CpuSliceCycles;

                CPU._moira.RunForCycles(want);

                int elapsed = (int)(CPU._moira.Clock - before);
                if (elapsed <= 0) break;   // no progress (e.g. a swallowed exception): never spin forever

                _mfp.UpdateTimers(elapsed);
                _ym.Sync(elapsed);
                if (isSTE) STEDmaSound.Tick(elapsed);
                WD1772.Tick();
            }
        }

        /// <summary>
        /// Consumes every event currently in SDL's queue and forwards it to
        /// <see cref="HandleEvents"/>. Runs on the emulation thread: SDL_PeepEvents(GETEVENT)
        /// only takes the queue lock — it never pumps the platform event loop, which stays on
        /// the UI thread (<see cref="_sdlPumpTimer"/>).
        /// </summary>
        static void DrainSdlEvents()
        {
            while (true)
            {
                int n = SDL_PeepEvents(_drainBuffer, MaxEventsPerDrain, SDL_eventaction.SDL_GETEVENT,
                                       SDL_EventType.SDL_FIRSTEVENT, SDL_EventType.SDL_LASTEVENT);
                if (n <= 0) break;    // empty (0) or SDL error (-1, e.g. mid-shutdown)

                for (int i = 0; i < n; i++)
                    HandleEvents(_drainBuffer[i]);

                if (n < MaxEventsPerDrain) break;
            }
        }

        /// <summary>Copies the latest published frame into <paramref name="dest"/> under the
        /// frame lock, so the GL thread never reads a frame that is still being drawn.</summary>
        public static void SnapshotScreen(byte[] dest)
        {
            lock (_syncLock)
                System.Buffer.BlockCopy(ScreenBuffer, 0, dest, 0, dest.Length);
        }

        /// <summary>
        /// Parks the emulation thread at the next frame boundary, runs <paramref name="action"/>
        /// (writing a snapshot or a screenshot) and resumes it. Must NOT be called from the
        /// emulation thread itself — it would wait for its own acknowledgment.
        /// </summary>
        public static bool RunWhilePaused(Action action, out string error)
        {
            error = null;

            _freezeRequest = true;
            try
            {
                // The loop acknowledges within one frame (~20 ms); the timeout only covers the
                // thread not running at all (shutdown, failed init).
                var sw = Stopwatch.StartNew();
                while (!_frozen && _thread != null && _thread.IsAlive && sw.ElapsedMilliseconds < 1000)
                    Thread.Sleep(1);

                action();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                _freezeRequest = false;
            }
        }

        /// <summary>Saves a full machine snapshot (same format as the Debug window's Snapshot
        /// button) into the configured snapshots directory with a timestamped name, pausing the
        /// emulation while the file is written. Bound to F11 and File > Save snapshot.</summary>
        public static bool SaveSnapshot(out string path, out string error)
        {
            string dir = Config.SnapshotsDir;
            string file = NewTimestampedPath(dir, "ase_snapshot", "snap");
            path = file;

            return RunWhilePaused(() =>
            {
                Directory.CreateDirectory(dir);
                Snapshot.Save(file);
            }, out error);
        }

        /// <summary>Saves the current ST screen as a PNG into the configured screenshots
        /// directory with a timestamped name, pausing the emulation while the file is written.
        /// Bound to Shift+F11.</summary>
        public static bool SaveScreenshot(out string path, out string error)
        {
            string dir = Config.ScreenshotsDir;
            string file = NewTimestampedPath(dir, "ase_screenshot", "png");
            path = file;

            return RunWhilePaused(() =>
            {
                Directory.CreateDirectory(dir);
                WriteScreenshotPng(file);
            }, out error);
        }

        static string NewTimestampedPath(string dir, string prefix, string extension)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = Path.Combine(dir, $"{prefix}_{stamp}.{extension}");

            // Several captures within the same second: append a counter
            for (int n = 2; File.Exists(path); n++)
                path = Path.Combine(dir, $"{prefix}_{stamp}_{n}.{extension}");

            return path;
        }

        /// <summary>
        /// Writes the last published frame as a PNG. Follows ShowBorders (full overscan buffer or
        /// just the 640x200 display area) and doubles each scanline vertically — the framebuffer
        /// is already horizontally doubled, so the image keeps the on-screen aspect ratio.
        /// </summary>
        static void WriteScreenshotPng(string path)
        {
            bool borders = ConfigOptions.RunninConfig.ShowBorders;
            int srcX = borders ? 0 : VideoTiming.DISPLAY_ORIGIN_X;
            int srcY = borders ? 0 : VideoTiming.DISPLAY_ORIGIN_Y;
            int width = borders ? VideoTiming.BUFFER_WIDTH : VideoTiming.DISPLAY_TEX_WIDTH;
            int height = borders ? VideoTiming.BUFFER_HEIGHT : VideoTiming.DISPLAY_TEX_HEIGHT;

            // ScreenBuffer pixels are RGBA in memory (0xAABBGGRR), same layout the GL texture uses
            byte[] row = new byte[width * 4];

            using var bmp = new WriteableBitmap(new PixelSize(width, height * 2), new Vector(96, 96),
                                                PixelFormats.Rgba8888, AlphaFormat.Opaque);
            using (var fb = bmp.Lock())
            {
                lock (_syncLock)
                {
                    for (int y = 0; y < height; y++)
                    {
                        System.Buffer.BlockCopy(ScreenBuffer,
                            ((srcY + y) * VideoTiming.BUFFER_WIDTH + srcX) * 4, row, 0, row.Length);
                        Marshal.Copy(row, 0, fb.Address + (2 * y) * fb.RowBytes, row.Length);
                        Marshal.Copy(row, 0, fb.Address + (2 * y + 1) * fb.RowBytes, row.Length);
                    }
                }
            }

            bmp.Save(path);
        }

        /// <summary>
        /// Powers on the emulated machine and starts the emulation thread.
        /// </summary>
        /// <param name="afterInit">Optional hook that runs after the CPU/chips have been
        /// initialized but BEFORE the emulation thread starts — used to apply a snapshot
        /// over the freshly initialized machine without any thread contention.</param>
        public static bool TurnOn(Action afterInit = null)
        {
            // Starts with mouse uncaptured
            CaptureMouse(false);

            _ym = new YM2149(sampleRate: Config.ConfigOptions.RunninConfig.SampleRate, chipClockHz: 2000000.0);

            // TOS missing or invalid: leave the machine off (black screen). The user can pick a
            // TOS in the Configuration window, whose OK triggers a HardReset and lands here again.
            if (!CPU.InitCpu())
                return false;

            _isRunning = true;
            afterInit?.Invoke();

            _thread = new Thread(EmulatorLoop);
            _thread.Start();

            return true;
        }

        public static void HardReset()
        {
            StopEmulationThread();
            TurnOn();
        }

        /// <summary>Stops the emulation thread, waiting until it exits. Safe to call when the
        /// machine never powered on (e.g. first launch without a TOS ROM).</summary>
        static void StopEmulationThread()
        {
            _isRunning = false;

            if (_thread != null && _thread.IsAlive)
                _thread.Join();
        }

        /// <summary>
        /// Restores a machine snapshot: stops the emulation thread (like a hard reset), switches
        /// the running configuration to the snapshot's ST model and RAM size, re-initializes the
        /// machine and applies the saved state before restarting the thread. The persisted user
        /// config file is not touched. If the file is invalid the running machine is left
        /// untouched; if applying fails midway it falls back to a clean boot.
        /// </summary>
        public static bool RestoreSnapshot(string path, out string error)
        {
            Snapshot.SnapshotFile snap;
            try
            {
                snap = Snapshot.ReadFile(path);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            StopEmulationThread();

            return StartWithSnapshot(snap, out error);
        }

        /// <summary>
        /// Powers on the machine from an already-parsed snapshot. The emulation thread must NOT
        /// be running. On failure it reverts to the previous configuration and boots clean, so
        /// the machine is always left running.
        /// </summary>
        static bool StartWithSnapshot(Snapshot.SnapshotFile snap, out string error)
        {
            error = null;

            var prevModel = ConfigOptions.RunninConfig.STModel;
            var prevRam = ConfigOptions.RunninConfig.RAMConfiguration;

            ConfigOptions.RunninConfig.STModel = snap.Model;
            ConfigOptions.RunninConfig.RAMConfiguration = snap.RamConfig;

            try
            {
                return TurnOn(() => Snapshot.Apply(snap));
            }
            catch (Exception ex)
            {
                // Estado a medio aplicar: volver a un arranque limpio con la config anterior
                error = ex.Message;
                ConfigOptions.RunninConfig.STModel = prevModel;
                ConfigOptions.RunninConfig.RAMConfiguration = prevRam;

                TurnOn();
                return false;
            }
        }

        public static void Shutdown()
        {
            _isRunning = false;

            // Wait for the emulation thread before tearing SDL down: it reads the event queue
            // (SDL_PeepEvents) every scanline and must not race SDL_Quit. It never blocks on
            // the UI thread (all its dispatcher calls are fire-and-forget), so joining from
            // here cannot deadlock.
            if (_thread != null && _thread != Thread.CurrentThread)
                _thread.Join();

            _sdlPumpTimer?.Stop();

            if (_audiodev != 0)
            {
                SDL.SDL_CloseAudioDevice(_audiodev);
                _audiodev = 0;
            }
            if (GamepadController != nint.Zero)
            {
                SDL.SDL_GameControllerClose(GamepadController);
                GamepadController = nint.Zero;
            }
            SDL.SDL_Quit();
        }

        // Middle mouse button toggles input capture, like F12. The same physical press can be
        // reported twice — through the SDL queue and through the Avalonia overlay, depending on
        // platform — so the toggle fires only on the press edge and re-arms on release.
        static volatile bool _middleButtonPressed;

        public static void MiddleButtonDown()
        {
            if (_middleButtonPressed)
                return;

            _middleButtonPressed = true;
            CaptureMouse(!IsMouseCaptured);
        }

        public static void MiddleButtonUp() => _middleButtonPressed = false;

        public static void CaptureMouse(bool capture = true)
        {
            IsMouseCaptured = capture;

            // Also reached from the emulation thread (F12 in HandleEvents): the SDL video call
            // and the Avalonia properties must run on the UI thread.
            Dispatcher.UIThread.Post(() =>
            {
                SDL_SetRelativeMouseMode(capture ? SDL.SDL_bool.SDL_TRUE : SDL.SDL_bool.SDL_FALSE);

                MainWindow.Cursor = capture ? new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.None) : null;
                MainWindow.ShowMenu(!capture);
            }, DispatcherPriority.Input);
        }

        public static void GamepadButton(ConfigOptions.GamepadButtonsMapping buttonMapping, bool pressed)
        {
            switch (buttonMapping)
            {
                case ConfigOptions.GamepadButtonsMapping.Fire:
                    ACIA.UpdateJoystick(ACIA.JOY_FIRE, pressed);
                    break;
                case ConfigOptions.GamepadButtonsMapping.Up:
                    ACIA.UpdateJoystick(ACIA.JOY_UP, pressed);
                    break;
                case ConfigOptions.GamepadButtonsMapping.Space:
                    ACIA.PushIkbd((byte)(pressed ? 0x39 : (0x39 | 0x80)));
                    break;
                case ConfigOptions.GamepadButtonsMapping.Y:
                    ACIA.PushIkbd((byte)(pressed ? 0x15 : (0x15 | 0x80)));
                    break;
                case ConfigOptions.GamepadButtonsMapping.N:
                    ACIA.PushIkbd((byte)(pressed ? 0x31 : (0x31 | 0x80)));
                    break;
                case ConfigOptions.GamepadButtonsMapping.T:
                    ACIA.PushIkbd((byte)(pressed ? 0x14 : (0x14 | 0x80)));
                    break;
            }
        }

        public static void HandleEvents(SDL_Event e)
        {
            bool IgnoreCtlrKeyUp = false;

            // First, check the host keyboard that emulates the joystick
            if ((e.type == SDL_EventType.SDL_KEYDOWN && e.key.repeat == 0) || e.type == SDL_EventType.SDL_KEYUP)
            {
                bool pressed = (e.type == SDL_EventType.SDL_KEYDOWN);
                bool isJoyKey = true;

                if(e.key.keysym.scancode == ConfigOptions.RunninConfig.KeyJoy1Up)
                    ACIA.UpdateJoystick(ACIA.JOY_UP, pressed);
                else if (e.key.keysym.scancode == ConfigOptions.RunninConfig.KeyJoy1Down)
                    ACIA.UpdateJoystick(ACIA.JOY_DOWN, pressed);
                else if (e.key.keysym.scancode == ConfigOptions.RunninConfig.KeyJoy1Left)
                    ACIA.UpdateJoystick(ACIA.JOY_LEFT, pressed);
                else if (e.key.keysym.scancode == ConfigOptions.RunninConfig.KeyJoy1Right)
                    ACIA.UpdateJoystick(ACIA.JOY_RIGHT, pressed);
                else if (e.key.keysym.scancode == ConfigOptions.RunninConfig.KeyJoy1Fire)
                    ACIA.UpdateJoystick(ACIA.JOY_FIRE, pressed);
                else
                    isJoyKey = false;

                if (isJoyKey) return;
            }

            // again, for the gamepad
            if (e.type == SDL.SDL_EventType.SDL_CONTROLLERBUTTONDOWN || e.type == SDL.SDL_EventType.SDL_CONTROLLERBUTTONUP)
            {
                bool pressed = (e.type == SDL.SDL_EventType.SDL_CONTROLLERBUTTONDOWN);
                var btn = (SDL.SDL_GameControllerButton)e.cbutton.button;

                switch (btn)
                {
                    case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_UP:
                        ACIA.UpdateJoystick(ACIA.JOY_UP, pressed);
                        break;
                    case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_DOWN:
                        ACIA.UpdateJoystick(ACIA.JOY_DOWN, pressed);
                        break;
                    case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_LEFT:
                        ACIA.UpdateJoystick(ACIA.JOY_LEFT, pressed);
                        break;
                    case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_RIGHT:
                        ACIA.UpdateJoystick(ACIA.JOY_RIGHT, pressed);
                        break;
                    case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_A:
                        GamepadButton(Config.ConfigOptions.RunninConfig.GamepadButtonA, pressed);
                        break;
                    case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_B:
                        GamepadButton(Config.ConfigOptions.RunninConfig.GamepadButtonB, pressed);
                        break;
                    case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_X:
                        GamepadButton(Config.ConfigOptions.RunninConfig.GamepadButtonX, pressed);
                        break;
                    case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_Y:
                        GamepadButton(Config.ConfigOptions.RunninConfig.GamepadButtonY, pressed);
                        break;
                    case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_LEFTSHOULDER:
                        GamepadButton(Config.ConfigOptions.RunninConfig.GamepadButtonLB, pressed);
                        break;
                    case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_RIGHTSHOULDER:
                        GamepadButton(Config.ConfigOptions.RunninConfig.GamepadButtonRB, pressed);
                        break;
                    case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_LEFTSTICK:
                        GamepadButton(Config.ConfigOptions.RunninConfig.GamepadButtonLS, pressed);
                        break;
                    case SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_RIGHTSTICK:
                        GamepadButton(Config.ConfigOptions.RunninConfig.GamepadButtonRS, pressed);
                        break;
                }

                return;
            }

            // Gamepad connected
            if (e.type == SDL.SDL_EventType.SDL_CONTROLLERDEVICEADDED)
            {
                int deviceIndex = e.cdevice.which;
                if (GamepadController == nint.Zero && SDL.SDL_IsGameController(deviceIndex) == SDL.SDL_bool.SDL_TRUE)
                {
                    GamepadController = SDL.SDL_GameControllerOpen(deviceIndex);
                    ColoredConsole.WriteLine("[[green]]Gamepad connected![[/green]]", ConfigOptions.DebugModes.Quiet);
                }
                return;
            }

            // Gamepad disconnected
            if (e.type == SDL.SDL_EventType.SDL_CONTROLLERDEVICEREMOVED)
            {
                if (GamepadController != nint.Zero)
                {
                    var instanceId = e.cdevice.which;
                    var currentInstanceId = SDL.SDL_JoystickInstanceID(SDL.SDL_GameControllerGetJoystick(GamepadController));

                    if (instanceId == currentInstanceId)
                    {
                        SDL.SDL_GameControllerClose(GamepadController);
                        GamepadController = nint.Zero;

                        // Reset joystick state
                        ACIA.UpdateJoystick(ACIA.JOY_UP, false);
                        ACIA.UpdateJoystick(ACIA.JOY_DOWN, false);
                        ACIA.UpdateJoystick(ACIA.JOY_LEFT, false);
                        ACIA.UpdateJoystick(ACIA.JOY_RIGHT, false);
                        ACIA.UpdateJoystick(ACIA.JOY_FIRE, false);

                        ColoredConsole.WriteLine("[[yellow]]Gamepad disconnected![[/yellow]]", ConfigOptions.DebugModes.Quiet);
                    }
                }
                return;
            }

            if (e.type == SDL.SDL_EventType.SDL_CONTROLLERAXISMOTION)
            {
                var axis = (SDL.SDL_GameControllerAxis)e.caxis.axis;
                int v = e.caxis.axisValue;

                // left stick
                if (axis == SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTX)
                {
                    bool left = v < -GamepadDeadzone;
                    bool right = v > GamepadDeadzone;

                    ACIA.UpdateJoystick(ACIA.JOY_LEFT, left);
                    ACIA.UpdateJoystick(ACIA.JOY_RIGHT, right);

                    // center stick
                    if (!left && !right)
                    {
                        ACIA.UpdateJoystick(ACIA.JOY_LEFT, false);
                        ACIA.UpdateJoystick(ACIA.JOY_RIGHT, false);
                    }
                }
                else if (axis == SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTY)
                {
                    bool up = v < -GamepadDeadzone;
                    bool down = v > GamepadDeadzone;

                    ACIA.UpdateJoystick(ACIA.JOY_UP, up);
                    ACIA.UpdateJoystick(ACIA.JOY_DOWN, down);

                    if (!up && !down)
                    {
                        ACIA.UpdateJoystick(ACIA.JOY_UP, false);
                        ACIA.UpdateJoystick(ACIA.JOY_DOWN, false);
                    }
                }

                return;
            }

            if (e.type == SDL_EventType.SDL_KEYDOWN && e.key.repeat == 0)
            {
                var key = e.key.keysym;

                // Pressing F12 disables the menu so it doesn't interfere with keyboard input and captures
                // the mouse in the window.
                if (e.key.keysym.scancode == SDL.SDL_Scancode.SDL_SCANCODE_F12)
                {
                    CaptureMouse(!IsMouseCaptured);
                    return;
                }

                // F11 saves a machine snapshot; Shift+F11 saves a PNG screenshot. Posted to the
                // UI thread because the save parks the emulation thread at a frame boundary
                // (RunWhilePaused) and this handler runs on that very thread.
                if (key.scancode == SDL.SDL_Scancode.SDL_SCANCODE_F11)
                {
                    bool shift = (key.mod & SDL.SDL_Keymod.KMOD_SHIFT) != 0;

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (shift)
                            MainWindow.DoSaveScreenshot();
                        else
                            MainWindow.DoSaveSnapshot();
                    }, DispatcherPriority.Input);

                    return;
                }

                if (e.type == SDL.SDL_EventType.SDL_KEYDOWN)
                {
                    int scancode = (int)e.key.keysym.scancode;

                    if (scancode < ACIA.AtariScancodes.Length && ACIA.AtariScancodes[scancode] != 0)
                        ACIA.PushIkbd(ACIA.AtariScancodes[scancode]);
                }
            }

            if (e.type == SDL.SDL_EventType.SDL_KEYUP)
            {
                if (e.key.keysym.scancode == SDL.SDL_Scancode.SDL_SCANCODE_LCTRL && IgnoreCtlrKeyUp)
                {
                    IgnoreCtlrKeyUp = false;
                    return;
                }

                if (e.key.keysym.scancode == SDL.SDL_Scancode.SDL_SCANCODE_F12 ||
                    e.key.keysym.scancode == SDL.SDL_Scancode.SDL_SCANCODE_F11)
                {
                    return;
                }
                else
                {
                    int scancode = (int)e.key.keysym.scancode;

                    if (scancode < ACIA.AtariScancodes.Length && ACIA.AtariScancodes[scancode] != 0)
                        // Scancode | 0x80 -> Scancode que se ha soltado en el ST
                        ACIA.PushIkbd((byte)(ACIA.AtariScancodes[scancode] | 0x80));
                }
            }

            // Middle button toggles capture regardless of the current state (that is how you
            // capture in the first place), so it is handled outside the IsMouseCaptured block.
            if (e.type == SDL.SDL_EventType.SDL_MOUSEBUTTONDOWN && e.button.button == SDL.SDL_BUTTON_MIDDLE)
            {
                MiddleButtonDown();
                return;
            }

            if (e.type == SDL.SDL_EventType.SDL_MOUSEBUTTONUP && e.button.button == SDL.SDL_BUTTON_MIDDLE)
            {
                MiddleButtonUp();
                return;
            }

            if (IsMouseCaptured)
            {
                if (e.type == SDL.SDL_EventType.SDL_MOUSEMOTION && (e.motion.xrel != 0 || e.motion.yrel != 0))
                {
                    // Transporta el movimiento relativo del ratón dentro de la ventana del emulador al ST
                    ACIA.SendMousePacket(e.motion.xrel, e.motion.yrel);
                }
                else if (e.type == SDL.SDL_EventType.SDL_MOUSEBUTTONDOWN)
                {
                    if (e.button.button == SDL.SDL_BUTTON_LEFT)
                        ACIA._mouseButtons |= 0x02; // Bit 1
                    if (e.button.button == SDL.SDL_BUTTON_RIGHT)
                        ACIA._mouseButtons |= 0x01; // Bit 0

                    // Fuerza la actualización del ratón en el ST
                    ACIA.SendMousePacket(0, 0);
                }
                else if (e.type == SDL.SDL_EventType.SDL_MOUSEBUTTONUP)
                {
                    if (e.button.button == SDL.SDL_BUTTON_LEFT)
                        ACIA._mouseButtons &= ~0x02; // Apagar Bit 1
                    if (e.button.button == SDL.SDL_BUTTON_RIGHT)
                        ACIA._mouseButtons &= ~0x01; // Apagar Bit 0

                    ACIA.SendMousePacket(0, 0);
                }
            }
        }

    }
}
