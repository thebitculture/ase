using ASE.Models;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LibVLCSharp.Shared;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using TinyDialogsNet;

namespace ASE;

public partial class LibraryWindow : Window
{
    public LibraryCollection libraryCollection { get; set; } = new LibraryCollection();

    /// <summary>Entries currently shown in the grid (search-filtered view over the full library).
    /// Closes with the full path of the game to insert as the dialog result, or null.</summary>
    public ObservableCollection<GameEntry> Games { get; } = new();

    /// <summary>Catalogue entry the dialog closed with, beside the disk-image path it
    /// returns: MainWindow needs the entry itself to apply the game's MT-32 instrument
    /// profile (see <see cref="MT32.Mt32Profiles"/>). Null when nothing was launched.</summary>
    public LibraryItem SelectedGame { get; private set; }

    readonly List<GameEntry> _allGames = new();
    readonly CancellationTokenSource _coversCts = new();
    readonly string _libraryPath;

    /// <summary>Tile currently under the mouse pointer; Enter launches it over the selection.</summary>
    GameEntry _hovered;

    // Detail overlay state. LibVLC is created lazily on the first video and reused;
    // it stays null when libvlc isn't available (the panel then shows the screenshot).
    LibVLC _libVlc;
    MediaPlayer _mediaPlayer;
    GameEntry _detailGame;
    bool _detailOpen;

    public LibraryWindow()
    {
        InitializeComponent();

        // Park the emulator (CPU + audio) while browsing: the PSG output would
        // otherwise mix with the game videos. Resumed in the Closed handler.
        ASEMain.EnterUiPause();

        // The ListBox itself handles Enter (confirms the focused item's selection) and
        // marks it Handled before XAML-subscribed handlers run, so the Enter case never
        // fired when subscribed via KeyDown="..."; listen with handledEventsToo instead
        GamesList.AddHandler(KeyDownEvent, OnGamesKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);

        _libraryPath = Config.ConfigOptions.RunninConfig.LibraryPath ?? "";
        string libraryJson = Path.Combine(_libraryPath, "Library.json");

        if (_libraryPath.Length > 0 && File.Exists(libraryJson))
            libraryCollection = JsonSerializer.Deserialize<LibraryCollection>(File.ReadAllText(libraryJson));

        if (libraryCollection.Collection != null)
        {
            string mediaDir = Path.Combine(_libraryPath, "Media");

            _allGames.AddRange(libraryCollection.Collection
                .Select(item => new GameEntry(item, mediaDir))
                .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase));
        }

        DataContext = this;

        ApplyFilter();

        // Covers are decoded off the UI thread and pushed into the tiles as they arrive
        Task.Run(() => LoadCovers(_coversCts.Token));

