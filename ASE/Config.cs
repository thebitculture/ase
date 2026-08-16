using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using static SDL2.SDL;

namespace ASE
{
    /// <summary>
    /// Provides methods and properties for managing the configuration of the Atari System Emulator, including loading
    /// and saving configuration settings.
    /// </summary>
    /// <remarks>The Config class handles the initialization and management of emulator settings, supporting
    /// both default and user-specified configurations. It processes command-line arguments to customize emulator
    /// behavior and manages configuration persistence through JSON files. If a configuration file does not exist, a
    /// default configuration is created automatically. This class is intended to be used as the central point for
    /// accessing and modifying emulator configuration options.</remarks>
    public class Config
    {
        /// <summary>
        /// Represents the configuration options for the application, including paths, hardware flags, and debug
        /// settings.
        /// </summary>
        /// <remarks>This class holds the active configuration instance and provides properties to
        /// customize various settings such as mouse sensitivity and sample rate. The 'RunninConfig' static member
        /// serves as the default configuration accessible throughout the application.</remarks>
        public class ConfigOptions
        {
            public enum STModels
            {
                ST = 0,
                Mega = 1,
                STE = 2
            }

            public enum RAMConfigurations
            {
                RAM_512KB = 0,
                RAM_1MB = 1,
                RAM_2MB = 2,
                RAM_4MB = 3,
            }

            public enum GamepadButtonsMapping
            {
                None, Fire, Space, Up, Y, N, T
            }

            public enum MIDIEmulationOptions
            {
                None, System, BuiltInMT32
            }
            
            /// <summary>
            /// Console debug verbosity. Each level is a superset of the previous one.
            /// </summary>
            public enum DebugModes
            {
                None = 0,           // No debug output (equivalent to the old 'false')
                Quiet = 1,          // Only initialization messages and important warnings (TOS load, ROM write attempts, ...)
                Information = 2,    // Adds operational detail (e.g. commands reaching the ACIA)
                Full = 3            // Everything, including low-level data traffic (every ACIA byte, joystick packets, ...)
            }

            /// <summary>
            /// Holds the active configuration
            /// </summary>
            public static ConfigOptions RunninConfig = new ConfigOptions();

            public string TOSPath { get; set; } = "tos.rom";

            // Hardware flags
            public STModels STModel { get; set; } = STModels.ST;
            public RAMConfigurations RAMConfiguration { get; set; } = RAMConfigurations.RAM_1MB;
            public  bool MaxSpeed { get; set; } = false;
            public string FloppyImagePath { get; set; } = "";
            /// <summary>
            /// Divisor applied to the host mouse movement before it reaches the ST
            /// (<c>dx = accumulated / MouseSensitivity</c>, see ACIA.cs), so a bigger number
            /// means a *slower* pointer. Stored and taken from --mouse-sensitivity in this
            /// form; the Configuration window shows <see cref="MousePointerSpeed"/> instead.
            /// </summary>
            public float MouseSensitivity { get; set; } = 2;

            /// <summary>
            /// The same setting the other way round: how fast the ST pointer moves, relative
            /// to the default (x1.0 == the default divisor of 2). This is what the slider in
            /// the Configuration window edits — a divisor is an implementation detail, and a
            /// control where a higher number means slower reads backwards to everyone.
            /// Not serialized: <see cref="MouseSensitivity"/> is the stored form.
            /// </summary>
            [JsonIgnore]
            public float MousePointerSpeed
            {
                get => MouseSensitivity >= 0.1f ? DefaultMouseSensitivity / MouseSensitivity : 1f;
                set => MouseSensitivity = value >= 0.1f ? DefaultMouseSensitivity / value : 1f;
            }

            /// <summary>Divisor that <see cref="MousePointerSpeed"/> calls x1.0.</summary>
            const float DefaultMouseSensitivity = 2f;

            public int SampleRate { get; set; } = 44100;

            // Granularity (in CPU cycles) at which the CPU is interleaved with the MFP timers and
            // the interrupt controller. Lower values deliver timer interrupts (e.g. the 200 Hz
            // Timer C) and update the timer data registers more promptly, at a slight throughput
            // cost; higher values are faster but coarser. Clamped to [1, 512] where it is used.
            // 4 = Maximun compatibility; 16 = Balanced; 64 = Faster but less precise.
            public int CpuSyncSliceCycles { get; set; } = 4;

