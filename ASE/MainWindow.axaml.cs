using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SDL2;
using System;
using TinyDialogsNet;
using Tmds.DBus.Protocol;

namespace ASE
{
    public partial class MainWindow : Window
    {
        private float GameAspectRatio;
        private bool _isResizing = false;

        public IntPtr _sdlWindowPtr;

        Bitmap BitmapLedDriveOn;
        Bitmap BitmapLedDriveOff;
        DateTime TimeLastDriveOn = DateTime.Now;
        DateTime TimeLastTimeTextBlock = DateTime.Now;

        string ZipFile = "";
        
        public MainWindow()
        {
            InitializeComponent();

            BitmapLedDriveOn = new Bitmap(AssetLoader.Open(new Uri("avares://ASE/Assets/drive_led_on.png")));
            BitmapLedDriveOff = new Bitmap(AssetLoader.Open(new Uri("avares://ASE/Assets/drive_led_off.png")));

            AddHandler(DragDrop.DropEvent, OnDrop);
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            // Maintains the aspect ratio of the emulator when resizing the window
            GameAspectRatio = (float)ASEMain.ScreenWidth / ((float)ASEMain.ScreenHeight + ((float)MainMenu.Height + (float)BottomStatusBar.Bounds.Height));
            this.GetObservable(Window.ClientSizeProperty).Subscribe(OnClientSizeChanged);

            // Attach SDL to the window
            var platformHandle = this.TryGetPlatformHandle();

            if (platformHandle != null)
            {
                _sdlWindowPtr = SDL.SDL_CreateWindowFrom(platformHandle.Handle);

                if (_sdlWindowPtr == IntPtr.Zero)
                {
                    var error = SDL.SDL_GetError();
                    ColoredConsole.WriteLine($"Error SDL_CreateWindowFrom: [[red]]{error}[[/red]]");

                    TinyDialogs.MessageBox("Error", "Error when calling SDL_CreateWindowFrom, I can't continue like this.", MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                    Close();
                }

                // If initialization is successful, start the emulator main loop
                ASEMain.Init(this);

                // The OpenGL control have a transparent overlay to capture input events
                GlInputOverlay.PointerPressed += GL_OnPointerPressed;
                GlInputOverlay.PointerReleased += GL_OnPointerReleased;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            ASEMain.Shutdown();
        }

        private void GL_OnPointerPressed(object sender, PointerPressedEventArgs e)
        {
            // SDL can't capture mouse button presses directly from Avalonia, so
            // I have to do it manually here and forward it to the ACIA mouse handling.

            var ctrl = (Control)sender!;
            var p = e.GetCurrentPoint(ctrl);

            // Middle button toggles input capture like F12 (works captured or not). The same
            // press may also arrive through the SDL queue; ASEMain edge-guards the toggle.
            if (p.Properties.IsMiddleButtonPressed)
            {
                ASEMain.MiddleButtonDown();
                e.Handled = true;
                return;
            }

            // Transmit the button press to the ACIA mouse handling
            if (p.Properties.IsLeftButtonPressed && ASEMain.IsMouseCaptured)
            {
                ACIA._mouseButtons |= 0x02;
                ACIA.SendMousePacket(0, 0);
                e.Handled = true;
            }
        }

        private void GL_OnPointerReleased(object sender, PointerReleasedEventArgs e)
        {
            // SDL can't capture mouse button releases directly from Avalonia, so
            // I have to do it manually here and forward it to the ACIA mouse handling.
            // I’m sure there are better ways to do this, but for now it does what
            // I need it to do and that’s enough by now.

            if (e.InitialPressMouseButton == MouseButton.Middle)
            {
                ASEMain.MiddleButtonUp();
                e.Handled = true;
                return;
            }

            if (e.InitialPressMouseButton == MouseButton.Left && ASEMain.IsMouseCaptured)
            {
                ACIA._mouseButtons &= ~0x02;
                ACIA.SendMousePacket(0, 0);
                e.Handled = true;
            }
        }

        public void ShowMenu(bool show)
        {
            MainMenu.IsEnabled = show;
        }

        private void OnClientSizeChanged(Size newSize)
        {
            if (_isResizing)
                return;

            double menuHeight = MainMenu.Bounds.Height;
            double statusbarHeight = BottomStatusBar.Bounds.Height;
            double barsHeight = menuHeight + statusbarHeight;

            // New height for the viewport
            double glHeight = newSize.Height - menuHeight - statusbarHeight;
            if (glHeight <= 1) return;

            double currentRatio = newSize.Width / glHeight;

            // MAintains aspect ratio of the window
            if (Math.Abs(currentRatio - GameAspectRatio) > 0.05)
            {
                _isResizing = true;

                double targetGlHeight = newSize.Width / GameAspectRatio;
                double targetClientHeight = targetGlHeight + barsHeight;

                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        // Adjust the window height.
                        // Note: Changing Width/Height affects the overall size (including OS borders).
                        // I need to calculate the total size based on the new desired ClientSize.
                        // Difference between total size and client size (borders, title bar)
                        var frameSize = this.FrameSize ?? new Size(this.Width, this.Height);
                        var clientSize = this.ClientSize;
                        double borderHeight = frameSize.Height - clientSize.Height;

                        this.Height = targetClientHeight + borderHeight;
                    }
                    finally
                    {
                        _isResizing = false;
                    }
                });
            }
        }

