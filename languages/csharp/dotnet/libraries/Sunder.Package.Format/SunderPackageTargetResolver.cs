namespace Sunder.Package.Format;

public enum SunderPackagePayloadLayer
{
    Shared,
    RoleShared,
    Target,
}

public readonly record struct SunderPackageTargetKey
{
    public SunderPackageTargetKey(string role, string rid)
    {
        if (!SunderPackageFormat.IsHostRole(role))
        {
            throw new ArgumentException($"Unknown package target role '{role}'.", nameof(role));
        }
        if (!SunderPackageFormat.IsRuntimeIdentifier(rid))
        {
            throw new ArgumentException($"Unknown package target RID '{rid}'.", nameof(rid));
        }

        Role = role;
        Rid = rid;
    }

    public string Role { get; }

    public string Rid { get; }

    public static bool TryCreate(string? role, string? rid, out SunderPackageTargetKey key)
    {
        if (SunderPackageFormat.IsHostRole(role) && SunderPackageFormat.IsRuntimeIdentifier(rid))
        {
            key = new SunderPackageTargetKey(role!, rid!);
            return true;
        }

        key = default;
        return false;
    }

    public override string ToString() => $"{Role}/{Rid}";
}

public sealed record SunderPackageProjectionFile(
    ArchiveRelativePath PhysicalPath,
    ArchiveRelativePath LogicalPath,
    SunderPackagePayloadLayer Layer);

public sealed record SunderPackageProjectionPlan(
    SunderPackageTargetKey Target,
    IReadOnlyList<SunderPackageProjectionFile> Files)
{
    public bool TryResolveLogicalPath(ArchiveRelativePath logicalPath, out ArchiveRelativePath physicalPath)
    {
        foreach (var file in Files)
        {
            if (file.LogicalPath == logicalPath)
            {
                physicalPath = file.PhysicalPath;
                return true;
            }
        }

        physicalPath = default;
        return false;
    }
}

public static class SunderPackageTargetResolver
{
    public static IReadOnlyList<SunderPackageTargetKey> EnumerateTargets(SunderPackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Targets is { Count: > SunderPackageFormat.MaxTargets })
        {
            throw new InvalidDataException($"Package manifest declares more than {SunderPackageFormat.MaxTargets} targets.");
        }
        var keys = new List<SunderPackageTargetKey>(manifest.Targets?.Count ?? 0);
        var seen = new HashSet<SunderPackageTargetKey>();
        foreach (var target in manifest.Targets ?? [])
        {
            if (target is null || !SunderPackageTargetKey.TryCreate(target.Role, target.Rid, out var key))
            {
                throw new InvalidDataException("Package manifest contains an invalid target key.");
            }
            if (!seen.Add(key))
            {
                throw new InvalidDataException($"Package manifest declares target '{key}' more than once.");
            }
            keys.Add(key);
        }

