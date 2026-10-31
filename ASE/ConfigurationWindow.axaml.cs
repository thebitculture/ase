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

        // Playing together online: only the Input tab (see the constructor)
        readonly bool _inputOnly;

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

        // Avalonia's runtime XAML loader (and the designer) look for a parameterless constructor.
        public ConfigurationWindow() : this(inputOnly: false) { }

        /// <param name="inputOnly">
        /// While playing together online (Netplay.Lockstep): only the Input tab can be used. Its
        /// settings are this computer's alone — what goes into each port reaches the shared
        /// machine as ordinary input, through the session like any key, and each player keeps
        /// their own mouse sensitivity — while everything in the other tabs belongs to the
        /// shared machine, or would change it.
        /// </param>
        public ConfigurationWindow(bool inputOnly)
        {
            InitializeComponent();
            _inputOnly = inputOnly;

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
                ColorizeDithering = Config.ConfigOptions.RunninConfig.ColorizeDithering,
                EdgeSmoothing = Config.ConfigOptions.RunninConfig.EdgeSmoothing,
                VideoSignal = Config.ConfigOptions.RunninConfig.VideoSignal,
                Scaling = Config.ConfigOptions.RunninConfig.Scaling,
                Persistence = Config.ConfigOptions.RunninConfig.Persistence,
                MaskType = Config.ConfigOptions.RunninConfig.MaskType,
                AntiAliasing = Config.ConfigOptions.RunninConfig.AntiAliasing,
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

            // Joystick ports: edited locally and committed on OK, so Cancel has nothing to
            // undo. A mouse on port 1 (only reachable by editing config.json) reads as empty.
            var port1 = Config.ConfigOptions.RunninConfig.Port1Device;
            ComboPort0.SelectedIndex = (int)Config.ConfigOptions.RunninConfig.Port0Device;
            ComboPort1.SelectedIndex = (int)(port1 == Config.ConfigOptions.PortDevices.Mouse
                ? Config.ConfigOptions.PortDevices.Disconnected : port1);

            RebindGLSliders();
            RebindJoymap();

            chkShowBorders.IsChecked = Config.ConfigOptions.RunninConfig.ShowBorders;
            chkColorizeDithering.IsChecked = Config.ConfigOptions.RunninConfig.ColorizeDithering;
            ComboEdgeSmoothing.SelectedIndex = (int)Config.ConfigOptions.RunninConfig.EdgeSmoothing;
            ComboAntiAliasing.SelectedIndex = (int)Config.ConfigOptions.RunninConfig.AntiAliasing;
            ComboVideoSignal.SelectedIndex = (int)Config.ConfigOptions.RunninConfig.VideoSignal;
            ComboScaling.SelectedIndex = (int)Config.ConfigOptions.RunninConfig.Scaling;
            SliderPersistence.Value = Config.ConfigOptions.RunninConfig.Persistence;
            ComboMaskType.SelectedIndex = (int)Config.ConfigOptions.RunninConfig.MaskType;
            UpdateSignalDependents();
            RefreshPresetSelection();
            // Segmented monitor selector: only the monochrome half carries the handler,
            // and setting both here runs before _uiReady, so it cannot request a reset.
            RadioMonochromeMonitor.IsChecked = Config.ConfigOptions.RunninConfig.MonochromeMonitor;
            RadioColourMonitor.IsChecked = !Config.ConfigOptions.RunninConfig.MonochromeMonitor;

            // The switch reads as "Effects on/off": checked = effects enabled = NOT disabled.
            // Assigning IsChecked raises IsCheckedChanged, which greys out the sliders when the
            // effects come up already off.
            ToggleEffects.IsChecked = !Config.ConfigOptions.RunninConfig.DisableCrtEffects;
            UpdateEffectsSlidersEnabled();

            // Hard disk tab: edited locally, committed on OK (with a reset — the disks
            // attach at power-on), so Cancel has nothing to restore.
            EnableGemDos.IsChecked = Config.ConfigOptions.RunninConfig.GemdosDriveEnabled;
            TextGEMDOSDir.Text = Config.ConfigOptions.RunninConfig.GemdosDrivePath;
            EnableACSI.IsChecked = Config.ConfigOptions.RunninConfig.AcsiImageEnabled;
            TextACSIDir.Text = Config.ConfigOptions.RunninConfig.AcsiImagePath;
            ChkBootFromHardDisk.IsChecked = Config.ConfigOptions.RunninConfig.BootFromHardDisk;

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

            // The clock chip of an ST or STE: edited locally and committed on OK (with a reset),
            // like the hard disks
            _realTimeClock = Config.ConfigOptions.RunninConfig.RealTimeClock;
            UpdateRealTimeClockCheckbox();

            _lastValidModelIndex = (int)Config.ConfigOptions.RunninConfig.STModel;
            _uiReady = true;

            if (inputOnly)
            {
                foreach (var tab in Tabs.Items.OfType<TabItem>())
                    tab.IsEnabled = tab == TabInput;

                Tabs.SelectedItem = TabInput;
                TextHeaderDescription.Text = "Playing together online: only your input can change here. Everything else belongs to the shared machine.";
            }
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
            // Only the Input tab was open: nothing else to check or apply, and no reset. The TOS
            // checks below would not even be fair here — on a guest the model is the host's,
            // borrowed, while the TOS path is still this computer's own.
            if (_inputOnly)
            {
                CommitInput();
                Program.Config.DumpJsonConfig();
                Close();
                return;
            }

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

            // Hard disks: validate what is switched on before committing anything. An enabled
            // option with a bad path would only report the failure to the console at power-on,
            // and the user would be left wondering where the drive went.
            string gemdosDir = (TextGEMDOSDir.Text ?? "").Trim();
            string acsiImage = (TextACSIDir.Text ?? "").Trim();
            bool gemdosOn = EnableGemDos.IsChecked == true;
            bool acsiOn = EnableACSI.IsChecked == true;

            if (gemdosOn && !Directory.Exists(gemdosDir))
            {
                TinyDialogs.MessageBox("Error", "The GEMDOS drive needs an existing folder of this computer to mount.", MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                return;
            }

            if (acsiOn && !CheckAcsiImage(acsiImage))
                return;

            // The disks are attached at power-on (the ACSI partition count even decides the
            // GEMDOS drive letter), so any change here needs a reset.
            bool bootFromHd = ChkBootFromHardDisk.IsChecked == true;
            bool hardDisksChanged = false;

            if (gemdosOn != Config.ConfigOptions.RunninConfig.GemdosDriveEnabled ||
                acsiOn != Config.ConfigOptions.RunninConfig.AcsiImageEnabled ||
                bootFromHd != Config.ConfigOptions.RunninConfig.BootFromHardDisk ||
                (gemdosOn && gemdosDir != Config.ConfigOptions.RunninConfig.GemdosDrivePath) ||
                (acsiOn && acsiImage != Config.ConfigOptions.RunninConfig.AcsiImagePath))
            {
                ForceReset = true;
                hardDisksChanged = true;
            }

            // The clock chip is fitted at power-on. On a Mega the choice is only stored.
            if (_realTimeClock != Config.ConfigOptions.RunninConfig.RealTimeClock &&
                Config.ConfigOptions.RunninConfig.STModel != Config.ConfigOptions.STModels.Mega)
                ForceReset = true;

            if (ForceReset)
            {
                MessageBoxButton result = TinyDialogs.MessageBox("Reset", $"ST must be reset to apply changes.", MessageBoxDialogType.YesNo, MessageBoxIconType.Warning, MessageBoxButton.Yes);
                if (result == MessageBoxButton.No)
                    return;
            }

            CommitInput();

            // Save the hard disks (validated above)
            Config.ConfigOptions.RunninConfig.GemdosDriveEnabled = gemdosOn;
            Config.ConfigOptions.RunninConfig.GemdosDrivePath = gemdosDir;
            Config.ConfigOptions.RunninConfig.AcsiImageEnabled = acsiOn;
            Config.ConfigOptions.RunninConfig.AcsiImagePath = acsiImage;
            Config.ConfigOptions.RunninConfig.BootFromHardDisk = bootFromHd;
            Config.ConfigOptions.RunninConfig.RealTimeClock = _realTimeClock;

            // Changing the disks is asking for them: a hard-disk game from the library would
            // otherwise keep the bus on the reset that is about to apply them
            if (hardDisksChanged)
                HardDiskGames.End();

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

            // What the dialog showed is the user's choice now, a machine borrowed from a shared
            // game included (see Netplay.Lockstep)
            Netplay.Lockstep.ForgetUserMachine();

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

        /// <summary>The Input tab's settings that are edited locally and committed on OK: the
        /// gamepad buttons and the joystick ports. The key bindings and the mouse speed apply as
        /// they are edited.</summary>
        void CommitInput()
        {
            Config.ConfigOptions.RunninConfig.GamepadButtonX = (Config.ConfigOptions.GamepadButtonsMapping)ComboX.SelectedIndex;
            Config.ConfigOptions.RunninConfig.GamepadButtonY = (Config.ConfigOptions.GamepadButtonsMapping)ComboY.SelectedIndex;
            Config.ConfigOptions.RunninConfig.GamepadButtonA = (Config.ConfigOptions.GamepadButtonsMapping)ComboA.SelectedIndex;
            Config.ConfigOptions.RunninConfig.GamepadButtonB = (Config.ConfigOptions.GamepadButtonsMapping)ComboB.SelectedIndex;
            Config.ConfigOptions.RunninConfig.GamepadButtonLS = (Config.ConfigOptions.GamepadButtonsMapping)ComboLS.SelectedIndex;
            Config.ConfigOptions.RunninConfig.GamepadButtonRS = (Config.ConfigOptions.GamepadButtonsMapping)ComboRS.SelectedIndex;
            Config.ConfigOptions.RunninConfig.GamepadButtonLB = (Config.ConfigOptions.GamepadButtonsMapping)ComboLB.SelectedIndex;
            Config.ConfigOptions.RunninConfig.GamepadButtonRB = (Config.ConfigOptions.GamepadButtonsMapping)ComboRB.SelectedIndex;

            // Save the joystick ports. No reset: the emulation thread plugs the devices in on
            // its next event drain (HostInput.ApplyPortConfiguration).
            Config.ConfigOptions.RunninConfig.Port0Device = (Config.ConfigOptions.PortDevices)ComboPort0.SelectedIndex;
            Config.ConfigOptions.RunninConfig.Port1Device = (Config.ConfigOptions.PortDevices)ComboPort1.SelectedIndex;
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
            Config.ConfigOptions.RunninConfig.ColorizeDithering = configBackup.ColorizeDithering;
            Config.ConfigOptions.RunninConfig.EdgeSmoothing = configBackup.EdgeSmoothing;
            Config.ConfigOptions.RunninConfig.VideoSignal = configBackup.VideoSignal;
            Config.ConfigOptions.RunninConfig.Scaling = configBackup.Scaling;
            Config.ConfigOptions.RunninConfig.Persistence = configBackup.Persistence;
            Config.ConfigOptions.RunninConfig.MaskType = configBackup.MaskType;
            Config.ConfigOptions.RunninConfig.AntiAliasing = configBackup.AntiAliasing;
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
            tospath = Config.ResolveFilePath(tospath);

            if (File.Exists(tospath))
            {
                long fileSize = new FileInfo(tospath).Length;
                if ((Config.ConfigOptions.RunninConfig.STModel == Config.ConfigOptions.STModels.ST || Config.ConfigOptions.RunninConfig.STModel == Config.ConfigOptions.STModels.Mega) && fileSize != 192 * 1024)
                {
                    TinyDialogs.MessageBox("Error", $"TOS image for STF/FM must be 1.00 to 1.04 (196.608 bytes)", MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                    return false;
                }
                else if (Config.ConfigOptions.RunninConfig.STModel == Config.ConfigOptions.STModels.STE && (fileSize != 256 * 1024))
                {
                    TinyDialogs.MessageBox("Error", $"TOS image for STE must be 1.06 to 2.06 (262.144 bytes)", MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
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

        // ==================== Hard disk tab ====================

        public void OnBrowseGemdosDirClick(object sender, RoutedEventArgs e)
            => FileUtils.BrowseDirectory(TextGEMDOSDir, "Select the folder to mount as a GEMDOS hard drive");

        public async void OnBrowseAcsiImageClick(object sender, RoutedEventArgs e)
        {
            var (canceled, selpath) = await Dialogs.OpenFile("Select ACSI hard disk image",
                Config.DialogStartFolder(Path.GetDirectoryName(TextACSIDir.Text ?? "") ?? ""),
                new FileFilter("Hard disk image", ["*.img", "*.hd", "*.acsi", "*.raw", "*.ahd"]));

            if (canceled || selpath.Count() != 1)
                return;

            string path = selpath.First();

            if (!CheckAcsiImage(path))
                return;

            TextACSIDir.Text = path;
            EnableACSI.IsChecked = true;
        }

        /// <summary>
        /// Opens the image creator and, when it produces one, points the ACSI field at it and
        /// switches the disk on. The image is attached at the next power-on, which OK arranges:
        /// the path now differs from the running config, so the reset prompt appears.
        /// </summary>
        public async void OnCreateAcsiImageClick(object sender, RoutedEventArgs e)
        {
            var window = new CreateHardDiskWindow(TextACSIDir.Text ?? "");
            string created = await window.ShowDialog<string>(this);

            if (string.IsNullOrEmpty(created))
                return;

            TextACSIDir.Text = created;
            EnableACSI.IsChecked = true;
        }

        /// <summary>
        /// Writes the ASE HD driver to an image that already exists. Everything inside the
        /// partition is left alone — only the boot sector and the reserved sectors below the
        /// partition are rewritten — but that is still enough to displace another vendor's
        /// driver, so the confirmation spells out what is being replaced before anything is
        /// written. <see cref="AtariHdDriver.Examine"/> refuses outright when the driver would
        /// not fit before the partition, which is how a disk laid out by another tool is kept
        /// from having its file system overwritten.
        /// </summary>
        public async void OnReinstallDriverClick(object sender, RoutedEventArgs e)
        {
            string path = (TextACSIDir.Text ?? "").Trim();

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                await Dialogs.MessageBox("Reinstall driver",
                    "Choose the ACSI image first: the driver is written to the image in the box above.",
                    MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                return;
            }

            if (Acsi.EmulationOn && SamePathAsRunning(path))
            {
                await Dialogs.MessageBox("Reinstall driver",
                    "That image is attached to the running machine. Reset with the ACSI disk " +
                    "switched off, or pick another file.",
                    MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                return;
            }

            if (AtariHdDriver.Unavailable is string driverProblem)
            {
                await Dialogs.MessageBox("Reinstall driver",
                    $"The ASE HD driver cannot be installed: {driverProblem}.",
                    MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                return;
            }

            AtariHdDriver.DiskInfo info = AtariHdDriver.Examine(path);

            if (!info.Valid)
            {
                await Dialogs.MessageBox("Reinstall driver",
                    $"The driver cannot be installed on '{Path.GetFileName(path)}':\n\n{info.Problem}",
                    MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                return;
            }

            long mb = info.PartitionSectors / 2048;

            string message =
                $"Reinstall the ASE HD driver on '{Path.GetFileName(path)}'?\n\n" +
                $"The boot sector and the driver area of this image will be replaced by the " +
                $"ASE HD driver.\n\n" +
                $"If the disk currently has another driver installed (ICD Pro, PPutnik, " +
                $"HDDriver or any other), IT WILL BE OVERWRITTEN and the disk will boot with " +
                $"the ASE HD driver from now on.\n\n" +
                $"Partition: starts at sector {info.PartitionStart}, {info.PartitionSectors:N0} " +
                $"sectors ({mb} MB), type {info.PartitionId}.\n";

            if (info.PartitionCount > 1)
                message += $"\nThis disk has {info.PartitionCount} primary partitions. The ASE HD " +
                           $"driver mounts every GEM/BGM partition it finds, from C: up.\n";

            if (info.AlreadyOurs)
                message += "\nThis disk already carries the ASE HD driver; it will be updated to " +
                           "the version in this build.\n";

            message += "\nThe files stored in the partition are not touched.";

            var answer = await Dialogs.MessageBox("Reinstall driver", message,
                MessageBoxDialogType.YesNo, MessageBoxIconType.Warning, MessageBoxButton.No);

            if (answer != MessageBoxButton.Yes)
                return;

            if (!AtariHdDriver.Reinstall(path, out string error))
            {
                await Dialogs.MessageBox("Reinstall driver",
                    $"The driver could not be installed:\n\n{error}",
                    MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                return;
            }

            await Dialogs.MessageBox("Reinstall driver",
                $"The ASE HD driver is installed on '{Path.GetFileName(path)}'.\n\n" +
                "Switch 'Boot from the hard disk' on and reset for the disk to boot itself.",
                MessageBoxDialogType.Ok, MessageBoxIconType.Question, MessageBoxButton.Ok);
        }

        static bool SamePathAsRunning(string path)
        {
            try
            {
                // What the ACSI emulation has open, which is not the configured image while a
                // hard-disk game from the library runs
                string running = Acsi.AttachedImagePath;
                return !string.IsNullOrWhiteSpace(running) &&
                       string.Equals(Path.GetFullPath(path), Path.GetFullPath(running),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// An ACSI image is a raw dump of the disk's sectors, so the only thing that can be
        /// checked without mounting it is that its size is a whole number of 512-byte sectors.
        /// Reports the problem and returns false when it is not usable.
        /// </summary>
        static bool CheckAcsiImage(string path)
        {
            if (!File.Exists(path))
            {
                TinyDialogs.MessageBox("Error", $"The ACSI image '{path}' does not exist.", MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                return false;
            }

            long size = new FileInfo(path).Length;

            if (size == 0 || (size % Acsi.SectorSize) != 0)
            {
                TinyDialogs.MessageBox("Error", $"'{Path.GetFileName(path)}' is not a valid hard disk image: its size must be a non-zero multiple of {Acsi.SectorSize} bytes.", MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                return false;
            }

            return true;
        }

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
            UpdateRealTimeClockCheckbox();
        }

        // The user's choice for an ST or STE; the Mega has the clock whatever it says
        bool _realTimeClock;

        void UpdateRealTimeClockCheckbox()
        {
            bool mega = Config.ConfigOptions.RunninConfig.STModel == Config.ConfigOptions.STModels.Mega;

            // Disabled first, so the handler does not take the Mega's forced tick for a choice
            ChkRealTimeClock.IsEnabled = !mega;
            ChkRealTimeClock.IsChecked = mega || _realTimeClock;
        }

        private void ChkRealTimeClock_OnIsCheckedChanged(object sender, RoutedEventArgs e)
        {
            if (ChkRealTimeClock.IsEnabled)
                _realTimeClock = ChkRealTimeClock.IsChecked == true;
        }

        // A 256KB TOS image (TOS 1.06-2.06) only boots on the STE.
        static bool IsSteOnlyRom(string tospath)
            => RomSize(tospath) == 256 * 1024;

        // A 192KB TOS image (TOS 1.00-1.04) only boots on an STF/FM (with or without blitter).
        static bool IsStfRom(string tospath)
            => RomSize(tospath) == 192 * 1024;

        static long RomSize(string tospath)
        {
            tospath = Config.ResolveFilePath(tospath);
            return File.Exists(tospath) ? new FileInfo(tospath).Length : -1;
        }
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
            NotePictureChanged();
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
            Config.ConfigOptions.RunninConfig.MaskType = Config.ConfigOptions.MaskTypes.Shadow;

            RebindGLSliders();
            ComboMaskType.SelectedIndex = (int)Config.ConfigOptions.MaskTypes.Shadow;
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

        // Dithering colorization is deliberately *not* tied to the effects switch next to it:
        // it corrects the picture rather than dressing it up, so it stays available with the CRT
        // shader bypassed (and in high resolution, where the plain blit is always used).
        private void ChkColorizeDithering_OnIsCheckedChanged(object sender, RoutedEventArgs e)
        {
            if (((CheckBox)sender).IsChecked is bool isChecked)
                Config.ConfigOptions.RunninConfig.ColorizeDithering = isChecked;
            NotePictureChanged();
        }

        // Edge smoothing (SuperEagle / xBR), independent of the effects switch for the same reason
        // as its neighbour: it corrects the picture rather than dressing it up, so it stays
        // available with the CRT shader bypassed and in high resolution. Items in enum order.
        private void EdgeSmoothing_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int index = ((ComboBox)sender).SelectedIndex;
            if (index >= 0 && Enum.IsDefined(typeof(Config.ConfigOptions.EdgeSmoothings), index))
                Config.ConfigOptions.RunninConfig.EdgeSmoothing = (Config.ConfigOptions.EdgeSmoothings)index;
            NotePictureChanged();
        }

        // Anti-aliasing (FXAA / SMAA), the third of the corrections and independent of the other
        // two and of the effects switch like them. Applied live, no reset: the GL control picks
        // the change up on its next frame.
        private void AntiAliasing_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int index = ((ComboBox)sender).SelectedIndex;
            if (index >= 0 && Enum.IsDefined(typeof(Config.ConfigOptions.AntiAliasingModes), index))
                Config.ConfigOptions.RunninConfig.AntiAliasing = (Config.ConfigOptions.AntiAliasingModes)index;
            NotePictureChanged();
        }

        // The video signal: with a television signal the two pattern-based corrections are
        // bypassed by the GL chain, so their controls grey out and a line says why.
        private void VideoSignal_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int index = ((ComboBox)sender).SelectedIndex;
            if (index >= 0 && Enum.IsDefined(typeof(Config.ConfigOptions.VideoSignals), index))
                Config.ConfigOptions.RunninConfig.VideoSignal = (Config.ConfigOptions.VideoSignals)index;
            UpdateSignalDependents();
            NotePictureChanged();
        }

        void UpdateSignalDependents()
        {
            bool rgb = Config.ConfigOptions.RunninConfig.VideoSignal == Config.ConfigOptions.VideoSignals.RGB;
            chkColorizeDithering.IsEnabled = rgb;
            ComboEdgeSmoothing.IsEnabled = rgb;
            TextSignalHint.IsVisible = !rgb;
        }

        private void Scaling_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int index = ((ComboBox)sender).SelectedIndex;
            if (index >= 0 && Enum.IsDefined(typeof(Config.ConfigOptions.ScalingModes), index))
                Config.ConfigOptions.RunninConfig.Scaling = (Config.ConfigOptions.ScalingModes)index;
            NotePictureChanged();
        }

        private void SliderPersistence_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            Config.ConfigOptions.RunninConfig.Persistence = (int)Math.Round(SliderPersistence.Value);
            NotePictureChanged();
        }

        private void MaskType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int index = ((ComboBox)sender).SelectedIndex;
            if (index >= 0 && Enum.IsDefined(typeof(Config.ConfigOptions.MaskTypes), index))
                Config.ConfigOptions.RunninConfig.MaskType = (Config.ConfigOptions.MaskTypes)index;
        }

        // ---- Presets: the ready-made looks of the Picture tab. Each is a full assignment of the
        // picture options plus whether the CRT effects are on (not how they are tuned: the
        // sliders are left alone), and the combo always shows the look that matches the current
        // settings exactly, or Custom. Two guards keep the two directions apart: _applyingPreset
        // while a preset writes the controls (their handlers must not re-derive the preset), and
        // _syncingPreset while the combo is set from the settings (its handler must not apply).
        private readonly record struct PictureLook(
            string Name, string Description,
            Config.ConfigOptions.VideoSignals Signal, bool Colorize,
            Config.ConfigOptions.EdgeSmoothings Smoothing, Config.ConfigOptions.AntiAliasingModes AntiAliasing,
            Config.ConfigOptions.ScalingModes Scaling, int Persistence, bool EffectsOn);

        static readonly PictureLook[] PictureLooks =
        {
            new("Atari monitor",
                "The SC1224 on the desk: the picture as the ST drew it, on a colour CRT; scanlines, mask, a touch of curvature. RGB signal, effects on, nothing else.",
                Config.ConfigOptions.VideoSignals.RGB, false, Config.ConfigOptions.EdgeSmoothings.None,
                Config.ConfigOptions.AntiAliasingModes.None, Config.ConfigOptions.ScalingModes.Smooth, 0, true),
            new("Home TV",
                "The ST on the living-room television through a composite lead: colour bleeding, softer detail, the rainbow shimmer on dithered areas, a little phosphor persistence, and the CRT effects.",
                Config.ConfigOptions.VideoSignals.Composite, false, Config.ConfigOptions.EdgeSmoothings.None,
                Config.ConfigOptions.AntiAliasingModes.None, Config.ConfigOptions.ScalingModes.Smooth, 25, true),
            new("Pixel perfect",
                "Every pixel exactly as drawn, sharp, at a whole number of screen pixels per line, with no effect at all. The choice for pixel art or for a weak GPU.",
                Config.ConfigOptions.VideoSignals.RGB, false, Config.ConfigOptions.EdgeSmoothings.None,
                Config.ConfigOptions.AntiAliasingModes.None, Config.ConfigOptions.ScalingModes.Integer, 0, false),
            new("Enhanced",
                "The picture cleaned up beyond what the hardware could show: dithering blended into real colours and gradients, curves and diagonals redrawn with xBR, edges anti-aliased with SMAA, crisp scaling, no CRT effects. The heaviest on the GPU.",
                Config.ConfigOptions.VideoSignals.RGB, true, Config.ConfigOptions.EdgeSmoothings.XBR,
                Config.ConfigOptions.AntiAliasingModes.SMAA, Config.ConfigOptions.ScalingModes.Sharp, 0, false),
        };

        const string CustomLookDescription = "Your own combination of the options below.";

        bool _applyingPreset;
        bool _syncingPreset;

        static bool Matches(in PictureLook look, Config.ConfigOptions cfg) =>
            cfg.VideoSignal == look.Signal && cfg.ColorizeDithering == look.Colorize &&
            cfg.EdgeSmoothing == look.Smoothing && cfg.AntiAliasing == look.AntiAliasing &&
            cfg.Scaling == look.Scaling && cfg.Persistence == look.Persistence &&
            cfg.DisableCrtEffects == !look.EffectsOn;

        /// <summary>Points the preset combo at the look the current settings match, or Custom.</summary>
        void RefreshPresetSelection()
        {
            var cfg = Config.ConfigOptions.RunninConfig;
            int index = PictureLooks.Length;   // Custom
            for (int i = 0; i < PictureLooks.Length; i++)
                if (Matches(PictureLooks[i], cfg)) { index = i; break; }

            _syncingPreset = true;
            ComboPicturePreset.SelectedIndex = index;
            _syncingPreset = false;

            TextPresetDescription.Text = index < PictureLooks.Length ? PictureLooks[index].Description : CustomLookDescription;
        }

        /// <summary>Called by every control a preset sets: re-derives the preset shown.</summary>
        void NotePictureChanged()
        {
            if (_uiReady && !_applyingPreset)
                RefreshPresetSelection();
        }

        private void PicturePreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingPreset || !_uiReady) return;

            int index = ComboPicturePreset.SelectedIndex;
            if (index < 0 || index >= PictureLooks.Length)
            {
                // Custom: nothing to apply, the settings are whatever they are.
                TextPresetDescription.Text = CustomLookDescription;
                return;
            }

            var look = PictureLooks[index];
            _applyingPreset = true;

            // Through the controls, so every handler runs and the config follows.
            ComboVideoSignal.SelectedIndex = (int)look.Signal;
            chkColorizeDithering.IsChecked = look.Colorize;
            ComboEdgeSmoothing.SelectedIndex = (int)look.Smoothing;
            ComboAntiAliasing.SelectedIndex = (int)look.AntiAliasing;
            ComboScaling.SelectedIndex = (int)look.Scaling;
            SliderPersistence.Value = look.Persistence;
            ToggleEffects.IsChecked = look.EffectsOn;

            _applyingPreset = false;
            TextPresetDescription.Text = look.Description;
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