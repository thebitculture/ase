using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Reactive;
using Avalonia.Threading;
using SDL2;
using System;
using TinyDialogsNet;
using Tmds.DBus.Protocol;
using static ASE.Config;

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

        // The one MT-32 toolbox window, or null when it is closed (see OnMt32ToolboxClick).
        MT32.Mt32Toolbox _mt32Toolbox;

        // Key this window's geometry is stored under in windows.json (see WindowLayouts).
        const string LayoutKey = "MainWindow";

        public MainWindow()
        {
            InitializeComponent();

            // Before the window is shown, so it comes up where it was left instead of
            // jumping there. A size that no longer matches the video mode is corrected by
            // the EnforceAspectRatio pass that runs after the first layout.
            WindowLayouts.Restore(this, LayoutKey, restoreSize: true);

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
            // Avalonia.Reactive.Observable (the Subscribe(Action<T>) extension) is internal; the
            // overload this used to bind to came from System.Reactive, which Avalonia 12's
            // ReactiveUI.Avalonia no longer drags in. AnonymousObserver is Avalonia's own public
            // adapter for exactly this.
            this.GetObservable(Window.ClientSizeProperty)
                .Subscribe(new AnonymousObserver<Size>(OnClientSizeChanged));

            // Snap the startup size to the correct ratio once the first layout is done
            Dispatcher.UIThread.Post(EnforceAspectRatio, DispatcherPriority.Loaded);

            // Attach SDL to the window
            var platformHandle = this.TryGetPlatformHandle();

            if (platformHandle != null)
            {
                // On Linux the window about to be adopted is an X11 one (Avalonia has no Wayland
                // backend), so SDL must be on its x11 driver. Verified instead of assumed: handing
                // an X11 window id to the Wayland driver is not an error SDL reports back, it is a
                // pointer dereference — on sdl2-compat it takes the whole process down with a
                // segmentation fault right here, before anything is drawn or logged.
                if (OperatingSystem.IsLinux() && !CanAdoptWindow(platformHandle))
                {
                    Close();
                    return;
                }

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

                ShowUpdateWindowIfNeeded();
            }
        }

        /// <summary>
        /// Whether SDL can take this window over. Linux only, called before SDL_CreateWindowFrom.
        /// The blocking condition is a video driver other than x11 (no X server was reachable, or
        /// one was forced): there SDL would misread the window handle instead of rejecting it.
        /// A handle that is not an XID is only reported — SDL is on X11 and will refuse it cleanly.
        /// </summary>
        /// <returns>false — after explaining it on the console and in an alert — when the window
        /// must not be handed to SDL.</returns>
        private static bool CanAdoptWindow(IPlatformHandle handle)
        {
            string driver = SDL.SDL_GetCurrentVideoDriver();

            if (driver == "x11")
            {
                if (handle.HandleDescriptor != "XID")
                    ColoredConsole.WriteLine($"Warning: the window handle is a [[yellow]]{handle.HandleDescriptor}[[/yellow]], not an X11 XID: keyboard, mouse capture and gamepad may not work.");

                return true;
            }

            string message = $"SDL is using the '{driver}' video driver, but the ASE window is an X11 one, " +
                             "so SDL cannot read its keyboard, mouse or gamepad. Inside a Wayland session ASE " +
                             "runs through XWayland: check that it is installed, and that SDL_VIDEODRIVER is " +
                             "not forced to another driver in your environment.";

            ColoredConsole.WriteLine($"[[red]]{message}[[/red]]");

            TinyDialogs.MessageBox("ASE cannot start", message,
                MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);

            return false;
        }

        /// <summary>
        /// Announces the newer release found at startup. The check runs in Program.Main, before
        /// Avalonia is up, so the window can only be raised here — posted rather than awaited,
        /// since ShowDialog needs an owner whose OnOpened has already returned. The dialog holds
        /// a UI pause, so the machine waits frozen for the answer instead of booting behind it.
        /// </summary>
        private void ShowUpdateWindowIfNeeded()
        {
            if (ReleaseChecker.ReleaseInfo?.ExistsNewVersion != true)
                return;

            Dispatcher.UIThread.Post(() => _ = new UpdateWindow().ShowDialog(this), DispatcherPriority.Loaded);
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);

            // Read here and not in OnClosed: by then the window is gone and its position
            // and size no longer mean anything.
            WindowLayouts.Remember(this, LayoutKey, rememberSize: true);
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

        /// <summary>Aspect ratio of the picture GLControl shows. On a colour monitor it is the
        /// full framebuffer (display + borders) or the 640x400 crop when borders are hidden,
        /// corrected by the pixel aspect of the original 4:3 monitor. On a monochrome monitor it
        /// is the native 640x400 with square pixels.</summary>
        private static double DisplayAspectRatio =>
            VideoTiming.Mono
                ? (double)VideoTiming.BUFFER_WIDTH / VideoTiming.BUFFER_HEIGHT
                : PIXEL_ASPECT * (Config.ConfigOptions.RunninConfig.ShowBorders
                    ? (double)VideoTiming.BUFFER_WIDTH / (VideoTiming.BUFFER_HEIGHT * 2)
                    : (double)VideoTiming.DISPLAY_TEX_WIDTH / (VideoTiming.DISPLAY_TEX_HEIGHT * 2));

        /// <summary>Re-applies the window aspect ratio to the current video mode. Called after a
        /// reset that may have switched the monitor type (colour ↔ monochrome).</summary>
        public void RefreshAspectRatio() => EnforceAspectRatio();

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
                        InsertDisk(s, null);
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
                InsertDisk(selpath.ElementAt(0), null);
        }

        public async void OnChangeDiskClick(object sender, RoutedEventArgs e)
        {
            // Another disk of the same zip is still the same game, so it keeps whatever
            // library entry (and MT-32 profile) is already loaded.
            InsertDisk(ZipFile, MT32.Mt32Profiles.CurrentGame);
        }

        /// <summary>
        /// Puts a disk image in drive A with the emulation thread parked at a frame boundary, so
        /// the image contents and geometry never change under a sector read in flight. Failures of
        /// the load itself (truncated image, corrupt zip) come back in <paramref name="message"/>
        /// instead of escaping the async void handlers that call this.
        /// </summary>
        static bool InsertIntoDriveA(string imageFile, out string message)
        {
            bool inserted = false;
            string loadMessage = "";

            if (!ASEMain.RunWhilePaused(() => inserted = ASEMain.driveA.Insert(imageFile, out loadMessage),
                                        out string error))
                loadMessage = $"Could not read [[red]]{imageFile}[[/red]]: {error}";

            message = loadMessage;
            return inserted;
        }

        /// <summary>
        /// Loads a disk image into drive A and offers the reboot. <paramref name="libraryGame"/>
        /// is the catalogue entry the image came from, or null for anything opened by hand:
        /// it is what decides the game's MT-32 instrument mapping (see <see cref="MT32.Mt32Profiles"/>).
        /// </summary>
        async void InsertDisk(string ImageFile, Models.LibraryItem libraryGame)
        {
            DisableEjectMenu();

            bool inserted = InsertIntoDriveA(ImageFile, out string message);

            if (!inserted)
            {
                if (ImageFile.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    // zip contains more than one image disk
                    var dialog = new FileList(message);
                    var selectedFile = await dialog.ShowDialog<string>(this);

                    // Nothing picked: whatever was in the drive is still in it, no disk change
                    if (selectedFile == null)
                    {
                        RefreshDiskMenus();
                        return;
                    }

                    if (!InsertIntoDriveA($"{ImageFile}|{selectedFile}", out message))
                    {
                        await Dialogs.MessageBox("Error", message, MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                        return;
                    }

                    ZipFile = ImageFile;
                    ItemMenuChangeDisk.IsEnabled = true;
                    ImageFile = selectedFile;
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

            // Applied before the reboot prompt on purpose: a power-cycle re-sends the mapped
            // programs to a fresh module (MidiManager.Initialize), so the game comes up with
            // its instruments already in place.
            MT32.Mt32Profiles.SetCurrentGame(libraryGame);

            // Answering No is a real option now: the FDC reports the disk change to the running
            // program (see FloppyImage.SignalDiskTransition), which is what multi-disk games and
            // GEMDOS need to pick up the new disk without rebooting.
            var response = await Dialogs.MessageBox("Disk inserted", "Reboot?", MessageBoxDialogType.YesNo, MessageBoxIconType.Question, MessageBoxButton.Yes);

            if (response == MessageBoxButton.Yes)
                ASEMain.HardReset();

            ItemMenuEjectDisk.IsEnabled = true;

            SetStatusBarText($"Disk {Path.GetFileName(ImageFile)} inserted in drive A");
        }

        public void OnEjecImageClick(object sender, RoutedEventArgs e)
        {
            // Same rendezvous as inserting: the emulation thread must not be reading the image
            // while it is taken away from under it.
            ASEMain.RunWhilePaused(ASEMain.driveA.Eject, out _);
            ZipFile = "";

            // Empty drive: no library game any more, so the MT-32 mapping goes with it.
            MT32.Mt32Profiles.SetCurrentGame(null);

            DisableEjectMenu();
        }

        void DisableEjectMenu()
        {
            ItemMenuChangeDisk.IsEnabled = false;
            ItemMenuEjectDisk.IsEnabled = false;
        }

        /// <summary>Puts the disk menu entries back in sync with what is actually in drive A.</summary>
        void RefreshDiskMenus()
        {
            ItemMenuEjectDisk.IsEnabled = ASEMain.driveA.HasDisk;
            ItemMenuChangeDisk.IsEnabled = !string.IsNullOrEmpty(ZipFile);
        }

        /// <summary>
        /// Decides the Emulation entries that depend on machine state, right when the user
        /// opens the menu — no polling, and no event plumbing from the places that can
        /// change it (the Configuration window, a restored snapshot, a reset).
        /// </summary>
        private void OnEmulationMenuOpened(object sender, RoutedEventArgs e)
        {
            // Single instance, and only meaningful with the built-in module wired up: with
            // any other MIDI mode there is no front panel to open, however many library
            // games carry a YM->MT-32 mapping.
            ItemMenuMt32Toolbox.IsEnabled = _mt32Toolbox == null && Mt32ModuleWiredUp;
        }

        /// <summary>Whether the machine was last powered on wired to the built-in MT-32 —
        /// not whether the module actually came up (bad ROMs still open the toolbox, dark
        /// and inert, which is how the user finds out).</summary>
        static bool Mt32ModuleWiredUp =>
            MidiManager.Mode == ConfigOptions.MIDIEmulationOptions.BuiltInMT32;

        /// <summary>
        /// Opens the MT-32 front panel (volume knob + LCD). Single instance: the menu entry
        /// is greyed out while the window is up and comes back when it closes. It is shown
        /// non-modally and holds no UI pause — unlike the emulator's dialogs, it is only
        /// useful with the machine running. The toolbox closes itself if the MIDI mode moves
        /// away from the module later on.
        /// </summary>
        public void OnMt32ToolboxClick(object sender, RoutedEventArgs e)
        {
            if (_mt32Toolbox != null)
            {
                _mt32Toolbox.Activate();
                return;
            }

            // The menu entry is already greyed out in this case; this is the guard for any
            // other way in (a hotkey, a future command) rather than a second UI decision.
            if (!Mt32ModuleWiredUp)
                return;

            ItemMenuMt32Toolbox.IsEnabled = false;

            _mt32Toolbox = new MT32.Mt32Toolbox();

            // Only clears the slot: whether the entry comes back enabled is recomputed when
            // the menu is next opened, since the toolbox may have closed itself precisely
            // because the module went away.
            _mt32Toolbox.Closed += (_, _) => _mt32Toolbox = null;

            _mt32Toolbox.Show(this);
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
                InsertDisk(gameFile, library.SelectedGame);
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
