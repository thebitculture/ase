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

using Mt32;
using SDL2;
using System.Diagnostics;
using System.Reflection;
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
        //
        // The unversioned libSDL2.so is a symlink that belongs to the *development* package: on a
        // machine with just the runtime package (libsdl2-2.0-0 and friends) only the SONAME
        // libSDL2-2.0.so.0 exists, which is the whole reason the default probing for
        // DllImport("SDL2") is not enough and the emulator died at startup with a
        // DllNotFoundException.
        static readonly string[] LinuxSdlNames =
        [
            "libSDL2.so",           // bundled next to the executable (checked in the output directory)
            "libSDL2-2.0.so.0",     // the SONAME every distribution installs with the runtime package
            "libSDL2-2.0.so"
        ];

        // Names dlopen() is asked for, in order, when resolving libmt32emu outside Windows. The
        // versioned one is what ASE ships next to the executable; the bare symlink belongs to a
        // distribution's development package and is only useful for a system-wide install.
        //
        // The binding declares DllImport("mt32emu-2"), which is the file name only on Windows:
        // Munt's CMakeLists.txt sets RUNTIME_OUTPUT_NAME mt32emu-<major> there and nowhere else,
        // so on the other platforms CMake applies its own convention for a library built with
        // VERSION/SOVERSION and the binary is called libmt32emu.so.2 / libmt32emu.2.dylib.
        // Default probing only tries [lib]mt32emu-2[.so|.dylib] and would never reach those, and
        // ASE keeps Munt's own names in native/<rid>/ rather than renaming the binaries.
        static string[] Mt32EmuNames => OperatingSystem.IsMacOS()
            ? ["libmt32emu.2.dylib", "libmt32emu.dylib"]
            : ["libmt32emu.so.2", "libmt32emu.so"];

        /// <summary>
        /// Teaches .NET how to find the native libraries whose file names do not match what
        /// <c>DllImport</c> asks for: SDL2 on Linux, where it is not bundled but taken from the
        /// distribution, and Munt's libmt32emu on macOS and Linux. See <see cref="LinuxSdlNames"/>
        /// and <see cref="Mt32EmuNames"/> for why the default probing misses them.
        ///
        /// <para>Both registrations go through this single method because .NET allows exactly one
        /// resolver per assembly and throws <c>InvalidOperationException: a resolver is already set
        /// for the assembly</c> on the second call. SDL2-CS and the Munt binding are compiled into
        /// the same assembly here, so registering them separately blew up on Linux — the only
        /// platform where both needed a resolver. Grouping by assembly keeps that from happening
        /// again whichever way the bindings are packaged: should another native library of ASE ever
        /// need one, add it to the list below instead of calling SetDllImportResolver elsewhere.</para>
        ///
        /// <para>Returning IntPtr.Zero leaves the default probing in charge, which is what every
        /// other native library of this assembly (moira) relies on.</para>
        /// </summary>
        static void RegisterNativeLibraryResolvers()
        {
            Dictionary<Assembly, List<(string Import, string[] Candidates)>> byAssembly = [];

            void Add(Assembly assembly, string import, string[] candidates)
            {
                if (!byAssembly.TryGetValue(assembly, out var entries))
                    byAssembly[assembly] = entries = [];

                entries.Add((import, candidates));
            }

            if (OperatingSystem.IsLinux())
                Add(typeof(SDL).Assembly, "SDL2", LinuxSdlNames);

            if (!OperatingSystem.IsWindows())
                Add(typeof(Mt32EmuNative).Assembly, Mt32EmuNative.DllName, Mt32EmuNames);

            foreach ((Assembly assembly, var entries) in byAssembly)
            {
                NativeLibrary.SetDllImportResolver(assembly, (libraryName, _, _) =>
                {
                    foreach ((string import, string[] candidates) in entries)
                    {
                        if (libraryName != import)
                            continue;

                        // A copy shipped next to the executable wins over the system one.
                        foreach (string name in candidates)
                            if (NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, name), out nint bundled))
                                return bundled;

                        foreach (string name in candidates)
                            if (NativeLibrary.TryLoad(name, out nint system))
                                return system;
                    }

                    return IntPtr.Zero;
                });
            }
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
        /// Pins SDL's video driver to X11 on Linux, where that is not a preference but a
        /// requirement: Avalonia has no Wayland backend, so the window later handed to
        /// <c>SDL_CreateWindowFrom</c> is always an X11 one (through XWayland inside a Wayland
        /// session). On the Wayland driver SDL receives an X11 window id where it expects a
        /// <c>wl_surface*</c> — plain SDL2 refuses and the emulator comes up with no input at all,
        /// while sdl2-compat (the SDL2 of Arch and others, which reimplements SDL2 on top of SDL3)
        /// dereferences it and the process dies with a segmentation fault before the window shows.
        ///
        /// <para>Hence OVERRIDE priority: a plain <c>SDL_SetHint</c> never wins over an
        /// <c>SDL_VIDEODRIVER</c> exported in the environment, a common tweak in Wayland setups.
        /// And the hint is set under both names because SDL3 renamed it to <c>SDL_VIDEO_DRIVER</c>,
        /// which leaves the SDL2 spelling a silent no-op on sdl2-compat.</para>
        /// </summary>
        static void ForceX11VideoDriver()
        {
            string environmentDriver = Environment.GetEnvironmentVariable("SDL_VIDEODRIVER");

            if (!string.IsNullOrEmpty(environmentDriver) && environmentDriver != "x11")
                ColoredConsole.WriteLine($"Ignoring [[yellow]]SDL_VIDEODRIVER={environmentDriver}[[/yellow]]: the ASE window is an X11 one, so SDL needs its x11 driver to receive the input.");

            SDL.SDL_SetHintWithPriority("SDL_VIDEODRIVER", "x11", SDL.SDL_HintPriority.SDL_HINT_OVERRIDE);    // SDL2 name
            SDL.SDL_SetHintWithPriority("SDL_VIDEO_DRIVER", "x11", SDL.SDL_HintPriority.SDL_HINT_OVERRIDE);   // SDL3 / sdl2-compat name

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
                ColoredConsole.WriteLine("[[yellow]]Warning:[[/yellow]] DISPLAY is not set, so there is no X server to talk to. Inside a Wayland session ASE needs XWayland.");
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
            RegisterNativeLibraryResolvers();

            Config = new Config();
            Config.LoadConfig(args);

            // Before anything else touches the CPU core: a machine without the native library (or
            // without what it depends on) must be told what to install, not handed a stack trace.
            if (!CheckNativeCpuCore())
                return;

            if (ConfigOptions.RunninConfig.CheckForUpdates)
            {
                ReleaseChecker.IsNewVersionAvailableAsync().Wait();

                // The window offering to update is raised by MainWindow.OnOpened: Avalonia is not
                // running yet at this point. This line stays for the console log.
                // ReleaseInfo is null when the query failed (no network, GitHub down): not a reason
                // to keep the emulator from starting.
                if (ReleaseChecker.ReleaseInfo?.ExistsNewVersion == true)
                    ColoredConsole.WriteLine($"⭐⭐ New release [[yellow]]{ReleaseChecker.ReleaseInfo.TagName}[[/yellow]] available!! from [[magenta]]{ReleaseChecker.ReleaseInfo.HtmlUrl}[[/magenta]] ⭐⭐", ConfigOptions.DebugModes.Quiet);
            }

            SDL.SDL_SetHint(SDL.SDL_HINT_MOUSE_FOCUS_CLICKTHROUGH, "1");
            SDL.SDL_SetHint(SDL.SDL_HINT_MAC_BACKGROUND_APP, "1");

            if (OperatingSystem.IsLinux())
                ForceX11VideoDriver();

            if (SDL.SDL_Init(SDL.SDL_INIT_AUDIO | SDL.SDL_INIT_GAMECONTROLLER | SDL.SDL_INIT_VIDEO) < 0)
            {
                ColoredConsole.WriteLine($"[[red]]Error SDL: {SDL.SDL_GetError()}[[/red]]");

                if (OperatingSystem.IsLinux())
                    ColoredConsole.WriteLine("ASE needs SDL's x11 video driver. Inside a Wayland session that means XWayland must be installed and reachable.");

                return;
            }

            // Printed whatever the debug level is: which SDL build got loaded and which drivers it
            // picked is the first thing worth knowing about any startup, input or audio report —
            // the same distribution name can ship plain SDL2 or sdl2-compat, which behave differently.
            SDL.SDL_GetVersion(out SDL.SDL_version sdlVersion);
            ColoredConsole.WriteLine($"SDL [[green]]{sdlVersion.major}.{sdlVersion.minor}.{sdlVersion.patch}[[/green]] — video driver [[green]]{SDL.SDL_GetCurrentVideoDriver()}[[/green]], audio driver [[green]]{SDL.SDL_GetCurrentAudioDriver()}[[/green]]");

            if (!string.IsNullOrEmpty(ConfigOptions.RunninConfig.FloppyImagePath))
            {
                string message;
                bool inserted = ASEMain.driveA.Insert(ConfigOptions.RunninConfig.FloppyImagePath, out message);
                ColoredConsole.WriteLine(message);
            }

            // Drive B only exists when it is connected (--drive-b, or the File menu on a previous
            // run); --floppy-b turns it on by itself, so a path here always has a drive to go in.
            if (ConfigOptions.RunninConfig.DriveBEnabled
                && !string.IsNullOrEmpty(ConfigOptions.RunninConfig.FloppyBImagePath))
            {
                ASEMain.driveB.Insert(ConfigOptions.RunninConfig.FloppyBImagePath, out string messageB);
                ColoredConsole.WriteLine(messageB);
            }

            // Create window

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        }

    }
}
