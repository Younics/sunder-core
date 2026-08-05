using Avalonia.Controls;
using Avalonia.Interactivity;
using Sunder.App.Services;

namespace Sunder.App.Views;

public partial class AboutSunderWindow : Window
{
    private readonly OwnedTaskObserver _tasks = new("About window");
    private static readonly Uri SunderSiteUri = new("https://sunderapp.io?utm_campaign=sunder_app");
    private static readonly Uri SunderHubUri = new("https://hub.sunderapp.io?utm_campaign=sunder_app");
    private static readonly Uri YounicsUri = new("https://younics.com?utm_source=sunderapp.io&utm_medium=referral&utm_campaign=sunder_app");

    public AboutSunderWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _tasks.Dispose();
    }

    private void SunderSiteButton_OnClick(object? sender, RoutedEventArgs e)
        => _tasks.Run(_ => OpenUriAsync(SunderSiteUri), "opening the Sunder site");

    private void SunderHubButton_OnClick(object? sender, RoutedEventArgs e)
        => _tasks.Run(_ => OpenUriAsync(SunderHubUri), "opening Sunder Hub");

    private void YounicsButton_OnClick(object? sender, RoutedEventArgs e)
        => _tasks.Run(_ => OpenUriAsync(YounicsUri), "opening the Younics site");

    private async Task OpenUriAsync(Uri uri)
    {
        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is not null)
        {
            await launcher.LaunchUriAsync(uri);
        }
    }
}
