using ASE.Models;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using TinyDialogsNet;
using System.Text.Json;

namespace ASE;

public partial class ScraperWindow : Window
{
    int SSAtariSTId;
    string logFile = "";

    LibraryCollection libraryCollection = new LibraryCollection();
    ScreenScraperClient screenScraperClient = new ScreenScraperClient();
    readonly CancellationTokenSource _scanCts = new CancellationTokenSource();

    /// <summary>Set when a ScreenScraper call reports the daily quota is exhausted, so the
    /// scan is aborted (via _scanCts).</summary>
    bool _quotaExceeded = false;

    public ScraperWindow()
    {
        InitializeComponent();
        ButtonCancelScraper.Click += (_, _) => _scanCts.Cancel();
        ButtonOk.Click += (_, _) => Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Stop an in-flight scan when the window is closed.
        _scanCts.Cancel();
        base.OnClosed(e);
        ASEMain.ExitUiPause();
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (Design.IsDesignMode)
            return;

        // The emulator parks (and stops receiving host input) while scraping
        ASEMain.EnterUiPause();

        if (string.IsNullOrEmpty(Config.ConfigOptions.RunninConfig.ScreenScraperUser) || string.IsNullOrEmpty(Config.ConfigOptions.RunninConfig.LibraryPath) || !Directory.Exists(Config.ConfigOptions.RunninConfig.LibraryPath))
        {
            TinyDialogs.MessageBox("Error", $"You must configure your library before you can update the metadata.", MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
            ButtonsScraper(false);
            return;
        }

        bool _error = false;

        ButtonsScraper(true);
        TextFilename.Text = "Initializing...";

        var gmidentifier = GameMenuIdentifier.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "GameMenus.json"));

        if (File.Exists(Path.Combine(Config.ConfigOptions.RunninConfig.LibraryPath, "Library.json")))
        {
            string _libraryJson = File.ReadAllText(Path.Combine(Config.ConfigOptions.RunninConfig.LibraryPath, "Library.json"));
            libraryCollection = JsonSerializer.Deserialize<LibraryCollection>(_libraryJson);
        }

        if (libraryCollection.Collection == null)
            libraryCollection.Collection = new List<LibraryItem>();

        // Persists whatever has been scraped so far; called both on a normal finish and when
        // the scan is stopped early (cancelled by the user or out of credits), so partial
        // progress is never lost.
        void SaveLibrary()
        {
            libraryCollection.LastUpdate = DateTime.Now;
            libraryCollection.ASEVersion = Config.Version;

            string json = JsonSerializer.Serialize(libraryCollection, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(Config.ConfigOptions.RunninConfig.LibraryPath, "Library.json"), json);
        }

