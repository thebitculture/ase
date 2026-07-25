using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ASE;

public partial class LibraryConfigurationWindow : Window
{
    const string PasswordTextMask = "********";

    public LibraryConfigurationWindow()
    {
        InitializeComponent();

        TextLibraryPath.Text = Config.ConfigOptions.RunninConfig.LibraryPath;
        TextSSUser.Text = Config.ConfigOptions.RunninConfig.ScreenScraperUser;
        TextSSPass.Text = PasswordTextMask;
        SwitchDownloadMedia.IsChecked = Config.ConfigOptions.RunninConfig.ScrapeMedia;

        // libVLC's default-location search only applies on Windows (macOS locates
        // VLC.app on its own, Linux typically has libvlc on the system library path).
        CardVlcPath.IsVisible = OperatingSystem.IsWindows();
        TextVlcPath.Text = Config.ConfigOptions.RunninConfig.VlcInstallPath;
    }

    // The emulator parks (and stops receiving host input) while the window is open;
    // the pause is reference-counted, so opening the scraper on top nests cleanly
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ASEMain.EnterUiPause();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ASEMain.ExitUiPause();
    }

    private void OnLibraryPathClick(object sender, RoutedEventArgs e)
    {
        FileUtils.BrowseDirectory(TextLibraryPath, "Select library directory");
    }

    private void OnVlcPathClick(object sender, RoutedEventArgs e)
    {
        FileUtils.BrowseDirectory(TextVlcPath, "Select VLC installation directory");
    }

    void SaveConfig()
    {
        Config.ConfigOptions.RunninConfig.LibraryPath = TextLibraryPath.Text;
        Config.ConfigOptions.RunninConfig.ScreenScraperUser = TextSSUser.Text;
        Config.ConfigOptions.RunninConfig.ScrapeMedia = (bool)SwitchDownloadMedia.IsChecked;
        Config.ConfigOptions.RunninConfig.VlcInstallPath = TextVlcPath.Text;

        if (!string.IsNullOrEmpty(TextSSPass.Text) && !TextSSPass.Text.Equals(PasswordTextMask))
            Config.ConfigOptions.RunninConfig.ScreenScraperPasswordRaw = TextSSPass.Text;

        Program.Config.DumpJsonConfig();
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        SaveConfig();
        Close();
    }

    private void OnScanLibraryClick(object sender, RoutedEventArgs e)
    {
        SaveConfig();

        ScraperWindow scraperWindow = new ScraperWindow();
        scraperWindow.ShowDialog(this);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}