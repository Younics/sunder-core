namespace Sunder.Runtime.Host.Services;

internal enum PackageSessionOverlayOwner
{
    Startup = 0,
    HotReload = 1,
    Sdk = 2,
    AppInvocation = 3,
}

internal sealed record PackageSessionDevOverlay(
    string PackageId,
    string Folder,
    bool Watch,
    PackageSessionOverlayOwner Owner,
    string? OwnerId = null)
{
    public string OwnerKey => Owner == PackageSessionOverlayOwner.AppInvocation
        ? $"app:{OwnerId ?? throw new InvalidOperationException("An App invocation overlay requires an owner id.")}"
        : $"runtime:{Owner}";
}

internal sealed class PackageSessionSourceState
{
    private readonly object _gate = new();
    private Dictionary<string, Dictionary<string, PackageSessionDevOverlay>> _devOverlays = CreateOverlayMap();

    public PackageSessionSourceSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new PackageSessionSourceSnapshot(CloneOverlayMap(_devOverlays));
        }
    }

    public void Replace(PackageSessionSourceSnapshot snapshot)
    {
        lock (_gate)
        {
            _devOverlays = snapshot.CloneOverlayMap();
        }
    }

    public PackageSessionDevOverlay? TryGetActiveDevOverlay(string packageId)
    {
        lock (_gate)
        {
            return _devOverlays.TryGetValue(packageId, out var overlays)
                ? PackageSessionSourceSnapshot.GetActiveDevOverlay(overlays)
                : null;
        }
    }

    internal static Dictionary<string, Dictionary<string, PackageSessionDevOverlay>> CreateOverlayMap()
        => new(StringComparer.OrdinalIgnoreCase);

    internal static Dictionary<string, Dictionary<string, PackageSessionDevOverlay>> CloneOverlayMap(
        Dictionary<string, Dictionary<string, PackageSessionDevOverlay>> source)
    {
        var clone = CreateOverlayMap();
        foreach (var (packageId, overlays) in source)
        {
            clone[packageId] = new Dictionary<string, PackageSessionDevOverlay>(overlays, StringComparer.Ordinal);
        }

        return clone;
    }
}

internal sealed class PackageSessionSourceSnapshot(
    Dictionary<string, Dictionary<string, PackageSessionDevOverlay>> devOverlays)
{
    public IReadOnlyList<PackageSessionDevOverlay> ActiveDevOverlays => devOverlays.Values
        .Select(GetActiveDevOverlay)
        .Where(static overlay => overlay is not null)
        .Select(static overlay => overlay!)
        .ToArray();

    public IReadOnlyList<PackageSessionDevOverlay> DevOverlays => devOverlays.Values
        .SelectMany(static overlays => overlays.Values)
        .ToArray();

    public void RemoveDevOverlaysOwnedBy(params PackageSessionOverlayOwner[] owners)
    {
        foreach (var packageId in devOverlays.Keys.ToArray())
        {
            var overlays = devOverlays[packageId];
            foreach (var ownerKey in overlays
                         .Where(pair => owners.Contains(pair.Value.Owner))
                         .Select(static pair => pair.Key)
                         .ToArray())
            {
                overlays.Remove(ownerKey);
            }

            if (overlays.Count == 0)
            {
                devOverlays.Remove(packageId);
            }
        }
    }

    public void SetDevOverlay(PackageSessionDevOverlay overlay)
    {
        if (!devOverlays.TryGetValue(overlay.PackageId, out var overlays))
        {
            overlays = new Dictionary<string, PackageSessionDevOverlay>(StringComparer.Ordinal);
            devOverlays[overlay.PackageId] = overlays;
        }

        overlays[overlay.OwnerKey] = overlay;
    }

    public bool RemoveDevOverlay(string packageId, PackageSessionOverlayOwner owner)
    {
        var ownerKey = $"runtime:{owner}";
        if (!devOverlays.TryGetValue(packageId, out var overlays) || !overlays.Remove(ownerKey))
        {
            return false;
        }

        if (overlays.Count == 0)
        {
            devOverlays.Remove(packageId);
        }

        return true;
    }

    public void ReplaceDevOverlaysForAppOwner(
        string ownerId,
        IReadOnlyList<PackageSessionDevOverlay> replacements)
    {
        RemoveDevOverlaysForAppOwner(ownerId);
        foreach (var replacement in replacements)
        {
            SetDevOverlay(replacement);
        }
    }

    public bool RemoveDevOverlaysForAppOwner(string ownerId)
    {
        var removed = false;
        var ownerKey = $"app:{ownerId}";
        foreach (var packageId in devOverlays.Keys.ToArray())
        {
            var overlays = devOverlays[packageId];
            removed |= overlays.Remove(ownerKey);
            if (overlays.Count == 0)
            {
                devOverlays.Remove(packageId);
            }
        }

        return removed;
    }

    internal Dictionary<string, Dictionary<string, PackageSessionDevOverlay>> CloneOverlayMap()
        => PackageSessionSourceState.CloneOverlayMap(devOverlays);

    internal static PackageSessionDevOverlay? GetActiveDevOverlay(
        IReadOnlyDictionary<string, PackageSessionDevOverlay> overlays)
    {
        if (overlays.TryGetValue($"runtime:{PackageSessionOverlayOwner.Sdk}", out var sdkOverlay))
        {
            return sdkOverlay with
            {
                Watch = overlays.Values.Any(overlay =>
                    PathsEqual(overlay.Folder, sdkOverlay.Folder) && overlay.Watch),
            };
        }

        var appOverlays = overlays.Values
            .Where(static overlay => overlay.Owner == PackageSessionOverlayOwner.AppInvocation)
            .OrderBy(static overlay => overlay.OwnerId, StringComparer.Ordinal)
            .ToArray();
        if (appOverlays.Length > 0)
        {
            return appOverlays[0] with { Watch = appOverlays.Any(static overlay => overlay.Watch) };
        }

        if (overlays.TryGetValue($"runtime:{PackageSessionOverlayOwner.HotReload}", out var hotReloadOverlay))
        {
            return hotReloadOverlay;
        }

        return overlays.TryGetValue($"runtime:{PackageSessionOverlayOwner.Startup}", out var startupOverlay)
            ? startupOverlay
            : null;
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