        try
        {
            if (!Directory.Exists(Path.Combine(Config.ConfigOptions.RunninConfig.LibraryPath, "Media")))
                Directory.CreateDirectory(Path.Combine(Config.ConfigOptions.RunninConfig.LibraryPath, "Media"));

            logFile = Path.Combine(Config.ConfigOptions.RunninConfig.LibraryPath, $"Scraper {DateTime.Today.ToString("yyyyMMdd")}.log");

            File.AppendAllText(logFile, $"*** Scraper starts at {DateTime.Now} ***" + Environment.NewLine);

            // Verify credentials and remaining daily credits before touching any file.
            TextFilename.Text = "Checking ScreenScraper account...";
            var userInfo = await screenScraperClient.GetUserInfosAsync(_scanCts.Token);

            if (!userInfo.Success)
            {
                string accountMessage = userInfo.QuotaExceeded
                    ? "There are no download credits remaining on your ScreenScraper account today. Please try again later."
                    : "Could not verify your ScreenScraper credentials. Please check your username and password in the configuration.";

                File.AppendAllText(logFile, $"Account check failed: {(string.IsNullOrEmpty(userInfo.Error) ? accountMessage : userInfo.Error)}" + Environment.NewLine);
                TinyDialogs.MessageBox("Error", accountMessage, MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                ButtonsScraper(false);
                return;
            }

            // maxrequestsperday == 0 means "unlimited" for this account level.
            if (int.TryParse(userInfo.Data.MaxRequestsPerDay, out int maxRequestsPerDay) && maxRequestsPerDay > 0 &&
                int.TryParse(userInfo.Data.RequestsToday, out int requestsToday) && requestsToday >= maxRequestsPerDay)
            {
                File.AppendAllText(logFile, $"Account check failed: no download credits remaining ({requestsToday}/{maxRequestsPerDay})" + Environment.NewLine);
                TinyDialogs.MessageBox("Error", "There are no download credits remaining on your ScreenScraper account today. Please try again later.", MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                ButtonsScraper(false);
                return;
            }

            var result = await screenScraperClient.GetSystemsAsync(_scanCts.Token);

            if (result.Data != null && result.Data.Count > 0)
            {
                // Search for Atari ST Id
                var atariItem = result.Data.Find(x => x.Noms?.Eu == "Atari ST");

                if (atariItem != null)
                {
                    SSAtariSTId = atariItem.Id;
                    string[] _validExtensions = { ".zip", ".st", ".stx", ".msa" };

                    // Scan directory
                    string[] _files = Directory.GetFiles(Config.ConfigOptions.RunninConfig.LibraryPath);
                    List<string> _validfiles = new List<string>();

                    foreach (var file in _files)
                    {
                        string extension = Path.GetExtension(file);

                        if (_validExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                            _validfiles.Add(file);
                    }

                    if (_validfiles.Count > 0)
                    {
                        for (int i = 0; i < _validfiles.Count; i++)
                        {
                            _scanCts.Token.ThrowIfCancellationRequested();

                            ProgressScraper.Value = i * 100 / _validfiles.Count;
                            TextFilename.Text = _validfiles[i];

                            // File already scrapered
                            if (libraryCollection.Collection.Find(x => x.Filename == Path.GetFileName(_validfiles[i])) != null)
                            {
                                File.AppendAllText(logFile, $"{Path.GetFileName(_validfiles[i])} -> File already scrapered" + Environment.NewLine);
                                continue;
                            }

                            long filelenght = new FileInfo(_validfiles[i]).Length;
                            string FileSHA = FileUtils.CalculateSHA1(_validfiles[i]);

                            // Search for gamemenu
                            var gmresult = gmidentifier.IdentifyAll(Path.GetFileNameWithoutExtension(_validfiles[i]));

                            if (gmresult.Count > 0)
                            {
                                foreach (var r in gmresult)
                                {
                                    _scanCts.Token.ThrowIfCancellationRequested();

                                    LibraryItem item = await ScrapeTitle(r.GameName, filelenght, FileSHA, Path.GetFileNameWithoutExtension(_validfiles[i]));

                                    if (item != null)
                                    {
                                        item.GameMenuNumber = r.MatchedCode.Substring(2);
                                        item.GameMenuGroupName = r.MatchedGroup;
                                        item.GameMenuId = r.MatchedCode;
                                        item.Filename = Path.GetFileName(_validfiles[i]);

                                        libraryCollection.Collection.Add(item);
                                    }
                                }
                            }
                            else 
                            {
                                // Not gamemenu

                                LibraryItem item = await ScrapeTitle(Path.GetFileName(_validfiles[i]), filelenght, FileSHA);

                                if(item!=null)
                                    libraryCollection.Collection.Add(item);
                            }
                        }

                        // Save library list
                        SaveLibrary();

                        ProgressScraper.Value = 100;
                        TextFilename.Text = "Scan completed.";
                        File.AppendAllText(logFile, $"--- Scan completed at {DateTime.Now} ---" + Environment.NewLine);
                        ButtonsScraper(false);
                    }
                    else
                    {
                        TinyDialogs.MessageBox("Error", $"No valid disk images were found in the selected directory. Please verify that the configuration is correct.", MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                        ButtonsScraper(false);
                        return;
                    }

                }
                else
                    _error = true;
            }
            else
                _error = true;

            if (_error)
            {
                string message = result.QuotaExceeded
                    ? "There are no download credits remaining on your ScreenScraper account today. Please try again later."
                    : string.IsNullOrEmpty(result.Error)
                        ? "An error occurred while retrieving the Atari ST system from ScreenScraper."
                        : $"An error occurred while retrieving the Atari ST system from ScreenScraper: {result.Error}";

                TinyDialogs.MessageBox("Error", message, MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
                ButtonsScraper(false);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Whatever was already scraped this run must not be lost, whether the scan was
            // stopped by the user or because the account ran out of daily credits.
            SaveLibrary();

            if (_quotaExceeded)
            {
                TextFilename.Text = "Scan stopped: no credits remaining.";
                File.AppendAllText(logFile, $"--- Scan stopped at {DateTime.Now}: no download credits remaining ---" + Environment.NewLine);
                TinyDialogs.MessageBox("Error", "There are no download credits remaining on your ScreenScraper account today. The scan has been stopped and your progress so far has been saved.", MessageBoxDialogType.Ok, MessageBoxIconType.Error, MessageBoxButton.Ok);
            }
            else
            {
                TextFilename.Text = "Scan cancelled.";
                File.AppendAllText(logFile, $"--- Scan cancelled by user at {DateTime.Now} ---" + Environment.NewLine);
            }

            ButtonsScraper(false);
        }
    }

    void ButtonsScraper(bool working)
    {
        if (working)
        {
            ButtonOk.IsEnabled = false;
            ButtonCancelScraper.IsEnabled = true;
        }
        else
        {
            ButtonOk.IsEnabled = true;
            ButtonCancelScraper.IsEnabled = false;
        }
    }

    /// <summary>
    /// Scrapea una imagen de disco de ST buscando sus datos mediante la API de Screenscraper.fr
    /// </summary>
    /// <param name="filenameOrName">Si el fichero se identifica como gamemenu, se pasa el nombre del juego encontrado, si no, se pasa el nombre del archivo.</param>
    /// <param name="filelenght">Tamaño del fichero. Screenscraper lo usará como referencia para hacer la búsqueda y desempatar cuando haya más de una coincidencia.</param>
    /// <param name="FileSHA">Checksum del fichero. Screenscraper lo usará como referencia para hacer la búsqueda y desempatar cuando haya más de una coincidencia.</param>
    /// <param name="OriginalFilename">Nombre del fichero original, ya que en el parámetro filenameOrName podría venir el nombre del juego y no el del fichero. En realidad este parámetro sólo es útil para el log.</param>
    /// <returns></returns>
    async Task<LibraryItem> ScrapeTitle(string filenameOrName, long filelenght, string FileSHA, string OriginalFilename = "")
    {
        if(string.IsNullOrEmpty(OriginalFilename))
            OriginalFilename = filenameOrName;

        var GameInfo = await screenScraperClient.GetGameAsync(SSAtariSTId, Path.GetFileName(filenameOrName), filelenght, FileSHA, _scanCts.Token);

        if (GameInfo.QuotaExceeded)
        {
            // Out of credits: abort the whole scan (via _scanCts) instead of moving on to the
            // next file. The message is shown once, from the cancellation handler.
            File.AppendAllText(logFile, $"{filenameOrName} -> No download credits remaining, stopping scan" + Environment.NewLine);
            _quotaExceeded = true;
            _scanCts.Cancel();
            return null;
        }

        // Game not found in scraper API
        if (GameInfo.Data == null)
        {
            File.AppendAllText(logFile, $"{filenameOrName} -> Metadata not found for {OriginalFilename}" + Environment.NewLine);
            return null;
        }

        // Game already scrapered (by id)
        if (libraryCollection.Collection.Find(x => x.Id == GameInfo.Data.Id) != null)
        {
            File.AppendAllText(logFile, $"{filenameOrName} -> Already scrapered (Id={GameInfo.Data.Id})" + Environment.NewLine);
            return null;
        }

        LibraryItem item = new LibraryItem
        {
            Id = GameInfo.Data.Id,
            Filename = Path.GetFileName(filenameOrName),
            Developer = GameInfo.Data.Developpeur.Text,
            Publisher = GameInfo.Data.Editeur.Text
        };

        item.Name = new List<LibraryItem.RegionText>();

        // Region program names
        if (GameInfo.Data.Noms.Count == 0)
            item.Name.Add(new LibraryItem.RegionText { Region = "ss", Text = item.Filename });
        else
        {
            bool firstnamefound = false;
            foreach (var Name in GameInfo.Data.Noms)
            {
                item.Name.Add(new LibraryItem.RegionText() { Region = Name.Region, Text = Name.Text });
                if(!firstnamefound)
                    File.AppendAllText(logFile, $"{OriginalFilename} -> Found as or containing '{Name.Text}' searching for '{filenameOrName}'" + Environment.NewLine);

                firstnamefound = true;
            }
        }

        // Language synopsis
        if (GameInfo.Data.Synopsis.Count > 0)
        {
            item.Synopsis = new List<LibraryItem.RegionText>();
            foreach (var Synopsis in GameInfo.Data.Synopsis)
            {
                item.Synopsis.Add(new LibraryItem.RegionText() { Region = Synopsis.Langue, Text = Synopsis.Text });
                File.AppendAllText(logFile, $"   | Downloading synopsis {Synopsis.Langue}" + Environment.NewLine);
            }
        }

        // Region release date
        if (GameInfo.Data.Dates.Count > 0)
        {
            item.Date = new List<LibraryItem.RegionText>();
            foreach (var _date in GameInfo.Data.Dates)
            {
                item.Date.Add(new LibraryItem.RegionText() { Region = _date.Region, Text = _date.Text });
                File.AppendAllText(logFile, $"   | Downloading release date for {_date.Region}" + Environment.NewLine);
            }
        }

        // Genre
        if (GameInfo.Data.Genres.Count > 0 && GameInfo.Data.Genres[0].Noms.Count > 0)
        {
            item.Genre = new List<LibraryItem.RegionText>();
            foreach (var genre in GameInfo.Data.Genres[0].Noms)
            {
                item.Genre.Add(new LibraryItem.RegionText() { Region = genre.Langue, Text = genre.Text });
                File.AppendAllText(logFile, $"   | Downloading genre for region {genre.Langue}" + Environment.NewLine);
            } 
        }

        if (Config.ConfigOptions.RunninConfig.ScrapeMedia)
        {
            bool QuotaExceeded = false;

            // Box

            var MediaBox = GameInfo.Data.Medias.Find(x => x.Type == "box-2D");

            if (MediaBox != null && !string.IsNullOrEmpty(MediaBox.Url))
            {
                string mediaPath = Path.Combine(Config.ConfigOptions.RunninConfig.LibraryPath, "Media", $"Box-{GameInfo.Data.Id}.{MediaBox.Format ?? "png"}");

                if (!File.Exists(mediaPath))
                {
                    File.AppendAllText(logFile, $"   | Downloading box media" + Environment.NewLine);
                    TextFilename.Text = $@"Downloading box for {item.Name[0].Text}";
                    var media = await screenScraperClient.DownloadMediaAsync(MediaBox.Url, mediaPath, _scanCts.Token);

                    if (media.QuotaExceeded)
                        QuotaExceeded = true;
                }
                else
                    File.AppendAllText(logFile, $"   | Box already exists" + Environment.NewLine);
            }

            // Title screen

            var TittleScreen = GameInfo.Data.Medias.Find(x => x.Type == "sstitle");

            if (!QuotaExceeded && TittleScreen != null && !string.IsNullOrEmpty(TittleScreen.Url))
            {
                string mediaPath = Path.Combine(Config.ConfigOptions.RunninConfig.LibraryPath, "Media", $"TitleScreen-{GameInfo.Data.Id}.{TittleScreen.Format ?? "png"}");

                if (!File.Exists(mediaPath))
                {
                    File.AppendAllText(logFile, $"   | Downloading title screen" + Environment.NewLine);
                    TextFilename.Text = $@"Downloading title screen for {item.Name[0].Text}";
                    var media = await screenScraperClient.DownloadMediaAsync(TittleScreen.Url, mediaPath, _scanCts.Token);

                    if (media.QuotaExceeded)
                        QuotaExceeded = true;
                }
                else
                    File.AppendAllText(logFile, $"   | Title already exists" + Environment.NewLine);
            }

            // Screenshot

            var ss = GameInfo.Data.Medias.Find(x => x.Type == "ss");

            if (!QuotaExceeded && ss != null && !string.IsNullOrEmpty(ss.Url))
            {

                string mediaPath = Path.Combine(Config.ConfigOptions.RunninConfig.LibraryPath, "Media", $"Screenshot-{GameInfo.Data.Id}.{ss.Format ?? "png"}");

                if (!File.Exists(mediaPath))
                {
                    File.AppendAllText(logFile, $"   | Downloading screenshot" + Environment.NewLine);
                    TextFilename.Text = $@"Downloading screenshot for {item.Name[0].Text}";
                    var media = await screenScraperClient.DownloadMediaAsync(ss.Url, mediaPath, _scanCts.Token);

                    if (media.QuotaExceeded)
                        QuotaExceeded = true;
                }
                else
                    File.AppendAllText(logFile, $"   | Screenshot already exists" + Environment.NewLine);
            }

            // Video

            var MediaVideo = GameInfo.Data.Medias.Find(x => x.Type == "video-normalized");

            if (!QuotaExceeded && MediaVideo != null && !string.IsNullOrEmpty(MediaVideo.Url))
            {
                string mediaPath = Path.Combine(Config.ConfigOptions.RunninConfig.LibraryPath, "Media",
                    $"{GameInfo.Data.Id}.{MediaVideo.Format ?? "mp4"}");

                if (!File.Exists(mediaPath))
                {
                    File.AppendAllText(logFile, $"   | Downloading video" + Environment.NewLine);
                    TextFilename.Text = $@"Downloading video for {item.Name[0].Text}";
                    var media = await screenScraperClient.DownloadMediaAsync(MediaVideo.Url, mediaPath, _scanCts.Token);

                    if (media.QuotaExceeded)
                        QuotaExceeded = true;
                }
                else
                    File.AppendAllText(logFile, $"   | Video already exists" + Environment.NewLine);
            }

            if (QuotaExceeded)
            {
                // Same as above: stop the whole scan rather than just this item.
                File.AppendAllText(logFile, $"   | No download credits remaining, stopping scan" + Environment.NewLine);
                _quotaExceeded = true;
                _scanCts.Cancel();
                return null;
            }
        }

        return item;

    }
}
