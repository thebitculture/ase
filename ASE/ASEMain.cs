/*
 * 
 * ASE Main loop
 *
 * This is the main loop, along with window management and interaction with the operating system.
 * 
 * At the beginning of the project, this file was quite simple and was structured more for educational purposes
 * than for compatibility. It has now become one of the most essential parts of the emulator, and it is fairly complex.
 * 
 * Like the rest of the project, it is heavily commented thanks to AI from small pieces of clues, since I’m building
 * the entire emulator as I go, without much planning, and I need to keep everything well documented here.
 * My memory isn’t very good.
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
        // The framebuffers (and the GL texture) are allocated at the largest geometry of both
        // monitor types (see VideoTiming.MAX_*). The active mode — colour 832x288 @ 50 Hz or
        // monochrome 640x400 @ 71 Hz — fills a tightly-packed sub-region whose size is
        // VideoTiming.BUFFER_WIDTH x BUFFER_HEIGHT, and the GL thread resizes its texture to it.
        public const int ScreenViewSize = VideoTiming.MAX_VIEW_SIZE;

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

        // Frame hand-off to the GL thread, without copying anything.
        // Three buffers rotate through three roles: the one the emulation is drawing into, the one
        // holding the last finished frame, and the one the GL thread is uploading.
        static uint[] _renderBuffer = new uint[ScreenViewSize];
        static uint[] _readyBuffer = new uint[ScreenViewSize];
        static uint[] _glBuffer = new uint[ScreenViewSize];

        /// <summary>Whichever buffer holds the most recently published frame — the ready slot, or
        /// the GL thread's once it has taken it. Nobody writes to either, so screenshots can read
        /// it under the lock.</summary>
        static uint[] _lastPublished;

        /// <summary>Incremented on every published frame; the GL thread compares it against the
        /// one it last uploaded to know whether there is anything new to push to the texture.</summary>
        static long _frameSeq;

        static long _vblCount;

        /// <summary>VBLs (emulated frames) elapsed since the process started. Written only by the
        /// emulation thread. The floppy media-change detection measures its window in VBLs, like
        /// the TOS routine that polls it (see <see cref="FloppyImage.SignalDiskTransition"/>): the
        /// counter stops while the machine is paused, so a disk swapped from a dialog is still
        /// seen when the machine resumes instead of the window expiring in dead time.</summary>
        public static long VblCount => Volatile.Read(ref _vblCount);

        // Hard disk activity light. Unlike the floppy — whose commands are rare enough that the
        // FDC can post UI work for each one — a hard disk can serve thousands of accesses per
        // second (a program reading a file in a loop), so the emulation side only stamps the
        // current VBL here and MainWindow.RefreshDriveLed reads it once per frame. Measured in
        // VBLs rather than wall clock so the light freezes with a paused machine.
        static long _hdActivityVbl = long.MinValue / 2;

        /// <summary>How long the hard disk light stays lit after the last access (~200 ms).</summary>
        const long HdActivityHoldVbls = 10;

        /// <summary>Called by the hard disk emulations (ACSI commands, served GEMDOS file
        /// calls) from the emulation thread. Just a stamp: no UI work, no allocation.</summary>
        public static void SignalHardDiskActivity() => Volatile.Write(ref _hdActivityVbl, VblCount);

        /// <summary>True while the hard disk light should be on.</summary>
        public static bool HardDiskActive => VblCount - Volatile.Read(ref _hdActivityVbl) < HdActivityHoldVbls;

        public static bool IsMouseCaptured = false;
        public static MainWindow MainWindow;

        static readonly object _syncLock = new object();
        static Thread _thread;
        static bool _isRunning;
        public static bool IsPaused = false;

        /// <summary>Pauses/resumes the emulation together with the audio output. IsPaused
        /// alone parks the CPU but the SDL callback keeps sounding the PSG's frozen state,
        /// which bleeds into anything else playing audio (e.g. the library's game videos).</summary>
        public static void PauseEmulation(bool pause)
        {
            IsPaused = pause;

            if (_audiodev != 0)
                SDL.SDL_PauseAudioDevice(_audiodev, pause ? 1 : 0);
        }

        /// <summary>
        /// Runs <paramref name="action"/> with the SDL audio device locked, i.e. with the
        /// audio callback guaranteed not to be executing. This is how state the callback
        /// reads (the MT-32 backend, see <see cref="MidiManager"/>) is swapped or torn
        /// down without racing a render in flight. Works before the device exists too.
        /// </summary>
        public static void WithAudioLocked(Action action)
        {
            uint dev = _audiodev;
            if (dev == 0)
            {
                action();
                return;
            }

            SDL.SDL_LockAudioDevice(dev);
            try { action(); }
            finally { SDL.SDL_UnlockAudioDevice(dev); }
        }

        // Depth of the UI-window pause: every emulator dialog (library, configuration,
        // scraper, debugger, about) holds one reference while it is open, so stacked
        // windows (library configuration -> scraper) only resume when the last one closes.
        static int _uiPauseDepth;

        /// <summary>Pauses the emulation for a UI window. Reference-counted: call
        /// <see cref="ExitUiPause"/> exactly once when the window closes. While this pause
        /// is held, <see cref="HandleEvents"/> also stops feeding host input to the IKBD.
        /// UI thread only.</summary>
        public static void EnterUiPause()
        {
            if (++_uiPauseDepth == 1)
                PauseEmulation(true);
        }

        public static void ExitUiPause()
        {
            if (_uiPauseDepth > 0 && --_uiPauseDepth == 0)
                PauseEmulation(false);
        }

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
                // Interleaved stereo. The PSG is mono on real hardware, but the STE's DMA
                // sound has two channels and the LMC1992 an independent volume for each,
                // and folding them together here threw all of that away.
                channels = 2,
                samples = 1024,             // Buffer size, in frames (2 floats each)
                callback = YM2149.AudioCallback
            };

            _audiocallback = want.callback; // avoid GC

            SDL.SDL_AudioSpec have;
            _audiodev = SDL.SDL_OpenAudioDevice(null, 0, ref want, out have, 0);
            
            if (_audiodev == 0)
            {
                ColoredConsole.WriteLine($"[[red]]SDL_OpenAudioDevice error: {SDL.SDL_GetError()}[[/red]]");
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

            // Boot from a snapshot if requested on the command line (--snapshot=<path>);
            // the machine powers on directly with the restored state, without booting TOS.
            string startupSnapshot = Config.StartupSnapshot;
            Config.StartupSnapshot = "";    // only applies to the first power-on, not to resets

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

                // With an invalid file (or if applying fails midway, with an internal
                // fallback) we carry on with a clean boot: the emulator always ends up on.
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
            //  Speed control variables. The frame period follows the active monitor: ~0.02 s at
            //  50 Hz (colour PAL) or ~0.014 s at ~71 Hz (monochrome). Fixed for this thread's life
            //  because the monitor type only changes through a reset (which restarts the thread).
            double frame = VideoTiming.FRAME_SECONDS;
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
                long profileFrameStart = FrameProfiler.Stamp();

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
                     * The PAL Atari ST has 313 scanlines per frame. A 50 Hz line lasts 512 CPU
                     * cycles at 8 MHz (64 us) and a 60 Hz one 508. Sync ($FF820A) and resolution
                     * ($FF8260) writes are timestamped by their CPU clock as the chunks run, so
                     * VideoTiming can resolve the Display Enable window — and therefore the
                     * top/bottom/left/right borders and the live Video Address Pointer — per line.
                     */
                    // Absolute clock at the start of the frame, and the running start of the
                    // current line. Every run target inside a line is taken from lineBase, which
                    // advances by EXACT line lengths (512 at 50 Hz, 508 at 60 Hz, 224 in mono) and
                    // never by the real CPU clock, so RunForCycles' per-instruction overshoot is
                    // absorbed line to line instead of accumulating — that is what keeps the video
                    // boundaries locked to the cycle grid Spectrum 512's screen-wide cycle-counted
                    // palette routine relies on (otherwise its colours drift down the picture).
                    // Aligned DOWN to the 4-cycle MMU bus grid: the raw clock at this point still
                    // carries the overshoot of the previous frame's last instruction (0..~20
                    // cycles, arbitrary parity), and taking it verbatim re-phases every line of
                    // the new frame against the bus-slot grid ApplyBusWait aligns CPU accesses
                    // to. On real hardware that phase is rigid — the video fetches ARE the grid —
                    // and cycle-counted palette code (Spectrum 512) shows the drift as colour
                    // columns shimmering a few pixels frame to frame. All line lengths (512/508/
                    // 224) are multiples of 4, so alignment established here holds all frame.
                    long frameClockBase = CPU._moira.Clock & ~3L;
                    long lineBase = frameClockBase;
                    bool hitBreakpoint = false;

                    for (int scanline = 0; scanline < VideoTiming.SCANLINES_PER_FRAME; scanline++)
                    {
                        // Input first: consume whatever the UI thread has pumped into SDL's
                        // queue and feed it to the IKBD at scanline granularity, so a key or
                        // gamepad change lands mid-frame like on a real ST instead of waiting
                        // for the next frame boundary.
                        long profileMark = FrameProfiler.Stamp();
                        DrainSdlEvents();
                        profileMark = FrameProfiler.AddInput(profileMark);

                        VideoTiming.StartLine(scanline, lineBase);

                        // Run the CPU until the Display Enable (DE) signal drops. This is where
                        // Timer B and the HBL MUST fire (cycle 376 at 50 Hz colour, 184 in mono).
                        int cyclesToDEStop = VideoTiming.DE_STOP_CYCLE;
                        if (RunCpuUntil(lineBase + cyclesToDEStop)) { hitBreakpoint = true; break; }

                        // Fire Timer B exactly when Display Enable drops (like the real hardware).
                        // Only on lines that actually display, and that is the DE the GLUE is
                        // producing right now, NOT the fixed 63..262 window: with the top border
                        // opened the display really starts at line 34 and with the bottom one
                        // opened it runs past 263, and Timer B (whose event input is wired to DE)
                        // counts those extra lines on real hardware. Border-opening code relies
                        // on it — the classic "open both borders" routine arms Timer B for line 228
                        // counted from the *opened* top border (229 displayed lines), so a counter
                        // that only ever sees 200 events never fires and the bottom border stays.
                        //
                        // Timer B ONLY. It is the MFP's TBI pin that the GLUE's Display Enable
                        // drives; TAI is wired to the monochrome-detect line, and on the STE that
                        // same line also carries the DMA sound's XSINT — which is why
                        // STEDmaSound.SetXsint drives GPIP7 and Timer A's event input together.
                        // Ticking Timer A here as well fed it ~200 events per video frame: a
                        // replayer that arms Timer A in event-count mode to be told "end of sound
                        // frame" (the standard STE double-buffer scheme) was interrupted on every
                        // displayed line and reloaded the frame pointer continuously, so digital
                        // sound came out as noise. Hatari splits it the same way — video.c only
                        // calls MFP_TimerB_EventCount, dmaSnd.c only MFP_TimerA_EventCount.
                        if (VideoTiming.DisplayEnabledNow())
                            _mfp.TickTimerB_EventCount();

                        // Fire the HBL right at the start of the H-Blank
                        _mfp.irqController.RaiseHBL();

                        // Run the rest of the scanline (H-Blank / right border). Its length is NOT
                        // a constant: the GLUE's horizontal counter wraps at 508 cycles in 60 Hz
                        // and 512 in 50 Hz, so the frequency in effect at the wrap point decides.
                        // The CPU is run to the longest a line can be and the length is resolved
                        // afterwards — ResolveLineCycles only looks at the sync writes stamped at
                        // or before the wrap, so the answer is the same as stopping there, without
                        // the extra sync point per line that splitting the run would cost. On a
                        // 508-cycle line that leaves the CPU up to 4 cycles into the next line,
                        // which is what lineBase (below) absorbs, exactly like the per-instruction
                        // overshoot RunForCycles already produces.
                        if (RunCpuUntil(lineBase + VideoTiming.CYCLES_PER_LINE)) { hitBreakpoint = true; break; }
                        int lineCycles = VideoTiming.ResolveLineCycles();

                        // Everything above (both CPU slices and the chips they drive) is charged
                        // to 'cpu'; what follows, to 'video'.
                        profileMark = FrameProfiler.AddCpu(profileMark);

                        // Sync the remaining peripherals (the WD1772 is synced per slice
                        // inside RunCpuUntil: the DMA delivery of STX disks needs a
                        // granularity of a few cycles)
                        ACIA.Sync(lineCycles);
                        MidiAcia.Sync(lineCycles);

                        // Resolve the line (open borders, etc.)
                        VideoTiming.LineInfo line = VideoTiming.ResolveLine();

                        if (line.Visible)
                            AtariStRenderer.BlitLineWithBorders(_renderBuffer, line);

                        FrameProfiler.AddVideo(profileMark);

                        // Next line starts exactly one (508- or 512-cycle) line later. This walks
                        // in EXACT line lengths, never off the real CPU clock, so both the 60 Hz
                        // shortening above and RunForCycles' per-instruction overshoot are absorbed
                        // line to line instead of accumulating down the frame.
                        lineBase += lineCycles;
                    }

                    if (hitBreakpoint)
                    {
                        // Park the machine right where the CPU stopped (PC at the guarded
                        // instruction) and pop the debugger. The rest of the frame is abandoned:
                        // the partial framebuffer is not published (the previous full frame stays
                        // on screen) and the next run restarts cleanly from a frame boundary via
                        // VideoTiming.StartFrame. The DebugWindow constructor re-checks IsPaused
                        // and does its own freeze rendezvous, which this thread acknowledges from
                        // the paused branch below.
                        PauseEmulation(true);
                        Dispatcher.UIThread.Post(() =>
                            new DebugWindow().ShowDialog(MainWindow), DispatcherPriority.Input);
                        continue;
                    }

                    // Publish the finished frame to the GL thread atomically: a reference swap,
                    // no copy. The buffer taken in exchange still holds an old frame, which is
                    // harmless because every frame is redrawn in full — BlitLineWithBorders fills
                    // each row with the border colour before drawing the display over it, and it
                    // runs for every row of the visible window.
                    long profilePublish = FrameProfiler.Stamp();
                    lock (_syncLock)
                    {
                        (_renderBuffer, _readyBuffer) = (_readyBuffer, _renderBuffer);
                        _lastPublished = _readyBuffer;
                        _frameSeq++;
                    }
                    FrameProfiler.AddPublish(profilePublish);

                    // Vsync completed
                    _mfp.irqController.RaiseVBL();
                    Volatile.Write(ref _vblCount, _vblCount + 1);

                    // Steer the audio queue back towards its target latency. Once per frame is
                    // the right granularity: it reacts within a frame of a stall without the
                    // synthesis path having to look at the queue at all.
                    _ym.UpdateAudioFlowControl();

                    // The YM->MT-32 voice mapper samples the PSG once per VBL — the same
                    // 50 Hz grid YM register dumps use — and turns it into MIDI for the
                    // built-in module (a near no-op when nothing is mapped).
                    YmMidiMapper.OnFrame(_ym);

                    // For future use: recording, etc.
                    OnFrameComplete?.Invoke();

                    MainWindow.RefreshDriveLed();

                    // The wall-clock budget for this frame follows the cycles it ACTUALLY ran,
                    // not the fixed period: a screen held at 60 Hz gives 508-cycle lines, so the
                    // same 313 lines come out 1252 cycles shorter than the 512-cycle ones
                    // FRAME_SECONDS is derived from. Pacing those frames at the 50 Hz period runs
                    // the emulated CPU ~0.8% slow — and the whole audio pipeline is clocked off
                    // that CPU clock (YM2149.Sync, and the STE DMA sound engine it steps), so the
                    // emulator would feed the audio device slightly fewer samples than it eats,
                    // for a permanent underrun. 'frame' stays the fallback for a frame that never
                    // ran its scanlines. lineBase - frameClockBase is the sum of the exact line
                    // lengths, so this is the emulated duration of the frame to the cycle.
                    long frameCycles = lineBase - frameClockBase;
                    next += frameCycles > 0 ? frameCycles / 8_000_000.0 : frame;

                    if (!ConfigOptions.RunninConfig.MaxSpeed)
                    {
                        long profileIdle = FrameProfiler.Stamp();

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

                        FrameProfiler.AddIdle(profileIdle);

                        // Check if we are late
                        double late = (double)sw.ElapsedTicks / Stopwatch.Frequency - next;
                        if (late > 0.1)
                            next = (double)sw.ElapsedTicks / Stopwatch.Frequency;
                    }

                    FrameProfiler.EndFrame(profileFrameStart);
                }
                else
                {
                    // Paused: keep draining so the queue doesn't grow and releases /
                    // gamepad hot-plug are still seen (HandleEvents swallows the press
                    // events while paused), without busy-spinning a whole core.
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

        /// <returns>True when the run stopped early because the CPU reached a breakpoint
        /// (the PC is left AT the guarded instruction, not yet executed).</returns>
        static bool RunCpuUntil(long targetClock)
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

                // After the peripherals have caught up with the executed cycles, so the machine
                // state the debugger shows is consistent with the CPU clock.
                if (CPU._moira.BreakpointWasHit)
                {
                    // The GEMDOS hard drive rides on Moira breakpoints planted in its cartridge
                    // code: those are serviced here and the machine keeps running. Anything
                    // else is a user breakpoint and parks the machine in the debugger.
                    if (GemdosHD.TryHandleBreakpoint(CPU._moira.PC0))
                        continue;

                    return true;
                }
            }

            return false;
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

        /// <summary>
        /// Hands the GL thread the last published frame, or <c>null</c> when it already holds the
        /// newest one (the emulation has not finished another frame since — the GL thread usually
        /// draws faster than 50 Hz, so this is a common case and skips a pointless upload).
        /// <para>
        /// The returned buffer belongs to the caller until its next call: the emulation cannot
        /// draw into it in the meantime. <paramref name="seenSeq"/> is the caller's bookmark and
        /// is updated in place.
        /// </para>
        /// </summary>
        public static uint[] AcquireFrame(ref long seenSeq)
        {
            lock (_syncLock)
            {
                if (_frameSeq == seenSeq)
                    return null;

                seenSeq = _frameSeq;
                (_glBuffer, _readyBuffer) = (_readyBuffer, _glBuffer);
                return _glBuffer;
            }
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
        /// Writes the last published frame as a PNG. In colour it follows ShowBorders (full
        /// overscan buffer or just the 640x200 display area) and doubles each scanline vertically —
        /// the framebuffer is already horizontally doubled, so the image keeps the on-screen aspect
        /// ratio. In monochrome the buffer is a native 640x400 with square pixels, so it is written
        /// 1:1 (no crop, no doubling).
        /// </summary>
        static void WriteScreenshotPng(string path)
        {
            bool mono = VideoTiming.Mono;
            // Mono has no borders; colour honours ShowBorders (crop to the 320x200 display).
            bool borders = mono || ConfigOptions.RunninConfig.ShowBorders;
            int srcX = borders ? 0 : VideoTiming.DISPLAY_ORIGIN_X;
            int srcY = borders ? 0 : VideoTiming.DISPLAY_ORIGIN_Y;
            int width = borders ? VideoTiming.BUFFER_WIDTH : VideoTiming.DISPLAY_TEX_WIDTH;
            int height = borders ? VideoTiming.BUFFER_HEIGHT : VideoTiming.DISPLAY_TEX_HEIGHT;

            // Colour scanlines are shown at double height; mono pixels are ~square, written 1:1.
            int vScale = mono ? 1 : 2;

            // Framebuffer pixels are RGBA in memory (0xAABBGGRR), same layout the GL texture uses
            byte[] row = new byte[width * 4];

            using var bmp = new WriteableBitmap(new PixelSize(width, height * vScale), new Vector(96, 96),
                                                PixelFormats.Rgba8888, AlphaFormat.Opaque);
            using (var fb = bmp.Lock())
            {
                lock (_syncLock)
                {
                    // No frame published yet (machine off, or a capture before the very first
                    // frame): there is nothing to save, so no file is written.
                    if (_lastPublished == null)
                        return;

                    for (int y = 0; y < height; y++)
                    {
                        System.Buffer.BlockCopy(_lastPublished,
                            ((srcY + y) * VideoTiming.BUFFER_WIDTH + srcX) * 4, row, 0, row.Length);
                        for (int s = 0; s < vScale; s++)
                            Marshal.Copy(row, 0, fb.Address + (vScale * y + s) * fb.RowBytes, row.Length);
                    }
                }
            }

            // The encoder is picked by the options type (the old quality-based overload is
            // obsolete since Avalonia 12); the file is always a .png, see NewTimestampedPath above.
            bmp.Save(path, PngBitmapEncoderOptions.Default);
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

            // Hard disks attach at power-on, like the real hardware: the ACSI image first
            // (its partition count decides the GEMDOS drive's letter), then the host folder.
            Acsi.Initialize();
            GemdosHD.Initialize();

            // What the MIDI ACIA's DIN sockets are plugged into is decided at power-on,
            // so a reset is also what applies a MIDI configuration change (and what
            // power-cycles the built-in MT-32).
            MidiManager.Initialize();

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
            var prevMono = ConfigOptions.RunninConfig.MonochromeMonitor;

            ConfigOptions.RunninConfig.STModel = snap.Model;
            ConfigOptions.RunninConfig.RAMConfiguration = snap.RamConfig;
            ConfigOptions.RunninConfig.MonochromeMonitor = snap.Mono;

            try
            {
                bool ok = TurnOn(() => Snapshot.Apply(snap));
                MainWindow?.RefreshAspectRatio();   // the snapshot may have a different monitor type
                return ok;
            }
            catch (Exception ex)
            {
                // Half-applied state: fall back to a clean boot with the previous config
                error = ex.Message;
                ConfigOptions.RunninConfig.STModel = prevModel;
                ConfigOptions.RunninConfig.RAMConfiguration = prevRam;
                ConfigOptions.RunninConfig.MonochromeMonitor = prevMono;

                TurnOn();
                MainWindow?.RefreshAspectRatio();
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

            // After the audio device: with the callback gone, disposing the MT-32 (and
            // closing any host MIDI ports) cannot race a render.
            MidiManager.Shutdown();
            Acsi.Shutdown();    // the emulation thread is joined: no command can be in flight
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

            // While the emulation is paused a UI window owns the host input: swallow the
            // "press" events so keystrokes typed there (e.g. in the library search box)
            // don't pile up in the IKBD queue and replay into the program on resume.
            // Releases still pass — they only clear held keys/joystick directions, and a
            // break code for a key the ST never saw pressed is a no-op — and so do the
            // gamepad axis (returning to centre must keep clearing the direction state)
            // and hot-plug events.
            if (IsPaused)
            {
                switch (e.type)
                {
                    case SDL_EventType.SDL_KEYDOWN:
                    case SDL_EventType.SDL_MOUSEBUTTONDOWN:
                    case SDL_EventType.SDL_MOUSEMOTION:
                    case SDL_EventType.SDL_CONTROLLERBUTTONDOWN:
                        return;
                }
            }

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

                // Alt+Enter toggles full screen — the emulator convention, and one of the few
                // combinations no ST program wants (Esc and F11 are taken, by games and by the
                // snapshot). Consumed here so the ST never sees the Return.
                if ((key.scancode == SDL.SDL_Scancode.SDL_SCANCODE_RETURN ||
                     key.scancode == SDL.SDL_Scancode.SDL_SCANCODE_KP_ENTER) &&
                    (key.mod & SDL.SDL_Keymod.KMOD_ALT) != 0)
                {
                    Dispatcher.UIThread.Post(() => MainWindow.ToggleFullScreen(), DispatcherPriority.Input);
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
                        // Scancode | 0x80 -> scancode released on the ST
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
                    // Forwards the relative mouse motion inside the emulator window to the ST.
                    // The IKBD accumulates it and reports it on its own sample tick: a host
                    // mouse fires far more often than the 7812.5 baud line can carry.
                    ACIA.MouseMotion(e.motion.xrel, e.motion.yrel);
                }
                else if (e.type == SDL.SDL_EventType.SDL_MOUSEBUTTONDOWN ||
                         e.type == SDL.SDL_EventType.SDL_MOUSEBUTTONUP)
                {
                    // ACIA.MouseButtonChanged decides what the IKBD reports (relative packet,
                    // absolute packet, or nothing) and edge-guards the press: the same click
                    // also reaches the Avalonia overlay handler on Windows.
                    bool down = e.type == SDL.SDL_EventType.SDL_MOUSEBUTTONDOWN;
                    if (e.button.button == SDL.SDL_BUTTON_LEFT)
                        ACIA.MouseButtonChanged(left: true, pressed: down);
                    else if (e.button.button == SDL.SDL_BUTTON_RIGHT)
                        ACIA.MouseButtonChanged(left: false, pressed: down);
                }
            }
        }

    }
}
