using Sunder.Package.Format;

namespace Sunder.Runtime.Host.Services;

internal static class InstalledPackageProjection
{
    internal static IReadOnlyList<SunderPackageTargetKey>? Keys(InstalledPackageRecord package)
    {
        if (package.AcquiredTargets is not { } targets) return null;
        if (targets.Count > SunderPackageFormat.MaxTargets
            || targets.Any(target => target is null || !SunderPackageTargetKey.TryCreate(target.Role, target.Rid, out _))
            || targets.Distinct().Count() != targets.Count)
            throw new InvalidDataException("Installed package acquisition scope is invalid or duplicated.");
        return targets.Select(target => target.ToKey()).ToArray();
    }

    internal static bool Includes(IReadOnlyList<SunderPackageTargetKey>? targets, string? role, string? rid)
        => targets is null || SunderPackageTargetKey.TryCreate(role, rid, out var key) && targets.Contains(key);

    internal static bool IncludesRole(RuntimePackageSource source, string role)
        => source.AcquiredTargets is null || source.AcquiredTargets.Any(key => key.Role == role);

    internal static IEnumerable<SunderPackageTargetManifest> Targets(InstalledPackageRecord package, SunderPackageManifest manifest)
    {
        var acquired = Keys(package);
        return (manifest.Targets ?? []).OfType<SunderPackageTargetManifest>()
            .Where(target => Includes(acquired, target.Role, target.Rid));
    }

    internal static Task<SunderPackageArchiveValidationResult> ValidateAsync(InstalledPackageRecord package, CancellationToken token = default)
        => Keys(package) is { } keys
            ? SunderPackageArchiveInspector.ValidateExtractedProjectionSetAsync(package.InstallPath, keys, token)
            : SunderPackageArchiveInspector.ValidateExtractedPackageAsync(package.InstallPath, token);

    internal static void ValidateScope(IReadOnlyList<SunderPackageTargetKey>? keys, SunderPackageManifest manifest)
    {
        if (keys is null) return;
        if (keys.Count > SunderPackageFormat.MaxTargets || keys.Distinct().Count() != keys.Count
            || keys.Any(key => !SunderPackageTargetKey.TryCreate(key.Role, key.Rid, out _)
                || !SunderPackageTargetResolver.TryResolveTarget(manifest, key, out _)))
            throw new InvalidDataException("Installed projection acquisition targets do not match the complete source manifest.");
    }
}