        /// <summary>Turns the drive LED on/off; <paramref name="activity"/> ("A: T05 S09",
        /// captured by the WD1772 when the command starts) is shown next to the LED and
        /// cleared when the LED goes off.</summary>
        public void DriveLed(bool On, string activity = "")
        {
            if (On)
            {
                TimeLastDriveOn = DateTime.Now;
                DriveLedImage.Source = BitmapLedDriveOn;

                if (!string.IsNullOrEmpty(activity))
                    TextBlockDriveStatus.Text = activity;
            }
            else
            {
                DriveLedImage.Source = BitmapLedDriveOff;
                TextBlockDriveStatus.Text = "";
            }
        }

        public void SetStatusBarText(string text)
        {
            TextBlockStatusBar.Classes.Remove("fadeOut");
            TextBlockStatusBar.Opacity = 1.0;
            TextBlockStatusBar.Text = text;
            TimeLastTimeTextBlock = DateTime.Now;
        }
        
        public void RefreshDriveLed()
        {
            /*
             * This logic should reside within the disk drive emulation, 
             * toggling the LED according to the floppy motor state. 
             * Currently, I've implemented a 2-second timeout to turn off 
             * the LED after the last drive access
             */
            if ((DateTime.Now - TimeLastDriveOn).TotalSeconds > 2)
            {
                Dispatcher.UIThread.InvokeAsync(() => {
                    ASEMain.MainWindow.DriveLed(false);
                }, DispatcherPriority.Background);
            }

            // Update text in the statusbar
            if ((DateTime.Now - TimeLastTimeTextBlock).TotalSeconds > 10)
            {
                Dispatcher.UIThread.InvokeAsync(() => {
                    TextBlockStatusBar.Classes.Add("fadeOut");
                }, DispatcherPriority.Background);
            }
        }

        public void OnExitClick(object sender, RoutedEventArgs e)
        {
            ASEMain.Shutdown();
            Close();
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            bool HasValidFile = false;

            var files = e.DataTransfer.GetItems(DataFormat.File);

            foreach (var file in files)
            {
                IStorageItem storageItem = file.TryGetFile();

                // Get the first valid file
                if (storageItem != null)
                {
                    string s = storageItem.TryGetLocalPath();

                    if (s.EndsWith(".st", StringComparison.OrdinalIgnoreCase)
                        || s.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        || s.EndsWith(".msa", StringComparison.OrdinalIgnoreCase)
                        || s.EndsWith(".stx", StringComparison.OrdinalIgnoreCase)
                        )
                    {
                        HasValidFile = true;
                        InsertDisk(s);
                        break;
                    }
                }
            }

            if(!HasValidFile)
                SetStatusBarText($"❌ Dropped file is an invalid disk image");

            e.Handled = true;
        }

        public async void OnOpenImageClick(object sender, RoutedEventArgs e)
        {
            ASEMain.CaptureMouse(false);

            var (canceled, selpath) = await Dialogs.OpenFile("Select disk image file",
                Config.DialogStartFolder(Config.ConfigOptions.RunninConfig.DiskImagesPath),
                new FileFilter("ST disk images", ["*.st", "*.msa", "*.stx", "*.zip"]));

            if (!canceled && selpath.Count() == 1)
                InsertDisk(selpath.ElementAt(0));
        }

        public async void OnChangeDiskClick(object sender, RoutedEventArgs e)
        {
            InsertDisk(ZipFile);
        }

