using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Settings;
using Sunder.Package.Format;

namespace Sunder.Runtime.Host.Services;

internal sealed record ActiveLoadedPackage(
    ActivePackageDescriptor Descriptor,
    RuntimePackageSource Source,
    PackageSettingsSchemaDescriptor? SettingsSchema,
    IPackageKeyValueStore StateStore,
    JsonPackageSecretsStore SecretsStore,
    IPackageFileStore FileStore,
    IPackageSettings Settings,
    IRuntimePackageWorker Worker)
{
    public Guid RuntimeActivationId { get; init; } = Guid.NewGuid();

    public PackageSettingsSchema? CanonicalSettingsSchema { get; init; }

    public IReadOnlyDictionary<string, RuntimeRpcProviderRegistration> RpcProviders { get; init; }
        = new Dictionary<string, RuntimeRpcProviderRegistration>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, SunderRpcContractDescriptor> RpcContracts { get; init; }
        = new Dictionary<string, SunderRpcContractDescriptor>(StringComparer.Ordinal);

    public IReadOnlyList<SunderPackageContractUseManifest> RpcContractUses { get; init; } = [];

    public string RpcManifestSha256 { get; init; } = string.Empty;

    public PackageStorageMigrationJournal? MigrationJournal { get; init; }

    public Func<ActiveLoadedPackage>? RecoveryFactory { get; init; }

    public RuntimePackageExecutionRuntimeBinding? ExecutionRuntimeBinding { get; init; }

    public IPackageAuthHandler? AuthHandler => Worker.AuthHandler;

    public IPackageCallbackHandler? GetCallbackHandler(string callbackHandlerId)
        => Worker.GetCallbackHandler(callbackHandlerId);
}

internal sealed record DrainingPackageRecoveryCandidate(
    DrainingPackageFault Fault,
    ActiveLoadedPackage FailedPackage,
    DateTimeOffset FailedAtUtc);

internal sealed class ActivePackageSession
{
    private static readonly TimeSpan DefaultCleanupTimeout = TimeSpan.FromSeconds(10);
    private readonly Dictionary<string, ActiveLoadedPackage> _loadedPackageMap;
    private readonly Dictionary<string, SessionPackageDescriptor> _sessionPackageMap;
    private readonly Dictionary<string, RuntimePackageSource> _packageSourceMap;
    private readonly IReadOnlyList<IReadOnlyList<string>> _workerStartupWaves;
    private readonly int _maximumWorkerStartupConcurrency;
    private readonly object _lifecycleSync = new();
    private readonly List<Task> _lifecycleOperations = [];
    private readonly List<Task> _runtimeGenerationActivationOperations = [];
    private readonly HashSet<IRuntimePackageWorker> _committedRuntimeWorkers =
        new(ReferenceEqualityComparer.Instance);
    private Exception? _runtimeGenerationFaultBeforePublication;
    private PackageStorageActivationTransaction? _storageActivationTransaction;
    private Task? _disposalTask;
    private Task _postDisposalCleanupTask = Task.CompletedTask;

