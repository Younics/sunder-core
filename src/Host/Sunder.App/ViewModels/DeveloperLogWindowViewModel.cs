using System.Collections.ObjectModel;
using Avalonia.Threading;
using Sunder.App.Services;

namespace Sunder.App.ViewModels;

public sealed class DeveloperLogWindowViewModel : ViewModelBase, IDisposable
{
    private readonly DeveloperLogService _developerLog;
    private bool _disposed;

    public DeveloperLogWindowViewModel(DeveloperLogService developerLog)
    {
        _developerLog = developerLog;
        Reload();
        _developerLog.EntriesChanged += DeveloperLog_OnEntriesChanged;
    }

    public ObservableCollection<DeveloperLogEntryViewModel> Entries { get; } = [];

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _developerLog.EntriesChanged -= DeveloperLog_OnEntriesChanged;
    }

    private void DeveloperLog_OnEntriesChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Reload();
            return;
        }

        Dispatcher.UIThread.Post(Reload, DispatcherPriority.Background);
    }

    private void Reload()
    {
        Entries.Clear();
        foreach (var entry in _developerLog.Snapshot())
        {
            Entries.Add(new DeveloperLogEntryViewModel(entry));
        }
    }
}

public sealed class DeveloperLogEntryViewModel(DeveloperLogEntry entry)
{
    public string TimestampText => entry.Timestamp.ToString("HH:mm:ss.fff");

    public string LevelText => entry.Level.ToString().ToUpperInvariant();

    public string Source => entry.Source;

    public string Message => entry.Message;
}
