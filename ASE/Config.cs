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

            /// <summary>
            /// Holds the active configuration
            /// </summary>
            public static ConfigOptions RunninConfig = new ConfigOptions();

            public string TOSPath { get; set; } = "tos.rom";

            // Hardware flags
            public STModels STModel { get; set; } = STModels.ST; // Only STFM/F by now
            public RAMConfigurations RAMConfiguration { get; set; } = RAMConfigurations.RAM_1MB;
            public  bool MaxSpeed { get; set; } = false;
            public string FloppyImagePath { get; set; } = "";
            public float MouseSensitivity { get; set; } = 2;
            public int SampleRate { get; set; } = 44100;

            // Screen flags
            public float Curvature { get; set; } = 0.01f;
            public float Vignette { get; set; } = 0.18f;
            public float Scanline { get; set; } = 1.0f;
            public float ChromAb { get; set; } = 0.25f;
            public float Bloom { get; set; } = 0.22f;
            public float Mask { get; set; } = 0.50f;
            public float Noise { get; set; } = 0.25f;

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

            // Debug flags
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
            public bool DiskDump { get; set; } = false;  // Not exposed, only for my testing
            public bool DebugMode { get; set; } = false;
        }

        public static string Version = "";

        const string AppName = "ASE";
        const string DefaultConfigFileName = "config.json";

        string AppDataConfigPath;
        string PathToDefaultConfig;


        public void LoadConfig(string[] args)
        {
            AppDataConfigPath = GetAppDefaultConfigsFilePath();
            PathToDefaultConfig = Path.Combine(AppDataConfigPath, DefaultConfigFileName);

            Version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString();

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
                        ConfigOptions.RunninConfig.DebugMode = true;
                        break;
                    case "--maxspeed":
                        if (parts.Length > 1 && bool.TryParse(parts[1], out bool _maxs))
                            ConfigOptions.RunninConfig.MaxSpeed = _maxs;
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
                    case "--altconfig":
                        if (parts.Length > 1)
                        {
                            ColoredConsole.WriteLine($"Config override [[cyan]]{parts[1]}[[/cyan]]!");
                            LoadJsonConfig(parts[1]);
                        }
                        break;

                    default:
                        Console.WriteLine("Usage: ASE [options]");
                        Console.WriteLine("Options:");
                        Console.WriteLine("  --tos=<path>                  Path to the TOS ROM file (default: tos100.rom)");
                        Console.WriteLine("  --altconfig=<path>            Loads alternative config");
                        Console.WriteLine("  --debug                       Debug mode");
                        Console.WriteLine("  --maxspeed=[true/false]       Run at max speed or ST speed");
                        Console.WriteLine("  --floppy=[image file]         Starts with floppy image inserted");
                        Console.WriteLine("  --mouse-sensitivity=N         Set mouse sensitivity (default: 2)");
                        Console.WriteLine("  --help, -h                    Show this help message");
                        Environment.Exit(0);
                        break;
                }
            }
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