        async void InsertDisk(string ImageFile)
        {
            DisableEjectMenu();

            string message;
            bool inserted = ASEMain.driveA.Insert(ImageFile, out message);

            if (!inserted)
            {
                if (ImageFile.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    // zip contains more than one image disk
                    var dialog = new FileList(message);
                    var selectedFile = await dialog.ShowDialog<string>(this);

                    if (selectedFile != null)
                    {
                        bool insertedFromZip = ASEMain.driveA.Insert($"{ImageFile}|{selectedFile}", out message);

                        if (!insertedFromZip)
                        {
                            await Dialogs.MessageBox("Error", message, MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                            return;
                        }

                        ZipFile = ImageFile;
                        ItemMenuChangeDisk.IsEnabled = true;
                        ImageFile = selectedFile;
                    }
                }
                else
                {
                    await Dialogs.MessageBox("Error", message, MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                    return;
                }
            }
            else
            {
                ColoredConsole.WriteLine(message);
            }

            var response = await Dialogs.MessageBox("Disk inserted", "Reboot?", MessageBoxDialogType.YesNo, MessageBoxIconType.Question, MessageBoxButton.Yes);

            if (response == MessageBoxButton.Yes)
                ASEMain.HardReset();

            ItemMenuEjecDisk.IsEnabled = true;

            SetStatusBarText($"Disk {Path.GetFileName(ImageFile)} inserted in drive A");
        }

        public void OnEjecImageClick(object sender, RoutedEventArgs e)
        {
            ASEMain.driveA.Eject();
            ZipFile = "";
            DisableEjectMenu();
        }

        void DisableEjectMenu()
        {
            ItemMenuChangeDisk.IsEnabled = false;
            ItemMenuEjecDisk.IsEnabled = false;
        }

        public async void OnResetClick(object sender, RoutedEventArgs e)
        {
            var response = await Dialogs.MessageBox("Reset ST", "Are you sure?", MessageBoxDialogType.YesNo, MessageBoxIconType.Question, MessageBoxButton.Yes);

            // HardReset stops the emulation thread before re-initializing: InitCpu alone would
            // race the running loop (flaky resets) and never starts the thread if the machine
            // is off (first launch without TOS).
            if (response == MessageBoxButton.Yes)
                ASEMain.HardReset();
        }

        public void OnSaveSnapshotClick(object sender, RoutedEventArgs e) => DoSaveSnapshot();

        /// <summary>Saves a machine snapshot into the configured snapshots directory. Reached
        /// from the File menu and from F11 (posted here by ASEMain.HandleEvents). UI thread.</summary>
        public void DoSaveSnapshot()
        {
            if (ASEMain.SaveSnapshot(out string path, out string error))
                SetStatusBarText($"Snapshot saved: {Path.GetFileName(path)}");
            else
                SetStatusBarText($"❌ Could not save snapshot: {error}");
        }

        /// <summary>Saves the current ST screen as PNG into the configured screenshots
        /// directory. Reached from Shift+F11. UI thread.</summary>
        public void DoSaveScreenshot()
        {
            if (ASEMain.SaveScreenshot(out string path, out string error))
                SetStatusBarText($"Screenshot saved: {Path.GetFileName(path)}");
            else
                SetStatusBarText($"❌ Could not save screenshot: {error}");
        }

        public async void OnRestoreSnapshotClick(object sender, RoutedEventArgs e)
        {
            ASEMain.CaptureMouse(false);

            var (canceled, selpath) = await Dialogs.OpenFile("Restore ST snapshot",
                Config.DialogStartFolder(Config.SnapshotsDir),
                new FileFilter("ASE snapshot", ["*.snap"]));

            if (canceled || selpath.Count() != 1)
                return;

            string path = selpath.ElementAt(0);

            if (!ASEMain.RestoreSnapshot(path, out string error))
            {
                await Dialogs.MessageBox("Error", $"Could not restore snapshot: {error}",
                    MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                return;
            }

            // El snapshot puede haber reinsertado el disco que estaba en la unidad A
            ItemMenuEjecDisk.IsEnabled = ASEMain.driveA.HasDisk;

            SetStatusBarText($"Snapshot {Path.GetFileName(path)} restored");
        }

        public void OnConfigurationClick(object sender, RoutedEventArgs e)
        {
            var configWindow = new ConfigurationWindow();
            configWindow.ShowDialog(this);
        }

        public void OnDebugClick(object sender, RoutedEventArgs e)
        {
            var debugWindow = new DebugWindow();
            debugWindow.ShowDialog(this);
        }

        private void OnAboutClick(object sender, RoutedEventArgs e)
        {
            var aboutWindow = new AboutWindow();
            aboutWindow.ShowDialog(this);
        }
        
        private async void OnManualClick(object sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
                return;

            await topLevel.Launcher.LaunchUriAsync(new Uri("https://github.com/thebitculture/ase/wiki"));
        }
    }
}
