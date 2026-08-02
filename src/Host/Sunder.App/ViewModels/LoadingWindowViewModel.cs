using CommunityToolkit.Mvvm.ComponentModel;
using Sunder.App.Services;

namespace Sunder.App.ViewModels;

public partial class LoadingWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _statusMessage = "Loading shell...";

    [ObservableProperty]
    private double _progressWidth = 56;

    [ObservableProperty]
    private bool _isBusy;

    public string Title { get; } = "Sunder";

    public string Version { get; } = SunderAppVersion.CurrentDisplayText;

    public string Subtitle { get; } = "Your personal workspace";

    public string FooterText { get; } = "SUNDER PLATFORM";

    public bool TryBeginAttempt()
    {
        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        StatusMessage = "Loading shell...";
        ProgressWidth = 56;
        return true;
    }

    public void CompleteAttempt() => IsBusy = false;

    public void BeginCoreShellFallback()
    {
        StatusMessage = "Opening Core Shell...";
        ProgressWidth = 56;
    }
}
