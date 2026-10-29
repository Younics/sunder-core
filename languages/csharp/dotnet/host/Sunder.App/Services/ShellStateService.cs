using System.Text.Json;
using Sunder.App.Features.Shell.State;
using Sunder.App.Models;
using Sunder.App.Themes;
using Sunder.App.Views;

namespace Sunder.App.Services;

public sealed class ShellStateService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _stateFilePath;
    private readonly object _syncRoot = new();
    private readonly object _admissionGate = new();
    private readonly ShellStateWriter _writer = new();

    public ShellStateService(string? stateFilePath = null)
    {
        _stateFilePath = stateFilePath ?? AppLocalState.GetPath("shell-state.json");
    }

    public ShellState Load()
    {
        lock (_syncRoot)
        {
            if (!File.Exists(_stateFilePath))
            {
                return new ShellState();
            }

            try
            {
                var state = JsonSerializer.Deserialize<ShellState>(File.ReadAllText(_stateFilePath), JsonOptions) ?? new ShellState();
                state.ViewPlacements = new Dictionary<string, RailPlacement>(state.ViewPlacements ?? [], StringComparer.OrdinalIgnoreCase);
                state.ViewPackageIds = new Dictionary<string, string>(state.ViewPackageIds ?? [], StringComparer.OrdinalIgnoreCase);
                state.ViewOrder = new Dictionary<string, int>(state.ViewOrder ?? [], StringComparer.OrdinalIgnoreCase);
                state.HiddenHotbarViewIds = new HashSet<string>(state.HiddenHotbarViewIds ?? [], StringComparer.OrdinalIgnoreCase);
                state.ThemeId = string.IsNullOrWhiteSpace(state.ThemeId) ? SunderThemeDefinition.GraphiteDark.Id : state.ThemeId;

                if (state.LayoutVersion != ShellState.CurrentLayoutVersion)
                {
                    return new ShellState { ThemeId = state.ThemeId, PreferredRuntimeUrl = state.PreferredRuntimeUrl };
                }

                state.LeftPanelWidth = ShellLayoutCalculator.NormalizePanelWidth(state.LeftPanelWidth, ShellState.DefaultLeftPanelWidth);
                state.RightPanelWidth = ShellLayoutCalculator.NormalizePanelWidth(state.RightPanelWidth, ShellState.DefaultRightPanelWidth);
                state.TopRowHeightRatio = ShellLayoutCalculator.ClampTopRowRatio(state.TopRowHeightRatio);
                state.BottomSplitRatio = ShellLayoutCalculator.ClampBottomSplitRatio(state.BottomSplitRatio);
                state.SettingsSidebarWidth = ClampSecondarySidebarWidth(state.SettingsSidebarWidth, ShellState.DefaultSettingsSidebarWidth);
                state.PackagesSidebarWidth = ClampSecondarySidebarWidth(state.PackagesSidebarWidth, ShellState.DefaultPackagesSidebarWidth);
                state.StacksSidebarWidth = ClampSecondarySidebarWidth(state.StacksSidebarWidth, ShellState.DefaultStacksSidebarWidth);
                state.BackgroundTaskPopoverWidth = ClampBackgroundTaskPopoverWidth(state.BackgroundTaskPopoverWidth);
                state.BackgroundTaskPopoverHeight = ClampBackgroundTaskPopoverHeight(state.BackgroundTaskPopoverHeight);
                return state;
            }
            catch (Exception ex)
            {
                AppSessionLog.WriteError("Failed to load shell state. Defaults will be used.", ex);
                return new ShellState();
            }
        }
    }

    public void Save(ShellState state)
    {
        var snapshot = ShellStateSnapshotFactory.Clone(state);
        EnqueuePublicSnapshot(snapshot).GetAwaiter().GetResult();
        // Rejected stale snapshots retain their old revision; rejection must not
        // grant that same stale content permission to overwrite on its next save.
        state.Revision = snapshot.Revision;
    }

    private long WriteSnapshot(ShellState state, bool allowStale = false)
    {
        lock (_syncRoot)
        {
            var persisted = Load();
            // Public whole-state saves from independent stale readers still fail
            // closed. The session queue/flush is explicitly ordered and may carry
            // an old disk revision without carrying old UI preferences.
            if (!allowStale && state.Revision < persisted.Revision) return persisted.Revision;
            state.Revision = Math.Max(state.Revision, persisted.Revision) + 1;
            WriteCore(state);
            return state.Revision;
        }
    }

    public void Update(ShellState liveState, Action<ShellState> update)
    {
        lock (_admissionGate)
        {
            try { _writer.DrainAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) when (AppPackageUiFaultPolicy.IsRecoverable(ex)) { }
            var persisted = Load();
            update(persisted);
            var snapshot = ShellStateSnapshotFactory.Clone(persisted);
            var revision = _writer.Enqueue(() => WriteSnapshot(snapshot, allowStale: true)).GetAwaiter().GetResult();
            update(liveState);
            liveState.Revision = revision;
        }
    }

    public Task SaveAsync(ShellState state, CancellationToken cancellationToken = default)
        => EnqueuePublicSnapshot(ShellStateSnapshotFactory.Clone(state), cancellationToken);

    private Task<long> EnqueuePublicSnapshot(ShellState snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_admissionGate)
        {
            return _writer.Enqueue(() => WriteSnapshot(snapshot), cancellationToken: cancellationToken, supersedePending: false);
        }
    }

    internal Task QueueSave(ShellState liveState, Func<CancellationToken, Task> wait)
    {
        lock (_admissionGate)
        {
            var snapshot = ShellStateSnapshotFactory.Clone(liveState);
            return _writer.Enqueue(() => WriteSnapshot(snapshot, allowStale: true), wait);
        }
    }

    public Task FlushAsync(ShellState liveState)
    {
        lock (_admissionGate)
        {
            var snapshot = ShellStateSnapshotFactory.Clone(liveState);
            return _writer.Enqueue(() => WriteSnapshot(snapshot, allowStale: true));
        }
    }

    internal void Flush(ShellState liveState) => FlushAsync(liveState).GetAwaiter().GetResult();

    public void Dispose() => _writer.Dispose();

    private void WriteCore(ShellState state)
    {
        var serializedState = JsonSerializer.Serialize(state, JsonOptions);
        var directory = Path.GetDirectoryName(_stateFilePath)!;
        Directory.CreateDirectory(directory);

        var tempFilePath = Path.Combine(directory, $"{Path.GetFileName(_stateFilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempFilePath, serializedState);
            File.Move(tempFilePath, _stateFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            TryDeleteTempFile(tempFilePath);
            AppSessionLog.WriteError("Failed to save shell state.", ex);
            throw;
        }
    }

    private static void TryDeleteTempFile(string tempFilePath)
    {
        try
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to delete a temporary shell state file.", ex);
        }
    }

    private static double ClampSecondarySidebarWidth(double value, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            return fallback;
        }

        return Math.Clamp(value, 180, 900);
    }

    private static double ClampBackgroundTaskPopoverWidth(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            return ShellState.DefaultBackgroundTaskPopoverWidth;
        }

        return Math.Clamp(value, ShellState.MinimumBackgroundTaskPopoverWidth, ShellState.MaximumBackgroundTaskPopoverWidth);
    }

    private static double ClampBackgroundTaskPopoverHeight(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            return ShellState.DefaultBackgroundTaskPopoverHeight;
        }

        return Math.Clamp(value, ShellState.MinimumBackgroundTaskPopoverHeight, ShellState.MaximumBackgroundTaskPopoverHeight);
    }
}
