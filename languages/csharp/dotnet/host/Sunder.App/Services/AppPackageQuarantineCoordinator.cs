using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

internal sealed record AppPackageQuarantineContext(
    long BaselineRevision,
    IReadOnlySet<string> RetryPackageQuarantineIds,
    IReadOnlySet<string> RetryViewQuarantinePackageIds)
{
    public static AppPackageQuarantineContext Empty { get; } = new(
        0,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public bool IgnoresPackage(string packageId, long quarantineRevision)
        => quarantineRevision <= BaselineRevision
            && RetryPackageQuarantineIds.Contains(packageId);

    public bool IgnoresView(string packageId, long quarantineRevision)
        => quarantineRevision <= BaselineRevision
            && RetryViewQuarantinePackageIds.Contains(packageId);
}

internal sealed class AppPackageCandidateRebuildRequiredException(
    IReadOnlyList<string> packageIds)
    : Exception("An activated App package candidate was quarantined and must be rebuilt.")
{
    public IReadOnlyList<string> PackageIds { get; } = packageIds;
}

internal sealed record AppPackageFaultQuarantineAcceptance(
    Task<AppPackageUiCleanupOutcome> CleanupCompletion,
    string? ContentHash,
    Guid FaultedGenerationId,
    bool CanAttemptRecovery,
    AppPackageFaultNoticeIncident? FaultNotice);

internal readonly record struct AppPackageViewQuarantineAcceptance(
    AppPackageGeneration Generation,
    bool PackageQuarantined,
    bool NewlyQuarantined);

internal sealed class AppPackageQuarantineCoordinator(
    Func<AppPackageGeneration> getCurrentGeneration)
{
    // This gate owns quarantine state and candidate eligibility; publication stays with the publisher.
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, PackageQuarantineRecord> _packageQuarantines =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ViewQuarantineRecord> _viewQuarantines =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, PendingCandidateRegistration> _pendingCandidates = [];
    private readonly HashSet<string> _acceptedPackageFaultGenerations =
        new(StringComparer.OrdinalIgnoreCase);
    private long _quarantineRevision;

    internal int PendingCandidateCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _pendingCandidates.Count;
            }
        }
    }

    internal int PackageQuarantineCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _packageQuarantines.Count;
            }
        }
    }

    internal int AcceptedPackageFaultCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _acceptedPackageFaultGenerations.Count;
            }
        }
    }

    public void RegisterPendingCandidate(
        AppPackageGeneration generation,
        bool rejectOnFault)
    {
        lock (_syncRoot)
        {
            _pendingCandidates.Add(
                generation.Id,
                new PendingCandidateRegistration(
                    new WeakReference<AppPackageGeneration>(generation),
                    rejectOnFault));
        }
    }

    public void UnregisterPendingCandidate(AppPackageGeneration generation)
    {
        lock (_syncRoot)
        {
            RemovePendingCandidate(generation);
        }
    }

    public AppPackageQuarantineContext CaptureCandidateContext(
        IReadOnlyCollection<string>? retryDisabledPackageIds)
    {
        lock (_syncRoot)
        {
            var retryPackageIds = retryDisabledPackageIds is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(
                    retryDisabledPackageIds,
                    StringComparer.OrdinalIgnoreCase);
            var retryViewPackageIds = retryDisabledPackageIds
                is AppPackageQuarantineRetryIntent { PreserveKnownViewQuarantines: true }
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(retryPackageIds, StringComparer.OrdinalIgnoreCase);
            return new AppPackageQuarantineContext(
                _quarantineRevision,
                retryPackageIds,
                retryViewPackageIds);
        }
    }

    public async Task ApplyInheritedQuarantinesAsync(
        AppPackageGeneration generation,
        CancellationToken cancellationToken,
        bool rejectActivatedPackages = true)
    {
        QuarantineInheritance inheritance;
        lock (_syncRoot)
        {
            if (rejectActivatedPackages)
            {
                ThrowIfCandidateCannotCommit(generation);
            }
            inheritance = ApplyMatchingQuarantines(
                generation,
                commitCandidate: false);
        }

        foreach (var packageId in inheritance.PackageIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await generation.Composition.ApplyInheritedPackageQuarantineAsync(packageId)
                .ConfigureAwait(false);
        }
        foreach (var quarantine in inheritance.Views)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var drain = generation.Composition.ViewFacade.BeginCancelViewOperations(
                quarantine.ViewId);
            await drain.ConfigureAwait(false);
            await generation.ViewRegistry.RemoveCachedViewAsync(
                quarantine.PackageId,
                quarantine.ViewId,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    public void CommitWithFence(
        AppPackageGeneration candidate,
        Action commit)
    {
        lock (_syncRoot)
        {
            ThrowIfCandidateCannotCommit(candidate);
            var inheritance = ApplyMatchingQuarantines(
                candidate,
                commitCandidate: true);
            commit();
            RemovePendingCandidate(candidate);
            foreach (var quarantine in inheritance.PackageRecordsToClear)
            {
                if (_packageQuarantines.TryGetValue(
                        quarantine.PackageId,
                        out var current)
                    && current.Revision == quarantine.Revision)
                {
                    _packageQuarantines.Remove(quarantine.PackageId);
                }
            }
            foreach (var quarantine in inheritance.ViewRecordsToClear)
            {
                var key = GetViewQuarantineKey(
                    quarantine.PackageId,
                    quarantine.ViewId);
                if (_viewQuarantines.TryGetValue(key, out var current)
                    && current.Revision == quarantine.Revision)
                {
                    _viewQuarantines.Remove(key);
                }
            }
        }
    }

    public void ValidateCandidateForPublication(AppPackageGeneration candidate)
    {
        lock (_syncRoot)
        {
            ThrowIfCandidateCannotCommit(candidate);
        }
    }

    public void RememberPackageQuarantine(
        AppPackageGeneration generation,
        string packageId)
    {
        var contentHash = generation.State.GetPackageContentHash(packageId);
        if (string.IsNullOrWhiteSpace(contentHash))
        {
            return;
        }

        lock (_syncRoot)
        {
            _packageQuarantines[packageId] = new PackageQuarantineRecord(
                packageId,
                contentHash,
                ++_quarantineRevision);
        }
    }

    public bool TryQuarantineView(
        Guid generationId,
        string? contentHash,
        string packageId,
        string viewId,
        Action<Guid, string> quarantinePackageBoundaries,
        out AppPackageViewQuarantineAcceptance acceptance)
    {
        lock (_syncRoot)
        {
            var currentGeneration = getCurrentGeneration();
            AppPackageGeneration targetGeneration;
            if (currentGeneration.Id == generationId)
            {
                targetGeneration = currentGeneration;
            }
            else if (!string.IsNullOrWhiteSpace(contentHash)
                && string.Equals(
                    currentGeneration.State.GetPackageContentHash(packageId),
                    contentHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                targetGeneration = currentGeneration;
            }
            else
            {
                acceptance = default;
                return false;
            }
            if (targetGeneration.State.IsPackageDisabled(packageId))
            {
                quarantinePackageBoundaries(targetGeneration.Id, packageId);
                acceptance = new AppPackageViewQuarantineAcceptance(
                    targetGeneration,
                    PackageQuarantined: true,
                    NewlyQuarantined: false);
                return true;
            }
            if (!targetGeneration.ViewRegistry.TryQuarantineView(
                    packageId,
                    viewId,
                    out var newlyQuarantined))
            {
                acceptance = default;
                return false;
            }

            if (newlyQuarantined)
            {
                RememberViewQuarantine(targetGeneration, packageId, viewId);
            }
            acceptance = new AppPackageViewQuarantineAcceptance(
                targetGeneration,
                PackageQuarantined: false,
                newlyQuarantined);
            return true;
        }
    }

    public bool TryBeginPackageQuarantine(
        AppPackageFaultAttribution attribution,
        string packageId,
        string details,
        PackageFailureOrigin origin,
        Exception exception,
        Func<AppPackageGeneration, bool, string?, bool> shouldAttemptRecovery,
        out AppPackageFaultQuarantineAcceptance? acceptance)
    {
        lock (_syncRoot)
        {
            acceptance = null;
            var faultedContentHash = attribution.ContentHash;
            PendingCandidateRegistration? pendingCandidate = null;
            AppPackageGeneration targetGeneration;
            if (TryResolvePendingCandidate(
                    attribution,
                    packageId,
                    out var resolvedPendingCandidate,
                    out var resolvedCandidate))
            {
                pendingCandidate = resolvedPendingCandidate;
                targetGeneration = resolvedCandidate;
                if (pendingCandidate.FaultedPackageIds.Contains(packageId))
                {
                    return true;
                }
            }
            else
            {
                var currentGeneration = getCurrentGeneration();
                if (currentGeneration.Id == attribution.GenerationId)
                {
                    targetGeneration = currentGeneration;
                }
                else if (!string.IsNullOrWhiteSpace(faultedContentHash)
                    && string.Equals(
                        currentGeneration.State.GetPackageContentHash(packageId),
                        faultedContentHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    targetGeneration = currentGeneration;
                }
                else
                {
                    return false;
                }
            }

            var acceptedFaultKey = pendingCandidate is null
                && ReferenceEquals(targetGeneration, getCurrentGeneration())
                    ? CreateAcceptedPackageFaultKey(
                        attribution.GenerationId,
                        packageId,
                        faultedContentHash)
                    : null;
            if (acceptedFaultKey is not null
                && _acceptedPackageFaultGenerations.Contains(acceptedFaultKey))
            {
                return true;
            }

            _packageQuarantines.TryGetValue(packageId, out var previousQuarantine);
            if (!string.IsNullOrWhiteSpace(faultedContentHash))
            {
                _packageQuarantines[packageId] = new PackageQuarantineRecord(
                    packageId,
                    faultedContentHash!,
                    ++_quarantineRevision,
                    RejectCandidate: pendingCandidate?.RejectOnFault == true);
            }

            pendingCandidate?.FaultedPackageIds.Add(packageId);
            var reportedException = pendingCandidate is null
                ? exception
                : new InvalidOperationException(exception.ToString());
            var canAttemptRecovery = shouldAttemptRecovery(
                targetGeneration,
                pendingCandidate is not null,
                faultedContentHash);

            if (!targetGeneration.Composition.TryBeginDisablePackage(
                    packageId,
                    details,
                    origin,
                    reportedException,
                    deferNotification: canAttemptRecovery,
                    out var completion))
            {
                if (previousQuarantine is null)
                {
                    _packageQuarantines.Remove(packageId);
                }
                else
                {
                    _packageQuarantines[packageId] = previousQuarantine;
                }
                pendingCandidate?.FaultedPackageIds.Remove(packageId);
                return false;
            }

            if (acceptedFaultKey is not null)
            {
                _acceptedPackageFaultGenerations.Add(acceptedFaultKey);
            }

            acceptance = new AppPackageFaultQuarantineAcceptance(
                completion,
                faultedContentHash,
                attribution.GenerationId,
                canAttemptRecovery,
                targetGeneration.Composition.FaultNotifier
                    .GetPackageDisabledIncident(packageId));
            return true;
        }
    }

    public void RecordCleanupOutcome(
        Guid generationId,
        AppPackageUiCleanupOutcome cleanupOutcome)
    {
        if (cleanupOutcome == AppPackageUiCleanupOutcome.Proven)
        {
            return;
        }

        lock (_syncRoot)
        {
            var currentGeneration = getCurrentGeneration();
            if (currentGeneration.Id == generationId)
            {
                currentGeneration.Composition.RecordCleanupOutcome(cleanupOutcome);
                return;
            }
            if (_pendingCandidates.TryGetValue(generationId, out var pending)
                && pending.Generation.TryGetTarget(out var pendingGeneration))
            {
                pendingGeneration.Composition.RecordCleanupOutcome(cleanupOutcome);
            }
        }
    }

    private void ThrowIfCandidateCannotCommit(AppPackageGeneration generation)
    {
        if (_pendingCandidates.TryGetValue(generation.Id, out var pending)
            && pending.Generation.TryGetTarget(out var registeredGeneration)
            && ReferenceEquals(registeredGeneration, generation)
            && pending.FaultedPackageIds.Count > 0)
        {
            var packageIds = pending.FaultedPackageIds.ToArray();
            if (pending.RejectOnFault)
            {
                throw CreateCandidateFaultException(packageIds);
            }

            throw new AppPackageCandidateRebuildRequiredException(packageIds);
        }

        ThrowIfActivatedPackageRequiresRebuild(generation);
    }

    private void ThrowIfActivatedPackageRequiresRebuild(
        AppPackageGeneration generation)
    {
        var matchingQuarantines = _packageQuarantines
            .Where(entry => IsMatchingPackageQuarantine(
                generation,
                entry.Key,
                entry.Value))
            .Where(entry => generation.State.HasActivatedPackage(entry.Key))
            .ToArray();
        if (matchingQuarantines.Any(static entry => entry.Value.RejectCandidate))
        {
            throw CreateCandidateFaultException(
                matchingQuarantines.Select(static entry => entry.Key));
        }
        if (matchingQuarantines.Length > 0)
        {
            throw new AppPackageCandidateRebuildRequiredException(
                matchingQuarantines.Select(static entry => entry.Key).ToArray());
        }
    }

    private static InvalidOperationException CreateCandidateFaultException(
        IEnumerable<string> packageIds)
        => new(
            $"App package candidate failed after an asynchronous managed fault from '{string.Join("', '", packageIds)}'.");

    private static bool IsMatchingPackageQuarantine(
        AppPackageGeneration generation,
        string packageId,
        PackageQuarantineRecord quarantine)
        => !generation.QuarantineContext.IgnoresPackage(packageId, quarantine.Revision)
            && generation.State.GetPackageContentHash(packageId) is { } contentHash
            && string.Equals(
                contentHash,
                quarantine.ContentHash,
                StringComparison.OrdinalIgnoreCase);

    private QuarantineInheritance ApplyMatchingQuarantines(
        AppPackageGeneration generation,
        bool commitCandidate)
    {
        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packageRecordsToClear = new List<PackageQuarantineRecord>();
        foreach (var (packageId, quarantine) in _packageQuarantines.ToArray())
        {
            if (generation.QuarantineContext.IgnoresPackage(
                    packageId,
                    quarantine.Revision))
            {
                if (commitCandidate)
                {
                    packageRecordsToClear.Add(quarantine);
                }
                continue;
            }

            var candidateContentHash = generation.State.GetPackageContentHash(packageId);
            if (string.IsNullOrWhiteSpace(candidateContentHash))
            {
                continue;
            }
            if (!string.Equals(
                    candidateContentHash,
                    quarantine.ContentHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (commitCandidate)
                {
                    packageRecordsToClear.Add(quarantine);
                }
                continue;
            }

            generation.State.TryMarkPackageDisabled(packageId);
            packageIds.Add(packageId);
        }

        var views = new List<ViewQuarantineRecord>();
        var viewRecordsToClear = new List<ViewQuarantineRecord>();
        foreach (var quarantine in _viewQuarantines.Values.ToArray())
        {
            if (generation.QuarantineContext.IgnoresView(
                    quarantine.PackageId,
                    quarantine.Revision))
            {
                if (commitCandidate)
                {
                    viewRecordsToClear.Add(quarantine);
                }
                continue;
            }

            var candidateContentHash = generation.State.GetPackageContentHash(
                quarantine.PackageId);
            if (string.IsNullOrWhiteSpace(candidateContentHash))
            {
                continue;
            }
            if (!string.Equals(
                    candidateContentHash,
                    quarantine.ContentHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (commitCandidate)
                {
                    viewRecordsToClear.Add(quarantine);
                }
                continue;
            }
            if (packageIds.Contains(quarantine.PackageId))
            {
                continue;
            }
            if (generation.ViewRegistry.TryQuarantineView(
                    quarantine.PackageId,
                    quarantine.ViewId,
                    out _))
            {
                views.Add(quarantine);
            }
        }

        return new QuarantineInheritance(
            packageIds.ToArray(),
            views.ToArray(),
            packageRecordsToClear,
            viewRecordsToClear);
    }

    private bool TryResolvePendingCandidate(
        AppPackageFaultAttribution attribution,
        string packageId,
        out PendingCandidateRegistration pending,
        out AppPackageGeneration generation)
    {
        pending = null!;
        generation = null!;
        if (attribution.GenerationId == Guid.Empty
            || !_pendingCandidates.TryGetValue(attribution.GenerationId, out var candidate))
        {
            return false;
        }
        if (!candidate.Generation.TryGetTarget(out var registeredGeneration))
        {
            _pendingCandidates.Remove(attribution.GenerationId);
            return false;
        }
        generation = registeredGeneration;

        var contentHash = generation.State.GetPackageContentHash(packageId);
        if (string.IsNullOrWhiteSpace(contentHash)
            || !string.Equals(
                contentHash,
                attribution.ContentHash,
                StringComparison.OrdinalIgnoreCase))
        {
            generation = null!;
            return false;
        }

        pending = candidate;
        return true;
    }

    private void RemovePendingCandidate(AppPackageGeneration generation)
    {
        if (!_pendingCandidates.TryGetValue(generation.Id, out var pending)
            || pending.Generation.TryGetTarget(out var registeredGeneration)
            && !ReferenceEquals(registeredGeneration, generation))
        {
            return;
        }

        _pendingCandidates.Remove(generation.Id);
    }

    private void RememberViewQuarantine(
        AppPackageGeneration generation,
        string packageId,
        string viewId)
    {
        var contentHash = generation.State.GetPackageContentHash(packageId);
        if (string.IsNullOrWhiteSpace(contentHash))
        {
            return;
        }

        _viewQuarantines[GetViewQuarantineKey(packageId, viewId)] = new ViewQuarantineRecord(
            packageId,
            viewId,
            contentHash,
            ++_quarantineRevision);
    }

    private static string GetViewQuarantineKey(string packageId, string viewId)
        => $"{packageId}\0{viewId}";

    private static string? CreateAcceptedPackageFaultKey(
        Guid generationId,
        string packageId,
        string? contentHash)
        => generationId == Guid.Empty || string.IsNullOrWhiteSpace(contentHash)
            ? null
            : $"{generationId:N}\0{packageId.Trim()}\0{contentHash.Trim()}";

    private sealed record ViewQuarantineRecord(
        string PackageId,
        string ViewId,
        string ContentHash,
        long Revision);

    private sealed record PackageQuarantineRecord(
        string PackageId,
        string ContentHash,
        long Revision,
        bool RejectCandidate = false);

    private sealed class PendingCandidateRegistration(
        WeakReference<AppPackageGeneration> generation,
        bool rejectOnFault)
    {
        public WeakReference<AppPackageGeneration> Generation { get; } = generation;

        public bool RejectOnFault { get; } = rejectOnFault;

        public HashSet<string> FaultedPackageIds { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record QuarantineInheritance(
        IReadOnlyList<string> PackageIds,
        IReadOnlyList<ViewQuarantineRecord> Views,
        IReadOnlyList<PackageQuarantineRecord> PackageRecordsToClear,
        IReadOnlyList<ViewQuarantineRecord> ViewRecordsToClear);
}
