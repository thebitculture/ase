using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SDL2;
using TinyDialogsNet;
using Avalonia.Input;

namespace ASE
{
    public partial class ConfigurationWindow : Window
    {
        Config.ConfigOptions configBackup;
        bool ForceReset = false;

        // The model ComboBox raises Model_SelectionChanged while its binding is first
        // resolved (DataContext assignment in the constructor); the ROM/model guard must
        // not fire until the user actually interacts with the window.
        bool _uiReady = false;

        // Last model the ROM/model guard accepted, so a rejected change can snap back to it
        // (preserving e.g. a Mega/blitter choice) instead of a hardcoded default.
        int _lastValidModelIndex;

        bool _redefineIsRunning = false;
        Button _ButtonRedefineStarted;
        TextBlock _TextBlockJoykey;

        // Video-mode reveal: the CRT/borders card still collapses physically so the space is
        // reclaimed, but its contents also fade and move a few pixels. This makes the state change
        // read as a lightweight settings transition rather than a "roller blind".
        double _crtExpandedHeight;
        bool _crtLayoutReady;
        readonly TranslateTransform _crtRevealTranslate = new();

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
                DisableCrtEffects = Config.ConfigOptions.RunninConfig.DisableCrtEffects,
                ShowBorders = Config.ConfigOptions.RunninConfig.ShowBorders,
                MonochromeMonitor = Config.ConfigOptions.RunninConfig.MonochromeMonitor,

                MouseSensitivity = Config.ConfigOptions.RunninConfig.MouseSensitivity,

                GamepadButtonX = Config.ConfigOptions.RunninConfig.GamepadButtonX,
                GamepadButtonY = Config.ConfigOptions.RunninConfig.GamepadButtonY,
                GamepadButtonA = Config.ConfigOptions.RunninConfig.GamepadButtonA,
                GamepadButtonB = Config.ConfigOptions.RunninConfig.GamepadButtonB,
                GamepadButtonLS = Config.ConfigOptions.RunninConfig.GamepadButtonLS,
                GamepadButtonRS = Config.ConfigOptions.RunninConfig.GamepadButtonRS,
                GamepadButtonLB = Config.ConfigOptions.RunninConfig.GamepadButtonLB,
                GamepadButtonRB = Config.ConfigOptions.RunninConfig.GamepadButtonRB,

                // The emulation mode and the MT-32 volume: the MIDI ports and the ROM folder are
                // edited locally and reach the config in OnOkClick, so Cancel has nothing to undo
                // there. The volume, like the CRT sliders, is applied live and so needs restoring.
                MidiEmulation = Config.ConfigOptions.RunninConfig.MidiEmulation,
                Mt32Volume = Config.ConfigOptions.RunninConfig.Mt32Volume,
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
            // Segmented monitor selector: only the monochrome half carries the handler,
            // and setting both here runs before _uiReady, so it cannot request a reset.
            RadioMonochromeMonitor.IsChecked = Config.ConfigOptions.RunninConfig.MonochromeMonitor;
            RadioColourMonitor.IsChecked = !Config.ConfigOptions.RunninConfig.MonochromeMonitor;

            // The switch reads as "Effects on/off": checked = effects enabled = NOT disabled.
            // Assigning IsChecked raises IsCheckedChanged, which greys out the sliders when the
            // effects come up already off.
            ToggleEffects.IsChecked = !Config.ConfigOptions.RunninConfig.DisableCrtEffects;
            UpdateEffectsSlidersEnabled();

            // Directories tab: edited locally and committed to the config on OK only,
            // so Cancel discards the changes without needing backup fields
            TextScreenshotsDir.Text = Config.ConfigOptions.RunninConfig.ScreenshotsPath;
            TextSnapshotsDir.Text = Config.ConfigOptions.RunninConfig.SnapshotsPath;
            TextDiskImagesDir.Text = Config.ConfigOptions.RunninConfig.DiskImagesPath;
            TextTOSRomsDir.Text = Config.ConfigOptions.RunninConfig.TOSRomsPath;

            // MIDI tab: same deal as the directories above, plus the host port lists. The
            // emulation ComboBox itself is bound to the config and selected itself when the
            // DataContext was assigned.
            TextMt32RomDir.Text = Config.ConfigOptions.RunninConfig.MT32Rompath;
            // Ceiling before value: a Slider clamps whatever is assigned to its current range.
            SliderMt32Volume.Maximum = Mt32Backend.MaxVolume;
            SliderMt32Volume.Value = Config.ConfigOptions.RunninConfig.Mt32Volume;
            LoadMidiDevices();
            UpdateMidiVisibility();

