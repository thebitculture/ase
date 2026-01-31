/*
 * - ATARI SYSTEM EMULATOR - 
 * 
 * The Bit Culture 2026
 * 
 * This emulator is provided for educational purposes only and makes no claim of accuracy.
 * You are free to study, modify, and redistribute it under the terms of the GNU General 
 * Public License Version 3, 29 June 2007.
 * 
 * This software is provided “as is”, without any warranty of any kind.
 * 
 * Official repository 👉 https://github.com/thebitculture/ase
 * Follow me on 👉 https://youtube.com/@thebitculture?si=2s4M5Iu4QbIdq_hn
 *
 * To create this emulator, I have taken these documents as references, among many others:
 *
 * https://github.com/ggnkua/Atari_ST_Sources/blob/master/Docs/STINTERN.TXT
 * https://github.com/ggnkua/Atari_ST_Sources/blob/master/Docs/Hardware.txt
 * https://github.com/ggnkua/Atari_ST_Sources/blob/master/Docs/KEYBOARD.TXT
 * https://github.com/ggnkua/Atari_ST_Sources/blob/master/Docs/1772INFO.TXT
 * https://info-coach.fr/atari/documents/_mydoc/FD-HD_Programming.pdf
 * https://github.com/nguillaumin/perihelion-m68k-tutorials
 */

/*
 * The Main loop, it's a complete mess!
 */

using SDL2;
using System.Diagnostics;
using TinyDialogsNet;
using static ASE.Config;
using static ASE.Video;
using static SDL2.SDL;

namespace ASE
{

    internal class Program
    {
        public static Memory? _mem;
        public static MFP68901? _mfp;
        public static YM2149 _ym;

        static SDL.SDL_AudioCallback _audiocallback;
        static uint _audiodev;
        static nint GamepadController;
        const int GamepadDeadzone = 8000;

        public static FloppyImage driveA = new FloppyImage();

        static bool mouseCaptured = false;

        static uint[] ScreenBuffer = new uint[640 * 200];

        static IntPtr AtariWindow;
        static string DefaultTittle = "Atari System Emulator";
        static int AtariWindowTimerCount = 0;

        public static void SetAtariWindowsTittle(string Text, int FramesCountToRestore = 100)
        {
            if (!string.IsNullOrEmpty(Text) && AtariWindowTimerCount == 0)
            {
                SDL.SDL_SetWindowTitle(AtariWindow, $"{DefaultTittle} {Text}");
                AtariWindowTimerCount = FramesCountToRestore;
            }

            if(string.IsNullOrEmpty(Text) && AtariWindowTimerCount == 0)
                SDL.SDL_SetWindowTitle(AtariWindow, $"{DefaultTittle}");
        }

