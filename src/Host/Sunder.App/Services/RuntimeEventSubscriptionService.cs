using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed class RuntimeEventSubscriptionService(
    IRuntimeApiClientFactory runtimeApiClientFactory,
    DeveloperLogService developerLog,
    IUiDispatcher uiDispatcher) : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private Task _subscriptionTask = Task.CompletedTask;
    private WindowLauncher? _windowLauncher;
    private HashSet<string> _knownPackageIds = new(StringComparer.OrdinalIgnoreCase);
    private long _appliedGeneration;
    private bool _started;
    private int _disposed;

    public async Task StartAsync(
        WindowLauncher windowLauncher,
        long initialGeneration,
        IReadOnlyList<string> initialPackageIds,
        bool watchDevPackages,
        CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _windowLauncher = windowLauncher;
        _appliedGeneration = initialGeneration;
        _knownPackageIds = initialPackageIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        using (var client = runtimeApiClientFactory.CreateClient())
        {
            await client.SetDevPackageWatchIntentAsync(watchDevPackages, cancellationToken).ConfigureAwait(false);
        }

        _subscriptionTask = RunAsync(_cancellation.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cancellation.Cancel();
        try
        {
            await _subscriptionTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _windowLauncher = null;
            _cancellation.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        long sequenceId = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var client = runtimeApiClientFactory.CreateClient();
                var snapshot = await client.GetRuntimeEventSnapshotAsync(sequenceId, cancellationToken).ConfigureAwait(false);
                sequenceId = snapshot.SequenceId;
                await ApplyGenerationAsync(snapshot.SessionGeneration, snapshot.ActivePackageIds, cancellationToken).ConfigureAwait(false);

                await foreach (var runtimeEvent in client.StreamRuntimeEventsAsync(sequenceId, cancellationToken).ConfigureAwait(false))
                {
                    if (runtimeEvent.SequenceId <= sequenceId)
                    {
                        continue;
                    }

                    sequenceId = runtimeEvent.SequenceId;
                    if (runtimeEvent.Kind is RuntimeEventKind.Snapshot or RuntimeEventKind.SessionGenerationChanged)
                    {
                        await ApplyGenerationAsync(runtimeEvent.SessionGeneration, runtimeEvent.PackageIds, cancellationToken).ConfigureAwait(false);
                    }
                    else if (runtimeEvent.Kind == RuntimeEventKind.DevReloadCompleted)
                    {
                        var message = runtimeEvent.Message ?? (runtimeEvent.Success == true
                            ? "Dev package reload completed."
                            : "Dev package reload failed.");
                        developerLog.Write(
                            runtimeEvent.Success == true ? Sunder.Sdk.Logging.PackageLogLevel.Information : Sunder.Sdk.Logging.PackageLogLevel.Error,
                            "dev.hot_reload",
                            message);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                developerLog.Warning("runtime.events", $"Runtime event stream disconnected: {ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ApplyGenerationAsync(
        long generation,
        IReadOnlyList<string> packageIds,
        CancellationToken cancellationToken)
    {
        if (generation <= Interlocked.Read(ref _appliedGeneration) || _windowLauncher is null)
        {
            return;
        }

        await uiDispatcher.InvokeAsync(async () =>
        {
            if (generation <= _appliedGeneration || _windowLauncher is null)
            {
                return;
            }

            var currentPackageIds = packageIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var impactedPackageIds = _knownPackageIds.Union(currentPackageIds, StringComparer.OrdinalIgnoreCase).ToArray();
            await _windowLauncher.ApplyPackageLifecycleChangesAsync(impactedPackageIds, cancellationToken);
            _knownPackageIds = currentPackageIds;
            Interlocked.Exchange(ref _appliedGeneration, generation);
        });
    }
}