            // Screen flags

            public bool ShowBorders { get; set; } = true;   // show the screen borders (overscan) around the 320x200 display

            // Monochrome (SM124) monitor instead of a colour one. Detected by TOS through MFP
            // GPIP bit 7 at boot, which then selects high resolution (640x400, 1 plane, ~71 Hz).
            // Changing it requires a machine reset (the whole video geometry differs).
            public bool MonochromeMonitor { get; set; } = false;

            public bool CheckForUpdates { get; set; } = true;   // query GitHub for a newer release at startup

            // Default directories. Screenshots (Shift+F11) and snapshots (F11) default to
            // subfolders next to config.json; an empty value falls back to that default.
            // DiskImagesPath and TOSRomsPath preset the corresponding file dialogs (empty = none).
            public string ScreenshotsPath { get; set; } = Path.Combine(GetAppDefaultConfigsFilePath(), "Screenshots");
            public string SnapshotsPath { get; set; } = Path.Combine(GetAppDefaultConfigsFilePath(), "Snapshots");
            public string DiskImagesPath { get; set; } = "";
            public string LibraryPath { get; set; } = "";
            public string TOSRomsPath { get; set; } = "";

            // The ST MMU shares RAM between the CPU and the video shifter in a 2-cycle round-robin,
            // forcing every CPU bus access onto a 4-cycle grid: a misaligned access waits 2 cycles
            // for its slot. ROM is exempt (no wait states); the MFP/ACIA add fixed extra waits.
            // Moira (with MOIRA_PRECISE_TIMING) places each bus access at its exact in-instruction
            // cycle, so we can reproduce these waits. This is what keeps free-running cycle-counted
            // raster code (Spectrum 512, fullscreen demos) locked to the video instead of drifting.
            // I've tried different combinations by reproducing these waits in the emulator, leaving
            // only the most accurate timing in Moira, and I've gotten mixed results. I couldn't say
            // which combination works best.
            public bool CycleExactBus { get; set; } = true;

            // Phase of the 4-cycle MMU bus grid the CPU aligns to (0..3, effectively 0 or 2 since
            // the 68000 clock is even). Calibrated so cycle-counted rasters stay vertically stable.
            public int BusPhase { get; set; } = 0;

            // Fixed extra wait cycles for the MFP, added on top of bus alignment (~4 cycles is
            // the accepted approximation). The ACIAs are not configurable: they are 6800-type
            // (VPA) peripherals, so each access synchronizes with the E clock (CPU/10) — a
            // variable, self-stabilising wait modelled directly in Memory.ApplyBusWait.
            public int MfpWaitCycles { get; set; } = 4;

            // Bypasses the CRT shader entirely (a plain blit is used instead) rather than just
            // zeroing the sliders: with the effects at 0 the GPU still runs the whole fragment
            // program — the five bloom taps, the noise hash, the two gamma powers — for every
            // pixel. Meant for weak GPUs (Raspberry Pi and the like). The slider values are kept,
            // so switching it back off restores the previous look.
            public bool DisableCrtEffects { get; set; } = false;

            public float Curvature { get; set; } = 0.01f;
            public float Vignette { get; set; } = 0.18f;
            public float Scanline { get; set; } = 1.0f;
            public float ChromAb { get; set; } = 0.25f;
            public float Bloom { get; set; } = 0.22f;
            public float Mask { get; set; } = 0.50f;
            public float Noise { get; set; } = 0.05f;

            // Joystick emulation
            public SDL_Scancode KeyJoy1Up { get; set; } = SDL_Scancode.SDL_SCANCODE_KP_8;
            public SDL_Scancode KeyJoy1Down { get; set; } = SDL_Scancode.SDL_SCANCODE_KP_5;
            public SDL_Scancode KeyJoy1Left { get; set; } = SDL_Scancode.SDL_SCANCODE_KP_4;
            public SDL_Scancode KeyJoy1Right { get; set; } = SDL_Scancode.SDL_SCANCODE_KP_6;
            public SDL_Scancode KeyJoy1Fire { get; set; } = SDL_Scancode.SDL_SCANCODE_KP_0;