        static void Main(string[] args)
        {
            int W = 320;
            int H = 200;

            Config config = new Config();
            config.LoadConfig(args);

            SDL_SetHint(SDL_HINT_WINDOWS_DISABLE_THREAD_NAMING, "1");
            SDL_SetHint(SDL_HINT_MOUSE_FOCUS_CLICKTHROUGH, "1");

            if (SDL.SDL_Init(SDL.SDL_INIT_VIDEO | SDL.SDL_INIT_AUDIO | SDL.SDL_INIT_GAMECONTROLLER) < 0)
            {
                Console.WriteLine($"Error SDL: {SDL.SDL_GetError()}");
                return;
            }

            // Search for gamepads

            for (int i = 0; i < SDL.SDL_NumJoysticks(); i++)
            {
                if (SDL.SDL_IsGameController(i) == SDL.SDL_bool.SDL_TRUE)
                {
                    GamepadController = SDL.SDL_GameControllerOpen(i);
                    SDL_GameControllerOpen(i);
                    Console.WriteLine("Gamepad found.");
                    break;
                }
            }

            SDL_EventState(SDL_EventType.SDL_SYSWMEVENT, SDL_ENABLE);

            AtariWindow = SDL.SDL_CreateWindow(
                $"{DefaultTittle}",
                SDL.SDL_WINDOWPOS_CENTERED,
                SDL.SDL_WINDOWPOS_CENTERED,
                W * 2,
                H * 2,
                SDL.SDL_WindowFlags.SDL_WINDOW_SHOWN | SDL.SDL_WindowFlags.SDL_WINDOW_RESIZABLE
            );

            IntPtr renderer = SDL.SDL_CreateRenderer(
                AtariWindow, -1,
                SDL.SDL_RendererFlags.SDL_RENDERER_ACCELERATED | SDL.SDL_RendererFlags.SDL_RENDERER_PRESENTVSYNC
            );

            // Texture to render the ST screen
            IntPtr texture = SDL.SDL_CreateTexture(
                renderer,
                SDL.SDL_PIXELFORMAT_ARGB8888,
                (int)SDL.SDL_TextureAccess.SDL_TEXTUREACCESS_STREAMING,
                W * 2, H
            );

            // Init audio output

            var want = new SDL.SDL_AudioSpec
            {
                freq = ConfigOptions.RunninConfig.SampleRate,
                format = SDL.AUDIO_F32SYS,
                channels = 1,               // El ST es mono
                samples = 1024,             // tamaño de buffer
                callback =YM2149.AudioCallback
            };

            _audiocallback = want.callback; // evita GC

            SDL.SDL_AudioSpec have;
            _audiodev = SDL.SDL_OpenAudioDevice(null, 0, ref want, out have, 0);
            if (_audiodev == 0)
            {
                Console.WriteLine("SDL_OpenAudioDevice error: " + SDL.SDL_GetError());
                SDL.SDL_Quit();
                return;
            }

            _ym = new YM2149(sampleRate: have.freq, chipClockHz: 2000000.0);
            SDL.SDL_PauseAudioDevice(_audiodev, 0); // empieza a reproducir

            // Prepara interface

            SetAtariWindowsTittle("ℹ️ F11 load disk F12 capture mouse", 50*10); // 50 frames/second * 10 = 10 seconds

            SDL.SDL_Event e;
            CaptureMouse(false);

            if (!string.IsNullOrEmpty(ConfigOptions.RunninConfig.FloppyImagePath))
            {
                string message;
                bool inserted = driveA.Insert(ConfigOptions.RunninConfig.FloppyImagePath, out message);
                ColoredConsole.WriteLine(message);
            }

            CPU.InitCpu();

            //  Speed control variables
            bool running = true;
            const double frame = 1.0 / 50.0; // 1 ST pal frame -> 0.02 s
            var sw = Stopwatch.StartNew();
            double next = 0.0;
            var last = Stopwatch.StartNew();

            while (running)
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

                    if (scanline > 62 && scanline < 263)
                    {
                        _mem.Write8(Memory.STPortAdress.ST_HIVADRPOINT, (byte)(videoCounter >> 16));
                        _mem.Write8(Memory.STPortAdress.ST_MIVADRPOINT, (byte)(videoCounter >> 8));
                        _mem.Write8(Memory.STPortAdress.ST_LOVADRPOINT, (byte)(videoCounter));

                        // Render scanline
                        AtariStRenderer.BlitStLineToBuffer(ScreenBuffer, videoCounter, 0, scanline - 63);

                        // Next line: +160 bytes
                        videoCounter = (videoCounter + 160u) & 0xFFFFFFu;

                        _mfp.TickTimerB_EventCount();   // Timer B updates on every scanline
                    }

                    SDL_PumpEvents(); // Updates host events

                    while (SDL.SDL_PollEvent(out e) == 1)
                    {
                        if (e.type == SDL.SDL_EventType.SDL_QUIT)
                            running = false;

                        HandleEvents(e);
                    }
                }

                // Vsync completed
                _mfp.irqController.RaiseVBL();

                // Move the texture data from the screen buffer to the host window texture
                AtariStRenderer.BufferToTexture(texture, ScreenBuffer);

