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
            /// <summary>
            /// Holds the active configuration
            /// </summary>
            public static ConfigOptions RunninConfig = new ConfigOptions();

            public string TOSPath { get; set; } = "tos.rom";

            // Hardware flags
            public  bool MaxSpeed { get; set; } = false;
            public string FloppyImagePath { get; set; } = "";
            public int MouseXSensitivity { get; set; } = 2;
            public int MouseYSensitivity { get; set; } = 2;
            public int SampleRate { get; set; } = 44100;

            // Debug flags
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
            public bool DiskDump { get; set; } = false;  // Not exposed, only for testing
            public bool DebugMode { get; set; } = false;
        }

        /// <summary>
        /// Loads the emulator configuration from command-line arguments and a configuration file.
        /// </summary>
        /// <remarks>If the configuration file 'config.json' does not exist, a default configuration is
        /// created. Command-line arguments can override settings in the configuration file. Supported options include
        /// specifying the TOS ROM path, enabling debug mode, setting maximum speed, providing a floppy image, adjusting
        /// mouse sensitivity, and loading an alternative configuration file.</remarks>
        /// <param name="args">An array of command-line arguments that specify configuration options, such as ROM file paths, debug mode,
        /// and mouse sensitivity settings.</param>
        public void LoadConfig(string[] args)
        {
            var exePath = Environment.ProcessPath!;
            var fileVersionInfo = FileVersionInfo.GetVersionInfo(exePath);

            string fileVersion = fileVersionInfo.FileVersion;

            ColoredConsole.WriteLine($"[[white]]ATARI SYSTEM EMULATOR[[/white]] v{fileVersion} - The Bit Culture {DateTime.Now.Year}");
            ColoredConsole.WriteLine("👉 [[magenta]]https://github.com/thebitculture/ase[[/magenta]]");
            ColoredConsole.WriteLine("👉 [[magenta]]https://youtube.com/@thebitculture?si=2s4M5Iu4QbIdq_hn[[/magenta]]" + Environment.NewLine);

            if (File.Exists("config.json"))
                LoadJsonConfig("config.json");
            else
                DumpJsonConfig("config.json");  // Creates default configuration

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
                            Console.WriteLine("Invalid mouse sensitivity format. Use --mouse-sensitivity=X,Y where X and Y are integers.");
                            Environment.Exit(1);
                        }
                        break;
                    case "--altconfig":
                        if (parts.Length > 1)
                            LoadJsonConfig(parts[1]);
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

        /// <summary>
        /// Loads application configuration settings from a specified JSON file and applies them to the global
        /// configuration.
        /// </summary>
        /// <remarks>If the configuration file does not exist or cannot be parsed, an error message is
        /// displayed and the application terminates. The method updates the global configuration options based on the
        /// contents of the file.</remarks>
        /// <param name="ConfigFile">The path to the JSON configuration file to load. The file must exist and be in a valid JSON format.</param>
        void LoadJsonConfig(string ConfigFile)
        {
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
                        Console.WriteLine($"ERROR: Could not parse config file {ConfigFile}.");
                        Environment.Exit(1);
                    }

                    ConfigOptions.RunninConfig = cfg;

                    ColoredConsole.WriteLine($"I'm using [[green]]{ConfigFile}[[/green]] as config file.");
                    return;
                }

            }
            catch 
            {
                Console.WriteLine($"ERROR: Could not configure using {ConfigFile} config file.");
                Environment.Exit(1);
            }

            Console.WriteLine($"ERROR: Config file {ConfigFile} does not exists.");
            Environment.Exit(1);
        }

        /// <summary>
        /// Serializes the current configuration options to a JSON file specified by the ConfigFile parameter.
        /// </summary>
        /// <remarks>If an error occurs during the file writing process, an error message is displayed,
        /// and the application exits with a non-zero status.</remarks>
        /// <param name="ConfigFile">The path to the file where the JSON configuration will be written. This file will be created or overwritten
        /// if it already exists.</param>
        void DumpJsonConfig(string ConfigFile)
        {
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
                Console.WriteLine($"ERROR: Cannot create {ConfigFile} config file.");
                Environment.Exit(1);
            }
        }
    }
}
