/*
 * 
 * ASE Main loop
 * 
 * Official repository 👉 https://github.com/thebitculture/ase
 */

using SDL2;
using System.Diagnostics;
using Avalonia.Threading;
using TinyDialogsNet;
using static ASE.Config;
using static ASE.Video;
using static SDL2.SDL;

namespace ASE
{
    public static class ASEMain
    {
        public const int ScreenWidth = 640;
        public const int ScreenHeight = 400;
        public const int ScreenViewSize = ScreenWidth * ScreenHeight;

        public static Memory? _mem;
        public static MFP68901? _mfp;
        public static YM2149 _ym;

        static SDL.SDL_AudioCallback _audiocallback;
        static uint _audiodev;
        static nint GamepadController;
        const int GamepadDeadzone = 8000;

        public static FloppyImage driveA = new FloppyImage();
        public static FloppyImage driveB = new FloppyImage();

        public static uint[] ScreenBuffer = new uint[ScreenViewSize];
        public static bool IsMouseCaptured = false;
        public static MainWindow MainWindow;

        static readonly object _syncLock = new object();
        static Thread _thread;
        static bool _isRunning;

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
                    ColoredConsole.WriteLine("[[green]]Gamepad found![[/green]]");
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
                ColoredConsole.WriteLine($"Warning: Sample rate [[yellow]]{ConfigOptions.RunninConfig.SampleRate}[[/yellow]] not supported, got [[green]]{have.freq}[[/green]] instead.");
                ConfigOptions.RunninConfig.SampleRate = have.freq;
            }

            SDL.SDL_PauseAudioDevice(_audiodev, 0); // Turn on sound

            TurnOn();
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
                uint frameStart = SDL_GetTicks();

                uint baseHigh = _mem.Read8(Memory.STPortAdress.ST_SCRHIGHADDR);
                uint baseMid = _mem.Read8(Memory.STPortAdress.ST_SCRMIDADDR);
                uint videoBase = (baseHigh << 16) | (baseMid << 8);  // low byte 0 en ST
                uint videoCounter = videoBase;

                _mem.Write8(Memory.STPortAdress.ST_HIVADRPOINT, (byte)baseHigh);
                _mem.Write8(Memory.STPortAdress.ST_MIVADRPOINT, (byte)baseMid);
                _mem.Write8(Memory.STPortAdress.ST_LOVADRPOINT, (byte)0);

                /*
                 * The PAL Color Atari ST has 313 full scanlines per vertical synchronization (vsync), 
                 * of which 200 are visible lines and 112 belong to the top and bottom borders. 
                 * In monochrome mode, there would be 400 visible and 100 non-visible lines, 
                 * but in this emulator we will only support color mode.
                 */
                for (int scanline = 0; scanline < 313; scanline++)
                {
                    int ElapsedCycles = 0;

                    // Each scanline lasts 512 CPU cycles at 8 MHz = 15.66 kHz, or 64 microseconds.

                    /*
                     * This is how screen synchronization is handled in this emulator. It is not the most accurate method, 
                     * but it is sufficient for the vast majority of ST games and programs. I ported this loop directly 
                     * from the MS-DOS version of ASE, and it would need to be rewritten in order to also synchronize what 
                     * happens in the screen borders in some demos and games.
                     * 
                     *              448 cycles active display + 64 cycles H-Blank (right border)
                     *              -------------------+++
                     *              ********************** <- Top border (not rendered)
                     *              **********************
                     *              ***                *** <- Active display starts here (scanline 63)
                     *              ***                ***
                     *              ***                ***
                     *              ***                ***
                     *              ***                *** <- Active display ends here (scanline 262)
                     *              **********************
                     *              ********************** <- Bottom border (not rendered), Vsync
                     */

                    // Active display
                    int cyclesDuringScreenActive = 448; // 512 (total cycles/scanline) - 64 (right border) cycles;
                    CPU._moira.RunForCycles(cyclesDuringScreenActive);
                    ElapsedCycles += cyclesDuringScreenActive;

                    // Sync audio
                    _ym.Sync(cyclesDuringScreenActive);
                    // Sync interrupts
                    _mfp.UpdateTimers(cyclesDuringScreenActive);

                    // Actives H-Blank (right border)
                    int cyclesDuringHBL = 64;
                    CPU._moira.RunForCycles(cyclesDuringHBL);
                    ElapsedCycles += cyclesDuringHBL;

                    // Sync audio again
                    _ym.Sync(cyclesDuringHBL);
                    // Sync interrupts again when outscreen
                    _mfp.irqController.RaiseHBL();
                    _mfp.UpdateTimers(cyclesDuringHBL);
                    
                    // Sync ACIA
                    ACIA.Sync(ElapsedCycles);

                    if (scanline > 62 && scanline < 263)
                    {
                        _mem.Write8(Memory.STPortAdress.ST_HIVADRPOINT, (byte)(videoCounter >> 16));
                        _mem.Write8(Memory.STPortAdress.ST_MIVADRPOINT, (byte)(videoCounter >> 8));
                        _mem.Write8(Memory.STPortAdress.ST_LOVADRPOINT, (byte)(videoCounter));

                        // Render scanline
                        lock (_syncLock)
                        {
                            AtariStRenderer.BlitStLineToBuffer(ScreenBuffer, videoCounter, 0, scanline - 63);
                        }

                        // Next line: +160 bytes
                        videoCounter = (videoCounter + 160u) & 0xFFFFFFu;

                        _mfp.TickTimerA_EventCount();
                        _mfp.TickTimerB_EventCount();   // Timer B updates on every scanline
                    }
                }

