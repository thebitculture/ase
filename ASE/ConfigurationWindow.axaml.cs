using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls.Primitives;
using TinyDialogsNet;

namespace ASE
{
    public partial class ConfigurationWindow : Window
    {
        public class ConfigOptions : INotifyPropertyChanged
        {
            private int _stModel;
            public int STModel
            {
                get => _stModel;
                set
                {
                    if (_stModel == value) return;
                    _stModel = value;
                    OnPropertyChanged();
                }
            }

            private int _ramConfiguration;
            public int RAMConfiguration
            {
                get => _ramConfiguration;
                set
                {
                    if (_ramConfiguration == value) return;
                    _ramConfiguration = value;
                    OnPropertyChanged();
                }
            }

            private string? _tosPath;
            public string? TOSPath
            {
                get => _tosPath;
                set
                {
                    if (_tosPath == value) return;
                    _tosPath = value;
                    OnPropertyChanged();
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            private void OnPropertyChanged([CallerMemberName] string? p = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
        }

        Config.ConfigOptions configBackup;
        bool ForceReset = false;

        ConfigOptions options = new ConfigOptions
        {
            STModel = (int)Config.ConfigOptions.RunninConfig.STModel,
            RAMConfiguration = (int)Config.ConfigOptions.RunninConfig.RAMConfiguration,
            TOSPath = Config.ConfigOptions.RunninConfig.TOSPath
        };

        public ConfigurationWindow()
        {
            InitializeComponent();

            configBackup = new Config.ConfigOptions
            {
                Curvature = Config.ConfigOptions.RunninConfig.Curvature,
                Vignette = Config.ConfigOptions.RunninConfig.Vignette,
                Scanline = Config.ConfigOptions.RunninConfig.Scanline,
                ChromAb = Config.ConfigOptions.RunninConfig.ChromAb,
                Bloom = Config.ConfigOptions.RunninConfig.Bloom,
                Mask = Config.ConfigOptions.RunninConfig.Mask,
                Noise = Config.ConfigOptions.RunninConfig.Noise
            };

            DataContext = options;
            RebindGLSliders();
        }

        void RebindGLSliders()
        {
            SliderCurvature.Value = Config.ConfigOptions.RunninConfig.Curvature;
            SliderVignette.Value = Config.ConfigOptions.RunninConfig.Vignette;
            SliderScanline.Value = Config.ConfigOptions.RunninConfig.Scanline;
            SliderAberration.Value = Config.ConfigOptions.RunninConfig.ChromAb;
            SliderBloom.Value = Config.ConfigOptions.RunninConfig.Bloom;
            SliderMask.Value = Config.ConfigOptions.RunninConfig.Mask;
            SliderNoise.Value = Config.ConfigOptions.RunninConfig.Noise;
        }

        public void OnBrowseTOSImageClick(object sender, RoutedEventArgs e)
        {
            var (canceled, selpath) = TinyDialogs.OpenFileDialog("Select TOS image file", "", false, new FileFilter("TOS image", ["*.rom", "*.img", "*.tos"]));

            if (!canceled && selpath.Count() == 1)
            {
                string TOSImagePath = selpath.First();

                if (CheckTOSCompatibility(TOSImagePath))
                {
                    options.TOSPath = TOSImagePath;
                    ForceReset = true;
                }
            }
        }

        public void OnOkClick(object sender, RoutedEventArgs e)
        {
            // Checks if ST model was changed, if so, a reset is required to apply changes
            if (Config.ConfigOptions.RunninConfig.STModel != (Config.ConfigOptions.STModels)options.STModel ||
                Config.ConfigOptions.RunninConfig.RAMConfiguration != (Config.ConfigOptions.RAMConfigurations)options.RAMConfiguration)
                ForceReset = true;

            // TOS path cannot be empty
            if (string.IsNullOrEmpty(options.TOSPath))
            {
                TinyDialogs.MessageBox("Error", $"TOS image path cannot be empty.", MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                return;
            }

            // Cheks if ROM and ST model are compatible
            if (!CheckTOSCompatibility(options.TOSPath))
                return;

            if (ForceReset)
            {
                MessageBoxButton result = TinyDialogs.MessageBox("Reset", $"ST must be reset to apply changes.", MessageBoxDialogType.YesNo, MessageBoxIconType.Warning, MessageBoxButton.Yes);
                if (result == MessageBoxButton.No)
                    return;
            }

            Config.ConfigOptions.RunninConfig.STModel = (Config.ConfigOptions.STModels)options.STModel;
            Config.ConfigOptions.RunninConfig.RAMConfiguration = (Config.ConfigOptions.RAMConfigurations)options.RAMConfiguration;
            Config.ConfigOptions.RunninConfig.TOSPath = options.TOSPath;
            Program.Config.DumpJsonConfig();

            if (ForceReset)
                CPU.InitCpu();

            Close();
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            // Restores configuration values discarding any changes made in the UI

            Config.ConfigOptions.RunninConfig.Curvature = configBackup.Curvature;
            Config.ConfigOptions.RunninConfig.Vignette = configBackup.Vignette;
            Config.ConfigOptions.RunninConfig.Scanline = configBackup.Scanline;
            Config.ConfigOptions.RunninConfig.ChromAb = configBackup.ChromAb;
            Config.ConfigOptions.RunninConfig.Bloom = configBackup.Bloom;

            Close();
        }

        bool CheckTOSCompatibility(string tospath)
        {
            if (File.Exists(tospath))
            {
                long fileSize = new FileInfo(tospath).Length;
                if ((options.STModel == 0 || options.STModel == 1) && fileSize != 192 * 1024)
                {
                    TinyDialogs.MessageBox("Error", $"TOS image for STF/FM must be 1.00 to 1.04", MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                    return false;
                }
                else if (options.STModel == 2 && (fileSize != 256 * 1024))
                {
                    TinyDialogs.MessageBox("Error", $"TOS image for STE must be 1.06 to 2.06", MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                    return false;
                }
            }
            else
            {
                TinyDialogs.MessageBox("Error", $"Selected TOS image '{tospath}' does not exist.", MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                return false;
            }

            return true;
        }

        private void SliderCurvature_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Config.ConfigOptions.RunninConfig.Curvature = (float)SliderCurvature.Value;
        private void SliderVignette_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Config.ConfigOptions.RunninConfig.Vignette = (float)SliderVignette.Value;
        private void SliderScanline_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Config.ConfigOptions.RunninConfig.Scanline = (float)SliderScanline.Value;
        private void SliderAberration_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Config.ConfigOptions.RunninConfig.ChromAb = (float)SliderAberration.Value;
        private void SliderBloom_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Config.ConfigOptions.RunninConfig.Bloom = (float)SliderBloom.Value;
        private void SliderMask_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Config.ConfigOptions.RunninConfig.Mask = (float)SliderMask.Value;
        private void SliderNoise_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Config.ConfigOptions.RunninConfig.Noise = (float)SliderNoise.Value;

        private void ButtonDefaultGLValues_OnClick(object sender, RoutedEventArgs e)
        {
            Config.ConfigOptions.RunninConfig.Curvature = 0.01f;
            Config.ConfigOptions.RunninConfig.Vignette = 0.18f;
            Config.ConfigOptions.RunninConfig.Scanline = 1.00f;
            Config.ConfigOptions.RunninConfig.ChromAb = 0.25f;
            Config.ConfigOptions.RunninConfig.Bloom = 0.22f;
            Config.ConfigOptions.RunninConfig.Mask = 0.50f;
            Config.ConfigOptions.RunninConfig.Noise = 0.25f;
            
            RebindGLSliders();
        }
    }
}
