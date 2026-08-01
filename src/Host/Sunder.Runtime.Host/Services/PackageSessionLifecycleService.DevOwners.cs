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
        var sources = _sessions.Sources.Snapshot();
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

        var result = await LoadLifecycleCoreAsync([], operationToken, sources);
        if (!result.Success)
        {
            throw new RuntimePackageValidationException(
                result.Errors.FirstOrDefault() ?? result.Message ?? "The Runtime rejected the App dev package owner set.");
        }

        return ToOwnerPackages(replacements);
    }

    public async Task ReleaseAppDevPackageOwnerAsync(
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await _gate.EnterAsync(cancellationToken);
        var operationToken = operation.CancellationToken;
        var sources = _sessions.Sources.Snapshot();
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

        var result = await LoadLifecycleCoreAsync(
            [],
            operationToken,
            sources,
            allowPackageErrors: true);
        if (!result.Success)
        {
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
}