            _lastValidModelIndex = (int)Config.ConfigOptions.RunninConfig.STModel;
            _uiReady = true;
        }

        // The emulator parks (and stops receiving host input) while the window is open
        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            ASEMain.EnterUiPause();

            // Set up the CRT reveal once the first layout has run, so the expanded panel's
            // natural height is known (posted at Loaded priority, i.e. after layout).
            Dispatcher.UIThread.Post(InitCrtReveal, DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Measures the CRT card at full size and applies the initial monitor state without animation.
        /// Subsequent changes combine a quick fade and small vertical movement with the height change.
        /// The opacity finishes before most of the collapse, so the clipping is barely perceptible.
        /// </summary>
        void InitCrtReveal()
        {
            double h = CrtEffectsClip.Bounds.Height;
            _crtExpandedHeight = h > 1 ? h + 2 : 470;   // fallback if layout has not settled yet

            CrtEffectsClip.RenderTransform = _crtRevealTranslate;

            bool expanded = RadioMonochromeMonitor.IsChecked != true;
            CrtEffectsClip.MaxHeight = expanded ? _crtExpandedHeight : 0;
            CrtEffectsClip.Opacity = expanded ? 1 : 0;
            CrtEffectsClip.IsHitTestVisible = expanded;
            _crtRevealTranslate.Y = expanded ? 0 : -8;

            // Height is deliberately slower than opacity: when collapsing, the controls disappear
            // before the panel has visibly "rolled up"; on expand they gently settle into place.
            CrtEffectsClip.Transitions = new Transitions
            {
                new DoubleTransition { Property = Layoutable.MaxHeightProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = new CubicEaseInOut() },
                new DoubleTransition { Property = Visual.OpacityProperty,       Duration = TimeSpan.FromMilliseconds(130), Easing = new CubicEaseInOut() },
            };

            _crtRevealTranslate.Transitions = new Transitions
            {
                new DoubleTransition { Property = TranslateTransform.YProperty, Duration = TimeSpan.FromMilliseconds(180), Easing = new CubicEaseInOut() },
            };

            _crtLayoutReady = true;
        }

        /// <summary>Shows or hides the colour-only CRT controls with a compact fade-and-lift reveal.</summary>
        void SetCrtExpanded(bool expanded)
        {
            CrtEffectsClip.IsHitTestVisible = expanded;
            CrtEffectsClip.MaxHeight = expanded ? _crtExpandedHeight : 0;
            CrtEffectsClip.Opacity = expanded ? 1 : 0;
            _crtRevealTranslate.Y = expanded ? 0 : -8;
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

            if (canceled || selpath.Count() != 1)
                return;

            string TOSImagePath = selpath.First();

            if (!File.Exists(TOSImagePath))
            {
                TinyDialogs.MessageBox("Error", $"Selected TOS image '{TOSImagePath}' does not exist.", MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                return;
            }

            long fileSize = new FileInfo(TOSImagePath).Length;

            // A 192KB image is a TOS 1.00-1.04 (STF/FM); a 256KB one is a TOS 1.06-2.06 (STE).
            // Anything else is not a TOS we can boot.
            if (fileSize != 192 * 1024 && fileSize != 256 * 1024)
            {
                TinyDialogs.MessageBox("Error", $"Unrecognized TOS image. It must be 192KB (STF/FM, TOS 1.00-1.04) or 256KB (STE, TOS 1.06-2.06).", MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                return;
            }

            // Commit the ROM first so the model auto-adjust below (which runs through
            // Model_SelectionChanged) evaluates against the newly selected image.
            Config.ConfigOptions.RunninConfig.TOSPath = TOSImagePath;
            TextTOSImage.Text = TOSImagePath;
            ForceReset = true;

            // Rather than rejecting a ROM/model mismatch, steer the model to match the ROM:
            //  - a 256KB (STE-only) image  -> switch to Atari STE
            //  - a 192KB image on an STE   -> drop to Atari STF/FM (no blitter)
            // A 192KB image on an STF/FM (with or without blitter) already matches; leave it.
            if (fileSize == 256 * 1024)
            {
                if (Config.ConfigOptions.RunninConfig.STModel != Config.ConfigOptions.STModels.STE)
                    ComboModel.SelectedIndex = (int)Config.ConfigOptions.STModels.STE;
            }
            else if (Config.ConfigOptions.RunninConfig.STModel == Config.ConfigOptions.STModels.STE)
            {
                ComboModel.SelectedIndex = (int)Config.ConfigOptions.STModels.ST;
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

            // The built-in MT-32 cannot do anything without its ROMs. Only the folder is checked:
            // libmt32emu identifies the images by SHA-1, so their names prove nothing.
            string mt32Roms = (TextMt32RomDir.Text ?? "").Trim();
            bool mt32Selected = Config.ConfigOptions.RunninConfig.MidiEmulation == Config.ConfigOptions.MIDIEmulationOptions.BuiltInMT32;

            if (mt32Selected && !Directory.Exists(mt32Roms))
            {
                TinyDialogs.MessageBox("Error", "The built-in Roland MT-32 emulation needs a folder holding its control and PCM ROM images.", MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                return;
            }

            // A different ROM set is only picked up when the module is created again, i.e. on reset.
            if (mt32Selected && mt32Roms != Config.ConfigOptions.RunninConfig.MT32Rompath)
                ForceReset = true;

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

            // Save MIDI (the emulation mode itself is bound to the config and already applied).
            // The ports are stored by name, so an entry left over from a device that is currently
            // unplugged survives untouched.
            Config.ConfigOptions.RunninConfig.MT32Rompath = mt32Roms;
            Config.ConfigOptions.RunninConfig.MidiInDevice = SelectedMidiPort(ComboMidiIn);
            Config.ConfigOptions.RunninConfig.MidiOutDevice = SelectedMidiPort(ComboMidiOut);

            Program.Config.DumpJsonConfig();

            if (ForceReset)
            {
                ASEMain.HardReset();
                // The monitor type may have changed (colour ↔ monochrome): re-fit the window to
                // the new picture aspect ratio.
                ASEMain.MainWindow?.RefreshAspectRatio();
            }

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
            Config.ConfigOptions.RunninConfig.Mask = configBackup.Mask;
            Config.ConfigOptions.RunninConfig.Noise = configBackup.Noise;
            Config.ConfigOptions.RunninConfig.DisableCrtEffects = configBackup.DisableCrtEffects;
            Config.ConfigOptions.RunninConfig.ShowBorders = configBackup.ShowBorders;
            Config.ConfigOptions.RunninConfig.MonochromeMonitor = configBackup.MonochromeMonitor;

            Config.ConfigOptions.RunninConfig.MouseSensitivity = configBackup.MouseSensitivity;

            Config.ConfigOptions.RunninConfig.GamepadButtonX = configBackup.GamepadButtonX;
            Config.ConfigOptions.RunninConfig.GamepadButtonY = configBackup.GamepadButtonY;
            Config.ConfigOptions.RunninConfig.GamepadButtonA = configBackup.GamepadButtonA;
            Config.ConfigOptions.RunninConfig.GamepadButtonB = configBackup.GamepadButtonB;
            Config.ConfigOptions.RunninConfig.GamepadButtonLS = configBackup.GamepadButtonLS;
            Config.ConfigOptions.RunninConfig.GamepadButtonRS = configBackup.GamepadButtonRS;
            Config.ConfigOptions.RunninConfig.GamepadButtonLB = configBackup.GamepadButtonLB;
            Config.ConfigOptions.RunninConfig.GamepadButtonRB = configBackup.GamepadButtonRB;

            Config.ConfigOptions.RunninConfig.MidiEmulation = configBackup.MidiEmulation;
            Config.ConfigOptions.RunninConfig.Mt32Volume = configBackup.Mt32Volume;

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

        // ==================== MIDI tab ====================

        /// <summary>
        /// Reveals the options that belong to the selected MIDI emulation: the host port mapping
        /// for "System managed MIDI", the ROM folder for the built-in MT-32, neither when MIDI is
        /// not emulated. Both grids live in the same row of the card, so the one that is hidden
        /// gives its space back.
        /// </summary>
        void UpdateMidiVisibility()
        {
            var mode = Config.ConfigOptions.RunninConfig.MidiEmulation;

            GridMidiSystem.IsVisible = mode == Config.ConfigOptions.MIDIEmulationOptions.System;
            GridMidiMt32.IsVisible = mode == Config.ConfigOptions.MIDIEmulationOptions.BuiltInMT32;
        }

        private void MidiEmulation_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateMidiVisibility();

            // Raised as well while the binding resolves in the constructor, before the port lists
            // exist; everything below is a reaction to the user, so it waits for the real thing.
            if (!_uiReady)
                return;

            // Switching to the host ports re-reads them, which doubles as the refresh path for a
            // device plugged in while this window was open.
            if (Config.ConfigOptions.RunninConfig.MidiEmulation == Config.ConfigOptions.MIDIEmulationOptions.System)
                LoadMidiDevices();

            // What the ST's MIDI port is wired to is decided when the machine is powered on, and
            // MT-32 titles look for the module while loading, so a change only takes effect on a
            // reset (OnOkClick asks for the confirmation).
            if (Config.ConfigOptions.RunninConfig.MidiEmulation != configBackup.MidiEmulation)
                ForceReset = true;
        }

        /// <summary>Fills both port dropdowns with what the host OS publishes right now.</summary>
        void LoadMidiDevices()
        {
            // The first pass starts from the configured ports; on any later one (the list is read
            // again whenever the user returns to this mode) the selection already in the window
            // wins, so refreshing never undoes a choice that has not been saved yet.
            string wantedIn = ComboMidiIn.ItemsSource is null ? Config.ConfigOptions.RunninConfig.MidiInDevice : SelectedMidiPort(ComboMidiIn);
            string wantedOut = ComboMidiOut.ItemsSource is null ? Config.ConfigOptions.RunninConfig.MidiOutDevice : SelectedMidiPort(ComboMidiOut);

            FillMidiPortCombo(ComboMidiIn, HostMidi.Inputs(), wantedIn);
            FillMidiPortCombo(ComboMidiOut, HostMidi.Outputs(), wantedOut);
        }

        /// <summary>
        /// Lists the given host ports, preselecting the configured one. Entries carry the stored
        /// value in their Tag: the configuration keeps port *names*, since the platform indices
        /// shift as devices come and go (see <see cref="HostMidi"/>).
        /// </summary>
        static void FillMidiPortCombo(ComboBox combo, IReadOnlyList<HostMidiPort> ports, string configured)
        {
            var items = new List<ComboBoxItem> { new() { Content = "None", Tag = "" } };

            foreach (HostMidiPort port in ports)
                items.Add(new ComboBoxItem { Content = port.Name, Tag = port.Name });

            // A configured port that is not there right now (device unplugged, config brought from
            // another machine) still gets its own entry: opening the window and pressing OK must
            // not silently downgrade the choice to "None".
            if (!string.IsNullOrEmpty(configured) && !ports.Any(p => p.Name == configured))
                items.Add(new ComboBoxItem { Content = $"{configured}  (not connected)", Tag = configured });

            combo.ItemsSource = items;
            combo.SelectedItem = items.FirstOrDefault(i => (string)i.Tag == configured) ?? items[0];
        }

        /// <summary>Port name selected in one of the dropdowns, "" for "None".</summary>
        static string SelectedMidiPort(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

        public void OnBrowseMt32RomsClick(object sender, RoutedEventArgs e) => FileUtils.BrowseDirectory(TextMt32RomDir, "Select Roland MT-32 ROMs directory");

        /// <summary>
        /// The emulated module's volume knob. Written straight to the running config, which
        /// the audio mixer reads per buffer (see Mt32Backend.MixInto): the change is audible
        /// while dragging, no reset and no OK needed — and Cancel puts the backup back.
        /// </summary>
        private void SliderMt32Volume_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e)
            => Config.ConfigOptions.RunninConfig.Mt32Volume = (int)SliderMt32Volume.Value;

        public void OnBrowseScreenshotsDirClick(object sender, RoutedEventArgs e) => FileUtils.BrowseDirectory(TextScreenshotsDir, "Select screenshots directory");
        public void OnBrowseSnapshotsDirClick(object sender, RoutedEventArgs e) => FileUtils.BrowseDirectory(TextSnapshotsDir, "Select snapshots directory");
        public void OnBrowseDiskImagesDirClick(object sender, RoutedEventArgs e) => FileUtils.BrowseDirectory(TextDiskImagesDir, "Select disk images directory");
        public void OnBrowseTOSRomsDirClick(object sender, RoutedEventArgs e) => FileUtils.BrowseDirectory(TextTOSRomsDir, "Select TOS ROMs directory");

        private void Model_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Ignore the selection change raised while the binding is first resolved.
            if (!_uiReady)
                return;

            var model = Config.ConfigOptions.RunninConfig.STModel;
            string tospath = Config.ConfigOptions.RunninConfig.TOSPath;

            // A 256KB TOS only runs on the STE. If one is loaded and the user picks an
            // STF/FM model, we can't silently swap the ROM (unlike the browse flow), so
            // reject it and snap the model back to STE.
            if (model != Config.ConfigOptions.STModels.STE && IsSteOnlyRom(tospath))
            {
                TinyDialogs.MessageBox("Error", $"The selected TOS ROM is only compatible with the Atari STE.", MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                ComboModel.SelectedIndex = _lastValidModelIndex;
                return;
            }

            // Conversely, a 192KB TOS only runs on an STF/FM. If one is loaded and the user
            // picks STE, reject it and snap back to the previous (STF/FM) model, keeping any
            // blitter choice.
            if (model == Config.ConfigOptions.STModels.STE && IsStfRom(tospath))
            {
                TinyDialogs.MessageBox("Error", $"The selected TOS ROM is only compatible with the Atari STF/FM.", MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                ComboModel.SelectedIndex = _lastValidModelIndex;
                return;
            }

            _lastValidModelIndex = (int)model;
            ForceReset = model != configBackup.STModel;
        }

        // A 256KB TOS image (TOS 1.06-2.06) only boots on the STE.
        static bool IsSteOnlyRom(string tospath)
            => File.Exists(tospath) && new FileInfo(tospath).Length == 256 * 1024;

        // A 192KB TOS image (TOS 1.00-1.04) only boots on an STF/FM (with or without blitter).
        static bool IsStfRom(string tospath)
            => File.Exists(tospath) && new FileInfo(tospath).Length == 192 * 1024;
        private void Memory_SelectionChanged(object sender, SelectionChangedEventArgs e) => ForceReset = (Config.ConfigOptions.RunninConfig.RAMConfiguration != configBackup.RAMConfiguration) ? true : false;

        private void SliderCurvature_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Config.ConfigOptions.RunninConfig.Curvature = (float)SliderCurvature.Value;
        private void SliderVignette_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Config.ConfigOptions.RunninConfig.Vignette = (float)SliderVignette.Value;
        private void SliderScanline_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Config.ConfigOptions.RunninConfig.Scanline = (float)SliderScanline.Value;
        private void SliderAberration_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Config.ConfigOptions.RunninConfig.ChromAb = (float)SliderAberration.Value;
        private void SliderBloom_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Config.ConfigOptions.RunninConfig.Bloom = (float)SliderBloom.Value;
        private void SliderMask_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Config.ConfigOptions.RunninConfig.Mask = (float)SliderMask.Value;
        private void SliderNoise_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Config.ConfigOptions.RunninConfig.Noise = (float)SliderNoise.Value;

        /// <summary>
        /// "Effects on/off" switch. On (checked) keeps the full CRT shader; off makes GLControl
        /// draw with a plain blit instead of zeroing the sliders — on a weak GPU the effects cost
        /// the same at 0 as at any other value. The slider values survive, so turning it back on
        /// restores the previous look; they are just greyed out meanwhile. The switch is the
        /// inverse of the stored <c>DisableCrtEffects</c> flag.
        /// </summary>
        private void ToggleEffects_OnIsCheckedChanged(object sender, RoutedEventArgs e)
        {
            if (((ToggleSwitch)sender).IsChecked is bool isChecked)
                Config.ConfigOptions.RunninConfig.DisableCrtEffects = !isChecked;

            UpdateEffectsSlidersEnabled();
        }

        void UpdateEffectsSlidersEnabled()
        {
            bool effectsOn = !Config.ConfigOptions.RunninConfig.DisableCrtEffects;

            GridEffectSliders.IsEnabled = effectsOn;
            PanelNoiseSlider.IsEnabled = effectsOn;
            ButtonDefaultGLValues.IsEnabled = effectsOn;
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

        // Switching between a colour and a monochrome monitor changes the whole video geometry
        // (resolution, refresh rate, buffer size), so it can only take effect on a reset. The
        // value is applied live like the other settings; ForceReset makes OnOkClick ask for the
        // reset confirmation and reboot the machine. The CRT/borders card, which is meaningless in
        // high resolution, collapses/expands to match.
        //
        // Only the monochrome segment is handled: picking the colour one unchecks this radio and
        // raises the event with false, so both directions arrive here.
        private void RadioMonochromeMonitor_OnIsCheckedChanged(object sender, RoutedEventArgs e)
        {
            if (((RadioButton)sender).IsChecked is not bool isChecked)
                return;

            Config.ConfigOptions.RunninConfig.MonochromeMonitor = isChecked;

            if (_uiReady && isChecked != configBackup.MonochromeMonitor)
                ForceReset = true;

            if (_crtLayoutReady)
                SetCrtExpanded(!isChecked);
        }
    }
}