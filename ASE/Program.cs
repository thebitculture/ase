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
using System.Runtime.InteropServices;
using TinyDialogsNet;
using Avalonia;
using Avalonia.Native;
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
                .With(new AvaloniaNativePlatformOptions
                {
                    RenderingMode = new[] { AvaloniaNativeRenderingMode.OpenGl }
                })
                .LogToTrace()
                .UseReactiveUI(_ => { });


        // Names dlopen() is asked for, in order, when resolving SDL2 on Linux. A copy shipped
        // next to the executable wins over the system one, as the bundled SDL2.dll/libSDL2.dylib
        // do on the other platforms.
        static readonly string[] LinuxSdlNames =
        [
            "libSDL2.so",           // bundled next to the executable (checked in the output directory)
            "libSDL2-2.0.so.0",     // the SONAME every distribution installs with the runtime package
            "libSDL2-2.0.so"
        ];

        /// <summary>
        /// Teaches .NET how to find SDL2 on Linux, where it is not bundled but taken from the
        /// distribution. The default probing for <c>DllImport("SDL2")</c> only tries the
        /// unversioned <c>libSDL2.so</c>, a symlink that belongs to the *development* package:
        /// on a machine with just the runtime package (<c>libsdl2-2.0-0</c> and friends) only the
        /// SONAME <c>libSDL2-2.0.so.0</c> exists and the emulator died at startup with a
        /// DllNotFoundException. Returning IntPtr.Zero leaves the default probing in charge.
        /// </summary>
        static void RegisterSdlLibraryResolver()
        {
            if (!OperatingSystem.IsLinux())
                return;

            NativeLibrary.SetDllImportResolver(typeof(SDL).Assembly, (libraryName, assembly, searchPath) =>
            {
                if (libraryName != "SDL2")
                    return IntPtr.Zero;

                foreach (string name in LinuxSdlNames)
                    if (NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, name), out nint bundled))
                        return bundled;

                foreach (string name in LinuxSdlNames)
                    if (NativeLibrary.TryLoad(name, out nint system))
                        return system;

                return IntPtr.Zero;
            });
        }

        [STAThread]
        static void Main(string[] args)
        {
            RegisterSdlLibraryResolver();

            Config = new Config();
            Config.LoadConfig(args);

            if (ConfigOptions.RunninConfig.CheckForUpdates)
            {
                ReleaseChecker.IsNewVersionAvailableAsync().Wait();

                if (ReleaseChecker.ReleaseInfo.ExistsNewVersion)
                    ColoredConsole.WriteLine($"⭐⭐ New release [[yellow]]{ReleaseChecker.ReleaseInfo.TagName}[[/yellow]] available!! from [[magenta]]{ReleaseChecker.ReleaseInfo.HtmlUrl}[[/magenta]] ⭐⭐", ConfigOptions.DebugModes.Quiet);
            }

            SDL.SDL_SetHint(SDL.SDL_HINT_MOUSE_FOCUS_CLICKTHROUGH, "1");
            SDL.SDL_SetHint(SDL.SDL_HINT_MAC_BACKGROUND_APP, "1");

            // Avalonia only has an X11 backend on Linux (even inside a Wayland session, where it
            // runs through XWayland), so SDL must pick X11 as well: SDL_CreateWindowFrom cannot
            // adopt an X11 window if SDL chose the Wayland driver — the default on several
            // distributions — and the emulator would come up with no input at all. An explicit
            // SDL_VIDEODRIVER in the environment still wins: SDL_SetHint never overrides it.
            if (OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
                SDL.SDL_SetHint("SDL_VIDEODRIVER", "x11");

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