            // Gamepad button mapping
            public GamepadButtonsMapping GamepadButtonX { get; set; } = GamepadButtonsMapping.Up;
            public GamepadButtonsMapping GamepadButtonY { get; set; } = GamepadButtonsMapping.Fire;
            public GamepadButtonsMapping GamepadButtonA { get; set; } = GamepadButtonsMapping.Fire;
            public GamepadButtonsMapping GamepadButtonB { get; set; } = GamepadButtonsMapping.Space;
            public GamepadButtonsMapping GamepadButtonLS { get; set; } = GamepadButtonsMapping.Y;
            public GamepadButtonsMapping GamepadButtonRS { get; set; } = GamepadButtonsMapping.N;
            public GamepadButtonsMapping GamepadButtonLB { get; set; } = GamepadButtonsMapping.T;
            public GamepadButtonsMapping GamepadButtonRB { get; set; } = GamepadButtonsMapping.Space;

            // MIDI

            /// <summary>
            /// What the ST's MIDI ports are connected to: nothing, the host's own MIDI devices
            /// (<see cref="MIDIEmulationOptions.System"/>, mapped through
            /// <see cref="MidiInDevice"/>/<see cref="MidiOutDevice"/>) or the built-in Roland
            /// MT-32 emulation (Munt, ROMs in <see cref="MT32Rompath"/>). Changing it takes
            /// effect on a machine reset: MT-32 titles probe and initialise the module while
            /// loading, so attaching one to a running machine would go unnoticed.
            /// </summary>
            public MIDIEmulationOptions MidiEmulation { get; set; } = MIDIEmulationOptions.None;

            /// <summary>
            /// Host MIDI ports the emulated ST is wired to in <see cref="MIDIEmulationOptions.System"/>
            /// mode, or "" for none. Stored as the *name* the operating system gives the port and
            /// not as its index: an index is only a position in the driver's list and shifts as
            /// soon as a device is plugged, unplugged or reordered, so a saved index quietly ends
            /// up addressing a different instrument. <see cref="HostMidi"/> enumerates the ports
            /// and is where the name is resolved back to a platform handle.
            /// </summary>
            public string MidiInDevice { get; set; } = "";
            /// <inheritdoc cref="MidiInDevice"/>
            public string MidiOutDevice { get; set; } = "";

            /// <summary>
            /// Directory holding the Roland MT-32 control and PCM ROM images, used by the built-in
            /// emulation. The file names do not matter: libmt32emu identifies every image by its
            /// SHA-1 (see Mt32Synth.LoadRoms). The ROMs are copyrighted and not shipped with ASE.
            /// </summary>
            public string MT32Rompath { get; set; } = "";

            /// <summary>
            /// Output level of the built-in MT-32 in the mix, in percent — the real
            /// module's front-panel volume knob. 100 folds Munt's line level 1:1 over the
            /// PSG's, which turns out noticeably quieter than the ST's own sound, so the
            /// default boosts it; 0 mutes, 400 is the ceiling. Read live by the audio
            /// mixer (Mt32Backend.MixInto), so the slider works without a reset.
            /// </summary>
            public int Mt32Volume { get; set; } = 200;

            // Screenscraper
            public string ScreenScraperUser { get; set; }
            /// <summary>Protected value; use ScreenScraperPasswordRaw to read it.</summary>
            public string ScreenScraperPassword { get; set; }
            [JsonIgnore]
            public string ScreenScraperPasswordRaw
            {
                get => StringOfuscator.Unprotect(ScreenScraperPassword) ?? string.Empty;
                set => ScreenScraperPassword = StringOfuscator.Protect(value);
            }
            public bool ScrapeMedia { get; set; } = true;

            /// <summary>Windows only: custom VLC installation directory (containing libvlc.dll)
            /// used to play game preview videos in the library without bundling libVLC with the
            /// emulator. Empty = auto-detect the default "Program Files\VideoLAN\VLC" install.</summary>
            public string VlcInstallPath { get; set; } = "";

            // Debug flags
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public bool DiskDump { get; set; } = false;  // Not exposed, only for my testing
            [JsonConverter(typeof(DebugModeJsonConverter))]
            public DebugModes DebugMode { get; set; } = DebugModes.None;
        }

