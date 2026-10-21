using System.Diagnostics;
using System.Threading.Channels;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed class RuntimeEventSubscriptionService(
    IRuntimeApiClientFactory runtimeApiClientFactory,
    DeveloperLogService developerLog,
    RuntimePackageRecoveryNotificationService? recoveryNotifications = null) : IAsyncDisposable, IRuntimeExecutionRuntimeStatusSource
{
    private static readonly TimeSpan PresentationWaitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RecoveryPollInterval = TimeSpan.FromMilliseconds(100);
    private const int MaximumTerminalOutcomes = 64;
    private const int MaximumRetiredRuntimeInstances = 8;
    private readonly object _syncRoot = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _presentationGate = new(1, 1);
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
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
    private CancellationTokenSource? _forcedRecoveryPresentation;
    private string? _forcedRecoveryPackageId;
    private string? _forcedRecoveryContentHash;
    private RuntimePackageSnapshot? _forcedRecoverySnapshot;
    private bool _forcedRecoverySuperseded;
    private RecoveryPresentationTransfer? _forcedRecoveryTransfer;
    private RecoveryPresentationTransfer? _recoveryPresentationTransfer;
    private TaskCompletionSource? _presentationsDrained;
    private TaskCompletionSource _appliedStampChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<RuntimePackageSnapshot, IReadOnlyList<PackageUiSnapshotDescriptor>, IReadOnlyCollection<string>?, CancellationToken, Task>? _writePresentationAsync;
    private Func<RuntimePackageSnapshot, CancellationToken, Task>? _writeMetadataAsync;
    private RuntimePackageSnapshot? _appliedSnapshot;
    private IReadOnlyList<PackageUiSnapshotDescriptor> _appliedPackageSources = [];
    private RuntimePackageSnapshot? _latestSnapshot;
    private long _latestPresentationRequestId;
    private int _activePresentationCount;
    private bool _started;
    private int _disposed;

    public event Action? ExecutionRuntimesChanged;

    private void NotifyExecutionRuntimesChanged()
    {
        foreach (var subscriber in ExecutionRuntimesChanged?.GetInvocationList() ?? [])
        {
            try { ((Action)subscriber)(); }
            catch (Exception error) { AppSessionLog.WriteError("Runtime settings change notification failed.", error); }
        }
    }

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
            cancellationToken,
            windowLauncher.ApplyPackageMetadataSnapshotAsync);

    internal async Task StartAsync(
        RuntimePackageSnapshot initialSnapshot,
        IReadOnlyList<PackageUiSnapshotDescriptor> initialPackageSources,
        Func<RuntimePackageSnapshot, IReadOnlyList<PackageUiSnapshotDescriptor>, IReadOnlyCollection<string>?, CancellationToken, Task> writePresentationAsync,
        CancellationToken cancellationToken = default,
        Func<RuntimePackageSnapshot, CancellationToken, Task>? writeMetadataAsync = null)
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

            InitializePresentationCore(
                initialSnapshot,
                initialPackageSources,
                writePresentationAsync,
                writeMetadataAsync);
            recoveryNotifications?.SeedSilently(
                initialSnapshot.RuntimeInstanceId,
                initialSnapshot.EventSequence);
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

    internal Task<AppPackageUiRecoveryPresentationOutcome> ForcePackageRecoveryPresentationAsync(
        string packageId,
        string contentHash,
        CancellationToken cancellationToken = default)
        => ForcePackageRecoveryPresentationAsync(
            packageId,
            contentHash,
            PresentationWaitTimeout,
            cancellationToken);

    internal async Task<AppPackageUiRecoveryPresentationOutcome> ForcePackageRecoveryPresentationAsync(
        string packageId,
        string contentHash,
        TimeSpan presentationWaitTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            presentationWaitTimeout,
            TimeSpan.Zero);

        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_writePresentationAsync is null)
            {
                throw new InvalidOperationException(
                    "The Runtime presentation writer has not been initialized.");
            }
            if (++_activePresentationCount == 1)
            {
                _presentationsDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        using var timeoutCancellation = new CancellationTokenSource(presentationWaitTimeout);
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token,
            _cancellation.Token);
        var recoveryGateHeld = false;
        try
        {
            await _recoveryGate.WaitAsync(waitCancellation.Token).ConfigureAwait(false);
            recoveryGateHeld = true;
            while (true)
            {
                waitCancellation.Token.ThrowIfCancellationRequested();
                var writerStarted = false;
                var shouldRetry = false;
                var presentationGateHeld = false;
                CancellationTokenSource? attemptCancellation = null;
                Task<AppPackageUiRecoveryPresentationOutcome>? transferredRecovery = null;
                try
                {
                    lock (_syncRoot)
                    {
                        ObjectDisposedException.ThrowIf(_disposed != 0, this);
                        attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                            waitCancellation.Token);
                        _forcedRecoveryPresentation = attemptCancellation;
                        _forcedRecoverySuperseded = false;
                        _forcedRecoveryTransfer = null;
                        _forcedRecoveryPackageId = packageId;
                        _forcedRecoveryContentHash = contentHash;
                        _forcedRecoverySnapshot = null;
                        _recoveryPresentationTransfer = null;
                    }

                    AppPackageRecoverySnapshotRead read;
                    try
                    {
                        read = await ReadRecoverySnapshotAsync(
                                packageId,
                                contentHash,
                                attemptCancellation.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException exception) when (
                        !waitCancellation.IsCancellationRequested
                        && attemptCancellation.IsCancellationRequested
                        && AppPackageUiFaultPolicy.IsRecoverable(exception))
                    {
                        if (GetForcedRecoveryTransferTask(attemptCancellation) is { } transfer)
                        {
                            transferredRecovery = transfer;
                        }
                        else
                        {
                            shouldRetry = true;
                        }
                        read = AppPackageRecoverySnapshotRead.Waiting;
                    }

                    if (read.Outcome == AppPackageRecoverySnapshotOutcome.ContentChanged)
                    {
                        return AppPackageUiRecoveryPresentationOutcome.ContentChanged;
                    }
                    if (read.Outcome == AppPackageRecoverySnapshotOutcome.Failed)
                    {
                        throw read.Exception!;
                    }
                    if (read.Outcome != AppPackageRecoverySnapshotOutcome.Ready)
                    {
                        shouldRetry = true;
                    }
                    else
                    {
                        lock (_syncRoot)
                        {
                            if (ReferenceEquals(_forcedRecoveryPresentation, attemptCancellation))
                            {
                                _forcedRecoverySnapshot = read.Snapshot;
                            }
                        }
                        await _presentationGate.WaitAsync(attemptCancellation.Token)
                            .ConfigureAwait(false);
                        presentationGateHeld = true;
                        attemptCancellation.Token.ThrowIfCancellationRequested();

                        Func<RuntimePackageSnapshot, IReadOnlyList<PackageUiSnapshotDescriptor>, IReadOnlyCollection<string>?, CancellationToken, Task> writePresentationAsync;
                        lock (_syncRoot)
                        {
                            if (_retiredRuntimeInstances.Contains(read.Snapshot!.RuntimeInstanceId)
                                || IsNewerGenerationTracked(read.Snapshot))
                            {
                                shouldRetry = true;
                                writePresentationAsync = null!;
                            }
                            else
                            {
                                writePresentationAsync = _writePresentationAsync
                                    ?? throw new InvalidOperationException(
                                        "The Runtime presentation writer has not been initialized.");
                            }
                        }

                        if (!shouldRetry)
                        {
                            writerStarted = true;
                            await writePresentationAsync(
                                    read.Snapshot!,
                                    read.PackageSources!,
                                    AppPackageQuarantineRetryIntent.ForAutomaticPackageRecovery(packageId),
                                    attemptCancellation.Token)
                                .ConfigureAwait(false);

                            var superseded = false;
                            lock (_syncRoot)
                            {
                                if (!ReferenceEquals(_forcedRecoveryPresentation, attemptCancellation)
                                    || _forcedRecoverySuperseded
                                    || _retiredRuntimeInstances.Contains(read.Snapshot.RuntimeInstanceId)
                                    || IsNewerGenerationTracked(read.Snapshot))
                                {
                                    superseded = true;
                                    transferredRecovery = ReferenceEquals(
                                        _forcedRecoveryPresentation,
                                        attemptCancellation)
                                            ? (_forcedRecoveryTransfer ?? _recoveryPresentationTransfer)
                                                ?.Completion.Task
                                            : null;
                                }
                                else
                                {
                                    CommitForcedRecoveryPresentation(
                                        read.Snapshot,
                                        read.PackageSources!);
                                }
                            }
                            if (superseded)
                            {
                                if (transferredRecovery is null)
                                {
                                    return AppPackageUiRecoveryPresentationOutcome.Superseded;
                                }
                            }
                            else
                            {
                                return AppPackageUiRecoveryPresentationOutcome.Applied;
                            }
                        }
                    }
                }
                catch (OperationCanceledException exception) when (
                    !waitCancellation.IsCancellationRequested
                    && attemptCancellation?.IsCancellationRequested == true
                    && AppPackageUiFaultPolicy.IsRecoverable(exception))
                {
                    if (writerStarted)
                    {
                        if (GetForcedRecoveryTransferTask(attemptCancellation) is { } transfer)
                        {
                            transferredRecovery = transfer;
                        }
                        else
                        {
                            return AppPackageUiRecoveryPresentationOutcome.Superseded;
                        }
                    }
                    else if (GetForcedRecoveryTransferTask(attemptCancellation) is { } transfer)
                    {
                        transferredRecovery = transfer;
                    }
                    else
                    {
                        shouldRetry = true;
                    }
                }
                finally
                {
                    lock (_syncRoot)
                    {
                        if (ReferenceEquals(_forcedRecoveryPresentation, attemptCancellation))
                        {
                            _forcedRecoveryPresentation = null;
                            _forcedRecoveryPackageId = null;
                            _forcedRecoveryContentHash = null;
                            _forcedRecoverySnapshot = null;
                            _forcedRecoverySuperseded = false;
                            _forcedRecoveryTransfer = null;
                        }
                    }
                    attemptCancellation?.Dispose();
                    if (presentationGateHeld)
                    {
                        _presentationGate.Release();
                    }
                }

                if (transferredRecovery is not null)
                {
                    try
                    {
                        return await transferredRecovery.WaitAsync(waitCancellation.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException exception) when (
                        !cancellationToken.IsCancellationRequested
                        && timeoutCancellation.IsCancellationRequested
                        && !_cancellation.IsCancellationRequested
                        && AppPackageUiFaultPolicy.IsRecoverable(exception))
                    {
                        lock (_syncRoot)
                        {
                            if (transferredRecovery.IsCompletedSuccessfully)
                            {
                                return transferredRecovery.Result;
                            }
                        }
                        throw;
                    }
                }
                if (shouldRetry)
                {
                    await Task.Delay(RecoveryPollInterval, waitCancellation.Token)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested
            && timeoutCancellation.IsCancellationRequested
            && !_cancellation.IsCancellationRequested
            && AppPackageUiFaultPolicy.IsRecoverable(exception))
        {
            return AppPackageUiRecoveryPresentationOutcome.TimedOut;
        }
        finally
        {
            TaskCompletionSource? presentationsDrained = null;
            lock (_syncRoot)
            {
                if (_recoveryPresentationTransfer is { } transfer
                    && string.Equals(
                        transfer.PackageId,
                        packageId,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        transfer.ContentHash,
                        contentHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _recoveryPresentationTransfer = null;
                }
                if (--_activePresentationCount == 0)
                {
                    presentationsDrained = _presentationsDrained;
                }
            }
            if (recoveryGateHeld)
            {
                _recoveryGate.Release();
            }
            presentationsDrained?.TrySetResult();
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

        var consistent = await ShellStartupCoordinator.LoadConsistentPackageUiStateAsync(
            client, snapshot,
            token => client.GetActivePackageUiSnapshotsAsync(AppPackageTargetEnvironment.CurrentRid, token),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await ApplySnapshotAsync(
            consistent.Snapshot,
            consistent.PackageSources,
            request.ExplicitRetryPackageIds,
            cancellationToken).ConfigureAwait(false);
    }

    internal void InitializePresentation(
        RuntimePackageSnapshot initialSnapshot,
        IReadOnlyList<PackageUiSnapshotDescriptor> initialPackageSources,
        Func<RuntimePackageSnapshot, IReadOnlyList<PackageUiSnapshotDescriptor>, IReadOnlyCollection<string>?, CancellationToken, Task> writePresentationAsync,
        Func<RuntimePackageSnapshot, CancellationToken, Task>? writeMetadataAsync = null)
    {
        ArgumentNullException.ThrowIfNull(writePresentationAsync);
        ValidateInitialSnapshot(initialSnapshot);

        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            InitializePresentationCore(
                initialSnapshot,
                initialPackageSources,
                writePresentationAsync,
                writeMetadataAsync);
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
            CancelForcedRecoveryPresentation();
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
        explicitRetryPackageIds = PrepareForcedRecoverySupersession(
            snapshot,
            packageSources,
            explicitRetryPackageIds,
            out var recoveryTransfer);
        if (await TryApplySameGenerationMetadataAsync(snapshot, packageSources, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        Func<RuntimePackageSnapshot, IReadOnlyList<PackageUiSnapshotDescriptor>, IReadOnlyCollection<string>?, CancellationToken, Task> writePresentationAsync;
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            writePresentationAsync = _writePresentationAsync
                ?? throw new InvalidOperationException("The Runtime presentation writer has not been initialized.");
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
                    if (ReferenceEquals(
                        _recoveryPresentationTransfer,
                        recoveryTransfer))
                    {
                        _recoveryPresentationTransfer = null;
                    }
                    recoveryTransfer?.Completion.TrySetResult(
                        AppPackageUiRecoveryPresentationOutcome.Applied);
                }
            }
            finally
            {
                _presentationGate.Release();
            }
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested
            && supersededPresentation.IsCancellationRequested
            && AppPackageUiFaultPolicy.IsRecoverable(exception))
        {
            // A newer Runtime stamp owns the next presentation commit.
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            recoveryTransfer?.Completion.TrySetException(ex);
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
            SupersedeForcedRecoveryPresentation();
            _recoveryPresentationTransfer?.Completion.TrySetResult(
                AppPackageUiRecoveryPresentationOutcome.Superseded);
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
                _writeMetadataAsync = null;
                _supersededPresentation?.Dispose();
                _supersededPresentation = null;
                _forcedRecoveryPresentation?.Dispose();
                _forcedRecoveryPresentation = null;
                _forcedRecoveryPackageId = null;
                _forcedRecoveryContentHash = null;
                _forcedRecoverySnapshot = null;
                _forcedRecoveryTransfer = null;
                _recoveryPresentationTransfer = null;
            }
            _presentationGate.Dispose();
            _recoveryGate.Dispose();
            _cancellation.Dispose();
        }
    }

    private void InitializePresentationCore(
        RuntimePackageSnapshot initialSnapshot,
        IReadOnlyList<PackageUiSnapshotDescriptor> initialPackageSources,
        Func<RuntimePackageSnapshot, IReadOnlyList<PackageUiSnapshotDescriptor>, IReadOnlyCollection<string>?, CancellationToken, Task> writePresentationAsync,
        Func<RuntimePackageSnapshot, CancellationToken, Task>? writeMetadataAsync)
    {
        if (_writePresentationAsync is not null)
        {
            throw new InvalidOperationException("The Runtime presentation writer is already initialized.");
        }

        ShellStartupCoordinator.ValidatePackageSourceGeneration(initialSnapshot, initialPackageSources);
        _writePresentationAsync = writePresentationAsync;
        _writeMetadataAsync = writeMetadataAsync;
        _appliedSnapshot = initialSnapshot;
        _appliedPackageSources = initialPackageSources.ToArray();
        _latestSnapshot = initialSnapshot;
    }

    private void ResetPresentationCore()
    {
        _started = false;
        _writePresentationAsync = null;
        _writeMetadataAsync = null;
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
                // Re-read after every connection/reconnection, including replay gaps.
                NotifyExecutionRuntimesChanged();
                recoveryNotifications?.SeedSilently(snapshot.RuntimeInstanceId, snapshot.SequenceId);
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
                        explicitRetryPackageIds: null);
                }
                else
                {
                    sequenceId = Math.Max(sequenceId, snapshot.SequenceId);
                    if (IsNewPresentationStamp(snapshot.RuntimeInstanceId, snapshot.SessionGeneration))
                    {
                        QueuePresentationRefresh(
                            snapshot.RuntimeInstanceId,
                            snapshot.SessionGeneration,
                            explicitRetryPackageIds: null);
                    }
                    else if (HasSnapshotMetadataChange(snapshot))
                    {
                        QueuePresentationRefresh(
                            snapshot.RuntimeInstanceId,
                            snapshot.SessionGeneration,
                            explicitRetryPackageIds: null);
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
                    if (runtimeEvent.Kind is RuntimeEventKind.ExecutionRuntimesChanged
                        or RuntimeEventKind.Snapshot or RuntimeEventKind.SessionGenerationChanged
                        or RuntimeEventKind.BootstrapStateChanged or RuntimeEventKind.SnapshotDiagnosticsChanged)
                        NotifyExecutionRuntimesChanged();
                    if (recoveryNotifications is not null)
                    {
                        await recoveryNotifications.HandleAsync(runtimeEvent, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    if (runtimeEvent.Kind is RuntimeEventKind.Snapshot
                        or RuntimeEventKind.SessionGenerationChanged
                        or RuntimeEventKind.BootstrapStateChanged
                        or RuntimeEventKind.SnapshotMetadataChanged)
                    {
                        QueuePresentationRefresh(
                            runtimeEvent.RuntimeInstanceId,
                            runtimeEvent.SessionGeneration,
                            explicitRetryPackageIds: null);
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
                explicitRetryPackageIds switch
                {
                    AppPackageQuarantineRetryIntent retryIntent => retryIntent.Snapshot(),
                    null => null,
                    _ => explicitRetryPackageIds.ToArray(),
                });
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

    private async Task<AppPackageRecoverySnapshotRead> ReadRecoverySnapshotAsync(
        string packageId,
        string contentHash,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = runtimeApiClientFactory.CreateClient<IRuntimeShellClient>();
            var snapshot = await client.GetRuntimePackageSnapshotAsync(cancellationToken)
                .ConfigureAwait(false);
            if (snapshot.BootstrapState == RuntimeBootstrapState.Failed)
            {
                return AppPackageRecoverySnapshotRead.Failed(CreateBootstrapFailure(snapshot));
            }
            if (snapshot.BootstrapState != RuntimeBootstrapState.Ready)
            {
                return AppPackageRecoverySnapshotRead.Waiting;
            }

            var consistent = await ShellStartupCoordinator.LoadConsistentPackageUiStateAsync(
                client, snapshot,
                token => client.GetActivePackageUiSnapshotsAsync(AppPackageTargetEnvironment.CurrentRid, token),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            snapshot = consistent.Snapshot;
            var packageSources = consistent.PackageSources;
            var matchingSources = packageSources
                .Where(source => string.Equals(
                    source.PackageId,
                    packageId,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matchingSources.Any(source => !string.Equals(
                    source.ContentHash,
                    contentHash,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return AppPackageRecoverySnapshotRead.ContentChanged;
            }

            var packageIsActive = snapshot.ActivePackages.Any(package =>
                string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
                && package.IsEnabled
                && (package.HostRoles & PackageHostRoles.App) != 0);
            return packageIsActive && matchingSources.Length == 1
                ? AppPackageRecoverySnapshotRead.Ready(snapshot, packageSources)
                : AppPackageRecoverySnapshotRead.Waiting;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (AppPackageUiFaultPolicy.IsRecoverable(exception))
        {
            return AppPackageRecoverySnapshotRead.WaitingAfter(exception);
        }
    }

    private void CommitForcedRecoveryPresentation(
        RuntimePackageSnapshot snapshot,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources)
    {
        var committedSnapshot = _latestSnapshot is { } latest
            && latest.RuntimeInstanceId == snapshot.RuntimeInstanceId
            && latest.SessionGeneration == snapshot.SessionGeneration
            && latest.EventSequence > snapshot.EventSequence
                ? latest
                : snapshot;
        if (_appliedSnapshot is { } applied
            && applied.RuntimeInstanceId != committedSnapshot.RuntimeInstanceId)
        {
            RetireRuntimeInstance(applied.RuntimeInstanceId);
        }

        _terminalOutcomes.Remove((
            committedSnapshot.RuntimeInstanceId,
            committedSnapshot.SessionGeneration));
        _latestSnapshot = committedSnapshot;
        _appliedSnapshot = committedSnapshot;
        _appliedPackageSources = packageSources.ToArray();
        PrunePresentationHistory(committedSnapshot);
        var changed = _appliedStampChanged;
        _appliedStampChanged = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        changed.TrySetResult();
    }

    private bool IsNewerGenerationTracked(RuntimePackageSnapshot snapshot)
        => _latestSnapshot is { } latest
            && latest.RuntimeInstanceId == snapshot.RuntimeInstanceId
            && latest.SessionGeneration > snapshot.SessionGeneration;

    private IReadOnlyCollection<string>? PrepareForcedRecoverySupersession(
        RuntimePackageSnapshot snapshot,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IReadOnlyCollection<string>? explicitRetryPackageIds,
        out RecoveryPresentationTransfer? transfer)
    {
        transfer = null;
        lock (_syncRoot)
        {
            if (_forcedRecoveryPresentation is null
                && _recoveryPresentationTransfer is null)
            {
                return explicitRetryPackageIds;
            }
            var recoveryPackageId = _recoveryPresentationTransfer?.PackageId
                ?? _forcedRecoveryPackageId!;
            var recoveryContentHash = _recoveryPresentationTransfer?.ContentHash
                ?? _forcedRecoveryContentHash!;
            if (_forcedRecoveryTransfer?.Completion.Task.IsCompletedSuccessfully == true)
            {
                return explicitRetryPackageIds;
            }
            var recoverySnapshot = _forcedRecoverySnapshot ?? _latestSnapshot;
            if (recoverySnapshot is null)
            {
                SupersedeForcedRecoveryPresentation();
                return explicitRetryPackageIds;
            }
            if (!ShouldAcceptSnapshot(snapshot)
                && (snapshot.RuntimeInstanceId != recoverySnapshot.RuntimeInstanceId
                    || snapshot.SessionGeneration != recoverySnapshot.SessionGeneration))
            {
                return explicitRetryPackageIds;
            }
            var supersedesRecovery = snapshot.RuntimeInstanceId != recoverySnapshot.RuntimeInstanceId
                || snapshot.SessionGeneration > recoverySnapshot.SessionGeneration;
            var exactTargetIsActive = IsExactRecoveryTargetActive(
                snapshot,
                packageSources,
                recoveryPackageId,
                recoveryContentHash);
            if (!supersedesRecovery)
            {
                if (snapshot.RuntimeInstanceId == recoverySnapshot.RuntimeInstanceId
                    && snapshot.SessionGeneration == recoverySnapshot.SessionGeneration
                    && !exactTargetIsActive)
                {
                    SupersedeForcedRecoveryPresentation();
                }
                return explicitRetryPackageIds;
            }

            if (!exactTargetIsActive)
            {
                _recoveryPresentationTransfer?.Completion.TrySetResult(
                    AppPackageUiRecoveryPresentationOutcome.ContentChanged);
                _recoveryPresentationTransfer = null;
                SupersedeForcedRecoveryPresentation();
                return explicitRetryPackageIds;
            }

            transfer = _recoveryPresentationTransfer ??= new RecoveryPresentationTransfer(
                recoveryPackageId,
                recoveryContentHash);
            _forcedRecoveryTransfer = transfer;
            SupersedeForcedRecoveryPresentation();
            return AppPackageQuarantineRetryIntent.ForAutomaticPackageRecovery(
                (explicitRetryPackageIds ?? []).Append(transfer.PackageId));
        }
    }

    private void CancelForcedRecoveryPresentation()
    {
        lock (_syncRoot)
        {
            _recoveryPresentationTransfer?.Completion.TrySetResult(
                AppPackageUiRecoveryPresentationOutcome.Superseded);
            SupersedeForcedRecoveryPresentation();
        }
    }

    private void SupersedeForcedRecoveryPresentation()
    {
        _forcedRecoverySuperseded = true;
        _forcedRecoveryPresentation?.Cancel();
    }

    private Task<AppPackageUiRecoveryPresentationOutcome>? GetForcedRecoveryTransferTask(
        CancellationTokenSource attemptCancellation)
    {
        lock (_syncRoot)
        {
            return ReferenceEquals(_forcedRecoveryPresentation, attemptCancellation)
                ? (_forcedRecoveryTransfer ?? _recoveryPresentationTransfer)?.Completion.Task
                : null;
        }
    }

    private static bool IsExactRecoveryTargetActive(
        RuntimePackageSnapshot snapshot,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        string packageId,
        string contentHash)
    {
        var packageIsActive = snapshot.ActivePackages.Any(package =>
            string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
            && package.IsEnabled
            && (package.HostRoles & PackageHostRoles.App) != 0);
        if (!packageIsActive)
        {
            return false;
        }

        var matchingSources = packageSources
            .Where(source => string.Equals(
                source.PackageId,
                packageId,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matchingSources.Length == 1
            && string.Equals(
                matchingSources[0].ContentHash,
                contentHash,
                StringComparison.OrdinalIgnoreCase);
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
        return snapshot.SessionGeneration > latest.SessionGeneration && snapshot.EventSequence >= latest.EventSequence
            || snapshot.SessionGeneration == latest.SessionGeneration && snapshot.EventSequence > latest.EventSequence;
    }

    private async Task<bool> TryApplySameGenerationMetadataAsync(
        RuntimePackageSnapshot snapshot,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        CancellationToken cancellationToken)
    {
        Func<RuntimePackageSnapshot, CancellationToken, Task>? writeMetadataAsync;
        lock (_syncRoot)
        {
            if (_latestSnapshot is not { } tracked
                || tracked.RuntimeInstanceId != snapshot.RuntimeInstanceId
                || tracked.SessionGeneration != snapshot.SessionGeneration)
            {
                return false;
            }
            if (snapshot.EventSequence <= tracked.EventSequence)
            {
                return true;
            }

            if ((_appliedPackageSources.Any(source => source.ActivationFingerprint is not null)
                    || packageSources.Any(source => source.ActivationFingerprint is not null))
                && !_appliedPackageSources.Select(source => (source.PackageId, source.ActivationFingerprint)).OrderBy(item => item.PackageId, StringComparer.Ordinal)
                    .SequenceEqual(packageSources.Select(source => (source.PackageId, source.ActivationFingerprint)).OrderBy(item => item.PackageId, StringComparer.Ordinal)))
                return false;

            _latestSnapshot = snapshot;
            writeMetadataAsync = _writeMetadataAsync;
            if (writeMetadataAsync is null)
            {
                if (_appliedSnapshot is { } applied
                    && applied.RuntimeInstanceId == snapshot.RuntimeInstanceId
                    && applied.SessionGeneration == snapshot.SessionGeneration)
                {
                    _appliedSnapshot = snapshot;
                }
                return true;
            }
        }

        try
        {
            await _presentationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await writeMetadataAsync(snapshot, cancellationToken).ConfigureAwait(false);
                lock (_syncRoot)
                {
                    if (_latestSnapshot is { } latest
                        && latest.RuntimeInstanceId == snapshot.RuntimeInstanceId
                        && latest.SessionGeneration == snapshot.SessionGeneration
                        && latest.EventSequence == snapshot.EventSequence)
                    {
                        _appliedSnapshot = snapshot;
                    }
                }
            }
            finally
            {
                _presentationGate.Release();
            }
        }
        catch
        {
            lock (_syncRoot)
            {
                if (_latestSnapshot is { } latest
                    && latest.RuntimeInstanceId == snapshot.RuntimeInstanceId
                    && latest.SessionGeneration == snapshot.SessionGeneration
                    && latest.EventSequence == snapshot.EventSequence)
                {
                    _latestSnapshot = _appliedSnapshot;
                }
            }
            throw;
        }
        return true;
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

        return explicitRetryPackageIds is AppPackageQuarantineRetryIntent retryIntent
            ? retryIntent.WithPackageIds(packageIds)
            : packageIds;
    }

    private static bool HasSnapshotMetadataChange(RuntimeEventSnapshot snapshot)
        => snapshot.Events.Any(runtimeEvent =>
            runtimeEvent.Kind == RuntimeEventKind.SnapshotMetadataChanged
            && runtimeEvent.RuntimeInstanceId == snapshot.RuntimeInstanceId
            && runtimeEvent.SessionGeneration == snapshot.SessionGeneration);

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

    private sealed record RecoveryPresentationTransfer(
        string PackageId,
        string ContentHash)
    {
        public TaskCompletionSource<AppPackageUiRecoveryPresentationOutcome> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private enum AppPackageRecoverySnapshotOutcome
    {
        Waiting,
        Ready,
        ContentChanged,
        Failed,
    }

    private sealed record AppPackageRecoverySnapshotRead(
        AppPackageRecoverySnapshotOutcome Outcome,
        RuntimePackageSnapshot? Snapshot = null,
        IReadOnlyList<PackageUiSnapshotDescriptor>? PackageSources = null,
        Exception? Exception = null)
    {
        public static AppPackageRecoverySnapshotRead Waiting { get; } = new(
            AppPackageRecoverySnapshotOutcome.Waiting);

        public static AppPackageRecoverySnapshotRead ContentChanged { get; } = new(
            AppPackageRecoverySnapshotOutcome.ContentChanged);

        public static AppPackageRecoverySnapshotRead Ready(
            RuntimePackageSnapshot snapshot,
            IReadOnlyList<PackageUiSnapshotDescriptor> packageSources)
            => new(AppPackageRecoverySnapshotOutcome.Ready, snapshot, packageSources);

        public static AppPackageRecoverySnapshotRead Failed(Exception exception)
            => new(AppPackageRecoverySnapshotOutcome.Failed, Exception: exception);

        public static AppPackageRecoverySnapshotRead WaitingAfter(Exception exception)
            => new(AppPackageRecoverySnapshotOutcome.Waiting, Exception: exception);
    }
}
