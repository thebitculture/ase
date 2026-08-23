/*
 *
 * Remembers where the emulator's windows were left, between sessions.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;

namespace ASE
{
    /// <summary>Geometry remembered for one window. Everything is nullable on purpose: a
    /// window that was only ever seen maximized has no normal-state position to restore,
    /// and 0 is a perfectly valid coordinate, so "not recorded" needs its own value.</summary>
    public class WindowLayout
    {
        /// <summary>Top-left corner of the window frame, in physical screen pixels
        /// (<see cref="Window.Position"/>), as it was in its normal state.</summary>
        public int? X { get; set; }
        public int? Y { get; set; }

        /// <summary>Client size in device-independent pixels — what Avalonia's
        /// <see cref="Window.Width"/>/<see cref="Window.Height"/> mean. Only recorded for
        /// windows whose size is worth remembering (i.e. resizable ones).</summary>
        public double? Width { get; set; }
        public double? Height { get; set; }

        public bool Maximized { get; set; }
    }

    /// <summary>
    /// Window positions and sizes, persisted to <c>windows.json</c> next to
    /// <c>config.json</c> (<see cref="Config.GetAppDefaultConfigsFilePath"/>). Deliberately
    /// its own file: this is not configuration — nobody edits it, no command-line option
    /// overrides it, and it must not travel with a config someone copies between machines
    /// or hands over with a bug report.
    ///
    /// Nothing here is worth failing over: an unreadable or unwritable file only means the
    /// windows open where they would have opened without it, so every failure is reported
    /// and swallowed. UI thread only.
    /// </summary>
    public static class WindowLayouts
    {
        const string FileName = "windows.json";

        /// <summary>Height of the strip that has to remain on a screen for the window to be
        /// reachable — its title bar, whether the system draws it or the window does.</summary>
        const int TitleBarHeight = 32;

        static Dictionary<string, WindowLayout> _layouts;

        static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        static string LayoutFile => Path.Combine(Config.GetAppDefaultConfigsFilePath(), FileName);

        static Dictionary<string, WindowLayout> Layouts
        {
            get
            {
                if (_layouts == null)
                    Load();

                return _layouts;
            }
        }

        /// <summary>
        /// Puts <paramref name="window"/> back where it was last left. Call it from the
        /// constructor, before the window is shown, so it opens in place instead of jumping
        /// there afterwards. Does nothing when there is nothing recorded for
        /// <paramref name="key"/> — the window then keeps the startup location its XAML asks
        /// for.
        /// </summary>
        /// <param name="restoreSize">Also restore the size. Only for resizable windows: on a
        /// fixed-size one the recorded size is meaningless.</param>
        public static void Restore(Window window, string key, bool restoreSize = false)
        {
            if (!Layouts.TryGetValue(key, out WindowLayout layout) || layout == null)
                return;

            try
            {
                bool hasSize = restoreSize && layout.Width is >= 1 && layout.Height is >= 1;
                Size size = hasSize ? new Size(layout.Width.Value, layout.Height.Value) : DeclaredSize(window);

                if (layout.X is int x && layout.Y is int y && IsReachable(window, new PixelPoint(x, y), size))
                {
                    if (hasSize)
                    {
                        window.Width = layout.Width.Value;
                        window.Height = layout.Height.Value;
                    }

                    // Manual is what keeps WindowStartupLocation (CenterScreen, CenterOwner…)
                    // from placing the window somewhere else right after this.
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Position = new PixelPoint(x, y);
                }

                if (layout.Maximized)
                    window.WindowState = WindowState.Maximized;
            }
            catch (Exception ex)
            {
                // Placing a window is never a reason not to open it.
                ColoredConsole.WriteLine($"Could not restore the [[cyan]]{key}[[/cyan]] position: [[red]]{ex.Message}[[/red]]");
            }
        }

        /// <summary>
        /// Records where <paramref name="window"/> is now and writes the file. Call it from
        /// <c>OnClosing</c>: by <c>OnClosed</c> the window is already torn down and its
        /// position no longer means anything.
        /// </summary>
        public static void Remember(Window window, string key, bool rememberSize = false)
        {
            if (!Layouts.TryGetValue(key, out WindowLayout layout) || layout == null)
                layout = new WindowLayout();

            try
            {
                // A maximized (or minimized) window reports the geometry of *that* state,
                // which is not what restoring it wants — un-maximizing has to give the user
                // their window back. So only a normal-state window updates the coordinates;
                // the rest just note how they should come up.
                if (window.WindowState == WindowState.Normal)
                {
                    PixelPoint position = window.Position;
                    layout.X = position.X;
                    layout.Y = position.Y;

                    if (rememberSize)
                    {
                        layout.Width = window.ClientSize.Width;
                        layout.Height = window.ClientSize.Height;
                    }
                }

                // A window closed in full screen says nothing about whether it was maximized
                // before, so its recorded flag is left as it was — full screen is remembered in
                // the configuration (Config.FullScreen), not here.
                if (window.WindowState != WindowState.FullScreen)
                    layout.Maximized = window.WindowState == WindowState.Maximized;
            }
            catch (Exception ex)
            {
                ColoredConsole.WriteLine($"Could not read the [[cyan]]{key}[[/cyan]] position: [[red]]{ex.Message}[[/red]]");
                return;
            }

            Layouts[key] = layout;
            Save();
        }

        /// <summary>
        /// Whether a window placed at <paramref name="position"/> would still be usable.
        /// What is tested is the title-bar strip against the screens' working areas, not the
        /// whole window: a monitor that has been unplugged (or a screen arrangement that
        /// changed since) would otherwise put the window somewhere the user cannot even
        /// grab it to drag it back.
        /// </summary>
        /// <summary>The window's size as far as it is known before it is shown: what its XAML
        /// asks for, or the current client size for a window that sizes itself. Only used to
        /// judge how much of the window would land on a screen.</summary>
        static Size DeclaredSize(Window window)
            => new(double.IsNaN(window.Width) ? window.ClientSize.Width : window.Width,
                   double.IsNaN(window.Height) ? window.ClientSize.Height : window.Height);

        static bool IsReachable(Window window, PixelPoint position, Size clientSize)
        {
            var screens = window.Screens;

            // No screen information: trust what was saved rather than second-guess it.
            if (screens == null || screens.ScreenCount == 0)
                return true;

            foreach (var screen in screens.All)
            {
                // The size was saved in device-independent pixels and each screen may scale
                // differently, so it is converted with the scaling of the screen being tested.
                int width = Math.Max(1, (int)(clientSize.Width * screen.Scaling));
                var titleBar = new PixelRect(position, new PixelSize(width, TitleBarHeight));

                if (screen.WorkingArea.Intersects(titleBar))
                    return true;
            }

            return false;
        }

        static void Load()
        {
            _layouts = new Dictionary<string, WindowLayout>(StringComparer.OrdinalIgnoreCase);

            try
            {
                string path = LayoutFile;

                if (!File.Exists(path))
                    return;

                var stored = JsonSerializer.Deserialize<Dictionary<string, WindowLayout>>(
                    File.ReadAllText(path), SerializerOptions);

                if (stored == null)
                    return;

                foreach (var entry in stored)
                    _layouts[entry.Key] = entry.Value;
            }
            catch (Exception ex)
            {
                ColoredConsole.WriteLine($"Could not read [[cyan]]{FileName}[[/cyan]]: [[red]]{ex.Message}[[/red]]. Windows will open at their default position.");
            }
        }

        static void Save()
        {
            try
            {
                File.WriteAllText(LayoutFile, JsonSerializer.Serialize(_layouts, SerializerOptions));
            }
            catch (Exception ex)
            {
                ColoredConsole.WriteLine($"Could not write [[cyan]]{FileName}[[/cyan]]: [[red]]{ex.Message}[[/red]]. Window positions will not be remembered.");
            }
        }
    }
}
