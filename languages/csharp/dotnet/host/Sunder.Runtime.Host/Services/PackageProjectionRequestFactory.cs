using Sunder.Package.Format;
using Sunder.Package.Projections;

namespace Sunder.Runtime.Host.Services;

internal static class PackageProjectionRequestFactory
{
    internal const string RulesIdentity = "sunder.package.target-union.v1";

    public static PackageProjectionRequest CreateRuntime(RuntimePackageTargetSource targetSource)
        => Create(targetSource, PackageProjectionRole.Runtime, includeManifest: false);

    public static PackageProjectionRequest CreateApp(RuntimePackageTargetSource targetSource)
        => Create(targetSource, PackageProjectionRole.App, includeManifest: true);

    public static PackageProjectionRequest Create(
        string sourceRoot,
        string packageId,
        string sourceContentIdentity,
        SunderPackageManifest manifest,
        SunderPackageContentIndex contentIndex,
        SunderPackageTargetKey targetKey,
        SunderPackageTargetManifest target,
        PackageProjectionRole role,
        bool includeManifest)
    {
        PackageManifestAdmissionPolicy.EnsureSupported(manifest);
        var entries = (contentIndex.Files
                       ?? throw new InvalidDataException("Package content index is missing files."))
            .ToDictionary(
                static entry => entry?.Path
                    ?? throw new InvalidDataException("Package content index contains a null entry."),
                static entry => entry!,
                StringComparer.Ordinal);
        var files = new Dictionary<string, PackageProjectionSourceFile>(StringComparer.Ordinal);
        if (includeManifest)
        {
            Add(SunderPackageFormat.ManifestPath, SunderPackageFormat.ManifestPath);
        }
        foreach (var projection in SunderPackageTargetResolver
                     .CreateProjectionPlan(manifest, contentIndex, targetKey)
                     .Files)
        {
            Add(projection.PhysicalPath.ToString(), projection.LogicalPath.ToString());
        }

        var warmPaths = new HashSet<string>(StringComparer.Ordinal)
        {
            target.EntryPoint!,
        };
        if (includeManifest)
        {
            warmPaths.Add(SunderPackageFormat.ManifestPath);
        }
        foreach (var bundle in manifest.ContractBundles ?? [])
        {
            if (bundle?.DescriptorPath is not null)
            {
                warmPaths.Add(bundle.DescriptorPath);
            }
        }
        warmPaths.IntersectWith(files.Keys);
        return new PackageProjectionRequest(
            sourceRoot,
            packageId,
            sourceContentIdentity,
            role,
            RulesIdentity,
            [targetKey.ToString()],
            files.Values.OrderBy(static file => file.ProjectionRelativePath, StringComparer.Ordinal).ToArray(),
            warmPaths.Order(StringComparer.Ordinal).ToArray());

        void Add(string physicalPath, string logicalPath)
        {
            if (!entries.TryGetValue(physicalPath, out var entry))
            {
                throw new InvalidDataException(
                    $"Package target '{targetKey}' references unindexed file '{physicalPath}'.");
            }
            if (!files.TryAdd(logicalPath, new PackageProjectionSourceFile(
                    physicalPath,
                    logicalPath,
                    entry.Sha256!,
                    entry.Size)))
            {
                throw new InvalidDataException(
                    $"Package target '{targetKey}' projection collides at '{logicalPath}'.");
            }
        }
    }

    private static PackageProjectionRequest Create(
        RuntimePackageTargetSource targetSource,
        PackageProjectionRole role,
        bool includeManifest)
    {
        var source = targetSource.Source;
        if (!InstalledPackageProjection.Includes(source.AcquiredTargets, targetSource.TargetKey.Role, targetSource.TargetKey.Rid))
            throw new InvalidDataException("Package projection target was not acquired by the installed transaction.");
        return Create(
            source.EffectiveSnapshotFolder,
            source.PackageId,
            source.ContentIdentity
            ?? throw new InvalidDataException($"Package source '{source.PackageId}' has no immutable content identity."),
            source.Manifest
            ?? throw new InvalidDataException($"Package source '{source.PackageId}' has no strict manifest."),
            source.ContentIndex
            ?? throw new InvalidDataException($"Package source '{source.PackageId}' has no strict content index."),
            targetSource.TargetKey,
            targetSource.Target,
            role,
            includeManifest);
    }
}
