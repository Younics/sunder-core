namespace Sunder.Package.Format;

public static class SunderPackageProjectionFormat
{
    public const int CurrentProjectionFormatVersion = 1;
    public const int CurrentContentIndexVersion = 1;
    public const string SharedKind = "shared";
    public const string AppKind = "app";
    public const string RuntimeKind = "runtime";
    public const string DescriptorPath = "manifest/sunder-projection.json";
    public const string ManifestPath = SunderPackageFormat.ManifestPath;
    public const string ContentIndexPath = SunderPackageFormat.ContentIndexPath;

    internal static bool TryMapPayloadPath(
        SunderPackageProjectionKey key,
        ArchiveRelativePath path,
        out ArchiveRelativePath logicalPath,
        out SunderPackagePayloadLayer layer)
    {
        logicalPath = default;
        layer = default;
        if (!key.IsValid
            || !SunderPackageFormat.TryClassifyPayloadPath(
                path.ToString(),
                out layer,
                out var role,
                out var rid,
                out var logical))
        {
            return false;
        }

        var selected = key.Kind switch
        {
            SharedKind => layer == SunderPackagePayloadLayer.Shared,
            AppKind or RuntimeKind => layer != SunderPackagePayloadLayer.Shared
                                      && string.Equals(role, key.Kind, StringComparison.Ordinal)
                                      && (layer == SunderPackagePayloadLayer.RoleShared
                                          || string.Equals(rid, key.Rid, StringComparison.Ordinal)),
            _ => false,
        };
        if (!selected)
        {
            layer = default;
            return false;
        }

        logicalPath = ArchiveRelativePath.Parse(
            logical,
            SunderPackageFormat.MaxLogicalPathLength,
            SunderPackageFormat.MaxLogicalPathDepth);
        return true;
    }

    internal static bool IsAllowedDirectoryPath(SunderPackageProjectionKey key, string path)
    {
        if (path is "manifest" or "payload")
        {
            return true;
        }

        if (key.Kind == SharedKind)
        {
            return IsRootOrDescendant(path, "payload/shared");
        }

        if (key.Kind is not (AppKind or RuntimeKind) || key.Rid is null)
        {
            return false;
        }

        var roleRoot = $"payload/{key.Kind}";
        return path == roleRoot
               || IsRootOrDescendant(path, roleRoot + "/shared")
               || IsRootOrDescendant(path, roleRoot + "/" + key.Rid);
    }

    private static bool IsRootOrDescendant(string path, string root)
        => string.Equals(path, root, StringComparison.Ordinal)
           || path.StartsWith(root + "/", StringComparison.Ordinal);
}

public readonly record struct SunderPackageProjectionKey
{
    public SunderPackageProjectionKey(string kind, string? rid)
    {
        if (!TryValidate(kind, rid))
        {
            throw new ArgumentException(
                "A projection key must be 'shared' without a RID, or 'app'/'runtime' with an exact supported RID.",
                nameof(kind));
        }

        Kind = kind;
        Rid = rid;
    }

    public string Kind { get; } = null!;

    public string? Rid { get; }

    public static SunderPackageProjectionKey Shared => new(SunderPackageProjectionFormat.SharedKind, null);

    public static SunderPackageProjectionKey App(string rid)
        => new(SunderPackageProjectionFormat.AppKind, rid);

    public static SunderPackageProjectionKey Runtime(string rid)
        => new(SunderPackageProjectionFormat.RuntimeKind, rid);

    public static bool TryCreate(string? kind, string? rid, out SunderPackageProjectionKey key)
    {
        if (kind is not null && TryValidate(kind, rid))
        {
            key = new SunderPackageProjectionKey(kind, rid);
            return true;
        }

        key = default;
        return false;
    }

    public bool TryGetTargetKey(out SunderPackageTargetKey targetKey)
    {
        if (Kind is SunderPackageProjectionFormat.AppKind or SunderPackageProjectionFormat.RuntimeKind
            && Rid is not null)
        {
            targetKey = new SunderPackageTargetKey(Kind, Rid);
            return true;
        }

        targetKey = default;
        return false;
    }

    public override string ToString()
        => Rid is null ? Kind ?? string.Empty : $"{Kind}/{Rid}";

    internal bool IsValid => TryValidate(Kind, Rid);

    private static bool TryValidate(string? kind, string? rid)
        => kind switch
        {
            SunderPackageProjectionFormat.SharedKind => rid is null,
            SunderPackageProjectionFormat.AppKind or SunderPackageProjectionFormat.RuntimeKind
                => SunderPackageFormat.IsRuntimeIdentifier(rid),
            _ => false,
        };
}