                // Render del buffer de la pantalla
                SDL.SDL_RenderClear(renderer);
                SDL.SDL_RenderCopy(renderer, texture, IntPtr.Zero, IntPtr.Zero);
                SDL.SDL_RenderPresent(renderer);

                next += frame;

                if (!ConfigOptions.RunninConfig.MaxSpeed)
                {
                    /*
                     * Dynamic wait loop. It combines longer waits using SDL_Delay, which do not block the process,
                     * and then performs a fine-grained adjustment during the last few microseconds using SpinWait,
                     * which does block but is more precise. The longer waits allow SDL to keep collecting events in
                     * the meantime, making the emulator feel smoother.
                     */
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

                // Update window title timer
                if (AtariWindowTimerCount > 0)
                    AtariWindowTimerCount--;
                else
                    SetAtariWindowsTittle("");
            }

            // Disassemble a bit on exit arround the PC register if in debug mode
            if (ConfigOptions.RunninConfig.DebugMode)
            {
                Debug.DumnpRegs();
                Debug.DisassembleAt(CPU._moira.PC - 40, 20);
            }

            SDL.SDL_DestroyTexture(texture);
            SDL.SDL_DestroyRenderer(renderer);
            SDL.SDL_DestroyWindow(AtariWindow);
            SDL.SDL_Quit();
        }

        public static void CaptureMouse(bool capture = true)
        {
            mouseCaptured = capture;
            SDL.SDL_SetRelativeMouseMode(capture ? SDL.SDL_bool.SDL_TRUE : SDL.SDL_bool.SDL_FALSE);
        }

        public static void HandleEvents(SDL_Event e)
        {
            bool IgnoreCtlrKeyUp = false;

            // Primero consultamos el teclado host que emula el joystick
            // Mapeo en el teclado numérico: 8=Arriba, 5=Abajo, 4=Izq, 6=Der, 0=Fuego
            // Esto debería poder configurarse en el futuro
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

            // Lo mismo, pero con el gamepad
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

                // Stick izquierdo del gamepad como direcciones
                if (axis == SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTX)
                {
                    bool left = v < -GamepadDeadzone;
                    bool right = v > GamepadDeadzone;

                    ACIA.UpdateJoystick(ACIA.JOY_LEFT, left);
                    ACIA.UpdateJoystick(ACIA.JOY_RIGHT, right);

                    // Si vuelve al centro, apaga ambos
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

                // Detección de F12 (TOGGLE MOUSE) / Desensamblador alrededor del registro PC actual
                if (e.key.keysym.scancode == SDL.SDL_Scancode.SDL_SCANCODE_F12 )
                {
                    if ((e.key.keysym.mod & SDL.SDL_Keymod.KMOD_LCTRL) != 0)
                    {
                        IgnoreCtlrKeyUp = true;

                        if (ConfigOptions.RunninConfig.DebugMode)
                            Debug.DisassembleRunningPC();

                        return;
                    }
                    
                    CaptureMouse(!mouseCaptured);
                    return;
                }

                if (e.key.keysym.scancode == SDL_Scancode.SDL_SCANCODE_F11)
                {
                    CaptureMouse(false);

                    var (canceled, selpath) = TinyDialogs.OpenFileDialog("Select ST Image file", "", false, new FileFilter("ST Disk image", ["*.st"]));

                    if (!canceled && selpath.Count() == 1)
                    {
                        string message;
                        bool inserted = driveA.Insert(selpath.ElementAt(0), out message);

                        if (!inserted)
                            TinyDialogs.MessageBox("Error", message, MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                        else
                            ColoredConsole.WriteLine(message);

                        var response = TinyDialogs.MessageBox("Disk inserted", "Reboot?", MessageBoxDialogType.YesNo, MessageBoxIconType.Question, MessageBoxButton.Yes);

                        if (response == MessageBoxButton.Yes)
                            CPU.InitCpu();
                    }

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

            if (mouseCaptured)
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
