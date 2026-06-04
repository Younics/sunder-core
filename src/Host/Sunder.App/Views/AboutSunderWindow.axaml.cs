using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sunder.App.Views;

public partial class AboutSunderWindow : Window
{
    private static readonly Uri SunderSiteUri = new("https://sunderapp.io?utm_campaign=sunder_app");
    private static readonly Uri SunderHubUri = new("https://hub.sunderapp.io?utm_campaign=sunder_app");
    private static readonly Uri YounicsUri = new("https://younics.com?utm_source=sunderapp.io&utm_medium=referral&utm_campaign=sunder_app");

    public AboutSunderWindow()
    {
        InitializeComponent();
    }

    private async void SunderSiteButton_OnClick(object? sender, RoutedEventArgs e)
        => await OpenUriAsync(SunderSiteUri);

    private async void SunderHubButton_OnClick(object? sender, RoutedEventArgs e)
        => await OpenUriAsync(SunderHubUri);

    private async void YounicsButton_OnClick(object? sender, RoutedEventArgs e)
        => await OpenUriAsync(YounicsUri);

    private async Task OpenUriAsync(Uri uri)
    {
        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is not null)
        {
            await launcher.LaunchUriAsync(uri);
        }
    }
}
