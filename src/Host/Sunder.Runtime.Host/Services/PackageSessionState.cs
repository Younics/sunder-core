using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageSessionState(
    ILogger logger,
    Action clearAuthSessions,
    Action<string> removePackageAuthSessions,
    TimeSpan? sessionDrainTimeout = null)
{
    private readonly object _syncRoot = new();
    private SessionEntry _activeEntry = new(ActivePackageSession.Empty, generation: 0);
    private long _generation;
    private readonly TimeSpan _sessionDrainTimeout = sessionDrainTimeout ?? TimeSpan.FromSeconds(10);

    public PackageSessionLease AcquireLease()
    {
        lock (_syncRoot)
        {
            var entry = _activeEntry;
            if (entry.Draining)
            {
                throw new RuntimeUnavailableException("The active package session is draining and is not accepting new requests.");
            }
            var leaseId = ++entry.LastLeaseId;
            entry.ActiveLeaseIds.Add(leaseId);
            return new PackageSessionLease(
                entry.Session,
                entry.Generation,
                entry,
                entry.Retirement.Token,
                () => ReleaseLease(entry, leaseId));
        }
    }

    public IReadOnlyList<ActivePackageDescriptor> GetActivePackages()
    {
        lock (_syncRoot)
        {
            return _activeEntry.Session.GetActivePackages();
        }
    }

    public IReadOnlyList<SessionPackageDescriptor> GetSessionPackages()
    {
        lock (_syncRoot)
        {
            return _activeEntry.Session.GetSessionPackages();
        }
    }

    public SessionPackageDescriptor? GetSessionPackage(string packageId)
    {
        lock (_syncRoot)
        {
            return _activeEntry.Session.TryGetSessionPackage(packageId, out var package) ? package : null;
        }
    }

    public IReadOnlyList<RuntimePackageSource> GetActivePackageSources()
    {
        lock (_syncRoot)
        {
            return _activeEntry.Session.GetActivePackageSources();
        }
    }

    public IReadOnlyList<ActiveLoadedPackage> ListEnabledLoadedPackages(PackageSessionLease lease)
    {
        lock (_syncRoot)
        {
            return lease.Session.LoadedPackageMap.Values
                .Where(package => lease.Session.IsPackageEnabled(package.Descriptor.PackageId))
                .ToArray();
        }
    }

    public async Task<IReadOnlyList<string>> ClearActiveSessionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SessionEntry? previousEntry = null;
        CancellationTokenSource? retirement = null;
        lock (_syncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(_activeEntry.Session, ActivePackageSession.Empty))
            {
                // Authentication state is external to the session lock.
            }
            else
            {
                previousEntry = _activeEntry;
                if (previousEntry.Draining)
                {
                    throw new RuntimeUnavailableException("The active package session is already draining.");
                }
                previousEntry.Draining = true;
                retirement = previousEntry.Retirement;
                SignalRetirementIfDrained(previousEntry);
            }
        }

        if (previousEntry is null)
        {
            clearAuthSessions();
            return [];
        }

        var activeRetirement = retirement!;
        activeRetirement.Cancel();
        try
        {
            await previousEntry.Drained.Task.WaitAsync(_sessionDrainTimeout, cancellationToken);
        }
        catch
        {
            AbortDrain(previousEntry, activeRetirement);
            throw;
        }

        lock (_syncRoot)
        {
            if (!ReferenceEquals(_activeEntry, previousEntry) || !previousEntry.Draining)
            {
                throw new InvalidOperationException("The active package session changed while it was draining.");
            }
            previousEntry.Retired = true;
            _activeEntry = new SessionEntry(ActivePackageSession.Empty, _generation);
        }

        clearAuthSessions();
        return await RetireSessionAsync(previousEntry);
    }

    public async Task<(long Generation, IReadOnlyList<string> Warnings)> PublishSessionAsync(
        ActivePackageSession session,
        CancellationToken cancellationToken = default,
        long? expectedGeneration = null,
        Action<long>? committed = null)
    {
        var publication = await PreparePublicationAsync(session, expectedGeneration, cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await CommitPublicationAsync(publication, committed);
        }
        catch
        {
            DiscardPublication(publication);
            throw;
        }
    }

    internal async Task<SessionPublication> PreparePublicationAsync(
        ActivePackageSession session,
        long? expectedGeneration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        SessionEntry previousEntry;
        CancellationTokenSource retirement;
        lock (_syncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(session, _activeEntry.Session))
            {
                throw new InvalidOperationException("The active package session cannot be published again.");
            }
            if (expectedGeneration is not null && expectedGeneration != _generation)
            {
                throw new RuntimeStaleGenerationException("The active package session changed while the replacement was being prepared.");
            }

            previousEntry = _activeEntry;
            if (previousEntry.Draining)
            {
                throw new RuntimeUnavailableException("The active package session is already draining.");
            }
            previousEntry.Draining = true;
            retirement = previousEntry.Retirement;
            SignalRetirementIfDrained(previousEntry);
        }

        retirement.Cancel();
        try
        {
            await previousEntry.Drained.Task.WaitAsync(_sessionDrainTimeout, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new SessionPublication(session, previousEntry, retirement, expectedGeneration);
        }
        catch (TimeoutException exception)
        {
            AbortDrain(previousEntry, retirement);
            throw new RuntimeUnavailableException(
                $"The active package session did not drain within {_sessionDrainTimeout.TotalSeconds:0.###} seconds; reload was not applied.",
                exception);
        }
        catch
        {
            AbortDrain(previousEntry, retirement);
            throw;
        }
    }

    internal async Task<(long Generation, IReadOnlyList<string> Warnings)> CommitPublicationAsync(
        SessionPublication publication,
        Action<long>? committed = null)
    {
        long generation;
        lock (_syncRoot)
        {
            if (publication.Completed)
            {
                throw new InvalidOperationException("The package session publication is already complete.");
            }
            if (!ReferenceEquals(_activeEntry, publication.PreviousEntry) || !publication.PreviousEntry.Draining)
            {
                throw new InvalidOperationException("The active package session changed while it was draining.");
            }
            if (publication.ExpectedGeneration is not null && publication.ExpectedGeneration != _generation)
            {
                throw new RuntimeStaleGenerationException("The active package session changed while the replacement was being prepared.");
            }

            generation = checked(_generation + 1);
            publication.PreviousEntry.Retired = true;
            publication.Completed = true;
            publication.Committed = true;
            _generation = generation;
            _activeEntry = new SessionEntry(publication.Session, generation);
        }

        Exception? publicationException = null;
        try
        {
            clearAuthSessions();
            committed?.Invoke(generation);
        }
        catch (Exception exception)
        {
            publicationException = exception;
        }

        var warnings = await RetireSessionAsync(publication.PreviousEntry);
        if (publicationException is not null)
        {
            throw publicationException;
        }
        return (generation, warnings);
    }

    internal void DiscardPublication(SessionPublication publication)
    {
        if (publication.Completed)
        {
            return;
        }
        publication.Completed = true;
        AbortDrain(publication.PreviousEntry, publication.Retirement);
    }

    public bool HandlePackageFault(
        string packageId,
        long generation,
        PackageFailureOrigin origin,
        Exception exception,
        string action,
        Action<long>? committed = null)
    {
        ActiveLoadedPackage? packageToDeactivate;
        PackageDeactivationWork? deactivation;
        long committedGeneration;
        lock (_syncRoot)
        {
            if (generation != _generation
                || _activeEntry.Draining
                || !_activeEntry.Session.MarkPackageFailed(packageId, origin, exception.Message, out packageToDeactivate))
            {
                return false;
            }

            _generation = checked(_generation + 1);
            _activeEntry.Generation = _generation;
            committedGeneration = _generation;
            deactivation = CreatePackageDeactivationLocked(packageId, packageToDeactivate);
        }

        removePackageAuthSessions(packageId);
        committed?.Invoke(committedGeneration);
        QueuePackageDeactivation(deactivation);
        logger.LogError(exception, "Failed to {Action} for package {PackageId}; package disabled for current session", action, packageId);
        return true;
    }

    public ActiveLoadedPackage? GetLoadedPackage(PackageSessionLease lease, string packageId)
    {
        lock (_syncRoot)
        {
            return lease.Session.TryGetLoadedPackage(packageId, out var loadedPackage) ? loadedPackage : null;
        }
    }

    public bool ExecuteIfCurrent(PackageSessionLease lease, Func<bool> action)
    {
        lock (_syncRoot)
        {
            if (!ReferenceEquals(lease.Identity, _activeEntry)
                || lease.Generation != _activeEntry.Generation
                || _activeEntry.Draining)
            {
                return false;
            }

            return action();
        }
    }

    public long Generation
    {
        get
        {
            lock (_syncRoot)
            {
                return _generation;
            }
        }
    }

    internal ActivePackageSession ActiveSession
    {
        get
        {
            lock (_syncRoot)
            {
                return _activeEntry.Session;
            }
        }
    }

    public IReadOnlyList<PackageExtensionContribution<TContract>> GetExtensionContributions<TContract>(
        PackageSessionLease lease,
        PackageExtensionPoint<TContract> extensionPoint)
    {
        lock (_syncRoot)
        {
            return lease.Session.GetExtensionContributions(extensionPoint);
        }
    }

    public string? TryResolvePackageAssetPath(string packageId, string assetPath)
    {
        lock (_syncRoot)
        {
            return _activeEntry.Session.TryResolvePackageAssetPath(packageId, assetPath);
        }
    }

    private PackageDeactivationWork? CreatePackageDeactivationLocked(
        string packageId,
        ActiveLoadedPackage? loadedPackage)
    {
        if (loadedPackage is null)
        {
            return null;
        }

        var barrier = CaptureLeaseBarrier(_activeEntry);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeEntry.PendingCleanups.Add(completion.Task);
        return new PackageDeactivationWork(packageId, loadedPackage, barrier.Completion.Task, completion);
    }

    private void QueuePackageDeactivation(PackageDeactivationWork? work)
    {
        if (work is null)
        {
            return;
        }

        work.StartedTask = RunPackageDeactivationAsync(work);
    }

    private async Task RunPackageDeactivationAsync(PackageDeactivationWork work)
    {
        try
        {
            await work.LeasesDrained;
            await DeactivateLoadedPackageAsync(work.PackageId, work.LoadedPackage);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to deactivate package {PackageId} after a package fault", work.PackageId);
        }
        finally
        {
            work.Completion.TrySetResult();
        }
    }

    private async Task<IReadOnlyList<string>> RetireSessionAsync(SessionEntry entry)
    {
        await entry.Drained.Task;
        var cleanup = CleanupRetiredSessionAsync(entry);
        try
        {
            return await cleanup.WaitAsync(_sessionDrainTimeout);
        }
        catch (TimeoutException)
        {
            const string warning = "Previous package session cleanup exceeded the drain deadline; its code remains quarantined until cleanup exits.";
            logger.LogWarning(warning);
            _ = ObserveRetiredSessionCleanupAsync(cleanup);
            return [warning];
        }
        catch (Exception exception)
        {
            var warning = $"Previous package session cleanup failed: {exception.Message}";
            logger.LogWarning(exception, "Previous package session cleanup failed");
            return [warning];
        }
    }

    private async Task<IReadOnlyList<string>> CleanupRetiredSessionAsync(SessionEntry entry)
    {
        var started = Stopwatch.GetTimestamp();
        Task[] pendingCleanups;
        lock (_syncRoot)
        {
            pendingCleanups = entry.PendingCleanups.ToArray();
        }
        await Task.WhenAll(pendingCleanups);

        if (ReferenceEquals(entry.Session, ActivePackageSession.Empty))
        {
            entry.Retirement.Dispose();
            return [];
        }

        var warnings = new List<string>();
        try
        {
            var stopStarted = Stopwatch.GetTimestamp();
            await entry.Session.StopBackgroundServicesAsync(logger, _sessionDrainTimeout);
            logger.LogInformation(
                "Stopped retired package background services in {ElapsedMilliseconds} ms",
                Stopwatch.GetElapsedTime(stopStarted).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to stop background services while retiring the previous package session");
            warnings.Add($"Previous package background services did not stop cleanly: {ex.Message}");
        }

        try
        {
            var disposeStarted = Stopwatch.GetTimestamp();
            await entry.Session.DisposeAsync();
            logger.LogInformation(
                "Disposed the retired package session in {ElapsedMilliseconds} ms",
                Stopwatch.GetElapsedTime(disposeStarted).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to dispose the previous package session cleanly");
            warnings.Add($"Previous package session cleanup failed: {ex.Message}");
        }

        entry.Retirement.Dispose();
        logger.LogInformation(
            "Cleaned the retired package session in {ElapsedMilliseconds} ms",
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return warnings;
    }

    private async Task ObserveRetiredSessionCleanupAsync(Task<IReadOnlyList<string>> cleanup)
    {
        try
        {
            await cleanup;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Deferred package session cleanup failed");
        }
    }

    private void AbortDrain(SessionEntry entry, CancellationTokenSource cancelledRetirement)
    {
        lock (_syncRoot)
        {
            if (!ReferenceEquals(_activeEntry, entry) || entry.Retired || !entry.Draining)
            {
                return;
            }

            entry.Draining = false;
            entry.Retirement = new CancellationTokenSource();
            entry.Drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        cancelledRetirement.Dispose();
    }

    private void ReleaseLease(SessionEntry entry, long leaseId)
    {
        List<TaskCompletionSource>? completedBarriers = null;
        TaskCompletionSource? retirement = null;
        lock (_syncRoot)
        {
            if (!entry.ActiveLeaseIds.Remove(leaseId))
            {
                return;
            }

            foreach (var barrier in entry.LeaseBarriers.ToArray())
            {
                barrier.PendingLeaseIds.Remove(leaseId);
                if (barrier.PendingLeaseIds.Count == 0)
                {
                    entry.LeaseBarriers.Remove(barrier);
                    (completedBarriers ??= []).Add(barrier.Completion);
                }
            }

            if ((entry.Draining || entry.Retired) && entry.ActiveLeaseIds.Count == 0)
            {
                retirement = entry.Drained;
            }
        }

        if (completedBarriers is not null)
        {
            foreach (var barrier in completedBarriers)
            {
                barrier.TrySetResult();
            }
        }
        retirement?.TrySetResult();
    }

    private static LeaseBarrier CaptureLeaseBarrier(SessionEntry entry)
    {
        var barrier = new LeaseBarrier(entry.ActiveLeaseIds);
        if (barrier.PendingLeaseIds.Count == 0)
        {
            barrier.Completion.TrySetResult();
        }
        else
        {
            entry.LeaseBarriers.Add(barrier);
        }
        return barrier;
    }

    private static void SignalRetirementIfDrained(SessionEntry entry)
    {
        if (entry.ActiveLeaseIds.Count == 0)
        {
            entry.Drained.TrySetResult();
        }
    }

    private async Task DeactivateLoadedPackageAsync(string packageId, ActiveLoadedPackage loadedPackage)
    {
        var lifecycleOperations = new List<Task>();
        await PackageSessionLifecycle.StopBackgroundServicesAsync(
            loadedPackage.BackgroundServices,
            packageId,
            logger,
            operationStarted: lifecycleOperations.Add);
        try
        {
            await Task.WhenAll(lifecycleOperations);
        }
        catch
        {
            // Stop failures were logged above; disposal must still wait until package code exits.
        }
        await PackageSessionLifecycle.DisposeOwnedServiceProviderAsync(loadedPackage.ServiceProvider);
        loadedPackage.LoadContext.Unload();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    internal sealed class SessionEntry(ActivePackageSession session, long generation)
    {
        public ActivePackageSession Session { get; } = session;
        public long Generation { get; set; } = generation;
        public HashSet<long> ActiveLeaseIds { get; } = [];
        public List<LeaseBarrier> LeaseBarriers { get; } = [];
        public List<Task> PendingCleanups { get; } = [];
        public TaskCompletionSource Drained { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Retirement { get; set; } = new();
        public long LastLeaseId { get; set; }
        public bool Retired { get; set; }
        public bool Draining { get; set; }
    }

    internal sealed class SessionPublication
    {
        private readonly SessionEntry _previousEntry;
        private readonly CancellationTokenSource _retirement;

        internal SessionPublication(
            ActivePackageSession session,
            SessionEntry previousEntry,
            CancellationTokenSource retirement,
            long? expectedGeneration)
        {
            Session = session;
            _previousEntry = previousEntry;
            _retirement = retirement;
            ExpectedGeneration = expectedGeneration;
        }

        public ActivePackageSession Session { get; }
        internal SessionEntry PreviousEntry => _previousEntry;
        internal CancellationTokenSource Retirement => _retirement;
        public long? ExpectedGeneration { get; }
        public bool Completed { get; set; }
        public bool Committed { get; set; }
    }

    internal sealed class LeaseBarrier(IEnumerable<long> leaseIds)
    {
        public HashSet<long> PendingLeaseIds { get; } = [.. leaseIds];
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PackageDeactivationWork(
        string packageId,
        ActiveLoadedPackage loadedPackage,
        Task leasesDrained,
        TaskCompletionSource completion)
    {
        public string PackageId { get; } = packageId;
        public ActiveLoadedPackage LoadedPackage { get; } = loadedPackage;
        public Task LeasesDrained { get; } = leasesDrained;
        public TaskCompletionSource Completion { get; } = completion;
        public Task? StartedTask { get; set; }
    }
}
