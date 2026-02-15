using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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
            public int MouseXSensitivity { get; set; } = 2;
            public int MouseYSensitivity { get; set; } = 2;
            public int SampleRate { get; set; } = 44100;

            // Screen flags
            public float Curvature { get; set; } = 0.01f;
            public float Vignette { get; set; } = 0.18f;
            public float Scanline { get; set; } = 1.0f;
            public float ChromAb { get; set; } = 0.25f;
            public float Bloom { get; set; } = 0.22f;
            public float Mask { get; set; } = 0.50f;
            public float Noise { get; set; } = 0.25f;
            
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
                        if (parts.Length > 1 && Regex.IsMatch(parts[1], @"^\d+,\d+$"))
                        {
                            string[] sensParts = parts[1].Split(',');

                            if (int.TryParse(sensParts[0], out int xSens))
                                ConfigOptions.RunninConfig.MouseXSensitivity = xSens;

                            if (int.TryParse(sensParts[1], out int ySens))
                                ConfigOptions.RunninConfig.MouseYSensitivity = ySens;
                        }
                        else
                        {
                            ColoredConsole.WriteLine("Invalid mouse sensitivity format. Use --mouse-sensitivity=X,Y where X and Y are integers.");
                            ColoredConsole.WriteLine("Example: --mouse-sensitivity=3,3");
                            ColoredConsole.WriteLine($"Using default sensitivity [[cyan]]{ConfigOptions.RunninConfig.MouseXSensitivity},{ConfigOptions.RunninConfig.MouseYSensitivity}[[/cyan]].");
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
                        Console.WriteLine("  --floppy=[image.st]           Starts with .st floppy image inserted");
                        Console.WriteLine("  --mouse-sensitivity=X,Y       Set mouse sensitivity for X and Y axes (default: 2,2)");
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
                    ConfigOptions? cfg = JsonSerializer.Deserialize<ConfigOptions>(json, options);

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
