using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Services;

namespace Sunder.App.ViewModels;

public partial class LoadingWindowViewModel : ViewModelBase
{
    private Func<Task>? _retryAsync;
    private Func<Task>? _openCoreShellAsync;
    private Action? _quit;

    public LoadingWindowViewModel()
    {
        RetryCommand = new AsyncRelayCommand(
            () => _retryAsync?.Invoke() ?? Task.CompletedTask,
            () => HasFailed && !IsBusy);
        OpenCoreShellCommand = new AsyncRelayCommand(
            () => _openCoreShellAsync?.Invoke() ?? Task.CompletedTask,
            () => HasFailed && !IsBusy);
        QuitCommand = new RelayCommand(
            () => _quit?.Invoke(),
            () => HasFailed && !IsBusy);
    }

    [ObservableProperty]
    private string _statusMessage = "Loading shell...";

    [ObservableProperty]
    private double _progressWidth = 56;

    [ObservableProperty]
    private bool _hasFailed;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _failureDetails = string.Empty;

    public IAsyncRelayCommand RetryCommand { get; }

    public IAsyncRelayCommand OpenCoreShellCommand { get; }

    public IRelayCommand QuitCommand { get; }

    public string Title { get; } = "Sunder";

    public string Version { get; } = SunderAppVersion.CurrentDisplayText;

    public string Subtitle { get; } = "Your personal workspace";

    public string FooterText { get; } = "SUNDER PLATFORM";

    public void ConfigureFailureActions(
        Func<Task> retryAsync,
        Func<Task> openCoreShellAsync,
        Action quit)
    {
        _retryAsync = retryAsync;
        _openCoreShellAsync = openCoreShellAsync;
        _quit = quit;
    }

    public bool TryBeginAttempt()
    {
        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        HasFailed = false;
        FailureDetails = string.Empty;
        StatusMessage = "Loading shell...";
        ProgressWidth = 56;
        return true;
    }

    public void CompleteAttempt() => IsBusy = false;

    public void ShowFailure(Exception exception)
    {
        IsBusy = false;
        HasFailed = true;
        StatusMessage = exception is TimeoutException
            ? "Sunder took too long to start."
            : "Sunder could not finish loading.";
        FailureDetails = exception.Message;
    }

    partial void OnHasFailedChanged(bool value) => NotifyCommandsChanged();

    partial void OnIsBusyChanged(bool value) => NotifyCommandsChanged();

    private void NotifyCommandsChanged()
    {
        RetryCommand.NotifyCanExecuteChanged();
        OpenCoreShellCommand.NotifyCanExecuteChanged();
        QuitCommand.NotifyCanExecuteChanged();
    }
}
