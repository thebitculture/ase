using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using SDL2;
using TinyDialogsNet;
using Avalonia.Input;

namespace ASE
{
    public partial class ConfigurationWindow : Window
    {
        Config.ConfigOptions configBackup;
        bool ForceReset = false;

        bool _redefineIsRunning = false;
        Button _ButtonRedefineStarted;
        TextBlock _TextBlockJoykey;

        public ConfigurationWindow()
        {
            InitializeComponent();

            configBackup = new Config.ConfigOptions
            {
                STModel = Config.ConfigOptions.RunninConfig.STModel,
                RAMConfiguration = Config.ConfigOptions.RunninConfig.RAMConfiguration,
                TOSPath = Config.ConfigOptions.RunninConfig.TOSPath,

                Curvature = Config.ConfigOptions.RunninConfig.Curvature,
                Vignette = Config.ConfigOptions.RunninConfig.Vignette,
                Scanline = Config.ConfigOptions.RunninConfig.Scanline,
                ChromAb = Config.ConfigOptions.RunninConfig.ChromAb,
                Bloom = Config.ConfigOptions.RunninConfig.Bloom,
                Mask = Config.ConfigOptions.RunninConfig.Mask,
                Noise = Config.ConfigOptions.RunninConfig.Noise,
                ShowBorders = Config.ConfigOptions.RunninConfig.ShowBorders,
                
                MouseSensitivity = Config.ConfigOptions.RunninConfig.MouseSensitivity,

                GamepadButtonX = Config.ConfigOptions.RunninConfig.GamepadButtonX,
                GamepadButtonY = Config.ConfigOptions.RunninConfig.GamepadButtonY,
                GamepadButtonA = Config.ConfigOptions.RunninConfig.GamepadButtonA,
                GamepadButtonB = Config.ConfigOptions.RunninConfig.GamepadButtonB,
                GamepadButtonLS = Config.ConfigOptions.RunninConfig.GamepadButtonLS,
                GamepadButtonRS = Config.ConfigOptions.RunninConfig.GamepadButtonRS,
                GamepadButtonLB = Config.ConfigOptions.RunninConfig.GamepadButtonLB,
                GamepadButtonRB = Config.ConfigOptions.RunninConfig.GamepadButtonRB,
            };

            DataContext = Config.ConfigOptions.RunninConfig;

            ComboX.SelectedIndex = (int)Config.ConfigOptions.RunninConfig.GamepadButtonX;
            ComboY.SelectedIndex = (int)Config.ConfigOptions.RunninConfig.GamepadButtonY;
            ComboA.SelectedIndex = (int)Config.ConfigOptions.RunninConfig.GamepadButtonA;
            ComboB.SelectedIndex = (int)Config.ConfigOptions.RunninConfig.GamepadButtonB;
            ComboLS.SelectedIndex = (int)Config.ConfigOptions.RunninConfig.GamepadButtonLS;
            ComboRS.SelectedIndex = (int)Config.ConfigOptions.RunninConfig.GamepadButtonRS;
            ComboLB.SelectedIndex = (int)Config.ConfigOptions.RunninConfig.GamepadButtonLB;
            ComboRB.SelectedIndex = (int)Config.ConfigOptions.RunninConfig.GamepadButtonRB;

            RebindGLSliders();
            RebindJoymap();

            chkShowBorders.IsChecked = Config.ConfigOptions.RunninConfig.ShowBorders;
            
            // Directories tab: edited locally and committed to the config on OK only,
            // so Cancel discards the changes without needing backup fields
            TextScreenshotsDir.Text = Config.ConfigOptions.RunninConfig.ScreenshotsPath;
            TextSnapshotsDir.Text = Config.ConfigOptions.RunninConfig.SnapshotsPath;
            TextDiskImagesDir.Text = Config.ConfigOptions.RunninConfig.DiskImagesPath;
            TextTOSRomsDir.Text = Config.ConfigOptions.RunninConfig.TOSRomsPath;
        }

        // The emulator parks (and stops receiving host input) while the window is open
        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            ASEMain.EnterUiPause();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            ASEMain.ExitUiPause();
        }

