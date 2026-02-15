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

using SDL2;
using System.Diagnostics;
using TinyDialogsNet;
using Avalonia;
using ReactiveUI.Avalonia;
using static ASE.Config;
using static ASE.Video;

namespace ASE
{

    internal class Program
    {
        public static Config Config;

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace()
                .UseReactiveUI();


        [STAThread]
        static void Main(string[] args)
        {
            Config = new Config();
            Config.LoadConfig(args);

            SDL.SDL_SetHint(SDL.SDL_HINT_MOUSE_FOCUS_CLICKTHROUGH, "1");
            SDL.SDL_SetHint(SDL.SDL_HINT_MAC_BACKGROUND_APP, "1");
            
            if (SDL.SDL_Init(SDL.SDL_INIT_AUDIO | SDL.SDL_INIT_GAMECONTROLLER | SDL.SDL_INIT_VIDEO) < 0)
            {
                Console.WriteLine($"Error SDL: {SDL.SDL_GetError()}");
                return;
            }

            if (!string.IsNullOrEmpty(ConfigOptions.RunninConfig.FloppyImagePath))
            {
                string message;
                bool inserted = ASEMain.driveA.Insert(ConfigOptions.RunninConfig.FloppyImagePath, out message);
                ColoredConsole.WriteLine(message);
            }

            // Create window

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        }

    }
}
