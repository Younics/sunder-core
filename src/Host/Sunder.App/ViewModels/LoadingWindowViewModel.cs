using CommunityToolkit.Mvvm.ComponentModel;
using Sunder.App.Services;

namespace Sunder.App.ViewModels;

public partial class LoadingWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _statusMessage = "Loading shell...";

    [ObservableProperty]
    private double _progressWidth = 56;

    public string Title { get; } = "Sunder";

    public string Version { get; } = SunderAppVersion.CurrentDisplayText;

    public string Subtitle { get; } = "Local package platform";

    public string FooterText { get; } = "SUNDER PLATFORM";
}
