using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Splat;

namespace ASE;

public partial class AboutWindow : Window
{
    private DispatcherTimer _animationTimer;
    private Stopwatch _animationClock;
    private TranslateTransform _retroDudeTransform;

    public AboutWindow()
    {
        InitializeComponent();

        _retroDudeTransform = new TranslateTransform();
        RetroDude.RenderTransform = _retroDudeTransform;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // The emulator parks (and stops receiving host input) while the window is open.
        ASEMain.EnterUiPause();

        TextVersion.Text = TextVersion.Text.Replace("{{version}}", Config.Version);

        SwitchCheckForUpdates.IsChecked = Config.ConfigOptions.RunninConfig.CheckForUpdates;

        if (ReleaseChecker.ReleaseInfo != null && ReleaseChecker.ReleaseInfo.ExistsNewVersion)
        {
            TextNewVersion.Text = TextNewVersion.Text.Replace("{{version}}", ReleaseChecker.ReleaseInfo.TagName);
            StackNewVersion.IsVisible = true;
        }

        StartBackgroundAnimations();
    }

    private void StartBackgroundAnimations()
    {
        StopBackgroundAnimations();

        _animationClock = Stopwatch.StartNew();

        // 60-ish fps. DispatcherTimer runs on the UI dispatcher, so changes to
        // Opacity and RenderTransform are applied directly. The starfield behind the
        // card is not driven from here: it owns its own clock (see Starfield.cs).
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
        // Foreground idle animation: the mascot bobs, and the glow behind it breathes
        // out of phase so the halo does not read as part of the sprite.
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
        ASEMain.ExitUiPause();
    }

    private async void StackNewVersion_Tapped(object sender, Avalonia.Input.TappedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        await topLevel.Launcher.LaunchUriAsync(new Uri(ReleaseChecker.ReleaseInfo.HtmlUrl));
    }

    private async void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not HyperlinkButton hb)
            return;

        var url = hb.Tag as string ?? hb.Content?.ToString();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        await topLevel.Launcher.LaunchUriAsync(uri);
    }

    private void ButtonOkay_Click(object sender, RoutedEventArgs e)
    {
        Config.ConfigOptions.RunninConfig.CheckForUpdates = SwitchCheckForUpdates.IsChecked == true;
        Program.Config.DumpJsonConfig();

        Close();
    }
}
