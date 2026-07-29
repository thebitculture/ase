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

        // File names DllImport("moira") ends up loading, per platform. Spelled out so the preflight
        // check below names the same file the runtime looks for.
        static string MoiraFileName =>
            OperatingSystem.IsWindows() ? "moira.dll" :
            OperatingSystem.IsMacOS() ? "moira.dylib" : "moira.so";

        // Pieces of the Visual C++ runtime moira.dll imports when it is built against the dynamic
        // CRT (/MD). They are not part of Windows: they arrive with the "Visual C++ 2015-2022
        // Redistributable", which every developer machine has and a clean install does not.
        static readonly string[] WindowsVcRuntime =
        [
            "VCRUNTIME140.dll",
            "VCRUNTIME140_1.dll",
            "MSVCP140.dll"
        ];

        /// <summary>
        /// Loads the native CPU core up front so a failure is reported as something actionable.
        /// .NET raises "DllNotFoundException: Dll was not found" both when moira.dll is missing and
        /// when it is present but one of *its* dependencies cannot be resolved, naming in either
        /// case the library that was requested — which sends people hunting for a file that was
        /// never absent.
        /// </summary>
        /// <returns>true if the library loaded; false after reporting why it did not.</returns>
        static bool CheckNativeCpuCore()
        {
            if (NativeLibrary.TryLoad("moira", typeof(Moira).Assembly, null, out _))
                return true;

            string path = Path.Combine(AppContext.BaseDirectory, MoiraFileName);
            string reason;

            if (!File.Exists(path))
            {
                reason = $"{MoiraFileName} is missing from {AppContext.BaseDirectory}.";
            }
            else
            {
                string[] missing = OperatingSystem.IsWindows()
                    ? WindowsVcRuntime.Where(dll => !NativeLibrary.TryLoad(dll, out _)).ToArray()
                    : [];

                reason = missing.Length > 0
                    ? $"{MoiraFileName} could not be loaded because {string.Join(", ", missing)} is not " +
                       "installed on this system. Install the Microsoft Visual C++ 2015-2022 Redistributable " +
                       "(x64) from https://aka.ms/vs/17/release/vc_redist.x64.exe"
                    : $"{MoiraFileName} was found but could not be loaded: a library it depends on is " +
                      $"missing, or it was built for a different architecture.{LoaderError(path)}";
            }

            ColoredConsole.WriteLine($"[[red]]{reason}[[/red]]");

            Dialogs.MessageBox("ASE cannot start", ColoredConsole.Strip(reason),
                MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok)
                .GetAwaiter().GetResult();

            return false;
        }

        /// <summary>
        /// The platform loader's own explanation for a library that refuses to load, ready to be
        /// appended to a sentence. On Unix this is the dlerror text, which names the dependency it
        /// could not resolve; on Windows it is the HRESULT message. Empty when the load
        /// unexpectedly succeeds, which leaves the generic wording standing on its own.
        /// </summary>
        static string LoaderError(string path)
        {
            try
            {
                NativeLibrary.Load(path);
                return string.Empty;
            }
            catch (Exception ex)
            {
                return $" The system reports: {ex.Message}";
            }
        }

        [STAThread]
        static void Main(string[] args)
        {
            RegisterSdlLibraryResolver();

            Config = new Config();
            Config.LoadConfig(args);

            // Before anything else touches the CPU core: a machine without the native library (or
            // without what it depends on) must be told what to install, not handed a stack trace.
            if (!CheckNativeCpuCore())
                return;

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
