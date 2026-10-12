using Sunder.App.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.ViewModels;

public sealed partial class SettingsPackageRuntimesViewModel
{
    private const string RuntimeSourceMetadataKey = "sunder.runtime.source";
    private CancellationTokenSource? _refreshCancellation;
    private bool _refreshRequested;
    private bool _refreshRunning;
    private string? _refreshError;
    private PackageRuntimeFamilyViewModel? _foregroundFamily;

    private void OnRuntimeStatusChanged()
        => _tasks.Observe(_dispatcher.InvokeAsync(RequestStatusRefresh), "receiving package runtime changes");

    private void RequestStatusRefresh()
    {
        if (_disposed || _page is null) return;
        _refreshRequested = true;
        if (_refreshRunning) return;
        _refreshRunning = true;
        _tasks.Observe(RefreshStatusFromNotificationsAsync(_page), "reading changed package runtime status");
    }

    private async Task RefreshStatusFromNotificationsAsync(CancellationTokenSource page)
    {
        var token = page.Token;
        try
        {
            // Coalesce event bursts before touching the transport or bound properties.
            await Task.Yield();
            while (_refreshRequested && ReferenceEquals(_page, page) && !token.IsCancellationRequested)
            {
                if (_operationCompletion is { } foreground) await foreground.Task.WaitAsync(token);
                _refreshRequested = false;
                using var refresh = CancellationTokenSource.CreateLinkedTokenSource(token);
                _refreshCancellation = refresh;
                var entered = false;
                try
                {
                    await _requests.WaitAsync(refresh.Token);
                    entered = true;
                    var settings = await _client.GetExecutionRuntimeSettingsAsync(refresh.Token);
                    refresh.Token.ThrowIfCancellationRequested();
                    ApplySettings(settings);
                    // Local state only. Vendor discovery is on entry or explicit Check.
                    await RefreshInventoryAndStatusAsync(refresh.Token);
                    if (ErrorText == _refreshError) ErrorText = string.Empty;
                    _refreshError = null;
                }
                catch (OperationCanceledException) when (refresh.IsCancellationRequested)
                {
                    if (!token.IsCancellationRequested) _refreshRequested = true;
                }
                catch (Exception error)
                {
                    if (ReferenceEquals(_page, page))
                    {
                        _refreshError = "Runtime status could not be refreshed. " + error.Message;
                        ErrorText = _refreshError;
                    }
                    AppSessionLog.WriteError("Could not refresh changed package runtime status.", error);
                }
                finally
                {
                    if (entered) _requests.Release();
                    if (ReferenceEquals(_refreshCancellation, refresh)) _refreshCancellation = null;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            _refreshRunning = false;
            if (_refreshRequested && _page is not null && !_disposed) RequestStatusRefresh();
        }
    }

    private void OnQueuedProcessChanged(object? sender, BackgroundProcessChangedEventArgs args)
    {
        if (!args.Snapshot.Metadata.ContainsKey(RuntimeSourceMetadataKey)) return;
        _tasks.Observe(_dispatcher.InvokeAsync(() =>
        {
            if (_disposed || _page is null) return;
            RefreshQueuedOperations();
            if (args.Snapshot.IsTerminal) RequestStatusRefresh();
        }), "receiving runtime installation progress");
    }

    private void RefreshQueuedOperations()
    {
        var processes = _queue.ListProcesses(PackageOperationService.PackageStoreGroupKey);
        foreach (var family in Families)
        {
            if (ReferenceEquals(family, _foregroundFamily)) continue;
            var process = processes.Where(item => item.Metadata.GetValueOrDefault(RuntimeSourceMetadataKey) == family.Source.ToString())
                .OrderByDescending(item => item.IsActive).ThenByDescending(item => item.QueuedAtUtc).FirstOrDefault();
            family.SetOperation(process?.IsActive == true ? process.StatusText : null,
                process?.ProgressPercent, process?.State == BackgroundProcessState.Failed ? process.ErrorMessage : null);
        }
    }

    private async Task RunFamilyOperationAsync(PackageRuntimeFamilyViewModel? family, string status, Func<Task> action)
    {
        if (family is null || !CanAct || family.IsWorking) return;
        _foregroundFamily = family;
        family.SetOperation(status);
        try { await action(); }
        finally
        {
            _foregroundFamily = null;
            RefreshQueuedOperations();
        }
    }
}
