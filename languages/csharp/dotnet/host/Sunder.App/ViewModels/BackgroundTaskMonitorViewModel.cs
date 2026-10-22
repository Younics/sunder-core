using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.ViewModels;

public sealed partial class BackgroundTaskMonitorViewModel : ViewModelBase, IDisposable
{
    public const double MinimumPopoverWidth = ShellState.MinimumBackgroundTaskPopoverWidth;
    public const double MaximumPopoverWidth = ShellState.MaximumBackgroundTaskPopoverWidth;
    public const double MinimumPopoverHeight = ShellState.MinimumBackgroundTaskPopoverHeight;
    public const double MaximumPopoverHeight = ShellState.MaximumBackgroundTaskPopoverHeight;

    private readonly BackgroundTaskQueueService? _backgroundTasks;
    private readonly BackgroundTaskIndicator _indicator;
    private readonly string _emptyText;
    private readonly Action<double, double>? _persistPopoverSize;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly OwnedTaskObserver _tasks = new(nameof(BackgroundTaskMonitorViewModel));
    private bool _disposed;

    public BackgroundTaskMonitorViewModel(
        BackgroundTaskQueueService backgroundTasks,
        BackgroundTaskIndicator indicator,
        string emptyText = "No active tasks.",
        double popoverWidth = 500,
        double popoverHeight = 360,
        Action<double, double>? persistPopoverSize = null,
        IUiDispatcher? uiDispatcher = null)
    {
        _backgroundTasks = backgroundTasks;
        _indicator = indicator;
        _emptyText = emptyText;
        _persistPopoverSize = persistPopoverSize;
        _uiDispatcher = uiDispatcher ?? AvaloniaUiDispatcher.Instance;
        PopoverWidth = ClampPopoverWidth(popoverWidth);
        PopoverHeight = ClampPopoverHeight(popoverHeight);
        _backgroundTasks.TaskChanged += BackgroundTasks_OnTaskChanged;
        Refresh();
    }

    private BackgroundTaskMonitorViewModel()
    {
        _uiDispatcher = AvaloniaUiDispatcher.Instance;
        _indicator = BackgroundTaskIndicator.Hidden;
        _emptyText = "No active tasks.";
        FooterStatusText = _emptyText;
        PopoverWidth = 500;
        PopoverHeight = 360;
    }

    public static BackgroundTaskMonitorViewModel Empty { get; } = new();

    public ObservableCollection<BackgroundTaskItemViewModel> Processes { get; } = [];

    public bool HasProcesses => Processes.Count > 0;

    public bool HasNoProcesses => !HasProcesses;

    public bool ShowFooter => HasProcesses;

    public bool ShowFooterProgress => ShowFooter;

    public bool ShowAdditionalActiveProcessCount => AdditionalActiveProcessCount > 0;

    [ObservableProperty]
    private string _footerStatusText = string.Empty;

    [ObservableProperty]
    private bool _footerIsIndeterminate = true;

    [ObservableProperty]
    private double _footerProgressPercent;

    [ObservableProperty]
    private int _activeProcessCount;

    [ObservableProperty]
    private int _runningProcessCount;

    [ObservableProperty]
    private int _additionalActiveProcessCount;

    [ObservableProperty]
    private string _additionalActiveProcessCountText = string.Empty;

    [ObservableProperty]
    private double _popoverWidth;

    [ObservableProperty]
    private double _popoverHeight;

