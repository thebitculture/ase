using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Splat;

namespace ASE;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        TextVersion.Text = TextVersion.Text.Replace("{{version}}", Config.Version);

        if (ReleaseChecker.ReleaseInfo != null && ReleaseChecker.ReleaseInfo.ExistsNewVersion)
        {
            TextNewVersion.Text = TextNewVersion.Text.Replace("{{version}}", ReleaseChecker.ReleaseInfo.TagName);
            StackNewVersion.IsVisible = true;
        }
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

        var url = hb.Content?.ToString();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        await topLevel.Launcher.LaunchUriAsync(uri);
    }

    private void ButtonOkay_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }
}