        private static string GetDisplayKeyName(SDL.SDL_Scancode scancode)
        {
            var keycode = SDL.SDL_GetKeyFromScancode(scancode);
            return SDL.SDL_GetKeyName(keycode);
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

        void RebindJoymap()
        {
            TextUp.Text = GetDisplayKeyName(Config.ConfigOptions.RunninConfig.KeyJoy1Up);
            TextDown.Text = GetDisplayKeyName(Config.ConfigOptions.RunninConfig.KeyJoy1Down);
            TextLeft.Text = GetDisplayKeyName(Config.ConfigOptions.RunninConfig.KeyJoy1Left);
            TextRight.Text = GetDisplayKeyName(Config.ConfigOptions.RunninConfig.KeyJoy1Right);
            TextFire.Text = GetDisplayKeyName(Config.ConfigOptions.RunninConfig.KeyJoy1Fire);
        }

        public async void OnBrowseTOSImageClick(object sender, RoutedEventArgs e)
        {
            var (canceled, selpath) = await Dialogs.OpenFile("Select TOS image file",
                Config.DialogStartFolder(TextTOSRomsDir.Text), new FileFilter("TOS image", ["*.rom", "*.img", "*.tos"]));

            if (!canceled && selpath.Count() == 1)
            {
                string TOSImagePath = selpath.First();

                if (CheckTOSCompatibility(TOSImagePath))
                {
                    Config.ConfigOptions.RunninConfig.TOSPath = TOSImagePath;
                    TextTOSImage.Text = TOSImagePath;
                    ForceReset = true;
                }
            }
        }

        public void OnOkClick(object sender, RoutedEventArgs e)
        {
            // TOS path cannot be empty
            if (string.IsNullOrEmpty(Config.ConfigOptions.RunninConfig.TOSPath))
            {
                TinyDialogs.MessageBox("Error", $"TOS image path cannot be empty.", MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                return;
            }

            // Cheks if ROM and ST model are compatible
            if (!CheckTOSCompatibility(Config.ConfigOptions.RunninConfig.TOSPath))
                return;

            if (ForceReset)
            {
                MessageBoxButton result = TinyDialogs.MessageBox("Reset", $"ST must be reset to apply changes.", MessageBoxDialogType.YesNo, MessageBoxIconType.Warning, MessageBoxButton.Yes);
                if (result == MessageBoxButton.No)
                    return;
            }

            // Save gamepad buttons
            Config.ConfigOptions.RunninConfig.GamepadButtonX = (Config.ConfigOptions.GamepadButtonsMapping)ComboX.SelectedIndex;
            Config.ConfigOptions.RunninConfig.GamepadButtonY = (Config.ConfigOptions.GamepadButtonsMapping)ComboY.SelectedIndex;
            Config.ConfigOptions.RunninConfig.GamepadButtonA = (Config.ConfigOptions.GamepadButtonsMapping)ComboA.SelectedIndex;
            Config.ConfigOptions.RunninConfig.GamepadButtonB = (Config.ConfigOptions.GamepadButtonsMapping)ComboB.SelectedIndex;
            Config.ConfigOptions.RunninConfig.GamepadButtonLS = (Config.ConfigOptions.GamepadButtonsMapping)ComboLS.SelectedIndex;
            Config.ConfigOptions.RunninConfig.GamepadButtonRS = (Config.ConfigOptions.GamepadButtonsMapping)ComboRS.SelectedIndex;
            Config.ConfigOptions.RunninConfig.GamepadButtonLB = (Config.ConfigOptions.GamepadButtonsMapping)ComboLB.SelectedIndex;
            Config.ConfigOptions.RunninConfig.GamepadButtonRB = (Config.ConfigOptions.GamepadButtonsMapping)ComboRB.SelectedIndex;

            // Save directories (empty screenshots/snapshots fall back to the defaults
            // next to config.json at save time)
            Config.ConfigOptions.RunninConfig.ScreenshotsPath = (TextScreenshotsDir.Text ?? "").Trim();
            Config.ConfigOptions.RunninConfig.SnapshotsPath = (TextSnapshotsDir.Text ?? "").Trim();
            Config.ConfigOptions.RunninConfig.DiskImagesPath = (TextDiskImagesDir.Text ?? "").Trim();
            Config.ConfigOptions.RunninConfig.TOSRomsPath = (TextTOSRomsDir.Text ?? "").Trim();

            Program.Config.DumpJsonConfig();

            if (ForceReset)
                ASEMain.HardReset();

            Close();
        }

        void CompleteRedefine()
        {
            _ButtonRedefineStarted.Content = "Redefine";

            this.KeyDown -= Redefine_KeyDown;
            _redefineIsRunning = false;

            RebindJoymap();
        }

        public void OnRedefine(object sender, RoutedEventArgs e)
        {
            if (_redefineIsRunning)
            {
                CompleteRedefine();
            }
            else
            {
                _ButtonRedefineStarted = sender as Button;
                _ButtonRedefineStarted.Content = "Cancel";

                switch (_ButtonRedefineStarted.Name)
                {
                    case "btnRedefineUp":
                        _TextBlockJoykey = TextUp;
                        break;
                    case "btnRedefineDown":
                        _TextBlockJoykey = TextDown;
                        break;
                    case "btnRedefineLeft":
                        _TextBlockJoykey = TextLeft;
                        break;
                    case "btnRedefineRight":
                        _TextBlockJoykey = TextRight;
                        break;
                    case "btnRedefineFire":
                        _TextBlockJoykey = TextFire;
                        break;
                }

                _TextBlockJoykey.Text = "Press a key...";

                _redefineIsRunning = true;
                this.KeyDown += Redefine_KeyDown;
            }
        }

        private void Redefine_KeyDown(object sender, Avalonia.Input.KeyEventArgs e)
        {
            e.Handled = true;
            Key avaloniaKey = e.Key;

            SDL.SDL_Scancode sdlScancode = AvaloniaKeyToSDLScancode(avaloniaKey);

            if (sdlScancode != SDL.SDL_Scancode.SDL_SCANCODE_UNKNOWN)
            {
                switch (_ButtonRedefineStarted.Name)
                {
                    case "btnRedefineUp":
                        Config.ConfigOptions.RunninConfig.KeyJoy1Up = sdlScancode;
                        break;
                    case "btnRedefineDown":
                        Config.ConfigOptions.RunninConfig.KeyJoy1Down = sdlScancode;
                        break;
                    case "btnRedefineLeft":
                        Config.ConfigOptions.RunninConfig.KeyJoy1Left = sdlScancode;
                        break;
                    case "btnRedefineRight":
                        Config.ConfigOptions.RunninConfig.KeyJoy1Right = sdlScancode;
                        break;
                    case "btnRedefineFire":
                        Config.ConfigOptions.RunninConfig.KeyJoy1Fire = sdlScancode;
                        break;
                }

                CompleteRedefine();
            }
            else
            {
                _TextBlockJoykey.Text = "Try another key...";
            }
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            // Restores configuration values discarding any changes made in the UI

            Config.ConfigOptions.RunninConfig.STModel = configBackup.STModel;
            Config.ConfigOptions.RunninConfig.RAMConfiguration = configBackup.RAMConfiguration;
            Config.ConfigOptions.RunninConfig.TOSPath = configBackup.TOSPath;

            Config.ConfigOptions.RunninConfig.Curvature = configBackup.Curvature;
            Config.ConfigOptions.RunninConfig.Vignette = configBackup.Vignette;
            Config.ConfigOptions.RunninConfig.Scanline = configBackup.Scanline;
            Config.ConfigOptions.RunninConfig.ChromAb = configBackup.ChromAb;
            Config.ConfigOptions.RunninConfig.Bloom = configBackup.Bloom;
            Config.ConfigOptions.RunninConfig.ShowBorders = configBackup.ShowBorders;
            
            Config.ConfigOptions.RunninConfig.MouseSensitivity = configBackup.MouseSensitivity;

            Config.ConfigOptions.RunninConfig.GamepadButtonX = configBackup.GamepadButtonX;
            Config.ConfigOptions.RunninConfig.GamepadButtonY = configBackup.GamepadButtonY;
            Config.ConfigOptions.RunninConfig.GamepadButtonA = configBackup.GamepadButtonA;
            Config.ConfigOptions.RunninConfig.GamepadButtonB = configBackup.GamepadButtonB;
            Config.ConfigOptions.RunninConfig.GamepadButtonLS = configBackup.GamepadButtonLS;
            Config.ConfigOptions.RunninConfig.GamepadButtonRS = configBackup.GamepadButtonRS;
            Config.ConfigOptions.RunninConfig.GamepadButtonLB = configBackup.GamepadButtonLB;
            Config.ConfigOptions.RunninConfig.GamepadButtonRB = configBackup.GamepadButtonRB;
            
            Close();
        }

        bool CheckTOSCompatibility(string tospath)
        {
            if (File.Exists(tospath))
            {
                long fileSize = new FileInfo(tospath).Length;
                if ((Config.ConfigOptions.RunninConfig.STModel == Config.ConfigOptions.STModels.ST || Config.ConfigOptions.RunninConfig.STModel == Config.ConfigOptions.STModels.Mega) && fileSize != 192 * 1024)
                {
                    TinyDialogs.MessageBox("Error", $"TOS image for STF/FM must be 1.00 to 1.04", MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                    return false;
                }
                else if (Config.ConfigOptions.RunninConfig.STModel == Config.ConfigOptions.STModels.STE && (fileSize != 256 * 1024))
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

        public void OnBrowseScreenshotsDirClick(object sender, RoutedEventArgs e) => FileUtils.BrowseDirectory(TextScreenshotsDir, "Select screenshots directory");
        public void OnBrowseSnapshotsDirClick(object sender, RoutedEventArgs e) => FileUtils.BrowseDirectory(TextSnapshotsDir, "Select snapshots directory");
        public void OnBrowseDiskImagesDirClick(object sender, RoutedEventArgs e) => FileUtils.BrowseDirectory(TextDiskImagesDir, "Select disk images directory");
        public void OnBrowseTOSRomsDirClick(object sender, RoutedEventArgs e) => FileUtils.BrowseDirectory(TextTOSRomsDir, "Select TOS ROMs directory");

        private void Model_SelectionChanged(object sender, SelectionChangedEventArgs e) => ForceReset = (Config.ConfigOptions.RunninConfig.STModel != configBackup.STModel) ? true : false;
        private void Memory_SelectionChanged(object sender, SelectionChangedEventArgs e) => ForceReset = (Config.ConfigOptions.RunninConfig.RAMConfiguration != configBackup.RAMConfiguration) ? true : false;

        private void SliderCurvature_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Config.ConfigOptions.RunninConfig.Curvature = (float)SliderCurvature.Value;
        private void SliderVignette_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Config.ConfigOptions.RunninConfig.Vignette = (float)SliderVignette.Value;
        private void SliderScanline_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Config.ConfigOptions.RunninConfig.Scanline = (float)SliderScanline.Value;
        private void SliderAberration_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Config.ConfigOptions.RunninConfig.ChromAb = (float)SliderAberration.Value;
        private void SliderBloom_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Config.ConfigOptions.RunninConfig.Bloom = (float)SliderBloom.Value;
        private void SliderMask_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Config.ConfigOptions.RunninConfig.Mask = (float)SliderMask.Value;
        private void SliderNoise_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Config.ConfigOptions.RunninConfig.Noise = (float)SliderNoise.Value;

        private void ButtonZeroGLValues_OnClick(object sender, RoutedEventArgs e)
        {
            Config.ConfigOptions.RunninConfig.Curvature = 0.0f;
            Config.ConfigOptions.RunninConfig.Vignette = 0.0f;
            Config.ConfigOptions.RunninConfig.Scanline = 0.0f;
            Config.ConfigOptions.RunninConfig.ChromAb = 0.0f;
            Config.ConfigOptions.RunninConfig.Bloom = 0.0f;
            Config.ConfigOptions.RunninConfig.Mask = 0.0f;
            Config.ConfigOptions.RunninConfig.Noise = 0.0f;

            RebindGLSliders();
        }

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

        private SDL.SDL_Scancode AvaloniaKeyToSDLScancode(Key key)
        {
            return key switch
            {
                Key.A => SDL.SDL_Scancode.SDL_SCANCODE_A,
                Key.B => SDL.SDL_Scancode.SDL_SCANCODE_B,
                Key.C => SDL.SDL_Scancode.SDL_SCANCODE_C,
                Key.D => SDL.SDL_Scancode.SDL_SCANCODE_D,
                Key.E => SDL.SDL_Scancode.SDL_SCANCODE_E,
                Key.F => SDL.SDL_Scancode.SDL_SCANCODE_F,
                Key.G => SDL.SDL_Scancode.SDL_SCANCODE_G,
                Key.H => SDL.SDL_Scancode.SDL_SCANCODE_H,
                Key.I => SDL.SDL_Scancode.SDL_SCANCODE_I,
                Key.J => SDL.SDL_Scancode.SDL_SCANCODE_J,
                Key.K => SDL.SDL_Scancode.SDL_SCANCODE_K,
                Key.L => SDL.SDL_Scancode.SDL_SCANCODE_L,
                Key.M => SDL.SDL_Scancode.SDL_SCANCODE_M,
                Key.N => SDL.SDL_Scancode.SDL_SCANCODE_N,
                Key.O => SDL.SDL_Scancode.SDL_SCANCODE_O,
                Key.P => SDL.SDL_Scancode.SDL_SCANCODE_P,
                Key.Q => SDL.SDL_Scancode.SDL_SCANCODE_Q,
                Key.R => SDL.SDL_Scancode.SDL_SCANCODE_R,
                Key.S => SDL.SDL_Scancode.SDL_SCANCODE_S,
                Key.T => SDL.SDL_Scancode.SDL_SCANCODE_T,
                Key.U => SDL.SDL_Scancode.SDL_SCANCODE_U,
                Key.V => SDL.SDL_Scancode.SDL_SCANCODE_V,
                Key.W => SDL.SDL_Scancode.SDL_SCANCODE_W,
                Key.X => SDL.SDL_Scancode.SDL_SCANCODE_X,
                Key.Y => SDL.SDL_Scancode.SDL_SCANCODE_Y,
                Key.Z => SDL.SDL_Scancode.SDL_SCANCODE_Z,

                Key.D0 => SDL.SDL_Scancode.SDL_SCANCODE_0,
                Key.D1 => SDL.SDL_Scancode.SDL_SCANCODE_1,
                Key.D2 => SDL.SDL_Scancode.SDL_SCANCODE_2,
                Key.D3 => SDL.SDL_Scancode.SDL_SCANCODE_3,
                Key.D4 => SDL.SDL_Scancode.SDL_SCANCODE_4,
                Key.D5 => SDL.SDL_Scancode.SDL_SCANCODE_5,
                Key.D6 => SDL.SDL_Scancode.SDL_SCANCODE_6,
                Key.D7 => SDL.SDL_Scancode.SDL_SCANCODE_7,
                Key.D8 => SDL.SDL_Scancode.SDL_SCANCODE_8,
                Key.D9 => SDL.SDL_Scancode.SDL_SCANCODE_9,

                // Numeric pad
                Key.NumPad0 => SDL.SDL_Scancode.SDL_SCANCODE_KP_0,
                Key.NumPad1 => SDL.SDL_Scancode.SDL_SCANCODE_KP_1,
                Key.NumPad2 => SDL.SDL_Scancode.SDL_SCANCODE_KP_2,
                Key.NumPad3 => SDL.SDL_Scancode.SDL_SCANCODE_KP_3,
                Key.NumPad4 => SDL.SDL_Scancode.SDL_SCANCODE_KP_4,
                Key.NumPad5 => SDL.SDL_Scancode.SDL_SCANCODE_KP_5,
                Key.NumPad6 => SDL.SDL_Scancode.SDL_SCANCODE_KP_6,
                Key.NumPad7 => SDL.SDL_Scancode.SDL_SCANCODE_KP_7,
                Key.NumPad8 => SDL.SDL_Scancode.SDL_SCANCODE_KP_8,
                Key.NumPad9 => SDL.SDL_Scancode.SDL_SCANCODE_KP_9,
                Key.Add => SDL.SDL_Scancode.SDL_SCANCODE_KP_PLUS,      // Numpad +
                Key.Subtract => SDL.SDL_Scancode.SDL_SCANCODE_KP_MINUS,     // Numpad -
                Key.Multiply => SDL.SDL_Scancode.SDL_SCANCODE_KP_MULTIPLY,  // Numpad *
                Key.Divide => SDL.SDL_Scancode.SDL_SCANCODE_KP_DIVIDE,    // Numpad /
                Key.Decimal => SDL.SDL_Scancode.SDL_SCANCODE_KP_PERIOD,    // Numpad .

                Key.F1 => SDL.SDL_Scancode.SDL_SCANCODE_F1,
                Key.F2 => SDL.SDL_Scancode.SDL_SCANCODE_F2,
                Key.F3 => SDL.SDL_Scancode.SDL_SCANCODE_F3,
                Key.F4 => SDL.SDL_Scancode.SDL_SCANCODE_F4,
                Key.F5 => SDL.SDL_Scancode.SDL_SCANCODE_F5,
                Key.F6 => SDL.SDL_Scancode.SDL_SCANCODE_F6,
                Key.F7 => SDL.SDL_Scancode.SDL_SCANCODE_F7,
                Key.F8 => SDL.SDL_Scancode.SDL_SCANCODE_F8,
                Key.F9 => SDL.SDL_Scancode.SDL_SCANCODE_F9,
                Key.F10 => SDL.SDL_Scancode.SDL_SCANCODE_F10,

                Key.Up => SDL.SDL_Scancode.SDL_SCANCODE_UP,
                Key.Down => SDL.SDL_Scancode.SDL_SCANCODE_DOWN,
                Key.Left => SDL.SDL_Scancode.SDL_SCANCODE_LEFT,
                Key.Right => SDL.SDL_Scancode.SDL_SCANCODE_RIGHT,
                Key.Home => SDL.SDL_Scancode.SDL_SCANCODE_HOME,
                Key.End => SDL.SDL_Scancode.SDL_SCANCODE_END,
                Key.PageUp => SDL.SDL_Scancode.SDL_SCANCODE_PAGEUP,
                Key.PageDown => SDL.SDL_Scancode.SDL_SCANCODE_PAGEDOWN,
                Key.Insert => SDL.SDL_Scancode.SDL_SCANCODE_INSERT,
                Key.Delete => SDL.SDL_Scancode.SDL_SCANCODE_DELETE,

                Key.Space => SDL.SDL_Scancode.SDL_SCANCODE_SPACE,
                Key.Back => SDL.SDL_Scancode.SDL_SCANCODE_BACKSPACE,
                Key.Tab => SDL.SDL_Scancode.SDL_SCANCODE_TAB,

                Key.LeftShift => SDL.SDL_Scancode.SDL_SCANCODE_LSHIFT,
                Key.RightShift => SDL.SDL_Scancode.SDL_SCANCODE_RSHIFT,
                Key.LeftCtrl => SDL.SDL_Scancode.SDL_SCANCODE_LCTRL,
                Key.RightCtrl => SDL.SDL_Scancode.SDL_SCANCODE_RCTRL,
                Key.LeftAlt => SDL.SDL_Scancode.SDL_SCANCODE_LALT,   // Alt / Option (Mac)
                Key.RightAlt => SDL.SDL_Scancode.SDL_SCANCODE_RALT,   // AltGr / Option derecho (Mac)
                Key.LWin => SDL.SDL_Scancode.SDL_SCANCODE_LGUI,   // Win izq / Cmd izq (Mac)
                Key.RWin => SDL.SDL_Scancode.SDL_SCANCODE_RGUI,   // Win der / Cmd der (Mac)

                Key.PrintScreen => SDL.SDL_Scancode.SDL_SCANCODE_PRINTSCREEN,
                Key.Pause => SDL.SDL_Scancode.SDL_SCANCODE_PAUSE,

                _ => SDL.SDL_Scancode.SDL_SCANCODE_UNKNOWN
            };
        }

        private void ChkShowBorders_OnIsCheckedChanged(object sender, RoutedEventArgs e)
        {
            if (((CheckBox)sender).IsChecked is bool isChecked)
                Config.ConfigOptions.RunninConfig.ShowBorders = isChecked;
        }
    }
}
