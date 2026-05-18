namespace Sunder.Runtime.Host.Services;

internal enum PackageSessionOverlayOwner
{
    Startup = 0,
    HotReload = 1,
    Sdk = 2,
}

internal sealed record PackageSessionDevOverlay(
    string PackageId,
    string Folder,
    bool Watch,
    PackageSessionOverlayOwner Owner);

internal sealed class PackageSessionSourceState
{
    private readonly object _gate = new();
    private Dictionary<string, Dictionary<PackageSessionOverlayOwner, PackageSessionDevOverlay>> _devOverlays = CreateOverlayMap();

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

    internal static Dictionary<string, Dictionary<PackageSessionOverlayOwner, PackageSessionDevOverlay>> CreateOverlayMap()
        => new(StringComparer.OrdinalIgnoreCase);

    internal static Dictionary<string, Dictionary<PackageSessionOverlayOwner, PackageSessionDevOverlay>> CloneOverlayMap(
        Dictionary<string, Dictionary<PackageSessionOverlayOwner, PackageSessionDevOverlay>> source)
    {
        var clone = CreateOverlayMap();
        foreach (var (packageId, overlays) in source)
        {
            clone[packageId] = new Dictionary<PackageSessionOverlayOwner, PackageSessionDevOverlay>(overlays);
        }

        return clone;
    }
}

internal sealed class PackageSessionSourceSnapshot(
    Dictionary<string, Dictionary<PackageSessionOverlayOwner, PackageSessionDevOverlay>> devOverlays)
{
    public IReadOnlyList<PackageSessionDevOverlay> ActiveDevOverlays => devOverlays.Values
        .Select(GetActiveDevOverlay)
        .Where(static overlay => overlay is not null)
        .Select(static overlay => overlay!)
        .ToArray();

    public void RemoveDevOverlaysOwnedBy(params PackageSessionOverlayOwner[] owners)
    {
        foreach (var packageId in devOverlays.Keys.ToArray())
        {
            var overlays = devOverlays[packageId];
            foreach (var owner in owners)
            {
                overlays.Remove(owner);
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
            overlays = new Dictionary<PackageSessionOverlayOwner, PackageSessionDevOverlay>();
            devOverlays[overlay.PackageId] = overlays;
        }

        overlays[overlay.Owner] = overlay;
    }

    public bool RemoveDevOverlay(string packageId, PackageSessionOverlayOwner owner)
    {
        if (!devOverlays.TryGetValue(packageId, out var overlays) || !overlays.Remove(owner))
        {
            return false;
        }

        if (overlays.Count == 0)
        {
            devOverlays.Remove(packageId);
        }

        return true;
    }

    internal Dictionary<string, Dictionary<PackageSessionOverlayOwner, PackageSessionDevOverlay>> CloneOverlayMap()
        => PackageSessionSourceState.CloneOverlayMap(devOverlays);

    internal static PackageSessionDevOverlay? GetActiveDevOverlay(
        IReadOnlyDictionary<PackageSessionOverlayOwner, PackageSessionDevOverlay> overlays)
    {
        if (overlays.TryGetValue(PackageSessionOverlayOwner.Sdk, out var sdkOverlay))
        {
            return sdkOverlay;
        }

        if (overlays.TryGetValue(PackageSessionOverlayOwner.HotReload, out var hotReloadOverlay))
        {
            return hotReloadOverlay;
        }

        return overlays.TryGetValue(PackageSessionOverlayOwner.Startup, out var startupOverlay)
            ? startupOverlay
            : null;
    }
}
