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
        // Aspect-ratio keeping: last client size we accepted as correct; used both to
        // ignore the echo of our own corrections and to know which dimension the user dragged.
        private Size _lastStableClientSize;
        private DispatcherTimer _resizeDebounce;

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

            // Maintains the aspect ratio of the emulator when resizing the window.
            // Corrections are debounced so we never fight the user's interactive drag.
            _lastStableClientSize = ClientSize;
            _resizeDebounce = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(300) };
            _resizeDebounce.Tick += OnResizeSettled;
            this.GetObservable(Window.ClientSizeProperty).Subscribe(OnClientSizeChanged);

            // Snap the startup size to the correct ratio once the first layout is done
            Dispatcher.UIThread.Post(EnforceAspectRatio, DispatcherPriority.Loaded);

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
                    return;
                }

                // X11 hands over the window but not its input: unlike the Windows and macOS
                // backends, SDL selects no event on a window it did not create, so without this
                // it would never see the keyboard, the mouse capture or even the gamepad.
                if (OperatingSystem.IsLinux() && !X11Input.AttachInput(_sdlWindowPtr, out string x11Error))
                    ColoredConsole.WriteLine($"Warning: could not attach the X11 input to the window ([[red]]{x11Error}[[/red]]): keyboard, mouse capture and gamepad will not work");

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

        // Pixel aspect ratio of the ST on an original 4:3 PAL monitor: the tube shows
        // ~52 µs of active line over ~288 visible lines, so the 416 low-res pixels (8 MHz)
        // that fit in 52 µs span 288 * 4/3 = 384 line-height units -> each pixel is
        // 384/416 = 12/13 as wide as it is tall (slightly narrower than square).
        private const double PIXEL_ASPECT = 12.0 / 13.0;

        /// <summary>Aspect ratio of the picture GLControl shows — the full framebuffer
        /// (display + borders) or the 640x400 crop when borders are hidden — corrected
        /// by the pixel aspect of the original 4:3 monitor.</summary>
        private static double DisplayAspectRatio =>
            PIXEL_ASPECT * (Config.ConfigOptions.RunninConfig.ShowBorders
                ? (double)ASEMain.ScreenWidth / ASEMain.ScreenHeight
                : (double)VideoTiming.DISPLAY_TEX_WIDTH / (VideoTiming.DISPLAY_TEX_HEIGHT * 2));

        private void OnClientSizeChanged(Size newSize)
        {
            // Ignore the echo of our own correction (and the initial emission)
            if (Math.Abs(newSize.Width - _lastStableClientSize.Width) < 1 &&
                Math.Abs(newSize.Height - _lastStableClientSize.Height) < 1)
                return;

            // Debounce: correct only after the user stops dragging, otherwise we would
            // fight the interactive resize and the window becomes impossible to stretch
            _resizeDebounce.Stop();
            _resizeDebounce.Start();
        }

        private void OnResizeSettled(object sender, EventArgs e)
        {
            _resizeDebounce.Stop();
            EnforceAspectRatio();
        }

        private void EnforceAspectRatio()
        {
            // Never fight a maximized/minimized window
            if (WindowState != WindowState.Normal)
            {
                _lastStableClientSize = ClientSize;
                return;
            }

            double barsHeight = MainMenu.Bounds.Height + BottomStatusBar.Bounds.Height;
            Size size = ClientSize;

            // Height available for the GL viewport
            double glHeight = size.Height - barsHeight;
            if (glHeight <= 1) return;

            double ratio = DisplayAspectRatio;

            if (Math.Abs(size.Width / glHeight - ratio) < 0.01)
            {
                _lastStableClientSize = size;
                return;
            }

            // Honor the dimension the user actually dragged and derive the other one
            double dw = Math.Abs(size.Width - _lastStableClientSize.Width);
            double dh = Math.Abs(size.Height - _lastStableClientSize.Height);

            Size target = dw >= dh
                ? new Size(size.Width, size.Width / ratio + barsHeight)  // width led -> adjust height
                : new Size(glHeight * ratio, size.Height);               // height led -> adjust width

            _lastStableClientSize = target;

            // Window.Width/Height are the *client* size in Avalonia (the OS frame is not included).
            // If the OS clamps this (screen edge, minimum size), the resulting ClientSize change
            // re-enters through the debounce and converges from the clamped size.
            Width = target.Width;
            Height = target.Height;
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

            ItemMenuEjectDisk.IsEnabled = true;

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
            ItemMenuEjectDisk.IsEnabled = false;
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

            // The snapshot may have re-inserted the disk that was in drive A
            ItemMenuEjectDisk.IsEnabled = ASEMain.driveA.HasDisk;

            SetStatusBarText($"Snapshot {Path.GetFileName(path)} restored");
        }

        private async void OnLibraryClick(object sender, RoutedEventArgs e)
        {
            ASEMain.CaptureMouse(false);

            var library = new LibraryWindow();
            string gameFile = await library.ShowDialog<string>(this);

            if (!string.IsNullOrEmpty(gameFile))
                InsertDisk(gameFile);
        }
        
        private void OnConfigureLibraryClick(object sender, RoutedEventArgs e)
        {
            var configureLibrary = new LibraryConfigurationWindow();
            configureLibrary.ShowDialog(this);
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