    partial void OnAdditionalActiveProcessCountChanged(int value)
        => OnPropertyChanged(nameof(ShowAdditionalActiveProcessCount));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_backgroundTasks is not null)
        {
            _backgroundTasks.TaskChanged -= BackgroundTasks_OnTaskChanged;
        }

        Processes.Clear();
        _tasks.Dispose();
    }

    public void ResizePopover(double deltaWidth, double deltaHeight)
    {
        PopoverWidth = ClampPopoverWidth(PopoverWidth + deltaWidth);
        PopoverHeight = ClampPopoverHeight(PopoverHeight + deltaHeight);
    }

    public void PersistPopoverSize()
        => _persistPopoverSize?.Invoke(PopoverWidth, PopoverHeight);

    private void BackgroundTasks_OnTaskChanged(object? sender, BackgroundTaskChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (_uiDispatcher.CheckAccess())
        {
            Refresh();
            return;
        }

        _tasks.Observe(_uiDispatcher.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                Refresh();
            }
        }), "refreshing background task presentation");
    }

    public void Refresh()
    {
        if (_disposed || _backgroundTasks is null)
        {
            return;
        }

        var snapshots = _backgroundTasks.ListTasks()
            .Where(snapshot => snapshot.IsActive)
            .Where(snapshot => snapshot.Indicator == _indicator)
            .OrderBy(ProcessSortKey)
            .ThenBy(snapshot => snapshot.QueuedAtUtc)
            .ToArray();

        Processes.ReplaceWith(snapshots.Select(snapshot => new BackgroundTaskItemViewModel(snapshot, CancelProcess)));
        ActiveProcessCount = snapshots.Length;
        RunningProcessCount = snapshots.Count(snapshot => snapshot.State is BackgroundTaskState.Running or BackgroundTaskState.Cancelling);

        var footerProcess = RunningProcessCount == 1
            ? snapshots.FirstOrDefault(snapshot => snapshot.State is BackgroundTaskState.Running or BackgroundTaskState.Cancelling)
            : RunningProcessCount == 0
                ? snapshots.FirstOrDefault()
                : null;

        if (RunningProcessCount > 1)
        {
            FooterStatusText = $"{RunningProcessCount} tasks running";
            FooterIsIndeterminate = true;
            FooterProgressPercent = 0;
            AdditionalActiveProcessCount = Math.Max(0, ActiveProcessCount - RunningProcessCount);
        }
        else if (footerProcess is not null)
        {
            FooterStatusText = FormatFooterStatus(footerProcess);
            FooterIsIndeterminate = footerProcess.ProgressPercent is null;
            FooterProgressPercent = footerProcess.ProgressPercent ?? 0;
            AdditionalActiveProcessCount = Math.Max(0, ActiveProcessCount - 1);
        }
        else
        {
            FooterStatusText = _emptyText;
            FooterIsIndeterminate = true;
            FooterProgressPercent = 0;
            AdditionalActiveProcessCount = 0;
        }

        AdditionalActiveProcessCountText = AdditionalActiveProcessCount > 0 ? $"+{AdditionalActiveProcessCount}" : string.Empty;
        NotifyCollectionStateChanged();
    }

    private static int ProcessSortKey(BackgroundTaskSnapshot snapshot)
        => snapshot.State switch
        {
            BackgroundTaskState.Cancelling => 0,
            BackgroundTaskState.Running => 1,
            BackgroundTaskState.Queued => 2,
            _ => 3,
        };

    private void CancelProcess(Guid processId)
    {
        if (_backgroundTasks?.Cancel(processId) == true)
        {
            Refresh();
        }
    }

    private void NotifyCollectionStateChanged()
    {
        OnPropertyChanged(nameof(HasProcesses));
        OnPropertyChanged(nameof(HasNoProcesses));
        OnPropertyChanged(nameof(ShowFooter));
        OnPropertyChanged(nameof(ShowFooterProgress));
    }

    private static string FormatFooterStatus(BackgroundTaskSnapshot snapshot)
        => snapshot.State switch
        {
            BackgroundTaskState.Queued => string.IsNullOrWhiteSpace(snapshot.StatusText) || snapshot.StatusText == "Queued"
                ? $"Queued: {snapshot.Title}" : snapshot.StatusText,
            BackgroundTaskState.Cancelling => "Cancelling...",
            _ => string.IsNullOrWhiteSpace(snapshot.StatusText) ? snapshot.Title : snapshot.StatusText,
        };

    private static double ClampPopoverWidth(double value)
        => Math.Clamp(value, MinimumPopoverWidth, MaximumPopoverWidth);

    private static double ClampPopoverHeight(double value)
        => Math.Clamp(value, MinimumPopoverHeight, MaximumPopoverHeight);
}

public sealed partial class BackgroundTaskItemViewModel : ViewModelBase
{
    private readonly Guid _processId;
    private readonly Action<Guid> _cancelProcess;

    public BackgroundTaskItemViewModel(BackgroundTaskSnapshot snapshot, Action<Guid> cancelProcess)
    {
        _processId = snapshot.TaskId;
        _cancelProcess = cancelProcess;
        Title = snapshot.Title;
        GroupKey = snapshot.GroupKey;
        StatusText = FormatStatus(snapshot);
        DetailText = BuildDetailText(snapshot);
        ProgressPercent = snapshot.ProgressPercent ?? 0;
        IsIndeterminate = snapshot.ProgressPercent is null;
        State = snapshot.State;
        CanCancel = snapshot.CanCancel && snapshot.State is not BackgroundTaskState.Cancelling;
    }

    public string Title { get; }

    public string GroupKey { get; }

    public string StatusText { get; }

    public string DetailText { get; }

    public double ProgressPercent { get; }

    public bool IsIndeterminate { get; }

    public BackgroundTaskState State { get; }

    public bool CanCancel { get; }

    public bool ShowCancel => CanCancel;

    public bool ShowDetailText => !string.IsNullOrWhiteSpace(DetailText);

    [RelayCommand]
    private void Cancel()
    {
        if (CanCancel)
        {
            _cancelProcess(_processId);
        }
    }

    private static string FormatStatus(BackgroundTaskSnapshot snapshot)
        => snapshot.State switch
        {
            BackgroundTaskState.Queued => string.IsNullOrWhiteSpace(snapshot.StatusText) ? "Queued" : snapshot.StatusText,
            BackgroundTaskState.Running => string.IsNullOrWhiteSpace(snapshot.StatusText) ? "Running" : snapshot.StatusText,
            BackgroundTaskState.Cancelling => "Cancelling",
            _ => snapshot.StatusText,
        };

    private static string BuildDetailText(BackgroundTaskSnapshot snapshot)
    {
        var parts = new List<string>();
        if (PackageOperationMetadata.TryCreate(snapshot.Metadata, out var packageMetadata))
        {
            parts.Add(packageMetadata.DisplayName);
            if (packageMetadata.PackageIds.Count > 1) parts.Add(string.Join(", ", packageMetadata.PackageIds));
        }
        else if (PackageScopedBackgroundTaskMetadata.TryCreate(snapshot.Metadata, out var packageProcessMetadata))
        {
            parts.Add(packageProcessMetadata.PackageDisplayName);
        }

        if (!string.IsNullOrWhiteSpace(snapshot.ErrorMessage))
        {
            parts.Add(snapshot.ErrorMessage);
        }

        return string.Join(" | ", parts.Distinct(StringComparer.OrdinalIgnoreCase));
    }
}
