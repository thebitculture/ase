using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ASE;

/// <summary>
/// Announces a newer GitHub release and lets the user go and get it or carry on with the
/// version at hand. Shown once per launch from <see cref="MainWindow.OnOpened"/> — the check
/// itself runs in Program.Main, where Avalonia is not up yet and only the console was available.
/// </summary>
public partial class UpdateWindow : Window
{
    public UpdateWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // The emulator parks (and stops receiving host input) while the window is open
        ASEMain.EnterUiPause();

        // Set before anything can return early: OnClosed reads it back to decide whether the
        // preference changed, and an unset switch would silently turn the check off.
        SwitchCheckForUpdates.IsChecked = Config.ConfigOptions.RunninConfig.CheckForUpdates;

        var release = ReleaseChecker.ReleaseInfo;

        if (release == null)
        {
            Close();
            return;
        }

        TextNewVersion.Text = TextNewVersion.Text.Replace("{{version}}", release.TagName);
        TextCurrentVersion.Text = TextCurrentVersion.Text.Replace("{{version}}", Config.Version);

        // GitHub usually names the release after its tag; showing both just repeats it.
        if (!string.IsNullOrWhiteSpace(release.Name) && release.Name != release.TagName)
        {
            TextReleaseName.Text = release.Name;
            TextReleaseName.IsVisible = true;
        }

        TextPublished.Text = release.PublishedAt == default
            ? "Available now"
            : $"Published {release.PublishedAt.ToLocalTime():d MMMM yyyy}";

        LinkRelease.Content = release.HtmlUrl;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        // Applied here rather than in the buttons so closing with the title bar keeps the choice.
        ApplyCheckForUpdatesPreference();

        ASEMain.ExitUiPause();
    }

    private void ButtonContinue_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void ButtonUpdate_Click(object sender, RoutedEventArgs e)
    {
        await OpenReleasePage();
        Close();
    }

    private async void OpenRelease_Click(object sender, RoutedEventArgs e)
    {
        await OpenReleasePage();
    }

    /// <summary>Opens the release page in the host browser. The emulator keeps running: ASE
    /// cannot update itself, so the download is left for whenever the user feels like it.</summary>
    private async Task OpenReleasePage()
    {
        if (ReleaseChecker.ReleaseInfo == null ||
            !Uri.TryCreate(ReleaseChecker.ReleaseInfo.HtmlUrl, UriKind.Absolute, out var uri))
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        await topLevel.Launcher.LaunchUriAsync(uri);
    }

    /// <summary>Persists the switch only when it actually changed, so a plain "Continue"
    /// never rewrites config.json.</summary>
    private void ApplyCheckForUpdatesPreference()
    {
        bool check = SwitchCheckForUpdates.IsChecked == true;

        if (check == Config.ConfigOptions.RunninConfig.CheckForUpdates)
            return;

        Config.ConfigOptions.RunninConfig.CheckForUpdates = check;
        Program.Config.DumpJsonConfig();
    }
}