        Closed += (s, e) =>
        {
            ASEMain.ExitUiPause();
            _coversCts.Cancel();

            // Unhook the video host before disposing: Avalonia destroys the native
            // control deferred, after this handler, and it must not touch a disposed player
            var player = _mediaPlayer;
            _mediaPlayer = null;
            DetailVideo.MediaPlayer = null;
            player?.Dispose();
            _libVlc?.Dispose();
        };
        Opened += (s, e) => Dispatcher.UIThread.Post(FocusSelected, DispatcherPriority.Loaded);
    }

    void LoadCovers(CancellationToken ct)
    {
        foreach (var game in _allGames)
        {
            if (ct.IsCancellationRequested)
                return;

            if (game.CoverPath == null)
                continue;

            try
            {
                // 240 px ≈ the 160 px tile content at 150% scaling; keeps memory at
                // ~250 KB per cover, which is what dominates with big libraries
                Bitmap cover;
                using (var stream = File.OpenRead(game.CoverPath))
                    cover = Bitmap.DecodeToWidth(stream, 240);

                Dispatcher.UIThread.Post(() => game.Cover = cover);
            }
            catch
            {
                // Unreadable/corrupt image: the tile keeps its placeholder
            }
        }
    }

    void ApplyFilter()
    {
        string filter = SearchBox.Text?.Trim() ?? "";

        var filtered = filter.Length == 0
            ? _allGames
            : _allGames.Where(g => g.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        var previous = GamesList.SelectedItem as GameEntry;

        _hovered = null;
        Games.Clear();
        foreach (var game in filtered)
            Games.Add(game);

        if (Games.Count > 0)
            GamesList.SelectedIndex = previous != null && Games.Contains(previous) ? Games.IndexOf(previous) : 0;

        EmptyMessage.Text = _allGames.Count == 0
            ? "🤷‍♂️ The library is empty. Place some goodies in your library folder and scan with the scraper."
            : "❌ No games match the filter.";
        EmptyMessage.IsVisible = Games.Count == 0;
    }

    void FocusSelected()
    {
        if (GamesList.SelectedIndex < 0 && Games.Count > 0)
            GamesList.SelectedIndex = 0;

        if (GamesList.SelectedIndex >= 0 && GamesList.ContainerFromIndex(GamesList.SelectedIndex) is Control container)
            container.Focus();
        else
            GamesList.Focus();
    }

    void LoadSelected() => LoadGame(GamesList.SelectedItem as GameEntry);

    async void LoadGame(GameEntry game)
    {
        if (game == null || string.IsNullOrEmpty(game.Item.Filename))
            return;

        string path = Path.Combine(_libraryPath, game.Item.Filename);

        if (!File.Exists(path))
        {
            await Dialogs.MessageBox("Error", $"Game file not found: {path}",
                MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
            return;
        }

        SelectedGame = game.Item;
        Close(path);
    }

    /// <summary>Items per row in the wrap grid, derived from the realized tile and panel widths.</summary>
    int ColumnsPerRow()
    {
        if (GamesList.ContainerFromIndex(0) is not Control tile || tile.Bounds.Width <= 0)
            return 1;

        double tileWidth = tile.Bounds.Width + tile.Margin.Left + tile.Margin.Right;
        double panelWidth = GamesList.ItemsPanelRoot?.Bounds.Width ?? GamesList.Bounds.Width;

        return Math.Max(1, (int)((panelWidth + 0.5) / tileWidth));
    }

    // The horizontal WrapPanel only navigates Left/Right on its own; Up/Down jump a whole row here
    void MoveSelectionVertical(int direction)
    {
        if (Games.Count == 0)
            return;

        int index = GamesList.SelectedIndex;

        if (index < 0)
        {
            GamesList.SelectedIndex = 0;
            FocusSelected();
            return;
        }

        int columns = ColumnsPerRow();
        int target = index + direction * columns;

        if (direction > 0 && target >= Games.Count)
        {
            // From the last full row, land on the last game; from the last row, stay put
            if (index / columns == (Games.Count - 1) / columns)
                return;
            target = Games.Count - 1;
        }
        else if (target < 0)
            return;

        GamesList.SelectedIndex = target;
        FocusSelected();
    }

    void OnDetailsTapped(object sender, TappedEventArgs e)
    {
        // Keep the tap from bubbling into OnGamesTapped, which would launch the game
        e.Handled = true;

        if ((sender as Control)?.DataContext is GameEntry game)
            OpenDetail(game);
    }

    /// <summary>Context click on a tile: right button, or Ctrl+click on macOS — the
    /// one-button/trackpad secondary click, which Avalonia does not translate into a
    /// right click (AvaloniaUI/Avalonia#5575), so the menu is opened from code there.</summary>
    void OnTilePointerPressed(object sender, PointerPressedEventArgs e)
    {
        if (sender is not Border tile || tile.DataContext is not GameEntry game)
            return;

        var props = e.GetCurrentPoint(tile).Properties;
        bool ctrlClick = OperatingSystem.IsMacOS() && props.IsLeftButtonPressed &&
                         e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (!props.IsRightButtonPressed && !ctrlClick)
            return;

        // The menu must act on the tile under the cursor, not on a stale selection
        GamesList.SelectedItem = game;

        if (ctrlClick)
        {
            // Swallow the press so its Tapped never reaches OnGamesTapped (launch);
            // the IsOpen guard keeps this correct if Avalonia ever translates the click
            e.Handled = true;
            if (tile.ContextMenu is { IsOpen: false } menu)
                menu.Open(tile);
        }
    }

    void OnContextPlayClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is GameEntry game)
            LoadGame(game);
    }

    void OnContextDetailsClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is GameEntry game)
            OpenDetail(game);
    }

    void OnDetailBackdropTapped(object sender, TappedEventArgs e) => CloseDetail();

    void OnDetailCloseClick(object sender, RoutedEventArgs e) => CloseDetail();

    // The Play button in the detail overlay launches the game shown, the same way
    // the context-menu "Play" and Enter do (LoadGame closes the window with its path)
    void OnDetailPlayClick(object sender, RoutedEventArgs e) => LoadGame(_detailGame);

    void OpenDetail(GameEntry game)
    {
        _detailGame = game;
        _detailOpen = true;

        DetailTitle.Text = game.Name;
        DetailRelease.Text = game.Item.GameMenuGroupRelease;
        DetailRelease.IsVisible = !string.IsNullOrEmpty(game.Item.GameMenuGroupRelease);
        DetailSynopsis.Text = game.SynopsisText ?? "No description available.";

        // Box art fills the media area until the screenshot decodes
        DetailFallback.Source = game.Cover;

        // Title screen as the card backdrop; without one the card stays solid
        DetailBackdrop.Source = null;
        if (game.TitlePath != null)
            Task.Run(() =>
            {
                try
                {
                    Bitmap backdrop;
                    using (var stream = File.OpenRead(game.TitlePath))
                        backdrop = new Bitmap(stream);

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ReferenceEquals(_detailGame, game))
                            DetailBackdrop.Source = backdrop;
                    });
                }
                catch
                {
                    // Unreadable title screen: the card keeps its solid colour
                }
            });

        if (game.ScreenshotPath != null)
            Task.Run(() =>
            {
                try
                {
                    Bitmap shot;
                    using (var stream = File.OpenRead(game.ScreenshotPath))
                        shot = Bitmap.DecodeToWidth(stream, 672);

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ReferenceEquals(_detailGame, game))
                            DetailFallback.Source = shot;
                    });
                }
                catch
                {
                    // Unreadable screenshot: the box art stays
                }
            });

        DetailOverlay.IsVisible = true;
        // The class change must land after the overlay is attached or the fade won't run
        Dispatcher.UIThread.Post(() => DetailOverlay.Classes.Add("open"), DispatcherPriority.Loaded);

        PlayVideo(game.VideoPath);
    }

    void CloseDetail()
    {
        if (!_detailOpen)
            return;

        _detailOpen = false;
        _detailGame = null;

        _mediaPlayer?.Stop();
        DetailVideo.IsVisible = false;

        DetailOverlay.Classes.Remove("open");
        // Hide only after the fade-out finishes so the transition stays visible
        DispatcherTimer.RunOnce(() =>
        {
            if (!_detailOpen)
                DetailOverlay.IsVisible = false;
        }, TimeSpan.FromMilliseconds(200));

        FocusSelected();
    }

    void PlayVideo(string path)
    {
        if (path == null || !EnsureVlc())
            return;

        // The native VLC surface is positioned when shown and does not track render
        // transforms, so wait for the card's slide-in (220 ms) to settle before showing
        // it; otherwise the video stays offset by the transform at creation time
        var game = _detailGame;
        DispatcherTimer.RunOnce(() =>
        {
            if (!_detailOpen || !ReferenceEquals(_detailGame, game))
                return;

            DetailVideo.IsVisible = true;

            using var media = new Media(_libVlc, path);
            media.AddOption("input-repeat=65535"); // loop the preview
            _mediaPlayer.Play(media);
        }, TimeSpan.FromMilliseconds(320));
    }

    [System.Runtime.InteropServices.DllImport("libc")]
    static extern int setenv(string name, string value, int overwrite);

    bool EnsureVlc()
    {
        if (_mediaPlayer != null)
            return true;

        try
        {
            // On macOS there is no arm64 libvlc nuget: load it from the installed VLC.app.
            // libvlccore finds its plugins through getenv("VLC_PLUGIN_PATH"), and on Unix
            // Environment.SetEnvironmentVariable never reaches the native environment, so
            // the variable has to be set with libc's setenv.
            const string vlcAppLib = "/Applications/VLC.app/Contents/MacOS/lib";
            if (OperatingSystem.IsMacOS() && Directory.Exists(vlcAppLib))
            {
                setenv("VLC_PLUGIN_PATH", "/Applications/VLC.app/Contents/MacOS/plugins", 1);
                Core.Initialize(vlcAppLib);
            }
            else if (OperatingSystem.IsWindows() && FindWindowsVlcPath() is string vlcDir)
            {
                // libvlc isn't bundled with the emulator (it's tens of MB); reuse the
                // user's own VLC install instead, same idea as the macOS branch above.
                Core.Initialize(vlcDir);
            }
            else
            {
                Core.Initialize();
            }
            _libVlc = new LibVLC();
            _mediaPlayer = new MediaPlayer(_libVlc);
            DetailVideo.MediaPlayer = _mediaPlayer;
            return true;
        }
        catch (Exception ex)
        {
            ColoredConsole.WriteLine($"[[yellow]]LibVLC not available, game videos disabled: {ex.Message}[[/yellow]]");
            _libVlc?.Dispose();
            _libVlc = null;
            return false;
        }
    }

    /// <summary>
    /// Locates a Windows VLC install containing libvlc.dll: the user-configured directory
    /// (Library configuration window) if set and valid, otherwise the default 64/32-bit
    /// "Program Files" install locations. Returns null when none is found, in which case
    /// Core.Initialize() falls back to its own search (e.g. a bundled libvlc next to the
    /// executable, if one is ever shipped).
    /// </summary>
    static string FindWindowsVlcPath()
    {
        string configured = Config.ConfigOptions.RunninConfig.VlcInstallPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "libvlc.dll")))
            return configured;

        string[] defaultDirs =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VideoLAN", "VLC"),
        };

        foreach (var dir in defaultDirs)
        {
            if (File.Exists(Path.Combine(dir, "libvlc.dll")))
                return dir;
        }

        return null;
    }

    void OnGamesKeyDown(object sender, KeyEventArgs e)
    {
        // The detail overlay is modal: don't move the selection or launch behind it.
        // Its close keys are handled here (not only in OnWindowKeyDown) because the
        // focused ListBoxItem marks Enter/Space handled before they reach the window.
        if (_detailOpen)
        {
            if (e.Key is Key.Escape or Key.Enter or Key.Space)
            {
                CloseDetail();
                e.Handled = true;
            }
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                // The game under the mouse wins over the keyboard selection
                LoadGame(_hovered ?? GamesList.SelectedItem as GameEntry);
                e.Handled = true;
                break;

            case Key.Space:
                if ((_hovered ?? GamesList.SelectedItem as GameEntry) is GameEntry game)
                    OpenDetail(game);
                e.Handled = true;
                break;

            case Key.Up:
            case Key.Down:
                MoveSelectionVertical(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                break;
        }
    }

    void OnGamesTapped(object sender, TappedEventArgs e)
    {
        // A Ctrl+click already opened the tile's context menu in OnTilePointerPressed
        // (macOS secondary click): it must not double as a launch
        if (OperatingSystem.IsMacOS() && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;

        // Only when the tap landed on a tile, not on empty panel space or the scrollbar
        if (e.Source is Avalonia.Visual visual && visual.FindAncestorOfType<ListBoxItem>(true) != null)
            LoadSelected();
    }

    void OnTilePointerEntered(object sender, PointerEventArgs e) =>
        _hovered = (sender as Control)?.DataContext as GameEntry;

    void OnTilePointerExited(object sender, PointerEventArgs e)
    {
        if (ReferenceEquals(_hovered, (sender as Control)?.DataContext))
            _hovered = null;
    }

    void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (_detailOpen)
        {
            if (e.Key is Key.Escape or Key.Enter or Key.Space)
            {
                CloseDetail();
                e.Handled = true;
            }
            return;
        }

        // Enter launches the hovered game even when the list doesn't have focus
        // (e.g. while typing in the search box); with focus on the list,
        // OnGamesKeyDown already handled it and the event never reaches here
        if (e.Key == Key.Enter && _hovered != null)
        {
            LoadGame(_hovered);
            e.Handled = true;
            return;
        }

        // Space opens the details like in OnGamesKeyDown, but never while typing in
        // the search box, where it must keep inserting spaces
        if (e.Key == Key.Space && !SearchBox.IsFocused)
        {
            if ((_hovered ?? GamesList.SelectedItem as GameEntry) is GameEntry game)
            {
                OpenDetail(game);
                e.Handled = true;
            }
            return;
        }

        if (e.Key != Key.Escape)
            return;

        if (SearchBox.IsFocused && !string.IsNullOrEmpty(SearchBox.Text))
            SearchBox.Text = "";
        else
            Close(null);

        e.Handled = true;
    }

    /// <summary>Per-game view model for the grid: display name, box art and tooltip details.</summary>
    public class GameEntry : INotifyPropertyChanged
    {
        static readonly string[] RegionPreference = { "ss", "eu", "wor", "us" };

        Bitmap _cover;

        public LibraryItem Item { get; }
        public string Name { get; }
        public string CoverPath { get; }
        public string Details { get; }
        public string SynopsisText { get; }
        public string VideoPath { get; }
        public string ScreenshotPath { get; }
        public string TitlePath { get; }

        public Bitmap Cover
        {
            get => _cover;
            set
            {
                _cover = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Cover)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCover)));
            }
        }

        public bool HasCover => _cover != null;

        public event PropertyChangedEventHandler PropertyChanged;

        public GameEntry(LibraryItem item, string mediaDir)
        {
            Item = item;
            Name = PickText(item.Name) ?? item.Filename ?? item.Id;
            CoverPath = FindCover(item, mediaDir);
            Details = BuildDetails(item);
            SynopsisText = PickText(item.Synopsis);
            // The scraper saves the video-normalized preview as <Id>.<format>
            VideoPath = FindMedia(item, mediaDir, "{0}.*");
            ScreenshotPath = FindMedia(item, mediaDir, "Screenshot-{0}.*")
                          ?? FindMedia(item, mediaDir, "TitleScreen-{0}.*");
            TitlePath = FindMedia(item, mediaDir, "TitleScreen-{0}.*");
        }

        /// <summary>First media file matching the pattern ({0} = game id), or null.</summary>
        static string FindMedia(LibraryItem item, string mediaDir, string pattern)
        {
            if (string.IsNullOrEmpty(item.Id) || !Directory.Exists(mediaDir))
                return null;

            return Directory.EnumerateFiles(mediaDir, string.Format(pattern, item.Id)).FirstOrDefault();
        }

        static string PickText(List<LibraryItem.RegionText> texts)
        {
            if (texts == null)
                return null;

            foreach (var region in RegionPreference)
            {
                var match = texts.Find(t => t.Region == region && !string.IsNullOrWhiteSpace(t.Text));
                if (match != null)
                    return Decode(match.Text);
            }

            return Decode(texts.Find(t => !string.IsNullOrWhiteSpace(t.Text))?.Text);
        }

        // ScreenScraper texts come HTML-encoded (&quot;, &amp;…)
        static string Decode(string text) =>
            text == null ? null : System.Net.WebUtility.HtmlDecode(text);

        static string FindCover(LibraryItem item, string mediaDir)
        {
            if (string.IsNullOrEmpty(item.Id) || !Directory.Exists(mediaDir))
                return null;

            // The scraper saves Box-<Id>.png unless ScreenScraper reported another format
            string png = Path.Combine(mediaDir, $"Box-{item.Id}.png");
            if (File.Exists(png))
                return png;

            return Directory.EnumerateFiles(mediaDir, $"Box-{item.Id}.*").FirstOrDefault();
        }

        static string BuildDetails(LibraryItem item)
        {
            var lines = new List<string>();

            if (!string.IsNullOrWhiteSpace(item.Developer)) lines.Add($"Developer: {item.Developer}");
            if (!string.IsNullOrWhiteSpace(item.Publisher)) lines.Add($"Publisher: {item.Publisher}");

            string date = PickText(item.Date);
            if (date != null) lines.Add($"Released: {date}");

            string genre = PickText(item.Genre);
            if (genre != null) lines.Add($"Genre: {genre}");

            if (!string.IsNullOrWhiteSpace(item.Filename)) lines.Add($"File: {item.Filename}");

            return lines.Count > 0 ? string.Join("\n", lines) : null;
        }
    }
}