                // Vsync completed
                _mfp.irqController.RaiseVBL();

                Dispatcher.UIThread.InvokeAsync(() => 
                {
                    SDL_PumpEvents();
                    
                    while (SDL_PollEvent(out var ev) == 1)
                    {
                        HandleEvents(ev);
                    }
                }, DispatcherPriority.Background);

                // For future use: Screenshot, recording, etc.
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
        }

        public static bool TurnOn()
        {
            // Starts with mouse uncaptured
            CaptureMouse(false);

            _isRunning = true;
            _ym = new YM2149(sampleRate: Config.ConfigOptions.RunninConfig.SampleRate, chipClockHz: 2000000.0);

            CPU.InitCpu();

            // Init ok?
            if (!_isRunning)
                return false;         // No, just exit

            _thread = new Thread(EmulatorLoop);
            _thread.Start();

            return true;
        }

        public static void HardReset()
        {
            _isRunning = false;
            _thread.Join();

            TurnOn();
        }

        public static void Shutdown()
        {
            _isRunning = false;

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

        public static void CaptureMouse(bool capture = true)
        {
            IsMouseCaptured = capture;
            SDL.SDL_SetRelativeMouseMode(capture ? SDL.SDL_bool.SDL_TRUE : SDL.SDL_bool.SDL_FALSE);
            
            MainWindow.Cursor = capture ? new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.None) : null;
            MainWindow.ShowMenu(!capture);
        }

        public static void HandleEvents(SDL_Event e)
        {
            bool IgnoreCtlrKeyUp = false;

            // First, check the host keyboard that emulates the joystick
            // Numpad mapping: 8=Up, 5=Down, 4=Left, 6=Right, 0=Fire
            // This should be configurable in the future...
            if ((e.type == SDL_EventType.SDL_KEYDOWN && e.key.repeat == 0) || e.type == SDL_EventType.SDL_KEYUP)
            {
                bool pressed = (e.type == SDL_EventType.SDL_KEYDOWN);
                bool isJoyKey = true;

                switch (e.key.keysym.scancode)
                {
                    case SDL_Scancode.SDL_SCANCODE_KP_8:
                        ACIA.UpdateJoystick(ACIA.JOY_UP, pressed);
                        break;
                    case SDL_Scancode.SDL_SCANCODE_KP_5:
                        ACIA.UpdateJoystick(ACIA.JOY_DOWN, pressed);
                        break;
                    case SDL_Scancode.SDL_SCANCODE_KP_4:
                        ACIA.UpdateJoystick(ACIA.JOY_LEFT, pressed);
                        break;
                    case SDL_Scancode.SDL_SCANCODE_KP_6:
                        ACIA.UpdateJoystick(ACIA.JOY_RIGHT, pressed);
                        break;
                    case SDL_Scancode.SDL_SCANCODE_KP_0:
                        ACIA.UpdateJoystick(ACIA.JOY_FIRE, pressed);
                        break;
                    default:
                        isJoyKey = false;
                        break;
                }

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
                        ACIA.UpdateJoystick(ACIA.JOY_FIRE, pressed);
                        break;
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

                // Pressing F12 disables the menu so it doesn’t interfere with keyboard input and captures
                // the mouse in the window. Pressing Ctrl+F12 disassembles the code near the PC in the
                // console window. I’m using this to inspect what’s happening in the emulator;
                // it’s only there for testing and debugging. I’d need to build a more complete debugger
                // for it to be truly useful beyond my own experiments.
                if (e.key.keysym.scancode == SDL.SDL_Scancode.SDL_SCANCODE_F12)
                {
                    if ((e.key.keysym.mod & SDL.SDL_Keymod.KMOD_LCTRL) != 0)
                    {
                        IgnoreCtlrKeyUp = true;

                        if (ConfigOptions.RunninConfig.DebugMode)
                            Debug.DisassembleRunningPC();

                        return;
                    }

                    CaptureMouse(!IsMouseCaptured);
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

                    // Fuerza la actualización del ratón en el ST
                    ACIA.SendMousePacket(0, 0);
                }
            }
        }

    }
}
