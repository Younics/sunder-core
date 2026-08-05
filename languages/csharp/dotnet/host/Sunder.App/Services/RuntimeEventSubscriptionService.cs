using System.Diagnostics;
using System.Threading.Channels;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed class RuntimeEventSubscriptionService(
    IRuntimeApiClientFactory runtimeApiClientFactory,
    DeveloperLogService developerLog) : IAsyncDisposable
{
    private static readonly TimeSpan PresentationWaitTimeout = TimeSpan.FromSeconds(30);
    private const int MaximumTerminalOutcomes = 64;
    private const int MaximumRetiredRuntimeInstances = 8;
    private readonly object _syncRoot = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _presentationGate = new(1, 1);
    private readonly Channel<PresentationRefreshRequest> _presentationRequests = Channel.CreateBounded<PresentationRefreshRequest>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly TaskCompletionSource _presentationRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<Guid> _retiredRuntimeInstances = [];
    private readonly Queue<Guid> _retiredRuntimeInstanceOrder = [];
    private readonly Dictionary<(Guid RuntimeInstanceId, long SessionGeneration), PackagePresentationResult> _terminalOutcomes = [];
    private Task _subscriptionTask = Task.CompletedTask;
    private Task _presentationWorkerTask = Task.CompletedTask;
    private CancellationTokenSource? _supersededPresentation;
    private TaskCompletionSource? _presentationsDrained;
    private TaskCompletionSource _appliedStampChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<RuntimePackageSnapshot, IReadOnlyList<PackageUiSnapshotDescriptor>, IReadOnlyCollection<string>?, CancellationToken, Task>? _writePresentationAsync;
    private RuntimePackageSnapshot? _appliedSnapshot;
    private IReadOnlyList<PackageUiSnapshotDescriptor> _appliedPackageSources = [];
    private RuntimePackageSnapshot? _latestSnapshot;
    private long _latestPresentationRequestId;
    private int _activePresentationCount;
    private bool _started;
    private int _disposed;

    internal int TerminalOutcomeCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _terminalOutcomes.Count;
            }
        }
    }

    internal int RetiredRuntimeInstanceCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _retiredRuntimeInstances.Count;
            }
        }
    }

    public Task StartAsync(
        WindowLauncher windowLauncher,
        RuntimePackageSnapshot initialSnapshot,
        IReadOnlyList<PackageUiSnapshotDescriptor> initialPackageSources,
        CancellationToken cancellationToken = default)
        => StartAsync(
            initialSnapshot,
            initialPackageSources,
            windowLauncher.ApplyPackageLifecycleSnapshotAsync,
            cancellationToken);

    internal async Task StartAsync(
        RuntimePackageSnapshot initialSnapshot,
        IReadOnlyList<PackageUiSnapshotDescriptor> initialPackageSources,
        Func<RuntimePackageSnapshot, IReadOnlyList<PackageUiSnapshotDescriptor>, IReadOnlyCollection<string>?, CancellationToken, Task> writePresentationAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writePresentationAsync);
        ValidateInitialSnapshot(initialSnapshot);

        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_started)
            {
                return;
            }

            InitializePresentationCore(initialSnapshot, initialPackageSources, writePresentationAsync);
            _started = true;
        }

        try
        {
            lock (_syncRoot)
            {
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                _presentationWorkerTask = RunPresentationWorkerAsync(_cancellation.Token);
                _subscriptionTask = RunEventReaderAsync(initialSnapshot.EventSequence, _cancellation.Token);
            }
        }
        catch
        {
            lock (_syncRoot)
            {
                if (ReferenceEquals(_writePresentationAsync, writePresentationAsync))
                {
                    ResetPresentationCore();
                }
            }
            throw;
        }
    }

    public async Task WaitUntilAppliedAsync(
        RuntimePackageStamp stamp,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCancellation = new CancellationTokenSource(PresentationWaitTimeout);
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        PackagePresentationResult result;
        try
        {
            result = await WaitForPresentationAsync(stamp, waitCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCancellation.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The running shell did not apply Runtime package generation {stamp.SessionGeneration} within {PresentationWaitTimeout.TotalSeconds:0} seconds.");
        }
        if (result.Outcome != PackagePresentationOutcome.Applied)
        {
            throw result.CreateException();
        }
    }

    internal async Task<PackagePresentationResult> WaitForPresentationAsync(
        RuntimePackageStamp stamp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        if (stamp.RuntimeInstanceId == Guid.Empty || stamp.SessionGeneration < 0)
        {
            throw new ArgumentException("The Runtime package stamp is invalid.", nameof(stamp));
        }

        while (true)
        {
            Task changed;
            lock (_syncRoot)
            {
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                if (IsAppliedOrSuperseded(stamp))
                {
                    return PackagePresentationResult.Applied;
                }
                if (_terminalOutcomes.TryGetValue((stamp.RuntimeInstanceId, stamp.SessionGeneration), out var terminalOutcome))
                {
                    return terminalOutcome;
                }
                changed = _appliedStampChanged.Task;
            }

            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellation.Token);
            await changed.WaitAsync(waitCancellation.Token).ConfigureAwait(false);
        }
    }

    internal void ReleasePresentation()
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (!_started)
            {
                throw new InvalidOperationException("The Runtime event subscription has not been started.");
            }
        }

        _presentationRelease.TrySetResult();
    }

    private async Task RefreshPresentationAsync(
        PresentationRefreshRequest request,
        CancellationToken cancellationToken = default)
    {
        using var client = runtimeApiClientFactory.CreateClient<IRuntimeShellClient>();
        var snapshot = await ShellStartupCoordinator.WaitForReadyRuntimeSnapshotAsync(
            client,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var packageSources = await client.GetActivePackageUiSnapshotsAsync(
            AppPackageTargetEnvironment.CurrentRid,
            cancellationToken).ConfigureAwait(false);
        ShellStartupCoordinator.ValidatePackageSourceGeneration(snapshot, packageSources);
        await ApplySnapshotAsync(
            snapshot,
            packageSources,
            request.ExplicitRetryPackageIds,
            cancellationToken).ConfigureAwait(false);
    }

    internal void InitializePresentation(
        RuntimePackageSnapshot initialSnapshot,
        IReadOnlyList<PackageUiSnapshotDescriptor> initialPackageSources,
        Func<RuntimePackageSnapshot, IReadOnlyList<PackageUiSnapshotDescriptor>, IReadOnlyCollection<string>?, CancellationToken, Task> writePresentationAsync)
    {
        ArgumentNullException.ThrowIfNull(writePresentationAsync);
        ValidateInitialSnapshot(initialSnapshot);

        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            InitializePresentationCore(initialSnapshot, initialPackageSources, writePresentationAsync);
        }
    }

    internal async Task ApplySnapshotAsync(
        RuntimePackageSnapshot snapshot,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IReadOnlyCollection<string>? explicitRetryPackageIds = null,
        CancellationToken cancellationToken = default)
    {
        if (snapshot.BootstrapState == RuntimeBootstrapState.Failed)
        {
            var failure = CreateBootstrapFailure(snapshot);
            RecordTerminalFailure(snapshot.RuntimeInstanceId, snapshot.SessionGeneration, failure);
            throw failure;
        }
        if (snapshot.BootstrapState != RuntimeBootstrapState.Ready)
        {
            return;
        }

        CancellationTokenSource supersededPresentation;
        ShellStartupCoordinator.ValidatePackageSourceGeneration(snapshot, packageSources);
        Func<RuntimePackageSnapshot, IReadOnlyList<PackageUiSnapshotDescriptor>, IReadOnlyCollection<string>?, CancellationToken, Task> writePresentationAsync;
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            writePresentationAsync = _writePresentationAsync
                ?? throw new InvalidOperationException("The Runtime presentation writer has not been initialized.");
            if (_latestSnapshot is { } tracked
                && tracked.RuntimeInstanceId == snapshot.RuntimeInstanceId
                && snapshot.SessionGeneration == tracked.SessionGeneration)
            {
                if (snapshot.EventSequence > tracked.EventSequence)
                {
                    _latestSnapshot = snapshot;
                }
                return;
            }
            if (!ShouldAcceptSnapshot(snapshot))
            {
                return;
            }

            _terminalOutcomes.Remove((snapshot.RuntimeInstanceId, snapshot.SessionGeneration));
            _latestSnapshot = snapshot;
            _supersededPresentation?.Cancel();
            supersededPresentation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellation.Token);
            _supersededPresentation = supersededPresentation;
            if (++_activePresentationCount == 1)
            {
                _presentationsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        var visibleCommitSucceeded = false;
        try
        {
            await _presentationGate.WaitAsync(supersededPresentation.Token).ConfigureAwait(false);
            try
            {
                supersededPresentation.Token.ThrowIfCancellationRequested();
                IReadOnlyList<PackageUiSnapshotDescriptor> previousPackageSources;
                lock (_syncRoot)
                {
                    if (!ReferenceEquals(_supersededPresentation, supersededPresentation)
                        || _retiredRuntimeInstances.Contains(snapshot.RuntimeInstanceId))
                    {
                        return;
                    }
                    previousPackageSources = _appliedPackageSources;
                }

                var retryDisabledPackageIds = GetRetryDisabledPackageIds(
                    previousPackageSources,
                    packageSources,
                    explicitRetryPackageIds);
                await writePresentationAsync(
                    snapshot,
                    packageSources,
                    retryDisabledPackageIds,
                    supersededPresentation.Token).ConfigureAwait(false);

                lock (_syncRoot)
                {
                    if (_appliedSnapshot is { } applied
                        && applied.RuntimeInstanceId != snapshot.RuntimeInstanceId)
                    {
                        RetireRuntimeInstance(applied.RuntimeInstanceId);
                    }

                    _appliedSnapshot = snapshot;
                    _appliedPackageSources = packageSources.ToArray();
                    PrunePresentationHistory(snapshot);
                    var changed = _appliedStampChanged;
                    _appliedStampChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    changed.TrySetResult();
                    visibleCommitSucceeded = true;
                }
            }
            finally
            {
                _presentationGate.Release();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && supersededPresentation.IsCancellationRequested)
        {
            // A newer Runtime stamp owns the next presentation commit.
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            RecordTerminalFailure(snapshot.RuntimeInstanceId, snapshot.SessionGeneration, ex);
            throw;
        }
        finally
        {
            TaskCompletionSource? presentationsDrained = null;
            lock (_syncRoot)
            {
                if (ReferenceEquals(_supersededPresentation, supersededPresentation))
                {
                    _supersededPresentation = null;
                    if (!visibleCommitSucceeded)
                    {
                        _latestSnapshot = _appliedSnapshot;
                    }
                }

                if (--_activePresentationCount == 0)
                {
                    presentationsDrained = _presentationsDrained;
                }
            }

            supersededPresentation.Dispose();
            presentationsDrained?.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cancellation.Cancel();
        _presentationRequests.Writer.TryComplete();
        _presentationRelease.TrySetResult();
        Task presentationsDrained;
        lock (_syncRoot)
        {
            _supersededPresentation?.Cancel();
            _appliedStampChanged.TrySetResult();
            presentationsDrained = _presentationsDrained?.Task ?? Task.CompletedTask;
        }
        try
        {
            await _subscriptionTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            try
            {
                await _presentationWorkerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            await presentationsDrained.ConfigureAwait(false);
            lock (_syncRoot)
            {
                _writePresentationAsync = null;
                _supersededPresentation?.Dispose();
                _supersededPresentation = null;
            }
            _presentationGate.Dispose();
            _cancellation.Dispose();
        }
    }

    private void InitializePresentationCore(
        RuntimePackageSnapshot initialSnapshot,
        IReadOnlyList<PackageUiSnapshotDescriptor> initialPackageSources,
        Func<RuntimePackageSnapshot, IReadOnlyList<PackageUiSnapshotDescriptor>, IReadOnlyCollection<string>?, CancellationToken, Task> writePresentationAsync)
    {
        if (_writePresentationAsync is not null)
        {
            throw new InvalidOperationException("The Runtime presentation writer is already initialized.");
        }

        ShellStartupCoordinator.ValidatePackageSourceGeneration(initialSnapshot, initialPackageSources);
        _writePresentationAsync = writePresentationAsync;
        _appliedSnapshot = initialSnapshot;
        _appliedPackageSources = initialPackageSources.ToArray();
        _latestSnapshot = initialSnapshot;
    }

    private void ResetPresentationCore()
    {
        _started = false;
        _writePresentationAsync = null;
        _appliedSnapshot = null;
        _appliedPackageSources = [];
        _latestSnapshot = null;
        _retiredRuntimeInstances.Clear();
        _retiredRuntimeInstanceOrder.Clear();
        _terminalOutcomes.Clear();
    }

    private static void ValidateInitialSnapshot(RuntimePackageSnapshot initialSnapshot)
    {
        if (initialSnapshot.RuntimeInstanceId == Guid.Empty || initialSnapshot.BootstrapState != RuntimeBootstrapState.Ready)
        {
            throw new ArgumentException("The initial Runtime package snapshot must identify a ready Runtime instance.", nameof(initialSnapshot));
        }
    }

    private async Task RunEventReaderAsync(long initialSequenceId, CancellationToken cancellationToken)
    {
        var sequenceId = initialSequenceId;
        var reconnectFailures = 0;
        Guid runtimeInstanceId;
        lock (_syncRoot)
        {
            runtimeInstanceId = _latestSnapshot?.RuntimeInstanceId ?? Guid.Empty;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var connectedAt = Stopwatch.GetTimestamp();
            try
            {
                using var client = runtimeApiClientFactory.CreateClient<IRuntimeEventClient>();
                var snapshot = await client.GetRuntimeEventSnapshotAsync(sequenceId, cancellationToken).ConfigureAwait(false);
                if (snapshot.RuntimeInstanceId != runtimeInstanceId)
                {
                    using var handshakeClient = runtimeApiClientFactory.CreateClient<IRuntimeDevPackageOwnerClient>();
                    var handshake = await handshakeClient.GetRuntimeHandshakeAsync(cancellationToken).ConfigureAwait(false);
                    if (handshake.RuntimeInstanceId != snapshot.RuntimeInstanceId)
                    {
                        throw new InvalidDataException("Runtime instance changed while refreshing protocol negotiation.");
                    }
                    runtimeInstanceId = snapshot.RuntimeInstanceId;
                    sequenceId = snapshot.SequenceId;
                    QueuePresentationRefresh(
                        snapshot.RuntimeInstanceId,
                        snapshot.SessionGeneration,
                        GetCatchUpRetryPackageIds(snapshot));
                }
                else
                {
                    sequenceId = Math.Max(sequenceId, snapshot.SequenceId);
                    if (IsNewPresentationStamp(snapshot.RuntimeInstanceId, snapshot.SessionGeneration))
                    {
                        QueuePresentationRefresh(
                            snapshot.RuntimeInstanceId,
                            snapshot.SessionGeneration,
                            GetCatchUpRetryPackageIds(snapshot));
                    }
                }

                await foreach (var runtimeEvent in client.StreamRuntimeEventsAsync(sequenceId, cancellationToken).ConfigureAwait(false))
                {
                    if (runtimeEvent.RuntimeInstanceId == runtimeInstanceId && runtimeEvent.SequenceId <= sequenceId)
                    {
                        continue;
                    }

                    if (runtimeEvent.RuntimeInstanceId != runtimeInstanceId)
                    {
                        sequenceId = 0;
                        break;
                    }
                    sequenceId = runtimeEvent.SequenceId;
                    if (runtimeEvent.Kind is RuntimeEventKind.Snapshot
                        or RuntimeEventKind.SessionGenerationChanged
                        or RuntimeEventKind.BootstrapStateChanged)
                    {
                        QueuePresentationRefresh(
                            runtimeEvent.RuntimeInstanceId,
                            runtimeEvent.SessionGeneration,
                            runtimeEvent.Kind == RuntimeEventKind.SessionGenerationChanged
                                ? runtimeEvent.PackageIds
                                : null);
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

                reconnectFailures = ResetBackoffAfterStableConnection(connectedAt, reconnectFailures);
                await Task.Delay(
                    RuntimeReconnectBackoff.GetDelay(reconnectFailures++),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                developerLog.Warning("runtime.events", $"Runtime event stream disconnected: {ex.Message}");
                reconnectFailures = ResetBackoffAfterStableConnection(connectedAt, reconnectFailures);
                await Task.Delay(
                    RuntimeReconnectBackoff.GetDelay(reconnectFailures++),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static int ResetBackoffAfterStableConnection(long connectedAt, int failures)
        => Stopwatch.GetElapsedTime(connectedAt) >= TimeSpan.FromSeconds(30) ? 0 : failures;

    private async Task RunPresentationWorkerAsync(CancellationToken cancellationToken)
    {
        await _presentationRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        while (await _presentationRequests.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            PresentationRefreshRequest? request = null;
            while (_presentationRequests.Reader.TryRead(out var pending))
            {
                request = pending;
            }
            if (request is null)
            {
                continue;
            }

            try
            {
                await RefreshPresentationAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                developerLog.Warning(
                    "runtime.presentation",
                    $"Runtime package presentation for generation {request.SessionGeneration} failed: {ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                RequeuePresentationRetry(request);
            }
        }
    }

    private void QueuePresentationRefresh(
        Guid runtimeInstanceId,
        long sessionGeneration,
        IReadOnlyCollection<string>? explicitRetryPackageIds)
    {
        PresentationRefreshRequest request;
        lock (_syncRoot)
        {
            if (_disposed != 0)
            {
                return;
            }

            request = new PresentationRefreshRequest(
                ++_latestPresentationRequestId,
                runtimeInstanceId,
                sessionGeneration,
                explicitRetryPackageIds?.ToArray());
            if (_activePresentationCount > 0
                && _latestSnapshot is { } active
                && (active.RuntimeInstanceId != runtimeInstanceId
                    || sessionGeneration > active.SessionGeneration))
            {
                _supersededPresentation?.Cancel();
            }
        }

        _presentationRequests.Writer.TryWrite(request);
    }

    private void RequeuePresentationRetry(PresentationRefreshRequest request)
    {
        lock (_syncRoot)
        {
            if (_disposed != 0 || request.RequestId != _latestPresentationRequestId)
            {
                return;
            }
        }

        _presentationRequests.Writer.TryWrite(request);
    }

    private bool ShouldAcceptSnapshot(RuntimePackageSnapshot snapshot)
    {
        if (snapshot.RuntimeInstanceId == Guid.Empty || _retiredRuntimeInstances.Contains(snapshot.RuntimeInstanceId))
        {
            return false;
        }
        if (_latestSnapshot is not { } latest || latest.RuntimeInstanceId != snapshot.RuntimeInstanceId)
        {
            return true;
        }
        return snapshot.SessionGeneration > latest.SessionGeneration
               && snapshot.EventSequence >= latest.EventSequence;
    }

    private bool IsNewPresentationStamp(Guid runtimeInstanceId, long generation)
    {
        lock (_syncRoot)
        {
            return _latestSnapshot is not { } latest
                   || latest.RuntimeInstanceId != runtimeInstanceId
                   || generation > latest.SessionGeneration;
        }
    }

    private bool IsAppliedOrSuperseded(RuntimePackageStamp stamp)
    {
        if (_appliedSnapshot is not { } applied)
        {
            return false;
        }
        if (applied.RuntimeInstanceId == stamp.RuntimeInstanceId)
        {
            return applied.SessionGeneration >= stamp.SessionGeneration;
        }
        return _retiredRuntimeInstances.Contains(stamp.RuntimeInstanceId);
    }

    private static IReadOnlyCollection<string> GetRetryDisabledPackageIds(
        IReadOnlyList<PackageUiSnapshotDescriptor> previousPackageSources,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IReadOnlyCollection<string>? explicitRetryPackageIds)
    {
        var packageIds = explicitRetryPackageIds is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(explicitRetryPackageIds, StringComparer.OrdinalIgnoreCase);
        var previousSources = previousPackageSources
            .GroupBy(source => source.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var source in packageSources)
        {
            if (!previousSources.TryGetValue(source.PackageId, out var previous)
                || !string.Equals(previous.ContentHash, source.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                packageIds.Add(source.PackageId);
            }
        }

        return packageIds;
    }

    private static IReadOnlyCollection<string>? GetCatchUpRetryPackageIds(RuntimeEventSnapshot snapshot)
    {
        var packageIds = snapshot.Events
            .Where(runtimeEvent => runtimeEvent.Kind == RuntimeEventKind.SessionGenerationChanged
                                   && runtimeEvent.RuntimeInstanceId == snapshot.RuntimeInstanceId
                                   && runtimeEvent.SessionGeneration == snapshot.SessionGeneration)
            .SelectMany(runtimeEvent => runtimeEvent.PackageIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return packageIds.Length == 0 ? null : packageIds;
    }

    private void RecordTerminalFailure(Guid runtimeInstanceId, long sessionGeneration, Exception exception)
    {
        if (runtimeInstanceId == Guid.Empty || sessionGeneration < 0)
        {
            return;
        }

        TaskCompletionSource? changed = null;
        lock (_syncRoot)
        {
            var key = (runtimeInstanceId, sessionGeneration);
            if (_terminalOutcomes.ContainsKey(key))
            {
                return;
            }

            _terminalOutcomes[key] = PackagePresentationResult.Failed(
                $"The running shell rejected Runtime package generation {sessionGeneration}: {exception.Message}",
                exception);
            while (_terminalOutcomes.Count > MaximumTerminalOutcomes)
            {
                var oldest = _terminalOutcomes.Keys
                    .OrderBy(item => item.SessionGeneration)
                    .First();
                _terminalOutcomes.Remove(oldest);
            }
            changed = _appliedStampChanged;
            _appliedStampChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        changed.TrySetResult();
    }

    private void RetireRuntimeInstance(Guid runtimeInstanceId)
    {
        if (!_retiredRuntimeInstances.Add(runtimeInstanceId))
        {
            return;
        }

        _retiredRuntimeInstanceOrder.Enqueue(runtimeInstanceId);
        while (_retiredRuntimeInstanceOrder.Count > MaximumRetiredRuntimeInstances)
        {
            _retiredRuntimeInstances.Remove(_retiredRuntimeInstanceOrder.Dequeue());
        }
    }

    private void PrunePresentationHistory(RuntimePackageSnapshot appliedSnapshot)
    {
        foreach (var key in _terminalOutcomes.Keys
                     .Where(key => key.RuntimeInstanceId == appliedSnapshot.RuntimeInstanceId
                                   && key.SessionGeneration <= appliedSnapshot.SessionGeneration
                                   || _retiredRuntimeInstances.Contains(key.RuntimeInstanceId))
                     .ToArray())
        {
            _terminalOutcomes.Remove(key);
        }
    }

    internal static InvalidOperationException CreateBootstrapFailure(RuntimePackageSnapshot snapshot)
    {
        var details = snapshot.Errors.FirstOrDefault()
            ?? snapshot.Warnings.FirstOrDefault()
            ?? "Runtime package bootstrap failed.";
        return new InvalidOperationException(details);
    }

    private sealed record PresentationRefreshRequest(
        long RequestId,
        Guid RuntimeInstanceId,
        long SessionGeneration,
        IReadOnlyCollection<string>? ExplicitRetryPackageIds);
}
