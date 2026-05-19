using System.Collections.ObjectModel;
using Avalonia.Threading;
using Sunder.App.Services;

namespace Sunder.App.ViewModels;

public sealed class DeveloperLogWindowViewModel : ViewModelBase, IDisposable
{
    private readonly DeveloperLogService _developerLog;
    private readonly List<DeveloperLogEntryViewModel> _entries = [];
    private string _searchText = string.Empty;
    private DeveloperLogFilterOptionViewModel? _selectedFilter;
    private bool _disposed;

    public DeveloperLogWindowViewModel(DeveloperLogService developerLog)
    {
        _developerLog = developerLog;
        Reload();
        _developerLog.EntriesChanged += DeveloperLog_OnEntriesChanged;
    }

    public ObservableCollection<DeveloperLogEntryViewModel> FilteredEntries { get; } = [];

    public ObservableCollection<DeveloperLogFilterOptionViewModel> FilterOptions { get; } = [];

    public DeveloperLogFilterOptionViewModel? SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (!SetProperty(ref _selectedFilter, value))
            {
                return;
            }

            ApplyFilter();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? string.Empty))
            {
                return;
            }

            ApplyFilter();
        }
    }

    public string StatusText => $"{FilteredEntries.Count:N0} of {_entries.Count:N0} log entries";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _developerLog.EntriesChanged -= DeveloperLog_OnEntriesChanged;
    }

    private void DeveloperLog_OnEntriesChanged(object? sender, DeveloperLogEntriesChangedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyEntriesChange(e);
            return;
        }

        Dispatcher.UIThread.Post(() => ApplyEntriesChange(e), DispatcherPriority.Background);
    }

    private void Reload()
    {
        _entries.Clear();
        foreach (var entry in _developerLog.Snapshot())
        {
            _entries.Add(new DeveloperLogEntryViewModel(entry));
        }

        RefreshFilterOptions();
        ApplyFilter();
    }

    private void ApplyEntriesChange(DeveloperLogEntriesChangedEventArgs e)
    {
        if (e.Reset)
        {
            Reload();
            return;
        }

        var addedEntries = e.AddedEntries.Select(entry => new DeveloperLogEntryViewModel(entry)).ToArray();
        if (addedEntries.Length == 0)
        {
            return;
        }

        _entries.AddRange(addedEntries);
        RefreshFilterOptions();
        foreach (var entry in addedEntries)
        {
            if (MatchesCurrentFilter(entry))
            {
                FilteredEntries.Add(entry);
            }
        }

        OnPropertyChanged(nameof(StatusText));
    }

    private void ApplyFilter()
    {
        FilteredEntries.Clear();
        foreach (var entry in _entries)
        {
            if (MatchesCurrentFilter(entry))
            {
                FilteredEntries.Add(entry);
            }
        }

        OnPropertyChanged(nameof(StatusText));
    }

    private bool MatchesCurrentFilter(DeveloperLogEntryViewModel entry)
    {
        var filter = SelectedFilter ?? DeveloperLogFilterOptionViewModel.All;
        return filter.Matches(entry) && MatchesSearch(entry);
    }

    private bool MatchesSearch(DeveloperLogEntryViewModel entry)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return entry.SearchText.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshFilterOptions()
    {
        var selectedKey = SelectedFilter?.Key ?? DeveloperLogFilterOptionViewModel.All.Key;
        var packageOptions = _entries
            .Where(entry => entry.Scope == DeveloperLogEntryScope.Package)
            .Select(entry => entry.Source)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(DeveloperLogFilterOptionViewModel.ForPackage)
            .ToArray();

        FilterOptions.Clear();
        FilterOptions.Add(DeveloperLogFilterOptionViewModel.All);
        FilterOptions.Add(DeveloperLogFilterOptionViewModel.Application);
        FilterOptions.Add(DeveloperLogFilterOptionViewModel.AllPackages);
        foreach (var packageOption in packageOptions)
        {
            FilterOptions.Add(packageOption);
        }

        var selected = FilterOptions.FirstOrDefault(option => string.Equals(option.Key, selectedKey, StringComparison.Ordinal))
            ?? DeveloperLogFilterOptionViewModel.All;
        if (ReferenceEquals(SelectedFilter, selected))
        {
            return;
        }

        SetProperty(ref _selectedFilter, selected, nameof(SelectedFilter));
    }
}

public sealed class DeveloperLogEntryViewModel(DeveloperLogEntry entry)
{
    public string TimestampText => entry.Timestamp.ToString("HH:mm:ss.fff");

    public string LevelText => entry.Level.ToString().ToUpperInvariant();

    public DeveloperLogEntryScope Scope => entry.Scope;

    public string ScopeText => entry.Scope == DeveloperLogEntryScope.Package ? "PACKAGE" : "APP";

    public string Source => entry.Source;

    public string Message => entry.Message.ReplaceLineEndings("  ");

    public string SearchText => $"{TimestampText} {LevelText} {ScopeText} {Source} {Message}";
}

public sealed class DeveloperLogFilterOptionViewModel
{
    private DeveloperLogFilterOptionViewModel(string key, string label, DeveloperLogFilterKind kind, string? packageId = null)
    {
        Key = key;
        Label = label;
        Kind = kind;
        PackageId = packageId;
    }

    public static DeveloperLogFilterOptionViewModel All { get; } = new("all", "All logs", DeveloperLogFilterKind.All);

    public static DeveloperLogFilterOptionViewModel Application { get; } = new("app", "Application", DeveloperLogFilterKind.Application);

    public static DeveloperLogFilterOptionViewModel AllPackages { get; } = new("packages", "All packages", DeveloperLogFilterKind.AllPackages);

    public string Key { get; }

    public string Label { get; }

    public DeveloperLogFilterKind Kind { get; }

    public string? PackageId { get; }

    public static DeveloperLogFilterOptionViewModel ForPackage(string packageId)
        => new($"package:{packageId}", packageId, DeveloperLogFilterKind.Package, packageId);

    public bool Matches(DeveloperLogEntryViewModel entry)
        => Kind switch
        {
            DeveloperLogFilterKind.All => true,
            DeveloperLogFilterKind.Application => entry.Scope == DeveloperLogEntryScope.Application,
            DeveloperLogFilterKind.AllPackages => entry.Scope == DeveloperLogEntryScope.Package,
            DeveloperLogFilterKind.Package => entry.Scope == DeveloperLogEntryScope.Package
                                              && string.Equals(entry.Source, PackageId, StringComparison.OrdinalIgnoreCase),
            _ => true,
        };

    public override string ToString() => Label;
}

public enum DeveloperLogFilterKind
{
    All = 0,
    Application = 1,
    AllPackages = 2,
    Package = 3,
}
