using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.ViewModels;

public sealed partial class BackgroundProcessMonitorViewModel : ViewModelBase, IDisposable
{
    public const double MinimumPopoverWidth = ShellState.MinimumBackgroundProcessPopoverWidth;
    public const double MaximumPopoverWidth = ShellState.MaximumBackgroundProcessPopoverWidth;
    public const double MinimumPopoverHeight = ShellState.MinimumBackgroundProcessPopoverHeight;
    public const double MaximumPopoverHeight = ShellState.MaximumBackgroundProcessPopoverHeight;

    private readonly BackgroundProcessQueueService? _backgroundProcesses;
    private readonly BackgroundProcessIndicator _indicator;
    private readonly string _emptyText;
    private readonly Action<double, double>? _persistPopoverSize;
    private bool _disposed;

    public BackgroundProcessMonitorViewModel(
        BackgroundProcessQueueService backgroundProcesses,
        BackgroundProcessIndicator indicator,
        string emptyText = "No active processes.",
        double popoverWidth = 500,
        double popoverHeight = 360,
        Action<double, double>? persistPopoverSize = null)
    {
        _backgroundProcesses = backgroundProcesses;
        _indicator = indicator;
        _emptyText = emptyText;
        _persistPopoverSize = persistPopoverSize;
        PopoverWidth = ClampPopoverWidth(popoverWidth);
        PopoverHeight = ClampPopoverHeight(popoverHeight);
        _backgroundProcesses.ProcessChanged += BackgroundProcesses_OnProcessChanged;
        Refresh();
    }

    private BackgroundProcessMonitorViewModel()
    {
        _indicator = BackgroundProcessIndicator.Hidden;
        _emptyText = "No active processes.";
        FooterStatusText = _emptyText;
        PopoverWidth = 500;
        PopoverHeight = 360;
    }

    public static BackgroundProcessMonitorViewModel Empty { get; } = new();

    public ObservableCollection<BackgroundProcessItemViewModel> Processes { get; } = [];

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
        if (_backgroundProcesses is not null)
        {
            _backgroundProcesses.ProcessChanged -= BackgroundProcesses_OnProcessChanged;
        }

        Processes.Clear();
    }

    public void ResizePopover(double deltaWidth, double deltaHeight)
    {
        PopoverWidth = ClampPopoverWidth(PopoverWidth + deltaWidth);
        PopoverHeight = ClampPopoverHeight(PopoverHeight + deltaHeight);
    }

    public void PersistPopoverSize()
        => _persistPopoverSize?.Invoke(PopoverWidth, PopoverHeight);

    private void BackgroundProcesses_OnProcessChanged(object? sender, BackgroundProcessChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Refresh();
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                Refresh();
            }
        }, DispatcherPriority.Background);
    }

    public void Refresh()
    {
        if (_disposed || _backgroundProcesses is null)
        {
            return;
        }

        var snapshots = _backgroundProcesses.ListProcesses()
            .Where(snapshot => snapshot.IsActive)
            .Where(snapshot => snapshot.Indicator == _indicator)
            .OrderBy(ProcessSortKey)
            .ThenBy(snapshot => snapshot.QueuedAtUtc)
            .ToArray();

        Processes.ReplaceWith(snapshots.Select(snapshot => new BackgroundProcessItemViewModel(snapshot, CancelProcess)));
        ActiveProcessCount = snapshots.Length;
        RunningProcessCount = snapshots.Count(snapshot => snapshot.State is BackgroundProcessState.Running or BackgroundProcessState.Cancelling);

        var footerProcess = RunningProcessCount == 1
            ? snapshots.FirstOrDefault(snapshot => snapshot.State is BackgroundProcessState.Running or BackgroundProcessState.Cancelling)
            : RunningProcessCount == 0
                ? snapshots.FirstOrDefault()
                : null;

        if (RunningProcessCount > 1)
        {
            FooterStatusText = $"{RunningProcessCount} processes running";
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

    private static int ProcessSortKey(BackgroundProcessSnapshot snapshot)
        => snapshot.State switch
        {
            BackgroundProcessState.Cancelling => 0,
            BackgroundProcessState.Running => 1,
            BackgroundProcessState.Queued => 2,
            _ => 3,
        };

    private void CancelProcess(Guid processId)
    {
        if (_backgroundProcesses?.Cancel(processId) == true)
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

    private static string FormatFooterStatus(BackgroundProcessSnapshot snapshot)
        => snapshot.State switch
        {
            BackgroundProcessState.Queued => $"Queued: {snapshot.Title}",
            BackgroundProcessState.Cancelling => "Cancelling...",
            _ => string.IsNullOrWhiteSpace(snapshot.StatusText) ? snapshot.Title : snapshot.StatusText,
        };

    private static double ClampPopoverWidth(double value)
        => Math.Clamp(value, MinimumPopoverWidth, MaximumPopoverWidth);

    private static double ClampPopoverHeight(double value)
        => Math.Clamp(value, MinimumPopoverHeight, MaximumPopoverHeight);
}

public sealed partial class BackgroundProcessItemViewModel : ViewModelBase
{
    private readonly Guid _processId;
    private readonly Action<Guid> _cancelProcess;

    public BackgroundProcessItemViewModel(BackgroundProcessSnapshot snapshot, Action<Guid> cancelProcess)
    {
        _processId = snapshot.ProcessId;
        _cancelProcess = cancelProcess;
        Title = snapshot.Title;
        GroupKey = snapshot.GroupKey;
        StatusText = FormatStatus(snapshot);
        DetailText = BuildDetailText(snapshot);
        ProgressPercent = snapshot.ProgressPercent ?? 0;
        IsIndeterminate = snapshot.ProgressPercent is null;
        State = snapshot.State;
        CanCancel = snapshot.CanCancel && snapshot.State is not BackgroundProcessState.Cancelling;
    }

    public string Title { get; }

    public string GroupKey { get; }

    public string StatusText { get; }

    public string DetailText { get; }

    public double ProgressPercent { get; }

    public bool IsIndeterminate { get; }

    public BackgroundProcessState State { get; }

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

    private static string FormatStatus(BackgroundProcessSnapshot snapshot)
        => snapshot.State switch
        {
            BackgroundProcessState.Queued => "Queued",
            BackgroundProcessState.Running => string.IsNullOrWhiteSpace(snapshot.StatusText) ? "Running" : snapshot.StatusText,
            BackgroundProcessState.Cancelling => "Cancelling",
            _ => snapshot.StatusText,
        };

    private static string BuildDetailText(BackgroundProcessSnapshot snapshot)
    {
        var parts = new List<string>();
        if (PackageOperationMetadata.TryCreate(snapshot.Metadata, out var packageMetadata))
        {
            parts.Add(packageMetadata.DisplayName);
        }
        else if (PackageScopedBackgroundProcessMetadata.TryCreate(snapshot.Metadata, out var packageProcessMetadata))
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
