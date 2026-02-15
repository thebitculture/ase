using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
        
        public MainWindow()
        {
            InitializeComponent();

            BitmapLedDriveOn = new Bitmap(AssetLoader.Open(new Uri("avares://ASE/Assets/drive_led_on.png")));
            BitmapLedDriveOff = new Bitmap(AssetLoader.Open(new Uri("avares://ASE/Assets/drive_led_off.png")));
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            // Maintains the aspect ratio of the emulator when resizing the window
            GameAspectRatio = (float)ASEMain.ScreenWidth / ((float)ASEMain.ScreenHeight + ((float)MainMenu.Height + (float)BottomStatusBar.Bounds.Height));
            this.GetObservable(Window.ClientSizeProperty).Subscribe(OnClientSizeChanged);

            // El handle nativo, es lo que necesita SDL para acoplarse a la ventana.
            var platformHandle = this.TryGetPlatformHandle();

            if (platformHandle != null)
            {
                // platformHandle.Handle es el puntero (IntPtr) que necesitamos
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

        private void GL_OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            // SDL can't capture mouse button presses directly from Avalonia, so
            // I have to do it manually here and forward it to the ACIA mouse handling.

            var ctrl = (Control)sender!;
            var p = e.GetCurrentPoint(ctrl);

            // Transmit the button press to the ACIA mouse handling
            if (p.Properties.IsLeftButtonPressed && ASEMain.IsMouseCaptured)
            {
                ACIA._mouseButtons |= 0x02;
                ACIA.SendMousePacket(0, 0);
                e.Handled = true;
            }
        }

        private void GL_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            // SDL can't capture mouse button releases directly from Avalonia, so
            // I have to do it manually here and forward it to the ACIA mouse handling.
            // I’m sure there are better ways to do this, but for now it does what
            // I need it to do and that’s enough for me.

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

            // Si la relación de aspecto difiere del objetivo (con un margen de tolerancia)
            if (Math.Abs(currentRatio - GameAspectRatio) > 0.05)
            {
                _isResizing = true;

                double targetGlHeight = newSize.Width / GameAspectRatio;
                double targetClientHeight = targetGlHeight + barsHeight;

                // Dispatcher para cambiar el tamaño fuera del ciclo actual de layout
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

        public void DriveLed(bool On)
        {
            if (On)
            {
                TimeLastDriveOn = DateTime.Now;
                DriveLedImage.Source = BitmapLedDriveOn;
            }
            else
                DriveLedImage.Source = BitmapLedDriveOff;
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

        public void OnOpenImageClick(object sender, RoutedEventArgs e)
        {
            ASEMain.CaptureMouse(false);

            var (canceled, selpath) = TinyDialogs.OpenFileDialog("Select disk image file", "", false, new FileFilter("ST disk images", ["*.st", "*.msa"]));

            if (!canceled && selpath.Count() == 1)
            {
                string message;
                bool inserted = ASEMain.driveA.Insert(selpath.ElementAt(0), out message);

                if (!inserted)
                    TinyDialogs.MessageBox("Error", message, MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                else
                    ColoredConsole.WriteLine(message);

                var response = TinyDialogs.MessageBox("Disk inserted", "Reboot?", MessageBoxDialogType.YesNo, MessageBoxIconType.Question, MessageBoxButton.Yes);

                if (response == MessageBoxButton.Yes)
                    ASEMain.HardReset();

                ItemMenuEjecDisk.IsEnabled = true;
                
                SetStatusBarText( $"Disk {Path.GetFileName(( selpath.ElementAt(0)))} inserted in drive A");
            }
        }

        public void OnEjecImageClick(object sender, RoutedEventArgs e)
        {
            ASEMain.driveA.Eject();
            ItemMenuEjecDisk.IsEnabled = false;
        }

        public void OnResetClick(object sender, RoutedEventArgs e)
        {
            var response = TinyDialogs.MessageBox("Reset ST", "Are you sure?", MessageBoxDialogType.YesNo, MessageBoxIconType.Question, MessageBoxButton.Yes);

            if (response == MessageBoxButton.Yes)
                CPU.InitCpu();
        }

        public void OnConfigurationClick(object sender, RoutedEventArgs e)
        {
            var configWindow = new ConfigurationWindow();
            configWindow.ShowDialog(this);
        }

        private void OnAboutClick(object? sender, RoutedEventArgs e)
        {
            var aboutWindow = new AboutWindow();
            aboutWindow.ShowDialog(this);
        }
    }
}