        return keys;
    }

    public static bool TryResolveTarget(
        SunderPackageManifest manifest,
        SunderPackageTargetKey key,
        out SunderPackageTargetManifest? target)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        target = null;
        if (!SunderPackageFormat.IsHostRole(key.Role)
            || !SunderPackageFormat.IsRuntimeIdentifier(key.Rid))
        {
            return false;
        }
        foreach (var candidate in manifest.Targets ?? [])
        {
            if (candidate is null
                || !string.Equals(candidate.Role, key.Role, StringComparison.Ordinal)
                || !string.Equals(candidate.Rid, key.Rid, StringComparison.Ordinal))
            {
                continue;
            }
            if (target is not null)
            {
                throw new InvalidDataException($"Package manifest declares target '{key}' more than once.");
            }
            target = candidate;
        }

        return target is not null;
    }

    public static SunderPackageProjectionPlan CreateProjectionPlan(
        SunderPackageManifest manifest,
        SunderPackageContentIndex contentIndex,
        SunderPackageTargetKey key)
    {
        if (!TryResolveTarget(manifest, key, out _))
        {
            throw new KeyNotFoundException($"Package manifest does not declare exact target '{key}'.");
        }

        return CreateProjectionPlan(contentIndex, key);
    }

    public static SunderPackageProjectionPlan CreateProjectionPlan(
        SunderPackageContentIndex contentIndex,
        SunderPackageTargetKey key)
    {
        ArgumentNullException.ThrowIfNull(contentIndex);
        EnsureValidKey(key);
        if (contentIndex.Files is null)
        {
            throw new InvalidDataException("Package content index is missing files.");
        }
        if (contentIndex.Files.Count > SunderPackageFormat.MaxContentIndexEntries)
        {
            throw new InvalidDataException($"Package content index contains more than {SunderPackageFormat.MaxContentIndexEntries} files.");
        }

        var physicalPaths = new List<ArchiveRelativePath>(contentIndex.Files.Count);
        foreach (var entry in contentIndex.Files)
        {
            if (entry is null)
            {
                throw new InvalidDataException("Package content index contains a null entry.");
            }
            if (!ArchiveRelativePath.TryParse(
                    entry.Path,
                    SunderPackageFormat.MaxArchivePathLength,
                    SunderPackageFormat.MaxArchivePathDepth,
                    out var path,
                    out var error))
            {
                throw new InvalidDataException($"Package content index contains an invalid path: {error}.");
            }
            physicalPaths.Add(path);
        }

        return CreateProjectionPlan(key, physicalPaths);
    }

    public static bool TryMapPhysicalPath(
        SunderPackageTargetKey key,
        ArchiveRelativePath physicalPath,
        out ArchiveRelativePath logicalPath,
        out SunderPackagePayloadLayer layer)
    {
        if (!SunderPackageFormat.IsHostRole(key.Role)
            || !SunderPackageFormat.IsRuntimeIdentifier(key.Rid))
        {
            logicalPath = default;
            layer = default;
            return false;
        }
        if (SunderPackageFormat.TryClassifyPayloadPath(
                physicalPath.ToString(),
                out layer,
                out var role,
                out var rid,
                out var logical)
            && (layer == SunderPackagePayloadLayer.Shared
                || string.Equals(role, key.Role, StringComparison.Ordinal)
                && (layer == SunderPackagePayloadLayer.RoleShared
                    || string.Equals(rid, key.Rid, StringComparison.Ordinal))))
        {
            logicalPath = ArchiveRelativePath.Parse(
                logical,
                SunderPackageFormat.MaxLogicalPathLength,
                SunderPackageFormat.MaxLogicalPathDepth);
            return true;
        }

        logicalPath = default;
        layer = default;
        return false;
    }

    internal static SunderPackageProjectionPlan CreateProjectionPlan(
        SunderPackageTargetKey key,
        IEnumerable<ArchiveRelativePath> physicalPaths)
    {
        EnsureValidKey(key);
        var mappings = new List<SunderPackageProjectionFile>();
        foreach (var physicalPath in physicalPaths)
        {
            if (TryMapPhysicalPath(key, physicalPath, out var logicalPath, out var layer))
            {
                mappings.Add(new SunderPackageProjectionFile(physicalPath, logicalPath, layer));
            }
        }

        var orderedMappings = mappings
            .OrderBy(static mapping => mapping.Layer)
            .ThenBy(static mapping => mapping.PhysicalPath.ToString(), StringComparer.Ordinal)
            .ToArray();

        var logicalPaths = new ArchivePathRegistry();
        foreach (var mapping in orderedMappings)
        {
            logicalPaths.Register(mapping.LogicalPath, isDirectory: false);
        }
        return new SunderPackageProjectionPlan(key, orderedMappings);
    }

    private static void EnsureValidKey(SunderPackageTargetKey key)
    {
        if (!SunderPackageFormat.IsHostRole(key.Role)
            || !SunderPackageFormat.IsRuntimeIdentifier(key.Rid))
        {
            throw new ArgumentException("Package target key must contain an exact supported role and RID.", nameof(key));
        }
    }
}
