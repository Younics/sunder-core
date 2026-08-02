using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class PackageSessionLifecycleService
{
    public IReadOnlyList<DevPackageWatchTarget> GetDevWatchTargets()
        => _sessions.Sources.Snapshot().ActiveDevOverlays
            .Where(static overlay => overlay.Watch)
            .Select(static overlay => new DevPackageWatchTarget(overlay.PackageId, overlay.Folder))
            .ToArray();

    public async Task<IReadOnlyList<DevPackageOwnerPackage>> ReplaceAppDevPackageOwnerAsync(
        string ownerId,
        IReadOnlyList<DevPackageOwnerFolder> folders,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await _gate.EnterAsync(cancellationToken);
        var operationToken = operation.CancellationToken;
        var previousSources = _sessions.Sources.Snapshot();
        var sources = _sessions.Sources.Snapshot();
        var packageStateBefore = _sessions.GetSnapshot();
        var activeBefore = sources.ActiveDevOverlays;
        var replacements = await CreateAppOwnerOverlaysAsync(ownerId, folders, operationToken);
        foreach (var replacement in replacements)
        {
            operationToken.ThrowIfCancellationRequested();
            var conflict = sources.DevOverlays.FirstOrDefault(existing =>
                !string.Equals(existing.OwnerId, ownerId, StringComparison.Ordinal)
                && string.Equals(existing.PackageId, replacement.PackageId, StringComparison.OrdinalIgnoreCase)
                && !PathsEqual(existing.Folder, replacement.Folder));
            if (conflict is not null)
            {
                throw new RuntimeConflictException(
                    $"Dev package '{replacement.PackageId}' is already owned from folder '{conflict.Folder}' and cannot also use '{replacement.Folder}'.");
            }
        }

        sources.ReplaceDevOverlaysForAppOwner(ownerId, replacements);
        if (ActiveOverlaySetsEqual(activeBefore, sources.ActiveDevOverlays))
        {
            _sessions.Sources.Replace(sources);
            return ToOwnerPackages(replacements);
        }

        PackageLifecycleOperationResult result;
        var publicationWasCommitted = false;
        try
        {
            result = await LoadLifecycleCoreAsync(
                [],
                operationToken,
                sources,
                publicationCommitted: committed => publicationWasCommitted = committed);
        }
        catch (OperationCanceledException exception)
        {
            if (publicationWasCommitted
                && !await TryRestorePreviousPackageSessionAsync(
                    previousSources,
                    packageStateBefore).ConfigureAwait(false))
            {
                throw new RuntimeUnavailableException(
                    "The App dev package owner request was cancelled after publication and the previous package session could not be restored.",
                    exception);
            }
            throw;
        }
        if (!result.Success)
        {
            if (publicationWasCommitted)
            {
                if (!await TryRestorePreviousPackageSessionAsync(
                        previousSources,
                        packageStateBefore).ConfigureAwait(false))
                {
                    throw new RuntimeUnavailableException(
                        "The Runtime rejected the App dev package owner set but could not restore the previous package session.");
                }
            }
            throw new RuntimePackageValidationException(
                result.Errors.FirstOrDefault() ?? result.Message ?? "The Runtime rejected the App dev package owner set.");
        }

        return ToOwnerPackages(replacements);
    }

    private async Task<bool> TryRestorePreviousPackageSessionAsync(
        PackageSessionSourceSnapshot previousSources,
        RuntimePackageSnapshot packageStateBefore)
    {
        var restorationBudget = _lifecyclePolicy.PackageBackgroundServiceStartupTimeout
                                + _lifecyclePolicy.PackageRuntimeGenerationActivationTimeout
                                * _lifecyclePolicy.PackageRuntimeGenerationActivationAttempts
                                + _lifecyclePolicy.PackageBackgroundServiceCleanupTimeout;
        using var restorationDeadline = new CancellationTokenSource(restorationBudget);
        try
        {
            var restoration = await LoadLifecycleCoreAsync(
                [],
                restorationDeadline.Token,
                previousSources,
                allowPackageErrors: true);
            return restoration.Success
                   && DevOverlaySetsEqual(
                       previousSources.DevOverlays,
                       _sessions.Sources.Snapshot().DevOverlays)
                   && PackageStateSetsEqual(packageStateBefore, _sessions.GetSnapshot());
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to restore the previous package session after rejecting an App dev package owner request");
            return false;
        }
    }

    public async Task ReleaseAppDevPackageOwnerAsync(
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await _gate.EnterAsync(cancellationToken);
        var operationToken = operation.CancellationToken;
        var previousSources = _sessions.Sources.Snapshot();
        var sources = _sessions.Sources.Snapshot();
        var packageStateBefore = _sessions.GetSnapshot();
        var activeBefore = sources.ActiveDevOverlays;
        if (!sources.RemoveDevOverlaysForAppOwner(ownerId))
        {
            return;
        }

        if (ActiveOverlaySetsEqual(activeBefore, sources.ActiveDevOverlays))
        {
            _sessions.Sources.Replace(sources);
            return;
        }

        PackageLifecycleOperationResult result;
        var publicationWasCommitted = false;
        try
        {
            result = await LoadLifecycleCoreAsync(
                [],
                operationToken,
                sources,
                allowPackageErrors: true,
                publicationCommitted: committed => publicationWasCommitted = committed);
        }
        catch (OperationCanceledException exception)
        {
            if (publicationWasCommitted
                && !await TryRestorePreviousPackageSessionAsync(
                    previousSources,
                    packageStateBefore).ConfigureAwait(false))
            {
                throw new RuntimeUnavailableException(
                    "The App dev package owner release was cancelled after publication and the previous package session could not be restored.",
                    exception);
            }
            throw;
        }
        if (!result.Success)
        {
            if (publicationWasCommitted
                && !await TryRestorePreviousPackageSessionAsync(
                    previousSources,
                    packageStateBefore).ConfigureAwait(false))
            {
                throw new RuntimeUnavailableException(
                    "The Runtime could not release the App dev package owner or restore the previous package session.");
            }
            throw new RuntimePackageValidationException(
                result.Errors.FirstOrDefault() ?? result.Message ?? "The Runtime could not release the App dev package owner set.");
        }
    }

    private static async Task<IReadOnlyList<PackageSessionDevOverlay>> CreateAppOwnerOverlaysAsync(
        string ownerId,
        IReadOnlyList<DevPackageOwnerFolder> folders,
        CancellationToken cancellationToken)
    {
        var overlays = new Dictionary<string, PackageSessionDevOverlay>(StringComparer.OrdinalIgnoreCase);
        foreach (var requested in folders)
        {
            if (string.IsNullOrWhiteSpace(requested.Folder))
            {
                throw new RuntimeValidationException("A dev package owner folder cannot be empty.");
            }

            var fullPath = Path.GetFullPath(requested.Folder);
            var identity = await ReadDevPackageIdentityAsync(fullPath, cancellationToken);
            if (identity.PackageId is null)
            {
                throw new RuntimePackageValidationException(
                    identity.Error ?? $"'{fullPath}' is not a loadable Sunder dev package folder.");
            }
            var packageId = identity.PackageId;

            if (overlays.TryGetValue(packageId, out var existing))
            {
                if (!PathsEqual(existing.Folder, fullPath))
                {
                    throw new RuntimeConflictException(
                        $"Dev package owner set contains package '{packageId}' from both '{existing.Folder}' and '{fullPath}'.");
                }

                overlays[packageId] = existing with { Watch = existing.Watch || requested.Watch };
                continue;
            }

            overlays[packageId] = new PackageSessionDevOverlay(
                packageId,
                fullPath,
                requested.Watch,
                PackageSessionOverlayOwner.AppInvocation,
                ownerId);
        }

        return overlays.Values
            .OrderBy(static overlay => overlay.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<DevPackageOwnerPackage> ToOwnerPackages(
        IEnumerable<PackageSessionDevOverlay> overlays)
        => overlays
            .Select(static overlay => new DevPackageOwnerPackage(overlay.PackageId, overlay.Watch))
            .ToArray();

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool ActiveOverlaySetsEqual(
        IReadOnlyList<PackageSessionDevOverlay> left,
        IReadOnlyList<PackageSessionDevOverlay> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var rightById = right.ToDictionary(static overlay => overlay.PackageId, StringComparer.OrdinalIgnoreCase);
        return left.All(overlay =>
            rightById.TryGetValue(overlay.PackageId, out var candidate)
            && PathsEqual(overlay.Folder, candidate.Folder));
    }

    private static bool DevOverlaySetsEqual(
        IReadOnlyList<PackageSessionDevOverlay> left,
        IReadOnlyList<PackageSessionDevOverlay> right)
        => left.Count == right.Count
           && left.All(overlay => right.Any(candidate =>
               string.Equals(candidate.PackageId, overlay.PackageId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(candidate.OwnerKey, overlay.OwnerKey, StringComparison.Ordinal)
               && candidate.Watch == overlay.Watch
               && PathsEqual(candidate.Folder, overlay.Folder)));

    private static bool PackageStateSetsEqual(
        RuntimePackageSnapshot expected,
        RuntimePackageSnapshot actual)
        => expected.ActivePackages
               .Select(static package => ToComparableState(package, failureOrigin: null))
               .OrderBy(static package => package.PackageId, StringComparer.OrdinalIgnoreCase)
               .SequenceEqual(actual.ActivePackages
                   .Select(static package => ToComparableState(package, failureOrigin: null))
                   .OrderBy(static package => package.PackageId, StringComparer.OrdinalIgnoreCase))
           && expected.SessionPackages
               .Select(static package => ToComparableState(package, package.FailureOrigin))
               .OrderBy(static package => package.PackageId, StringComparer.OrdinalIgnoreCase)
               .SequenceEqual(actual.SessionPackages
                   .Select(static package => ToComparableState(package, package.FailureOrigin))
                   .OrderBy(static package => package.PackageId, StringComparer.OrdinalIgnoreCase));

    private static ComparablePackageState ToComparableState(
        ActivePackageDescriptor package,
        PackageFailureOrigin? failureOrigin)
        => new(
            package.PackageId.ToUpperInvariant(),
            package.Version,
            package.HostRoles,
            package.IsEnabled,
            package.Readiness,
            failureOrigin,
            string.Join('\n', package.Views.Select(static view => view.ViewId)));

    private static ComparablePackageState ToComparableState(
        SessionPackageDescriptor package,
        PackageFailureOrigin? failureOrigin)
        => new(
            package.PackageId.ToUpperInvariant(),
            package.Version,
            package.HostRoles,
            package.IsEnabled,
            package.Readiness,
            failureOrigin,
            string.Join('\n', package.Views.Select(static view => view.ViewId)));

    private sealed record ComparablePackageState(
        string PackageId,
        string Version,
        PackageHostRoles HostRoles,
        bool IsEnabled,
        PackageReadinessState Readiness,
        PackageFailureOrigin? FailureOrigin,
        string ViewIds);
}