        /// <summary>
        /// Serializes <see cref="ConfigOptions.DebugModes"/> as a readable string and, on read, also
        /// accepts the legacy boolean form (<c>true</c> -> <see cref="ConfigOptions.DebugModes.Full"/>,
        /// <c>false</c> -> <see cref="ConfigOptions.DebugModes.None"/>) so existing config files keep working.
        /// </summary>
        public class DebugModeJsonConverter : JsonConverter<ConfigOptions.DebugModes>
        {
            public override ConfigOptions.DebugModes Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.True:
                        return ConfigOptions.DebugModes.Full;
                    case JsonTokenType.False:
                        return ConfigOptions.DebugModes.None;
                    case JsonTokenType.Number:
                        int n = reader.GetInt32();
                        return Enum.IsDefined(typeof(ConfigOptions.DebugModes), n) ? (ConfigOptions.DebugModes)n : ConfigOptions.DebugModes.None;
                    case JsonTokenType.String:
                        return (Enum.TryParse(reader.GetString(), true, out ConfigOptions.DebugModes v) && Enum.IsDefined(typeof(ConfigOptions.DebugModes), v)) ? v : ConfigOptions.DebugModes.None;
                    default:
                        return ConfigOptions.DebugModes.None;
                }
            }

            public override void Write(Utf8JsonWriter writer, ConfigOptions.DebugModes value, JsonSerializerOptions options)
            {
                writer.WriteStringValue(value.ToString());
            }
        }

        public static string Version = "";

        // Snapshot to restore on the first power-on (--snapshot=<path>). Launch-only
        // argument: it is never persisted to the config file.
        public static string StartupSnapshot = "";

        const string AppName = "ASE";
        const string DefaultConfigFileName = "config.json";

        string AppDataConfigPath;
        string PathToDefaultConfig;


        public void LoadConfig(string[] args)
        {
            AppDataConfigPath = GetAppDefaultConfigsFilePath();
            PathToDefaultConfig = Path.Combine(AppDataConfigPath, DefaultConfigFileName);

            Version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString();
            Version = Regex.Replace(Version, @"^(\d+\.\d+).*", "$1");

            ColoredConsole.WriteLine($"[[white]]ATARI SYSTEM EMULATOR[[/white]] v{Version} - The Bit Culture {DateTime.Now.Year}");
            ColoredConsole.WriteLine("👉 [[magenta]]https://github.com/thebitculture/ase[[/magenta]]");
            ColoredConsole.WriteLine("👉 [[magenta]]https://youtube.com/@thebitculture?si=2s4M5Iu4QbIdq_hn[[/magenta]]" + Environment.NewLine);

            if (File.Exists(PathToDefaultConfig))
                LoadJsonConfig(PathToDefaultConfig);
            else
                DumpJsonConfig(PathToDefaultConfig);  // Creates default configuration

            foreach (string arg in args)
            {
                string[] parts = arg.Split('=');

                switch (parts[0].ToLower())
                {
                    case "--tos":
                        if(parts.Length > 1)
                            ConfigOptions.RunninConfig.TOSPath = parts[1];
                        break;
                    case "--debug":
                        if (parts.Length > 1)
                        {
                            if (Enum.TryParse(parts[1], true, out ConfigOptions.DebugModes lvl)
                                && Enum.IsDefined(typeof(ConfigOptions.DebugModes), lvl))
                            {
                                ConfigOptions.RunninConfig.DebugMode = lvl;
                            }
                            else
                            {
                                ColoredConsole.WriteLine($"Invalid debug level [[red]]{parts[1]}[[/red]]. Use none|quiet|information|full.");
                                ColoredConsole.WriteLine("Defaulting to [[cyan]]Quiet[[/cyan]].");
                                ConfigOptions.RunninConfig.DebugMode = ConfigOptions.DebugModes.Full;
                            }
                        }
                        else
                        {
                            // Bare '--debug' enables full verbosity (most useful default for debugging).
                            ConfigOptions.RunninConfig.DebugMode = ConfigOptions.DebugModes.Full;
                        }
                        break;
                    case "--maxspeed":
                        if (parts.Length > 1 && bool.TryParse(parts[1], out bool _maxs))
                            ConfigOptions.RunninConfig.MaxSpeed = _maxs;
                        break;
                    case "--profile":
                        {
                            // Default: one line per second of emulated time (50 Hz PAL).
                            int every = 50;

                            if (parts.Length > 1 && (!int.TryParse(parts[1], out every) || every <= 0))
                            {
                                ColoredConsole.WriteLine("Invalid profile interval. Use [[cyan]]--profile=N[[/cyan]], with N = frames between report lines.");
                                every = 50;
                            }

                            FrameProfiler.Configure(every);
                        }
                        break;
                    case "--floppy":
                        if (parts.Length > 1)
                            ConfigOptions.RunninConfig.FloppyImagePath = parts[1];
                        break;
                    case "--mouse-sensitivity":
                        if (parts.Length > 1 && Regex.IsMatch(parts[1], @"^\d+"))
                        {
                            if (parts.Length > 1 && float.TryParse(parts[1], out float xSens))
                                ConfigOptions.RunninConfig.MouseSensitivity = xSens;
                        }
                        else
                        {
                            ColoredConsole.WriteLine("Invalid mouse sensitivity format. Use --mouse-sensitivity=N");
                            ColoredConsole.WriteLine("Example: --mouse-sensitivity=2.5");
                            ColoredConsole.WriteLine($"Using default sensitivity [[cyan]]{ConfigOptions.RunninConfig.MouseSensitivity}[[/cyan]].");
                        }
                        break;
                    case "--cycleexact":
                        ConfigOptions.RunninConfig.CycleExactBus =
                            parts.Length < 2 || !bool.TryParse(parts[1], out bool _ce) || _ce;
                        break;
                    case "--busphase":
                        if (parts.Length > 1 && int.TryParse(parts[1], out int _bp))
                            ConfigOptions.RunninConfig.BusPhase = _bp;
                        break;
                    case "--mfpwait":
                        if (parts.Length > 1 && int.TryParse(parts[1], out int _mw))
                            ConfigOptions.RunninConfig.MfpWaitCycles = _mw;
                        break;
                    case "--altconfig":
                        if (parts.Length > 1)
                        {
                            ColoredConsole.WriteLine($"Config override [[cyan]]{parts[1]}[[/cyan]]!");
                            LoadJsonConfig(parts[1]);
                        }
                        break;
                    case "--snapshot":
                        if (parts.Length > 1)
                            StartupSnapshot = parts[1];
                        break;
                    case "--snapshots-dir":
                        if (parts.Length > 1)
                            ConfigOptions.RunninConfig.SnapshotsPath = parts[1];
                        break;
                    case "--screenshots-dir":
                        if (parts.Length > 1)
                            ConfigOptions.RunninConfig.ScreenshotsPath = parts[1];
                        break;
                    case "--library-dir":
                        if (parts.Length > 1)
                            ConfigOptions.RunninConfig.LibraryPath = parts[1];
                        break;
                    case "--no-effects":
                        ConfigOptions.RunninConfig.DisableCrtEffects =
                            parts.Length < 2 || !bool.TryParse(parts[1], out bool _nfx) || _nfx;
                        break;
                    case "--mono":
                    case "--monochrome":
                        ConfigOptions.RunninConfig.MonochromeMonitor =
                            parts.Length < 2 || !bool.TryParse(parts[1], out bool _mono) || _mono;
                        break;
                    case "--midi":
                        if (parts.Length > 1 && TryParseMidiMode(parts[1], out ConfigOptions.MIDIEmulationOptions _midi))
                        {
                            ConfigOptions.RunninConfig.MidiEmulation = _midi;
                        }
                        else
                        {
                            ColoredConsole.WriteLine($"Invalid MIDI mode [[red]]{(parts.Length > 1 ? parts[1] : "")}[[/red]]. Use none|system|mt32.");
                            ColoredConsole.WriteLine("Keeping the configured mode [[cyan]]" + ConfigOptions.RunninConfig.MidiEmulation + "[[/cyan]].");
                        }
                        break;
                    // The port names are the ones the host OS publishes (see HostMidi); they carry
                    // spaces, so on the command line they need quoting: --midi-out="USB MIDI".
                    case "--midi-in":
                        if (parts.Length > 1)
                            ConfigOptions.RunninConfig.MidiInDevice = parts[1];
                        break;
                    case "--midi-out":
                        if (parts.Length > 1)
                            ConfigOptions.RunninConfig.MidiOutDevice = parts[1];
                        break;
                    case "--mt32-roms":
                        if (parts.Length > 1)
                            ConfigOptions.RunninConfig.MT32Rompath = parts[1];
                        break;
                    case "--mt32-volume":
                        if (parts.Length > 1 && int.TryParse(parts[1], out int _mtv))
                        {
                            ConfigOptions.RunninConfig.Mt32Volume = Math.Clamp(_mtv, 0, Mt32Backend.MaxVolume);
                        }
                        else
                        {
                            ColoredConsole.WriteLine($"Invalid MT-32 volume. Use [[cyan]]--mt32-volume=N[[/cyan]] with N in percent (0-{Mt32Backend.MaxVolume}).");
                            ColoredConsole.WriteLine($"Keeping the configured volume [[cyan]]{ConfigOptions.RunninConfig.Mt32Volume}%[[/cyan]].");
                        }
                        break;

                    default:
                        // Anything unrecognized lands here as well, so name it before the list —
                        // otherwise a typo just looks like the emulator refusing to start.
                        if (parts[0].ToLower() is not ("--help" or "-h"))
                            ColoredConsole.WriteLine($"Unknown option [[red]]{parts[0]}[[/red]].{Environment.NewLine}");

                        PrintUsage();
                        Environment.Exit(0);
                        break;
                }
            }
        }

        /// <summary>
        /// Reads the value of <c>--midi</c>. Accepts the friendly spellings a user would try
        /// rather than the enum names, since "BuiltInMT32" is an implementation detail.
        /// </summary>
        static bool TryParseMidiMode(string value, out ConfigOptions.MIDIEmulationOptions mode)
        {
            switch (value.ToLower())
            {
                case "none":
                case "off":
                    mode = ConfigOptions.MIDIEmulationOptions.None;
                    return true;
                case "system":
                case "host":
                case "os":
                    mode = ConfigOptions.MIDIEmulationOptions.System;
                    return true;
                case "mt32":
                case "mt-32":
                case "builtinmt32":
                    mode = ConfigOptions.MIDIEmulationOptions.BuiltInMT32;
                    return true;
                default:
                    mode = ConfigOptions.MIDIEmulationOptions.None;
                    return false;
            }
        }

        /// <summary>
        /// Prints the command-line help. Every option the switch above understands is listed here;
        /// the ones in square brackets work bare as well as with a value.
        /// </summary>
        static void PrintUsage()
        {
            // Defaults are read from a fresh ConfigOptions so the help cannot drift from the code.
            var def = new ConfigOptions();

            ColoredConsole.WriteLine("Usage: [[white]]ASE[[/white]] [options]");

            HelpSection("Machine");
            HelpOption("--tos", "=<path>", "TOS ROM image (192 KB for ST/Mega, 256 KB for STE)");
            HelpOption("--altconfig", "=<path>", "Load this configuration file instead of the default one");
            HelpOption("--maxspeed", "=true|false", "Run as fast as the host allows instead of at ST speed");

            HelpSection("Media and directories");
            HelpOption("--floppy", "=<path>", "Start with a disk image (.st/.msa/.stx/.zip) in drive A");
            HelpOption("--snapshot", "=<path>", "Restore a machine snapshot (.snap) on startup");
            HelpOption("--library-dir", "=<path>", "Folder of the game library (images + Library.json)");
            HelpOption("--snapshots-dir", "=<path>", "Where F11 saves machine snapshots");
            HelpOption("--screenshots-dir", "=<path>", "Where Shift+F11 saves PNG screenshots");

            HelpSection("Display and input");
            HelpOption("--monochrome", "[=true|false]", "Monochrome (SM124) monitor: 640x400 high resolution");
            HelpOption("--no-effects", "[=true|false]", "Bypass the CRT shader: faster on weak GPUs");
            HelpOption("--mouse-sensitivity", "=N", $"Mouse movement divisor: higher is slower (default: {def.MouseSensitivity})");

            HelpSection("MIDI");
            HelpOption("--midi", "=<mode>", "MIDI emulation: none|system|mt32");
            HelpOption("--midi-in", "=<name>", "Host MIDI input port, by name (system mode)");
            HelpOption("--midi-out", "=<name>", "Host MIDI output port, by name (system mode)");
            HelpOption("--mt32-roms", "=<path>", "Folder with the Roland MT-32 ROM images (mt32 mode)");
            HelpOption("--mt32-volume", "=N", $"Built-in MT-32 volume in percent, 0-{Mt32Backend.MaxVolume} (default: {def.Mt32Volume})");

            HelpSection("Timing (advanced)");
            HelpOption("--cycleexact", "[=true|false]", $"Cycle-exact bus wait states (default: {(def.CycleExactBus ? "on" : "off")})");
            HelpOption("--busphase", "=N", $"Phase of the 4-cycle MMU bus grid, 0-3 (default: {def.BusPhase})");
            HelpOption("--mfpwait", "=N", $"Extra wait cycles per MFP access (default: {def.MfpWaitCycles})");

            HelpSection("Diagnostics");
            HelpOption("--debug", "[=level]", "Verbosity: none|quiet|information|full (bare = full)");
            HelpOption("--profile", "[=N]", "Timing breakdown every N frames (default: 50)");
            HelpOption("--help, -h", "", "Show this help message");
        }

        /// <summary>Section header of the help listing.</summary>
        static void HelpSection(string title) =>
            ColoredConsole.WriteLine($"{Environment.NewLine}[[white]]{title}:[[/white]]");

        /// <summary>
        /// One option of the help listing: flag in cyan, its argument in yellow, description
        /// aligned to a fixed column. The padding is computed from the visible text, since the
        /// colour markup is stripped before anything reaches the console.
        /// </summary>
        static void HelpOption(string flag, string argument, string description)
        {
            string padding = new string(' ', Math.Max(1, 30 - flag.Length - argument.Length));
            string coloredArg = argument.Length > 0 ? $"[[yellow]]{argument}[[/yellow]]" : "";

            ColoredConsole.WriteLine($"  [[cyan]]{flag}[[/cyan]]{coloredArg}{padding}{description}");
        }

        public static string GetAppDefaultConfigsFilePath()
        {
            string basePath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appFolderPath = Path.Combine(basePath, AppName);

            if (!Directory.Exists(appFolderPath))
            {
                Directory.CreateDirectory(appFolderPath);
            }

            return appFolderPath;
        }

        /// <summary>Directory where Shift+F11 saves PNG screenshots (configured value, or the
        /// default "Screenshots" folder next to config.json when unset).</summary>
        public static string ScreenshotsDir => DirOrDefault(ConfigOptions.RunninConfig.ScreenshotsPath, "Screenshots");

        /// <summary>Directory where F11 saves machine snapshots (configured value, or the
        /// default "Snapshots" folder next to config.json when unset).</summary>
        public static string SnapshotsDir => DirOrDefault(ConfigOptions.RunninConfig.SnapshotsPath, "Snapshots");

        static string DirOrDefault(string configured, string defaultSubfolder)
            => string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(GetAppDefaultConfigsFilePath(), defaultSubfolder)
                : configured;

        /// <summary>Initial location for a tinyfiledialogs dialog: the given directory with a
        /// trailing separator (which is how tinyfd tells folders from files), or "" when the
        /// directory is unset or missing so the dialog keeps its own default.</summary>
        public static string DialogStartFolder(string dir)
            => !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)
                ? dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar
                : "";

        public void LoadJsonConfig(string ConfigFile = "")
        {
            if(string.IsNullOrEmpty(ConfigFile))
                ConfigFile = PathToDefaultConfig;

            try
            {
                if (File.Exists(ConfigFile))
                {
                    JsonSerializerOptions options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    };

                    string json = File.ReadAllText(ConfigFile);
                    ConfigOptions cfg = JsonSerializer.Deserialize<ConfigOptions>(json, options);

                    if (cfg == null)
                    {
                        ColoredConsole.WriteLine($"ERROR: Could not parse config file [[red]]{ConfigFile}[[/red]].");
                        Environment.Exit(1);
                    }

                    ConfigOptions.RunninConfig = cfg;

                    ColoredConsole.WriteLine($"I'm using [[green]]{ConfigFile}[[/green]] as config file.");
                    return;
                }

            }
            catch 
            {
                ColoredConsole.WriteLine($"ERROR: Could not configure using [[red]]{ConfigFile}[[/red]] config file.");
                Environment.Exit(1);
            }

            ColoredConsole.WriteLine($"ERROR: Config file [[red]]{ConfigFile}[[/red]] does not exists.");
            Environment.Exit(1);
        }

        public void DumpJsonConfig(string ConfigFile = "")
        {
            if (string.IsNullOrEmpty(ConfigFile))
                ConfigFile = PathToDefaultConfig;

            try
            {
                JsonSerializerOptions options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                };

                string json = JsonSerializer.Serialize(ConfigOptions.RunninConfig, options);

                File.WriteAllText(ConfigFile, json);
            }
            catch
            {
                ColoredConsole.WriteLine($"ERROR: Cannot create [[red]]{ConfigFile}[[//red]] config file.");
                Environment.Exit(1);
            }
        }
    }
}
