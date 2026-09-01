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

        // The platform window handed to SDL_CreateWindowFrom. Kept to notice a backend that
        // recreates it behind our back when the window state changes (see VerifyNativeHandle).
        IntPtr _nativeWindowHandle;

        // Menu-bar state, split from how it is shown: ShowMenu records whether it should be
        // usable (i.e. the input is not captured) and ApplyMenuVisibility decides what that
        // means in the current window state.
        bool _menuEnabled = true;

        // Window state to give back when leaving full screen, and whether the full-screen side
        // effects (menu, screen saver, priority) are currently applied — the state change is
        // acted on in one place, OnWindowStateChanged, wherever it came from.
        WindowState _stateBeforeFullScreen = WindowState.Normal;
        bool _fullScreenApplied;

        // Raising the priority may be refused (Linux/macOS): say so once, not on every toggle.
        static bool _priorityWarned;

        // Full-screen setting as this session started (config file + command line), to tell on
        // closing whether the user changed it — see OnClosing.
        readonly bool _fullScreenAtStartup = Config.ConfigOptions.RunninConfig.FullScreen;

        Bitmap BitmapLedDriveOn;
        Bitmap BitmapLedDriveOff;

        // One entry per floppy drive (0 = A, 1 = B): when the FDC last lit its light, and whether
        // the turn-off has already been posted for the idle stretch it is in. Without that second
        // flag the once-per-frame poll would queue dispatcher work forever for an idle drive.
        // MinValue = never accessed, so both drives start idle and paint their label on the
        // first poll instead of two seconds into the session.
        readonly DateTime[] TimeLastDriveOn = { DateTime.MinValue, DateTime.MinValue };
        readonly bool[] DriveLedOffPosted = { false, false };

        // The idle label names the disk in the drive, so a disk inserted, ejected or swapped while
        // the light is off has to repaint it even though the idle state itself never changed. This
        // is the disk part of the label last posted for each drive (empty = drive empty).
        readonly string[] DriveLedDiskPosted = { "", "" };

        // Marquee for the idle labels. A disk file name is usually wider than the strip, so after
        // MarqueeIdleDelay seconds without activity the label slides left to show the end of the
        // name and back again, slowly. The timer only lives while there is something to scroll:
        // DriveLed starts it and OnMarqueeTick stops it when every label fits.
        const double MarqueeSpeed = 15.0;      // pixels per second
        const double MarqueeIdleDelay = 3.0;   // seconds of inactivity before it starts
        const double MarqueeEndPause = 1.5;    // seconds held at each end of the travel
        DispatcherTimer MarqueeTimer;
        readonly bool[] DriveLabelIdle = { true, true };
        readonly DateTime[] DriveLabelSince = { DateTime.Now, DateTime.Now };

        DateTime TimeLastTimeTextBlock = DateTime.Now;

        // Hard disk light. The emulation only stamps ASEMain.SignalHardDiskActivity(); this
        // side polls it once per frame in RefreshDriveLed and touches the UI only when the
        // state actually flips, so a program hammering the disk costs nothing here.
        Bitmap BitmapLedHDOn;
        Bitmap BitmapLedHDOff;
        bool HDLedIsOn;

        // Zip each drive's disk came from, so "Change disk from ZIP" knows where to look again.
        // Empty when the drive holds a plain image, or nothing at all.
        readonly string[] ZipFile = { "", "" };

        // Drive B as this session started, to tell on closing whether the user changed it —
        // same reasoning as _fullScreenAtStartup.
        readonly bool _driveBAtStartup = Config.ConfigOptions.RunninConfig.DriveBEnabled;

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
            BitmapLedHDOn = new Bitmap(AssetLoader.Open(new Uri("avares://ASE/Assets/hd_led_on.png")));
            BitmapLedHDOff = new Bitmap(AssetLoader.Open(new Uri("avares://ASE/Assets/hd_led_off.png")));

            // Drive B is remembered in config.json: put the menu and its light in the state the
            // machine was left in before anything can be inserted into it.
            if (!Design.IsDesignMode)
            {
                ApplyDriveBConnection(Config.ConfigOptions.RunninConfig.DriveBEnabled);
                AddHandler(DragDrop.DropEvent, OnDrop);
            }
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            if (Design.IsDesignMode)
                return;

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

            // Full screen is watched the same way rather than only acted on in SetFullScreen: the
            // window manager can put us in or out of it without asking (a title-bar button, its
            // own shortcut), and the menu bar, the screen saver and the priority have to follow.
            this.GetObservable(Window.WindowStateProperty)
                .Subscribe(new AnonymousObserver<WindowState>(OnWindowStateChanged));

            // Snap the startup size to the correct ratio once the first layout is done
            Dispatcher.UIThread.Post(EnforceAspectRatio, DispatcherPriority.Loaded);

            // Scrolls an idle drive label whose disk name does not fit the strip. Started here so
            // a disk inserted from the command line scrolls without waiting for the first FDC
            // command, and it stops itself as soon as nothing overflows.
            MarqueeTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(40) };
            MarqueeTimer.Tick += OnMarqueeTick;
            MarqueeTimer.Start();

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

                _nativeWindowHandle = platformHandle.Handle;
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

                // Come up full screen when that is how it was left (or --fullscreen was passed).
                // After Init, so the machine is already running behind the picture, and posted so
                // the first layout has settled: the state change is applied to a window the
                // platform has finished placing.
                if (Config.ConfigOptions.RunninConfig.FullScreen)
                    Dispatcher.UIThread.Post(() => SetFullScreen(true), DispatcherPriority.Loaded);

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

            // Full screen is a setting, so it goes to config.json — but the file is only ever
            // written when something deliberately changes it (no window here saves it on the way
            // out), and it would carry along whatever this session's command line overrode. So it
            // is written only when the user actually toggled full screen at some point.
            if (Config.ConfigOptions.RunninConfig.FullScreen != _fullScreenAtStartup
                || Config.ConfigOptions.RunninConfig.DriveBEnabled != _driveBAtStartup)
                Program.Config.DumpJsonConfig();
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

            // Transmit the button press to the ACIA mouse handling. The same click usually
            // arrives through the SDL queue as well; ACIA.MouseButtonChanged edge-guards it.
            if (ASEMain.IsMouseCaptured &&
                (p.Properties.IsLeftButtonPressed || p.Properties.IsRightButtonPressed))
            {
                ACIA.MouseButtonChanged(left: p.Properties.IsLeftButtonPressed, pressed: true);
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

            if (ASEMain.IsMouseCaptured &&
                (e.InitialPressMouseButton == MouseButton.Left ||
                 e.InitialPressMouseButton == MouseButton.Right))
            {
                ACIA.MouseButtonChanged(left: e.InitialPressMouseButton == MouseButton.Left,
                                        pressed: false);
                e.Handled = true;
            }
        }

        public void ShowMenu(bool show)
        {
            _menuEnabled = show;
            ApplyMenuVisibility();
        }

        /// <summary>
        /// Applies the menu-bar policy: it is disabled while the input is captured (so its
        /// accelerators don't eat keystrokes meant for the ST) and, in full screen, it also folds
        /// away — the picture then owns the whole screen and the bar comes back the moment the
        /// mouse is released with F12 or the middle button. In a normal window it always stays
        /// visible, only greyed out, which is what it has always done.
        /// </summary>
        private void ApplyMenuVisibility()
        {
            MainMenu.IsEnabled = _menuEnabled;
            MainMenu.IsVisible = _menuEnabled || !IsFullScreen;
        }

        /// <summary>Whether the window is currently in full screen.</summary>
        public bool IsFullScreen => WindowState == WindowState.FullScreen;

        public void OnToggleFullScreenClick(object sender, RoutedEventArgs e) => ToggleFullScreen();

        /// <summary>
        /// Enters or leaves full screen. This is Avalonia's own <see cref="WindowState.FullScreen"/>
        /// — the same window, still with its menu, status bar and dialogs — and not a maximized
        /// undecorated window: it is the one the platform implements itself (Win32 style change,
        /// <c>_NET_WM_STATE_FULLSCREEN</c> on X11, <c>toggleFullScreen:</c> on macOS), it covers
        /// the taskbar without us computing screen bounds, and above all it leaves the native
        /// window handle alone — the handle SDL adopted with SDL_CreateWindowFrom, and which the
        /// keyboard, the mouse capture and the gamepad all hang from (see VerifyNativeHandle).
        /// </summary>
        public void ToggleFullScreen() => SetFullScreen(!IsFullScreen);

        public void SetFullScreen(bool fullScreen)
        {
            if (fullScreen == IsFullScreen)
                return;

            // Remembered so leaving gives back the window the user had, maximized or not.
            if (fullScreen)
                _stateBeforeFullScreen = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;

            // Everything that follows from the change is applied by OnWindowStateChanged, which
            // also catches the window manager doing this on its own.
            WindowState = fullScreen ? WindowState.FullScreen : _stateBeforeFullScreen;
        }

        /// <summary>
        /// Reacts to the window entering or leaving full screen, whoever caused it — us, or the
        /// window manager (some let the user do it from the title bar or a keyboard shortcut of
        /// their own). Minimizing is ignored: an iconified full-screen window is still full
        /// screen as far as everything here is concerned, and it comes back as one.
        /// </summary>
        private void OnWindowStateChanged(WindowState state)
        {
            if (state == WindowState.Minimized)
                return;

            bool fullScreen = state == WindowState.FullScreen;

            if (fullScreen == _fullScreenApplied)
                return;

            _fullScreenApplied = fullScreen;

            ApplyMenuVisibility();
            ApplyFullScreenSystemState(fullScreen);
            Config.ConfigOptions.RunninConfig.FullScreen = fullScreen;

            // The aspect ratio is the GL control's business while we are full screen (it bars the
            // picture itself); on the way back the window has to be snapped to the right shape
            // again, once the platform has restored its normal geometry.
            if (!fullScreen)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    // From the restored size, not from the screen-sized one we were just at:
                    // EnforceAspectRatio reads the difference to tell which dimension the user
                    // dragged, and a full-screen "drag" of several hundred pixels is not one.
                    _lastStableClientSize = ClientSize;
                    EnforceAspectRatio();
                }, DispatcherPriority.Loaded);
            }

            Dispatcher.UIThread.Post(VerifyNativeHandle, DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Screen saver and process priority, both tied to being full screen: that is the "I am
        /// playing" state. The screen saver goes through SDL, which covers the three platforms at
        /// once. Priority is best-effort by nature — on Windows it just works, while on Linux and
        /// macOS a negative nice value needs privileges the emulator does not have, so the failure
        /// is reported once and dropped (there the answer is to launch it with <c>nice</c>).
        /// </summary>
        private static void ApplyFullScreenSystemState(bool fullScreen)
        {
            if (fullScreen)
                SDL.SDL_DisableScreenSaver();
            else
                SDL.SDL_EnableScreenSaver();

            try
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();

                // AboveNormal, not High: High competes with the system's own input handling and
                // makes the machine feel worse, not better.
                process.PriorityClass = fullScreen
                    ? System.Diagnostics.ProcessPriorityClass.AboveNormal
                    : System.Diagnostics.ProcessPriorityClass.Normal;
            }
            catch (Exception ex)
            {
                if (!_priorityWarned)
                {
                    _priorityWarned = true;
                    ColoredConsole.WriteLine($"Could not change the process priority ([[red]]{ex.Message}[[/red]]). On Linux/macOS raising it needs privileges: launch ASE with [[cyan]]nice -n -5[[/cyan]] if you want it.");
                }
            }
        }

        /// <summary>
        /// Checks that the platform window SDL was handed at startup is still the one this window
        /// owns. Changing the window state is not supposed to recreate it on any backend — that is
        /// the whole reason full screen is done with WindowState rather than by dropping the
        /// decorations — but if a platform ever did, the symptom (no keyboard, no mouse capture, no
        /// gamepad, and on X11 no event mask either) would be baffling without this line.
        /// </summary>
        private void VerifyNativeHandle()
        {
            if (_nativeWindowHandle == IntPtr.Zero)
                return;

            IntPtr current = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

            if (current != _nativeWindowHandle)
                ColoredConsole.WriteLine($"Warning: the platform window changed (0x{_nativeWindowHandle.ToInt64():X} -> 0x{current.ToInt64():X}); the SDL input is still attached to the old one.");
        }

        /// <summary>Aspect ratio of the picture the GL control shows, which is what the window is
        /// kept at. It lives in <see cref="GLControl"/> because that is where the picture is
        /// fitted to the surface (letterbox); both sides must read the same value or the window
        /// would settle at a shape the renderer then bars.</summary>
        private static double DisplayAspectRatio => GLControl.DisplayAspectRatio;

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

        /// <summary>Turns a floppy drive's LED on/off (0 = A, 1 = B); <paramref name="activity"/>
        /// ("A: T05 S09", captured by the WD1772 when the command starts) replaces the drive
        /// letter next to the LED and goes back to just the letter when the LED goes off.</summary>
        public void DriveLed(int drive, bool On, string activity = "")
        {
            var led = drive == 1 ? DriveBLedImage : DriveLedImage;
            var text = drive == 1 ? TextBlockDriveBStatus : TextBlockDriveStatus;

            // Any label change starts from the left; only the idle one is ever scrolled.
            Canvas.SetLeft(text, 0);

            if (On)
            {
                TimeLastDriveOn[drive] = DateTime.Now;
                led.Source = BitmapLedDriveOn;
                text.Opacity = 1.0;
                DriveLabelIdle[drive] = false;

                if (!string.IsNullOrEmpty(activity))
                    text.Text = activity;
            }
            else
            {
                led.Source = BitmapLedDriveOff;

                // Idle: the block keeps the drive letter, dimmed like the HDD label, so two
                // lights side by side are still telling you which drive is which, and names the
                // disk in it - with no head position to show, the light alone cannot say whether
                // the drive is loaded, let alone with what.
                text.Text = (drive == 1 ? "B: " : "A: ") + DriveDiskLabel(drive);
                text.Opacity = 0.5;

                DriveLabelIdle[drive] = true;
                DriveLabelSince[drive] = DateTime.Now;

                if (MarqueeTimer != null && !MarqueeTimer.IsEnabled)
                    MarqueeTimer.Start();
            }
        }

        /// <summary>The disk half of a drive idle label: the image file name, or "Empty" with no
        /// disk in the drive ("Disc" for a disk whose path we do not have).</summary>
        static string DriveDiskLabel(int drive)
        {
            var floppy = drive == 1 ? ASEMain.driveB : ASEMain.driveA;

            if (!floppy.HasDisk)
                return "Empty";

            return string.IsNullOrEmpty(floppy.DisplayName) ? "Disc" : floppy.DisplayName;
        }

        /// <summary>Slides an idle drive label left and back when its disk name is wider than the
        /// status-bar strip, so a long file name can be read in full. Stops itself once nothing
        /// overflows; <see cref="DriveLed"/> starts it again.</summary>
        void OnMarqueeTick(object sender, EventArgs e)
        {
            bool anyPending = false;

            for (int d = 0; d < DriveLabelIdle.Length; d++)
            {
                var clip = d == 1 ? DriveBLabelClip : DriveLabelClip;
                var text = d == 1 ? TextBlockDriveBStatus : TextBlockDriveStatus;

                if (!clip.IsVisible || !DriveLabelIdle[d])
                {
                    Canvas.SetLeft(text, 0);
                    continue;
                }

                // A label whose text was just replaced has not been laid out yet: keep the timer
                // alive rather than deciding from a zero width that there is nothing to scroll.
                if (text.Bounds.Width <= 0)
                {
                    anyPending = true;
                    continue;
                }

                // The canvas measures the label unconstrained, so Bounds.Width is the text's own
                // width and the difference is exactly what is hidden past the right edge.
                double overflow = text.Bounds.Width - clip.Bounds.Width;

                if (overflow <= 0.5)
                {
                    Canvas.SetLeft(text, 0);
                    continue;
                }

                anyPending = true;

                double t = (DateTime.Now - DriveLabelSince[d]).TotalSeconds - MarqueeIdleDelay;

                if (t < 0)
                {
                    Canvas.SetLeft(text, 0);
                    continue;
                }

                // One cycle is out, hold, back, hold: a ping-pong rather than a wrap-around, so
                // the drive letter at the head of the label always comes back into view.
                double travel = overflow / MarqueeSpeed;
                double cycle = 2 * travel + 2 * MarqueeEndPause;
                double p = t % cycle;

                double offset =
                    p < travel ? p * MarqueeSpeed :
                    p < travel + MarqueeEndPause ? overflow :
                    p < 2 * travel + MarqueeEndPause ? overflow - (p - travel - MarqueeEndPause) * MarqueeSpeed :
                    0;

                Canvas.SetLeft(text, -offset);
            }

            if (!anyPending)
                MarqueeTimer.Stop();
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
            for (int d = 0; d < TimeLastDriveOn.Length; d++)
            {
                bool idle = (DateTime.Now - TimeLastDriveOn[d]).TotalSeconds > 2;
                // ImagePath, not DisplayName: this runs on the emulation thread once per
                // frame and only needs to notice a change, so it must not allocate a string.
                string disk = d == 1 ? ASEMain.driveB.ImagePath : ASEMain.driveA.ImagePath;

                // Repaint on the on->off edge and also when the disk in an already idle drive
                // changed, since the label names it.
                bool repaint = idle && (idle != DriveLedOffPosted[d] || disk != DriveLedDiskPosted[d]);

                DriveLedOffPosted[d] = idle;
                DriveLedDiskPosted[d] = disk;

                if (repaint)
                {
                    int drive = d;
                    Dispatcher.UIThread.InvokeAsync(() => {
                        ASEMain.MainWindow.DriveLed(drive, false);
                    }, DispatcherPriority.Background);
                }
            }

            // Hard disk light: polled here (once per frame) rather than posted per access,
            // and only pushed to the UI on a change — the disk can be hit thousands of
            // times a second and every access would otherwise queue dispatcher work.
            bool hdOn = ASEMain.HardDiskActive;

            if (hdOn != HDLedIsOn)
            {
                HDLedIsOn = hdOn;
                Dispatcher.UIThread.InvokeAsync(() => {
                    HDLedImage.Source = hdOn ? BitmapLedHDOn : BitmapLedHDOff;
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
                        InsertDisk(0, s, null);
                        break;
                    }
                }
            }

            if(!HasValidFile)
                SetStatusBarText($"❌ Dropped file is an invalid disk image");

            e.Handled = true;
        }

        public void OnOpenImageClick(object sender, RoutedEventArgs e) => OpenImageInto(0);

        public void OnOpenImageBClick(object sender, RoutedEventArgs e) => OpenImageInto(1);

        async void OpenImageInto(int drive)
        {
            ASEMain.CaptureMouse(false);

            var (canceled, selpath) = await Dialogs.OpenFile($"Select disk image file for drive {DriveLetter(drive)}:",
                Config.DialogStartFolder(Config.ConfigOptions.RunninConfig.DiskImagesPath),
                new FileFilter("ST disk images", ["*.st", "*.msa", "*.stx", "*.zip"]));

            if (!canceled && selpath.Count() == 1)
                InsertDisk(drive, selpath.ElementAt(0), null);
        }

        public void OnChangeDiskClick(object sender, RoutedEventArgs e)
        {
            // Another disk of the same zip is still the same game, so it keeps whatever
            // library entry (and MT-32 profile) is already loaded.
            InsertDisk(0, ZipFile[0], MT32.Mt32Profiles.CurrentGame);
        }

        public void OnChangeDiskBClick(object sender, RoutedEventArgs e) => InsertDisk(1, ZipFile[1], null);

        /// <summary>The two floppy drives, by index: 0 = A, 1 = B.</summary>
        static FloppyImage Drive(int drive) => drive == 1 ? ASEMain.driveB : ASEMain.driveA;

        static char DriveLetter(int drive) => drive == 1 ? 'B' : 'A';

        /// <summary>
        /// Puts a disk image in a drive with the emulation thread parked at a frame boundary, so
        /// the image contents and geometry never change under a sector read in flight. Failures of
        /// the load itself (truncated image, corrupt zip) come back in <paramref name="message"/>
        /// instead of escaping the async void handlers that call this.
        /// </summary>
        static bool InsertIntoDrive(int drive, string imageFile, out string message)
        {
            bool inserted = false;
            string loadMessage = "";

            if (!ASEMain.RunWhilePaused(() => inserted = Drive(drive).Insert(imageFile, out loadMessage),
                                        out string error))
                loadMessage = $"Could not read [[red]]{imageFile}[[/red]]: {error}";

            message = loadMessage;
            return inserted;
        }

        /// <summary>
        /// Loads a disk image into a drive and, for drive A, offers the reboot.
        /// <paramref name="libraryGame"/> is the catalogue entry the image came from, or null for
        /// anything opened by hand: it is what decides the game's MT-32 instrument mapping
        /// (see <see cref="MT32.Mt32Profiles"/>).
        /// </summary>
        async void InsertDisk(int drive, string ImageFile, Models.LibraryItem libraryGame)
        {
            DisableEjectMenu(drive);

            bool inserted = InsertIntoDrive(drive, ImageFile, out string message);

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
                        RefreshDiskMenus(drive);
                        return;
                    }

                    if (!InsertIntoDrive(drive, $"{ImageFile}|{selectedFile}", out message))
                    {
                        await Dialogs.MessageBox("Error", message, MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                        return;
                    }

                    ZipFile[drive] = ImageFile;
                    ChangeDiskItem(drive).IsEnabled = true;
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

            // Drive A is where the game goes: it is the one that carries the MT-32 mapping and
            // the only one worth rebooting for. A disk put in B: is data for whatever is already
            // running, so it just goes in — the FDC reports the change on its own
            // (FloppyImage.SignalDiskTransition) and the program picks it up.
            if (drive == 0)
            {
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
            }

            EjectDiskItem(drive).IsEnabled = true;

            SetStatusBarText($"Disk {Path.GetFileName(ImageFile)} inserted in drive {DriveLetter(drive)}");
        }

        public void OnEjecImageClick(object sender, RoutedEventArgs e) => EjectFrom(0);

        public void OnEjecImageBClick(object sender, RoutedEventArgs e) => EjectFrom(1);

        void EjectFrom(int drive)
        {
            // Same rendezvous as inserting: the emulation thread must not be reading the image
            // while it is taken away from under it.
            ASEMain.RunWhilePaused(Drive(drive).Eject, out _);
            ZipFile[drive] = "";

            // Empty drive A: no library game any more, so the MT-32 mapping goes with it. B: never
            // carried one, so ejecting from it leaves the running game's instruments alone.
            if (drive == 0)
                MT32.Mt32Profiles.SetCurrentGame(null);

            DisableEjectMenu(drive);
        }

        MenuItem ChangeDiskItem(int drive) => drive == 1 ? ItemMenuChangeDiskB : ItemMenuChangeDisk;

        MenuItem EjectDiskItem(int drive) => drive == 1 ? ItemMenuEjectDiskB : ItemMenuEjectDisk;

        void DisableEjectMenu(int drive)
        {
            ChangeDiskItem(drive).IsEnabled = false;
            EjectDiskItem(drive).IsEnabled = false;
        }

        /// <summary>Puts a drive's disk menu entries back in sync with what is actually in it.</summary>
        void RefreshDiskMenus(int drive)
        {
            // A disconnected drive B has no entries to enable, whatever it is holding.
            bool present = drive == 0 || Config.ConfigOptions.RunninConfig.DriveBEnabled;

            EjectDiskItem(drive).IsEnabled = present && Drive(drive).HasDisk;
            ChangeDiskItem(drive).IsEnabled = present && !string.IsNullOrEmpty(ZipFile[drive]);
        }

        /// <summary>
        /// Plugs the external drive B: in or out. It is a cable, not a setting that waits for a
        /// reset: the FDC stops answering for B: the moment it goes (see WD1772.DriveSelected),
        /// and TOS' own floppy count follows within a frame (ASEMain.EnforceFloppyDriveCount).
        /// A program already running may still have the old drive map cached, which is what the
        /// reset in the message is for.
        /// </summary>
        public void OnConnectDriveBClick(object sender, RoutedEventArgs e)
        {
            bool connect = !Config.ConfigOptions.RunninConfig.DriveBEnabled;

            // Unplugging takes the disk with it: a drive that is not there cannot be holding one.
            if (!connect)
                EjectFrom(1);

            ApplyDriveBConnection(connect);

            SetStatusBarText(connect
                ? "Drive B: connected, reset ST if a program is already running"
                : "Drive B: disconnected");
        }

        /// <summary>Applies the drive B connection to the config, the File menu and the status
        /// bar light. Called from the constructor too, so a drive left connected in config.json
        /// comes back with its menu entries and its LED already in place.</summary>
        void ApplyDriveBConnection(bool connected)
        {
            Config.ConfigOptions.RunninConfig.DriveBEnabled = connected;

            // A drive that is not there has no zip left to change disks from either
            if (!connected)
                ZipFile[1] = "";

            ItemMenuConnectDriveB.Header = connected ? "Disconnect drive B:" : "Connect drive B:";
            ItemMenuOpenDiskB.IsEnabled = connected;
            RefreshDiskMenus(1);

            DriveBLedImage.IsVisible = connected;
            DriveBLabelClip.IsVisible = connected;
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

            // The snapshot carries the drives: which disks were in them, and whether B: was
            // even plugged in.
            ApplyDriveBConnection(Config.ConfigOptions.RunninConfig.DriveBEnabled);
            RefreshDiskMenus(0);

            SetStatusBarText($"Snapshot {Path.GetFileName(path)} restored");
        }

        private async void OnLibraryClick(object sender, RoutedEventArgs e)
        {
            ASEMain.CaptureMouse(false);

            var library = new LibraryWindow();
            string gameFile = await library.ShowDialog<string>(this);

            if (!string.IsNullOrEmpty(gameFile))
                InsertDisk(0, gameFile, library.SelectedGame);
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
