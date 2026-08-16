using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace ASE;

/// <summary>
/// Announces a newer GitHub release and lets the user go and get it or carry on with the
/// version at hand. Shown once per launch from <see cref="MainWindow.OnOpened"/> — the check
/// itself runs in Program.Main, where Avalonia is not up yet and only the console was available.
/// </summary>
public partial class UpdateWindow : Window
{
    private DispatcherTimer _animationTimer;
    private Stopwatch _animationClock;
    private TranslateTransform _retroDudeTransform;

    public UpdateWindow()
    {
        InitializeComponent();

        _retroDudeTransform = new TranslateTransform();
        RetroDude.RenderTransform = _retroDudeTransform;
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

        LinkRelease.Text = release.HtmlUrl;

        StartBackgroundAnimations();
    }

    /// <summary>Idle animation for the mascot, matching the About window: it bobs while the
    /// glow behind it breathes out of phase, so the halo does not read as part of the sprite.
    /// The starfield is not driven from here — it owns its own clock (see Starfield.cs).</summary>
    private void StartBackgroundAnimations()
    {
        StopBackgroundAnimations();

        _animationClock = Stopwatch.StartNew();

        _animationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };

        _animationTimer.Tick += AnimationTimer_Tick;
        _animationTimer.Start();

        // Draw the first animated state immediately rather than waiting for a timer tick.
        UpdateBackgroundAnimations(0);
    }

    private void StopBackgroundAnimations()
    {
        if (_animationTimer != null)
        {
            _animationTimer.Stop();
            _animationTimer.Tick -= AnimationTimer_Tick;
            _animationTimer = null;
        }

        _animationClock?.Stop();
        _animationClock = null;
    }

    private void AnimationTimer_Tick(object sender, EventArgs e)
    {
        if (_animationClock == null)
            return;

        UpdateBackgroundAnimations(_animationClock.Elapsed.TotalSeconds);
    }

    private void UpdateBackgroundAnimations(double t)
    {
        _retroDudeTransform.Y = Math.Sin(t * 1.75) * 4.0;
        RetroDude.Opacity = 0.95 + Osc01(t * 1.75 + 0.4) * 0.05;

        AvatarGlow.Opacity = 0.38 + Osc01(t * 0.85 + 2.2) * 0.30;
    }

    private static double Osc01(double phase)
        => 0.5 + Math.Sin(phase) * 0.5;

    protected override void OnClosed(EventArgs e)
    {
        StopBackgroundAnimations();

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

    private async void ReleaseCard_Tapped(object sender, TappedEventArgs e)
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