    public ActivePackageSession(
        string? sessionFolder,
        IDictionary<string, ActiveLoadedPackage> loadedPackages,
        IDictionary<string, SessionPackageDescriptor> sessionPackages,
        bool workersStarted = true,
        IEnumerable<RuntimePackageSource>? readySources = null,
        IEnumerable<IReadOnlyList<string>>? workerStartupWaves = null,
        int maximumWorkerStartupConcurrency = PackageSessionConcurrencyOptions.DefaultMaximumConcurrency)
    {
        if (maximumWorkerStartupConcurrency is < PackageSessionConcurrencyOptions.MinimumConcurrency
            or > PackageSessionConcurrencyOptions.MaximumConcurrency)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumWorkerStartupConcurrency));
        }
        SessionFolder = sessionFolder;
        _loadedPackageMap = new Dictionary<string, ActiveLoadedPackage>(loadedPackages, StringComparer.OrdinalIgnoreCase);
        _sessionPackageMap = new Dictionary<string, SessionPackageDescriptor>(sessionPackages, StringComparer.OrdinalIgnoreCase);
        _packageSourceMap = (readySources ?? loadedPackages.Values.Select(static package => package.Source))
            .ToDictionary(static source => source.PackageId, StringComparer.OrdinalIgnoreCase);
        _workerStartupWaves = NormalizeWorkerStartupWaves(_loadedPackageMap, workerStartupWaves);
        _maximumWorkerStartupConcurrency = maximumWorkerStartupConcurrency;
        _workersStarted = workersStarted;
    }

    private ActivePackageSession()
        : this(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase))
    {
        IsEmpty = true;
    }

    private bool _workersStarted;
    private bool _runtimeGenerationCommitted;
    private bool _runtimeGenerationMonitoringStarted;
    private long? _committedSessionGeneration;

    public static ActivePackageSession CreateEmpty(PackageSessionStartupTraceAttempt? startupTrace = null)
        => new() { StartupTrace = startupTrace };

    public bool IsEmpty { get; }

    public string? SessionFolder { get; }

    internal PackageSessionStartupTraceAttempt? StartupTrace { get; init; }
    internal RuntimeRequiredProviderPlan? RequiredProviderPlan { get; set; }
    internal Action<string, bool>? CandidateProviderReady { get; set; }
    private long? _startupGeneration;
    internal void BindStartupGeneration(long generation) => _startupGeneration = generation;

    public async Task StartWorkersAsync(
        ILogger logger,
        TimeSpan startupTimeout,
        TimeSpan cleanupTimeout,
        CancellationToken cancellationToken = default)
    {
        if (_workersStarted)
        {
            return;
        }

        var startupStarted = Stopwatch.GetTimestamp();
        var attemptedWorkers = new List<(string PackageId, IRuntimePackageWorker Worker)>();
        try
        {
            if (RequiredProviderPlan is { } plan)
            {
                await StartRequiredProvidersAsync(plan, attemptedWorkers, logger, startupTimeout, cancellationToken);
                _workersStarted = true;
                return;
            }
            for (var waveIndex = 0; waveIndex < _workerStartupWaves.Count; waveIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var packageIds = _workerStartupWaves[waveIndex];
                var packages = packageIds
                    .Where(_loadedPackageMap.ContainsKey)
                    .Select(packageId => _loadedPackageMap[packageId])
                    .ToArray();
                if (packages.Length == 0) continue;
                var attempted = new bool[packages.Length];
                var failures = new Exception?[packages.Length];
                StartupTrace?.RecordWorkerWaveStarted(
                    waveIndex,
                    _workerStartupWaves.Count,
                    packages.Length,
                    _maximumWorkerStartupConcurrency);
                using var waveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                try
                {
                    await Parallel.ForEachAsync(
                        Enumerable.Range(0, packages.Length),
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = _maximumWorkerStartupConcurrency,
                            CancellationToken = waveCancellation.Token,
                        },
                        async (index, token) =>
                        {
                            var package = packages[index];
                            attempted[index] = true;
                            try
                            {
                                await StartWorkerAsync(package, logger, startupTimeout, token)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception exception)
                            {
                                failures[index] = exception;
                                waveCancellation.Cancel();
                            }
                        }).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested
                    || failures.Any(static failure => failure is not null))
                {
                }
                finally
                {
                    for (var index = 0; index < packages.Length; index++)
                    {
                        if (attempted[index])
                        {
                            attemptedWorkers.Add((packages[index].Descriptor.PackageId, packages[index].Worker));
                        }
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    StartupTrace?.RecordWorkerWaveCompleted(
                        waveIndex,
                        _workerStartupWaves.Count,
                        packages.Length,
                        _maximumWorkerStartupConcurrency,
                        "canceled");
                    cancellationToken.ThrowIfCancellationRequested();
                }
                var owningFailure = failures.FirstOrDefault(static failure => failure is not OperationCanceledException)
                                    ?? failures.FirstOrDefault(static failure => failure is not null);
                if (owningFailure is not null)
                {
                    StartupTrace?.RecordWorkerWaveCompleted(
                        waveIndex,
                        _workerStartupWaves.Count,
                        packages.Length,
                        _maximumWorkerStartupConcurrency,
                        "failed");
                    ExceptionDispatchInfo.Capture(owningFailure).Throw();
                }

                foreach (var package in packages)
                {
                    RefreshWorkerMetadata(package.Descriptor.PackageId, package);
                }
                StartupTrace?.RecordWorkerWaveCompleted(
                    waveIndex,
                    _workerStartupWaves.Count,
                    packages.Length,
                    _maximumWorkerStartupConcurrency,
                    "succeeded");
            }

            _workersStarted = true;
            logger.LogInformation(
                "Started {PackageWorkerCount} package worker(s) in {ElapsedMilliseconds} ms",
                attemptedWorkers.Count,
                Stopwatch.GetElapsedTime(startupStarted).TotalMilliseconds);
        }
        catch
        {
            logger.LogWarning(
                "Package worker startup failed after {ElapsedMilliseconds} ms; stopping {AttemptedWorkerCount} attempted worker(s)",
                Stopwatch.GetElapsedTime(startupStarted).TotalMilliseconds,
                attemptedWorkers.Count);
            await RuntimeWorkerLifecycle.StopWorkersStrictAsync(
                attemptedWorkers.AsEnumerable().Reverse().ToArray(),
                logger,
                cleanupTimeout,
                operationStarted: TrackLifecycleOperation);
            throw;
        }
    }

    private async Task StartRequiredProvidersAsync(RuntimeRequiredProviderPlan plan,
        List<(string PackageId, IRuntimePackageWorker Worker)> attempted, ILogger logger, TimeSpan timeout, CancellationToken token)
    {
        var pending = _loadedPackageMap.Keys.ToHashSet(StringComparer.Ordinal);
        var ready = GetActivePackageSources().Select(source => source.PackageId).Where(id => !pending.Contains(id)).ToHashSet(StringComparer.Ordinal);
        var running = new Dictionary<string, Task>(StringComparer.Ordinal);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            while (pending.Count + running.Count > 0)
            {
                foreach (var id in pending.Where(id => plan.CanStart(id, ready)).Order(StringComparer.Ordinal).ToArray())
                {
                    if (running.Count >= _maximumWorkerStartupConcurrency) break;
                    pending.Remove(id);
                    var package = _loadedPackageMap[id];
                    attempted.Add((id, package.Worker));
                    running.Add(id, StartAsync(package, attempted.Count - 1));
                }
                if (running.Count == 0) throw plan.Stalled(pending);
                await Task.WhenAny(running.Values).WaitAsync(token);
                foreach (var id in running.Where(pair => pair.Value.IsCompleted).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray())
                {
                    await running[id];
                    running.Remove(id);
                    RefreshWorkerMetadata(id, _loadedPackageMap[id]);
                    ready.Add(id);
                    CandidateProviderReady?.Invoke(id, true);
                }
            }
        }
        catch
        {
            cancellation.Cancel();
            await Task.WhenAll(running.Values.Select(ObserveAsync));
            throw;
        }

        async Task StartAsync(ActiveLoadedPackage package, int frontierIndex)
        {
            StartupTrace?.RecordWorkerWaveStarted(frontierIndex, _loadedPackageMap.Count, 1, _maximumWorkerStartupConcurrency);
            var outcome = "failed";
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                deadline.CancelAfter(timeout);
                var compose = package.Worker.ComposeAsync(deadline.Token);
                TrackLifecycleOperation(compose);
                await compose.WaitAsync(deadline.Token);
                CandidateProviderReady?.Invoke(package.Descriptor.PackageId, false);
                await StartWorkerAsync(package, logger, timeout, deadline.Token);
                outcome = "succeeded";
            }
            catch (OperationCanceledException) { outcome = "canceled"; throw; }
            finally { StartupTrace?.RecordWorkerWaveCompleted(frontierIndex, _loadedPackageMap.Count, 1, _maximumWorkerStartupConcurrency, outcome); }
        }
    }

    private async Task StartWorkerAsync(
        ActiveLoadedPackage package,
        ILogger logger,
        TimeSpan startupTimeout,
        CancellationToken cancellationToken)
    {
        var workerStarted = Stopwatch.GetTimestamp();
        using var startupTimeoutCancellation = new CancellationTokenSource(startupTimeout);
        using var startupDeadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            startupTimeoutCancellation.Token);
        var startTask = Task.Run(
            () => package.Worker.StartAsync(startupDeadline.Token),
            CancellationToken.None);
        TrackLifecycleOperation(startTask);
        try
        {
            await startTask.WaitAsync(startupDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            startupTimeoutCancellation.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            if (!startTask.IsCompleted) _ = ObserveAsync(startTask);
            logger.LogWarning(
                "Package worker {PackageId} startup timed out after {ElapsedMilliseconds} ms",
                package.Descriptor.PackageId,
                Stopwatch.GetElapsedTime(workerStarted).TotalMilliseconds);
            throw new TimeoutException(
                $"Package worker '{package.Descriptor.PackageId}' did not start within {startupTimeout.TotalSeconds:0.###} seconds.",
                exception);
        }
        catch
        {
            if (!startTask.IsCompleted) _ = ObserveAsync(startTask);
            logger.LogWarning(
                "Package worker {PackageId} startup failed after {ElapsedMilliseconds} ms",
                package.Descriptor.PackageId,
                Stopwatch.GetElapsedTime(workerStarted).TotalMilliseconds);
            throw;
        }
        logger.LogInformation(
            "Started package worker {PackageId} in {ElapsedMilliseconds} ms",
            package.Descriptor.PackageId,
            Stopwatch.GetElapsedTime(workerStarted).TotalMilliseconds);
    }

    public IReadOnlyDictionary<string, ActiveLoadedPackage> LoadedPackageMap => _loadedPackageMap;

    public bool SharesPersistentWorkspaceWith(ActivePackageSession candidate)
    {
        var activeWorkspaces = _loadedPackageMap.Values
            .Select(static package => package.Worker.WorkspaceRootPath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => Path.GetFullPath(path!))
            .ToHashSet(PathComparer);
        return candidate._loadedPackageMap.Values
            .Select(static package => package.Worker.WorkspaceRootPath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => Path.GetFullPath(path!))
            .Any(activeWorkspaces.Contains);
    }

    public async Task QuiesceWorkersAsync(
        ILogger logger,
        TimeSpan warningTimeout)
    {
        if (!_workersStarted)
        {
            return;
        }

        _workersStarted = false;
        Task[] activationOperations;
        lock (_lifecycleSync)
        {
            activationOperations = _runtimeGenerationActivationOperations.ToArray();
        }
        await Task.WhenAll(activationOperations.Select(ObserveAsync)).ConfigureAwait(false);
        var workers = _loadedPackageMap.Values
            .Reverse()
            .Select(package => (package.Descriptor.PackageId, package.Worker))
            .ToArray();
        await RuntimeWorkerLifecycle.StopWorkersStrictAsync(
            workers,
            logger,
            warningTimeout,
            TrackLifecycleOperation).ConfigureAwait(false);
        Task[] lifecycleOperations;
        lock (_lifecycleSync)
        {
            lifecycleOperations = _lifecycleOperations.ToArray();
        }
        await Task.WhenAll(lifecycleOperations.Select(ObserveAsync)).ConfigureAwait(false);
    }

    public async Task BeginStorageActivationTransactionAsync(
        CancellationToken cancellationToken = default)
    {
        if (_storageActivationTransaction is not null)
        {
            throw new InvalidOperationException("The package storage activation transaction is already active.");
        }
        _storageActivationTransaction = await PackageStorageActivationTransaction.BeginAsync(
            _loadedPackageMap.Values.ToArray(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task CommitRuntimeGenerationAsync(
        long sessionGeneration,
        CancellationToken cancellationToken = default)
    {
        if (!_workersStarted)
        {
            throw new InvalidOperationException("Package workers must start before their Runtime generation is committed.");
        }
        if (_runtimeGenerationCommitted)
        {
            if (_committedSessionGeneration != sessionGeneration)
            {
                throw new InvalidOperationException("Package workers are already committed to another Runtime generation.");
            }
            return;
        }
        if (_committedSessionGeneration is { } existingGeneration && existingGeneration != sessionGeneration)
        {
            throw new InvalidOperationException("Package worker generation commit cannot change session generations after it begins.");
        }
        _committedSessionGeneration = sessionGeneration;

        foreach (var package in _loadedPackageMap.Values.ToArray())
        {
            var generation = new RuntimePackageGeneration(package.RuntimeActivationId, sessionGeneration);
            if (_committedRuntimeWorkers.Contains(package.Worker))
            {
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var commitTask = Task.Run(
                () => package.Worker.CommitGenerationAsync(generation, cancellationToken),
                CancellationToken.None);
            TrackRuntimeGenerationActivation(commitTask);
            await commitTask.WaitAsync(cancellationToken);
            _committedRuntimeWorkers.Add(package.Worker);
        }

        _runtimeGenerationCommitted = true;
        _startupGeneration = null;
    }

    public void StartRuntimeGenerationMonitoring(
        Action<string, RuntimePackageGeneration, Exception> generationFaulted)
    {
        ArgumentNullException.ThrowIfNull(generationFaulted);
        if (!_runtimeGenerationCommitted || _committedSessionGeneration is not { } sessionGeneration)
        {
            throw new InvalidOperationException("The Runtime generation must be committed before it can be monitored.");
        }
        if (_runtimeGenerationMonitoringStarted)
        {
            return;
        }

        _runtimeGenerationMonitoringStarted = true;
        foreach (var package in _loadedPackageMap.Values.ToArray())
        {
            if (!_committedRuntimeWorkers.Contains(package.Worker))
            {
                continue;
            }
            var generation = new RuntimePackageGeneration(package.RuntimeActivationId, sessionGeneration);
            Task completion;
            try
            {
                completion = package.Worker.GenerationCompletion
                    ?? Task.FromException(new InvalidOperationException(
                        "A package worker returned a null generation completion task."));
            }
            catch (Exception exception)
            {
                completion = Task.FromException(exception);
            }
            var monitorTask = MonitorRuntimeGenerationAsync(
                completion,
                package.Descriptor.PackageId,
                generation,
                generationFaulted);
            TrackLifecycleOperation(monitorTask);
        }
    }

    public async Task ActivateCommittedRuntimeGenerationAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_runtimeGenerationCommitted)
        {
            throw new InvalidOperationException("The Runtime generation must be committed before process workers are activated.");
        }
        var sessionGeneration = _committedSessionGeneration
                                ?? throw new InvalidOperationException(
                                    "The committed Runtime session generation is unavailable.");
        var workers = _loadedPackageMap.Values
            .Select(package => (
                package.Worker,
                Generation: new RuntimePackageGeneration(
                    package.RuntimeActivationId,
                    sessionGeneration)))
            .ToArray();
        foreach (var worker in workers)
        {
            await worker.Worker.PrepareStorageMigrationsAsync(
                    worker.Generation,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        _storageActivationTransaction ??= await PackageStorageActivationTransaction.BeginAsync(
            _loadedPackageMap.Values.ToArray(),
            cancellationToken).ConfigureAwait(false);
        await _storageActivationTransaction.ValidateMigrationPlansAsync(cancellationToken)
            .ConfigureAwait(false);
        await Task.WhenAll(workers.Select(
                worker => worker.Worker.ActivateGenerationAsync(cancellationToken)))
            .ConfigureAwait(false);
        await _storageActivationTransaction.WriteCompletionsAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public void CompleteStorageMigrations()
    {
        var transaction = Interlocked.Exchange(ref _storageActivationTransaction, null);
        transaction?.Complete();
    }

    public async Task RollbackStorageMigrationsAsync()
    {
        var transaction = Interlocked.Exchange(ref _storageActivationTransaction, null);
        if (transaction is not null)
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<DrainingPackageRecoveryCandidate>> ApplyDrainingFaultsAsync(
        IReadOnlyList<DrainingPackageFault> faults,
        ILogger logger,
        TimeSpan cleanupTimeout)
    {
        var recoveries = new List<DrainingPackageRecoveryCandidate>();
        foreach (var fault in faults)
        {
            if (!MarkPackageFailedInternal(
                    fault.PackageId,
                    fault.Origin,
                    fault.Exception.Message,
                    out var package,
                    fault.FailedAtUtc)
                || package is null)
            {
                continue;
            }
            await RuntimeWorkerLifecycle.StopWorkersStrictAsync(
                [(fault.PackageId, package.Worker)],
                logger,
                cleanupTimeout,
                TrackLifecycleOperation).ConfigureAwait(false);
            await DisposeWorkerStrictAsync(
                fault.PackageId,
                package.Worker,
                logger,
                cleanupTimeout).ConfigureAwait(false);
            if (package.RecoveryFactory is not null)
            {
                recoveries.Add(new DrainingPackageRecoveryCandidate(fault, package, fault.FailedAtUtc));
            }
        }
        return recoveries;
    }

    public async Task RebuildQuiescedWorkersAsync(
        ILogger logger,
        TimeSpan cleanupTimeout)
    {
        foreach (var packageId in _loadedPackageMap.Keys.ToArray())
        {
            var current = _loadedPackageMap[packageId];
            if (current.RecoveryFactory is null)
            {
                if (current.Worker.GenerationCompletion.IsFaulted
                    || current.Worker.GenerationCompletion.IsCanceled)
                {
                    throw new InvalidOperationException(
                        $"Package '{packageId}' has no recovery factory for its stopped or faulted worker.");
                }
                continue;
            }

            await DisposeWorkerStrictAsync(
                packageId,
                current.Worker,
                logger,
                cleanupTimeout).ConfigureAwait(false);
            _loadedPackageMap[packageId] = current.RecoveryFactory() with
            {
                Source = current.Source,
            };
        }

        lock (_lifecycleSync)
        {
            _lifecycleOperations.Clear();
            _runtimeGenerationActivationOperations.Clear();
            _committedRuntimeWorkers.Clear();
            _runtimeGenerationFaultBeforePublication = null;
            _runtimeGenerationMonitoringStarted = false;
            _runtimeGenerationCommitted = false;
            _committedSessionGeneration = null;
            _startupGeneration = null;
        }
        _workersStarted = false;
        _storageActivationTransaction = null;
    }

    public void PublishCommittedRuntimeGeneration(Action publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        lock (_lifecycleSync)
        {
            if (_runtimeGenerationFaultBeforePublication is not null)
            {
                throw new InvalidOperationException(
                    "A package Runtime generation faulted before publication.",
                    _runtimeGenerationFaultBeforePublication);
            }
            foreach (var worker in _committedRuntimeWorkers)
            {
                var completion = worker.GenerationCompletion
                                 ?? throw new InvalidOperationException(
                                     "A package worker returned a null generation completion task.");
                if (completion.IsFaulted || completion.IsCanceled)
                {
                    completion.GetAwaiter().GetResult();
                }
            }
            publish();
        }
    }

    public bool OwnsPackageActivation(string packageId, Guid runtimeActivationId)
        => _loadedPackageMap.TryGetValue(packageId, out var package)
           && package.RuntimeActivationId == runtimeActivationId
           && IsPackageEnabled(packageId);

    public bool OwnsRuntimeGeneration(
        string packageId,
        RuntimePackageGeneration generation)
        => (_runtimeGenerationCommitted && _committedSessionGeneration == generation.SessionGeneration
            || _startupGeneration == generation.SessionGeneration)
           && OwnsPackageActivation(packageId, generation.ActivationId);

    internal bool TryGetRuntimeGeneration(
        string packageId,
        out RuntimePackageGeneration generation)
    {
        if (_committedSessionGeneration is { } sessionGeneration
            && _loadedPackageMap.TryGetValue(packageId, out var package)
            && package.ExecutionRuntimeBinding is not null)
        {
            generation = new RuntimePackageGeneration(package.RuntimeActivationId, sessionGeneration);
            return true;
        }
        generation = null!;
        return false;
    }

    internal IReadOnlyList<string> GetExecutionRuntimeDependentPackageIds(
        IReadOnlySet<string> blockedIdentityKeys)
    {
        ArgumentNullException.ThrowIfNull(blockedIdentityKeys);
        lock (_lifecycleSync)
        {
            return _loadedPackageMap
                .Where(entry => IsPackageEnabled(entry.Key)
                                && entry.Value.ExecutionRuntimeBinding is { } binding
                                && blockedIdentityKeys.Contains(binding.Identity.SingleFlightKey))
                .Select(static entry => entry.Key)
                .OrderBy(static packageId => packageId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private async Task MonitorRuntimeGenerationAsync(
        Task completion,
        string packageId,
        RuntimePackageGeneration generation,
        Action<string, RuntimePackageGeneration, Exception> generationFaulted)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            lock (_lifecycleSync)
            {
                _runtimeGenerationFaultBeforePublication ??= exception;
            }
            generationFaulted(packageId, generation, exception);
        }
    }

    public IReadOnlyDictionary<string, SessionPackageDescriptor> SessionPackageMap => _sessionPackageMap;

    public IReadOnlyList<ActivePackageDescriptor> GetActivePackages()
    {
        return _sessionPackageMap.Values
            .Where(package => package.IsEnabled)
            .Select(ToActiveDescriptor)
            .ToArray();
    }

    public IReadOnlyList<RuntimePackageSource> GetActivePackageSources()
        => _packageSourceMap.Values
            .Where(source => IsPackageEnabled(source.PackageId))
            .ToArray();

    public void FinalizeCommittedInstalledSources()
    {
        foreach (var packageId in _packageSourceMap.Keys.ToArray())
        {
            var source = _packageSourceMap[packageId];
            if (source.Kind != PackageSourceKind.Installed)
            {
                continue;
            }
            if (!Directory.Exists(source.SourceFolder))
            {
                throw new InvalidDataException(
                    $"Committed installed package source '{source.SourceFolder}' for '{source.PackageId}' does not exist.");
            }

            var committedSource = source.AsCommittedInstalledSource();
            _packageSourceMap[packageId] = committedSource;
            if (_loadedPackageMap.TryGetValue(packageId, out var loadedPackage))
            {
                _loadedPackageMap[packageId] = loadedPackage with { Source = committedSource };
            }
        }
    }

    public IReadOnlyList<RuntimePackageSource> GetAppPackageSources()
    {
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(_packageSourceMap.Values
            .Where(source => IsPackageEnabled(source.PackageId) && (source.HostRoles & PackageHostRoles.App) != 0)
            .Select(static source => source.PackageId));
        while (pending.TryPop(out var packageId))
        {
            if (!required.Add(packageId) || !_packageSourceMap.TryGetValue(packageId, out var source)) continue;
            foreach (var dependency in source.PackageDependencies) pending.Push(dependency.PackageId);
        }

        return _packageSourceMap.Values
            .Where(source => required.Contains(source.PackageId) && IsPackageEnabled(source.PackageId))
            .ToArray();
    }

    public IReadOnlyList<SessionPackageDescriptor> GetSessionPackages()
    {
        return _sessionPackageMap.Values
            .OrderByDescending(package => !package.IsEnabled)
            .ThenBy(package => package.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool TryGetSessionPackage(string packageId, out SessionPackageDescriptor? package)
        => _sessionPackageMap.TryGetValue(packageId, out package);

    public bool TryGetPackageSource(string packageId, out RuntimePackageSource? source)
        => _packageSourceMap.TryGetValue(packageId, out source);

    public bool IsPackageEnabled(string packageId)
    {
        return _sessionPackageMap.TryGetValue(packageId, out var package)
            && package.IsEnabled;
    }

    public bool TryGetLoadedPackage(string packageId, out ActiveLoadedPackage? loadedPackage)
    {
        if (_loadedPackageMap.TryGetValue(packageId, out var package)
            && IsPackageEnabled(packageId))
        {
            loadedPackage = package;
            return true;
        }

        loadedPackage = null;
        return false;
    }

    internal bool SetRpcReadiness(string packageId, PackageReadinessState readiness)
    {
        if (!_sessionPackageMap.TryGetValue(packageId, out var package)
            || !package.IsEnabled
            || package.Readiness is not (PackageReadinessState.Ready or PackageReadinessState.Degraded)
            || package.Readiness == readiness)
        {
            return false;
        }
        _sessionPackageMap[packageId] = package with { Readiness = readiness };
        return true;
    }

    public string? TryResolvePackageAssetPath(string packageId, string assetPath)
    {
        if (!_packageSourceMap.TryGetValue(packageId, out var source)
            || !IsPackageEnabled(packageId))
        {
            return null;
        }

        return source.Kind switch
        {
            PackageSourceKind.Dev => PackageAssetPathResolver.TryResolveDevAssetPath(source.SourceFolder, assetPath),
            PackageSourceKind.Installed => PackageAssetPathResolver.TryResolveInstalledAssetPath(source.SourceFolder, assetPath),
            _ => null,
        };
    }

    public bool MarkPackageFailed(
        string packageId,
        PackageFailureOrigin origin,
        string message,
        out ActiveLoadedPackage? packageToDeactivate)
    {
        return MarkPackageFailedInternal(
            packageId,
            origin,
            message,
            out packageToDeactivate);
    }

    internal bool MarkPackageFailedInternal(
        string packageId,
        PackageFailureOrigin origin,
        string message,
        out ActiveLoadedPackage? packageToDeactivate,
        DateTimeOffset? failedAtUtc = null)
    {
        packageToDeactivate = null;
        if (!_sessionPackageMap.TryGetValue(packageId, out var package))
        {
            return false;
        }

        var updatedPackage = package with
        {
            IsEnabled = false,
            Readiness = PackageReadinessState.Failed,
            FailureOrigin = origin,
            LastError = message,
            LastFailureAtUtc = failedAtUtc ?? DateTimeOffset.UtcNow,
            FailureCount = package.FailureCount + 1,
        };

        _sessionPackageMap[packageId] = updatedPackage;
        _loadedPackageMap.Remove(packageId, out packageToDeactivate);

        return true;
    }

    internal bool IsExactFailedPackage(
        string packageId,
        RuntimePackageSource source)
        => _sessionPackageMap.TryGetValue(packageId, out var package)
           && !package.IsEnabled
           && package.Readiness == PackageReadinessState.Failed
           && !_loadedPackageMap.ContainsKey(packageId)
           && _packageSourceMap.TryGetValue(packageId, out var currentSource)
           && ReferenceEquals(currentSource, source);

    internal bool AreRecoveryDependenciesAvailable(string packageId)
    {
        if (!_packageSourceMap.TryGetValue(packageId, out var source))
        {
            return false;
        }

        foreach (var dependency in source.PackageDependencies)
        {
            if (!_sessionPackageMap.TryGetValue(dependency.PackageId, out var package)
                || !package.IsEnabled
                || package.Readiness is not (PackageReadinessState.Ready or PackageReadinessState.Degraded)
                || !PackageVersionRange.IsSatisfiedBy(package.Version, dependency.VersionRange))
            {
                return false;
            }
        }
        return true;
    }

    internal bool RecordRecoveryFailure(
        string packageId,
        RuntimePackageSource source,
        PackageFailureOrigin origin,
        string message,
        DateTimeOffset failedAtUtc)
    {
        if (!IsExactFailedPackage(packageId, source)
            || !_sessionPackageMap.TryGetValue(packageId, out var package))
        {
            return false;
        }

        _sessionPackageMap[packageId] = package with
        {
            FailureOrigin = origin,
            LastError = message,
            LastFailureAtUtc = failedAtUtc,
            FailureCount = package.FailureCount + 1,
        };
        return true;
    }

    internal ActiveLoadedPackage RefreshRecoveryWorkerMetadata(ActiveLoadedPackage package)
        => WithWorkerMetadata(package);

    internal bool PublishRecoveredPackage(
        string packageId,
        RuntimePackageSource source,
        ActiveLoadedPackage replacement,
        long activationGeneration)
    {
        lock (_lifecycleSync)
        {
            if (!_workersStarted
                || !_runtimeGenerationCommitted
                || _committedSessionGeneration != activationGeneration
                || !IsExactFailedPackage(packageId, source)
                || replacement.RuntimeActivationId == Guid.Empty
                || !string.Equals(replacement.Descriptor.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
                || !_sessionPackageMap.TryGetValue(packageId, out var failedPackage)
                || !string.Equals(replacement.Descriptor.Version, failedPackage.Version, StringComparison.Ordinal))
            {
                return false;
            }

            var completion = replacement.Worker.GenerationCompletion
                             ?? throw new InvalidOperationException(
                                 "A recovered package worker returned a null generation completion task.");
            if (completion.IsFaulted || completion.IsCanceled)
            {
                completion.GetAwaiter().GetResult();
            }

            replacement = replacement with
            {
                Source = source,
                Descriptor = ToActiveDescriptor(failedPackage with
                {
                    IsEnabled = true,
                    Readiness = PackageReadinessState.Ready,
                    FailureOrigin = null,
                    LastError = null,
                }),
            };
            _loadedPackageMap.Add(packageId, replacement);
            _sessionPackageMap[packageId] = failedPackage with
            {
                IsEnabled = true,
                Readiness = PackageReadinessState.Ready,
                FailureOrigin = null,
                LastError = null,
            };
            _committedRuntimeWorkers.Add(replacement.Worker);
            return true;
        }
    }

    internal bool StartRecoveredRuntimeGenerationMonitoring(
        string packageId,
        Guid runtimeActivationId,
        long activationGeneration,
        Action<string, RuntimePackageGeneration, Exception> generationFaulted)
    {
        ArgumentNullException.ThrowIfNull(generationFaulted);
        Task completion;
        lock (_lifecycleSync)
        {
            if (!_loadedPackageMap.TryGetValue(packageId, out var package)
                || package.RuntimeActivationId != runtimeActivationId
                || !_committedRuntimeWorkers.Contains(package.Worker))
            {
                return false;
            }
            completion = package.Worker.GenerationCompletion
                         ?? throw new InvalidOperationException(
                             "A recovered package worker returned a null generation completion task.");
        }

        var generation = new RuntimePackageGeneration(runtimeActivationId, activationGeneration);
        var monitor = MonitorRuntimeGenerationAsync(
            completion,
            packageId,
            generation,
            generationFaulted);
        lock (_lifecycleSync)
        {
            _lifecycleOperations.Add(monitor);
        }
        return true;
    }

    internal void RollbackRecoveredPackage(
        string packageId,
        RuntimePackageSource source,
        Guid runtimeActivationId,
        PackageFailureOrigin origin,
        string message,
        DateTimeOffset failedAtUtc)
    {
        lock (_lifecycleSync)
        {
            if (!_loadedPackageMap.TryGetValue(packageId, out var package)
                || package.RuntimeActivationId != runtimeActivationId
                || !ReferenceEquals(package.Source, source)
                || !_sessionPackageMap.TryGetValue(packageId, out var descriptor))
            {
                return;
            }
            _loadedPackageMap.Remove(packageId);
            _committedRuntimeWorkers.Remove(package.Worker);
            _sessionPackageMap[packageId] = descriptor with
            {
                IsEnabled = false,
                Readiness = PackageReadinessState.Failed,
                FailureOrigin = origin,
                LastError = message,
                LastFailureAtUtc = failedAtUtc,
            };
        }
    }

    public bool DisableInstalledPackage(string packageId, out ActiveLoadedPackage? packageToDeactivate)
    {
        packageToDeactivate = null;
        if (!_sessionPackageMap.TryGetValue(packageId, out var package))
        {
            return false;
        }

        _sessionPackageMap[packageId] = package with
        {
            IsEnabled = false,
            Readiness = PackageReadinessState.Disabled,
            FailureOrigin = null,
            LastError = null,
            LastFailureAtUtc = null,
        };
        _loadedPackageMap.Remove(packageId, out packageToDeactivate);
        return true;
    }

    public bool RemovePackage(string packageId, out ActiveLoadedPackage? packageToDeactivate)
    {
        var removedSessionPackage = _sessionPackageMap.Remove(packageId);
        var removedLoadedPackage = _loadedPackageMap.Remove(packageId, out packageToDeactivate);
        _packageSourceMap.Remove(packageId);
        if (removedSessionPackage || removedLoadedPackage)
        {
            return true;
        }

        return false;
    }

    public async Task StopWorkersAsync(
        ILogger logger,
        TimeSpan cleanupTimeout,
        CancellationToken cancellationToken = default)
    {
        if (!_workersStarted)
        {
            return;
        }

        _workersStarted = false;
        Task[] activationOperations;
        lock (_lifecycleSync)
        {
            activationOperations = _runtimeGenerationActivationOperations.ToArray();
        }
        await Task.WhenAll(activationOperations.Select(ObserveAsync));
        var workers = _loadedPackageMap.Values
            .Reverse()
            .Select(package => (package.Descriptor.PackageId, package.Worker))
            .ToArray();
        await RuntimeWorkerLifecycle.StopWorkersAsync(
            workers,
            logger,
            cleanupTimeout,
            cancellationToken,
            TrackLifecycleOperation);
    }

    public Task DisposeAsync()
        => DisposeAsync(DefaultCleanupTimeout);

    internal Task WaitForDisposalCompletionAsync()
        => GetOrStartDisposal();

    internal void RegisterPostDisposalCleanup(Task cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        lock (_lifecycleSync)
        {
            _postDisposalCleanupTask = _postDisposalCleanupTask.IsCompletedSuccessfully
                ? cleanup
                : Task.WhenAll(_postDisposalCleanupTask, cleanup);
        }
    }

    public async Task DisposeAsync(TimeSpan cleanupTimeout, CancellationToken cancellationToken = default)
    {
        var cleanup = GetOrStartDisposal();
        Task operation = cleanup;
        try
        {
            await cleanup.WaitAsync(cleanupTimeout, cancellationToken);
            lock (_lifecycleSync) operation = _postDisposalCleanupTask;
            await operation.WaitAsync(cleanupTimeout, cancellationToken);
        }
        catch
        {
            if (!operation.IsCompleted)
            {
                _ = ObserveAsync(operation);
            }
            throw;
        }
    }

    private Task GetOrStartDisposal()
    {
        StartupTrace?.TryFail("discarded");
        TaskCompletionSource? completion = null;
        lock (_lifecycleSync)
        {
            if (_disposalTask is not null)
            {
                return _disposalTask;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposalTask = completion.Task;
        }

        _ = CompleteDisposalAsync(completion);
        return completion.Task;
    }

    private async Task CompleteDisposalAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeAfterLifecycleOperationsAsync();
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task DisposeAfterLifecycleOperationsAsync()
    {
        Task[] lifecycleOperations;
        lock (_lifecycleSync)
        {
            lifecycleOperations = _lifecycleOperations.ToArray();
        }

        await Task.WhenAll(lifecycleOperations.Select(ObserveAsync));
        await DisposeCoreAsync();
    }

    private void TrackLifecycleOperation(Task operation)
    {
        lock (_lifecycleSync)
        {
            _lifecycleOperations.Add(operation);
        }
    }

    private void TrackRuntimeGenerationActivation(Task operation)
    {
        lock (_lifecycleSync)
        {
            _runtimeGenerationActivationOperations.Add(operation);
            _lifecycleOperations.Add(operation);
        }
    }

    private async Task DisposeWorkerStrictAsync(
        string packageId,
        IRuntimePackageWorker worker,
        ILogger logger,
        TimeSpan cleanupTimeout)
    {
        var disposal = worker.DisposeAsync().AsTask();
        TrackLifecycleOperation(disposal);
        try
        {
            await disposal.WaitAsync(cleanupTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            logger.LogWarning(
                exception,
                "Worker disposal for package {PackageId} exceeded its cleanup deadline; the worker remains quarantined",
                packageId);
            throw;
        }
    }

    private void RefreshWorkerMetadata(string packageId, ActiveLoadedPackage package)
    {
        _loadedPackageMap[packageId] = WithWorkerMetadata(package);
    }

    private static ActiveLoadedPackage WithWorkerMetadata(ActiveLoadedPackage package)
    {
        var schema = package.Worker.SettingsSchema;
        if (package.Settings is PackageSettings settings)
        {
            settings.Schema = schema;
        }
        return package with
        {
            SettingsSchema = PackageProtocolMapper.ToProtocolSettingsSchema(
                package.Descriptor.PackageId,
                package.Descriptor.DisplayName,
                schema),
            CanonicalSettingsSchema = schema,
        };
    }

    private async Task DisposeCoreAsync()
    {
        List<Exception>? disposeErrors = null;

        foreach (var package in _loadedPackageMap.Values.Reverse())
        {
            try
            {
                await package.Worker.DisposeAsync();
            }
            catch (Exception ex)
            {
                disposeErrors ??= [];
                disposeErrors.Add(new InvalidOperationException(
                    $"Failed to dispose worker for package '{package.Descriptor.PackageId}'.",
                    ex));
            }
        }

        TryDeleteSessionFolder();

        if (disposeErrors is { Count: > 0 })
        {
            throw new AggregateException(disposeErrors);
        }
    }

    private void TryDeleteSessionFolder()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(SessionFolder) && Directory.Exists(SessionFolder))
            {
                Directory.Delete(SessionFolder, recursive: true);
            }
        }
        catch
        {
            // Process teardown may retain a session file briefly; stale-session cleanup retries later.
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }

    private static IReadOnlyList<IReadOnlyList<string>> NormalizeWorkerStartupWaves(
        IReadOnlyDictionary<string, ActiveLoadedPackage> loadedPackages,
        IEnumerable<IReadOnlyList<string>>? workerStartupWaves)
    {
        var waves = (workerStartupWaves
                     ?? loadedPackages.Keys.Select(static packageId => (IReadOnlyList<string>)[packageId]))
            .Select(static wave => (IReadOnlyList<string>)wave.ToArray())
            .Where(static wave => wave.Count > 0)
            .ToArray();
        var packageIds = waves.SelectMany(static wave => wave).ToArray();
        if (packageIds.Count(packageId => !loadedPackages.ContainsKey(packageId)) != 0
            || packageIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != packageIds.Length
            || packageIds.Length != loadedPackages.Count)
        {
            throw new ArgumentException(
                "Worker startup waves must contain every loaded package exactly once.",
                nameof(workerStartupWaves));
        }
        return waves;
    }

    private static StringComparer PathComparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static ActivePackageDescriptor ToActiveDescriptor(SessionPackageDescriptor package)
        => new(
            package.PackageId,
            package.DisplayName,
            package.Version,
            package.HostRoles,
            package.Icon,
            package.IsEnabled,
            package.Readiness,
            package.Views);
}
