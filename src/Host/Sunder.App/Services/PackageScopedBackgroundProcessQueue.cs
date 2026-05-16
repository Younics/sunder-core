using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class PackageScopedBackgroundProcessQueue(
    string packageId,
    string packageDisplayName,
    BackgroundProcessQueueService backgroundProcesses)
    : IBackgroundProcessQueue, IDisposable
{
    private readonly string _packageId = packageId;
    private readonly string _packageDisplayName = packageDisplayName;
    private readonly BackgroundProcessQueueService _backgroundProcesses = backgroundProcesses;
    private bool _disposed;

    public event EventHandler<BackgroundProcessChangedEventArgs>? ProcessChanged;

    public BackgroundProcessSnapshot Enqueue(BackgroundProcessRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new ArgumentException("Background process title is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.GroupKey))
        {
            throw new ArgumentException("Background process group key is required.", nameof(request));
        }

        var metadata = PackageScopedBackgroundProcessMetadata.Create(
            _packageId,
            _packageDisplayName,
            request.GroupKey,
            request.Metadata);

        var snapshot = _backgroundProcesses.Enqueue(new BackgroundProcessRequest(
            request.Title,
            BuildHostGroupKey(_packageId, request.GroupKey),
            request.Indicator,
            request.ConcurrencyMode,
            request.CanCancel,
            async context => await request.ExecuteAsync(context).ConfigureAwait(false),
            metadata.ToHostMetadata()));

        return ToPackageSnapshot(snapshot, metadata);
    }

    public IReadOnlyList<BackgroundProcessSnapshot> ListProcesses(string? groupKey = null)
    {
        var snapshots = new List<BackgroundProcessSnapshot>();
        foreach (var snapshot in _backgroundProcesses.ListProcesses())
        {
            if (TryMap(snapshot, out var packageSnapshot)
                && (string.IsNullOrWhiteSpace(groupKey)
                    || string.Equals(packageSnapshot.GroupKey, groupKey, StringComparison.OrdinalIgnoreCase)))
            {
                snapshots.Add(packageSnapshot);
            }
        }

        return snapshots;
    }

    public bool Cancel(Guid processId)
    {
        var snapshot = _backgroundProcesses.GetProcess(processId);
        return snapshot is not null && TryMap(snapshot, out _) && _backgroundProcesses.Cancel(processId);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _backgroundProcesses.ProcessChanged -= BackgroundProcesses_OnProcessChanged;
        foreach (var process in ListProcesses().Where(process => process.IsActive))
        {
            _backgroundProcesses.Cancel(process.ProcessId);
        }
    }

    internal void Start() => _backgroundProcesses.ProcessChanged += BackgroundProcesses_OnProcessChanged;

    private void BackgroundProcesses_OnProcessChanged(object? sender, BackgroundProcessChangedEventArgs e)
    {
        if (!_disposed && TryMap(e.Snapshot, out var packageSnapshot))
        {
            ProcessChanged?.Invoke(this, new BackgroundProcessChangedEventArgs(packageSnapshot));
        }
    }

    private bool TryMap(BackgroundProcessSnapshot snapshot, out BackgroundProcessSnapshot packageSnapshot)
    {
        if (PackageScopedBackgroundProcessMetadata.TryCreate(snapshot.Metadata, out var metadata)
            && string.Equals(metadata.PackageId, _packageId, StringComparison.OrdinalIgnoreCase))
        {
            packageSnapshot = ToPackageSnapshot(snapshot, metadata);
            return true;
        }

        packageSnapshot = null!;
        return false;
    }

    private static BackgroundProcessSnapshot ToPackageSnapshot(
        BackgroundProcessSnapshot snapshot,
        PackageScopedBackgroundProcessMetadata metadata)
        => new(
            snapshot.ProcessId,
            snapshot.Title,
            metadata.PackageGroupKey,
            snapshot.Indicator,
            snapshot.ConcurrencyMode,
            snapshot.State,
            snapshot.StatusText,
            snapshot.ProgressPercent,
            snapshot.CanCancel,
            metadata.PackageMetadata,
            snapshot.ErrorMessage,
            snapshot.QueuedAtUtc,
            snapshot.StartedAtUtc,
            snapshot.CompletedAtUtc);

    private static string BuildHostGroupKey(string packageId, string groupKey)
        => $"package:{packageId}:{groupKey}";
}

internal sealed record PackageScopedBackgroundProcessMetadata(
    string PackageId,
    string PackageDisplayName,
    string PackageGroupKey,
    IReadOnlyDictionary<string, string> PackageMetadata)
{
    private const string PackageIdMetadataKey = "sunder.package.id";
    private const string PackageDisplayNameMetadataKey = "sunder.package.displayName";
    private const string PackageGroupKeyMetadataKey = "sunder.package.groupKey";
    private const string PackageMetadataKeyPrefix = "sunder.package.metadata.";

    public static PackageScopedBackgroundProcessMetadata Create(
        string packageId,
        string packageDisplayName,
        string packageGroupKey,
        IReadOnlyDictionary<string, string>? packageMetadata)
        => new(
            packageId,
            packageDisplayName,
            packageGroupKey,
            packageMetadata is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(packageMetadata, StringComparer.OrdinalIgnoreCase));

    public static bool TryCreate(IReadOnlyDictionary<string, string> hostMetadata, out PackageScopedBackgroundProcessMetadata metadata)
    {
        if (hostMetadata.TryGetValue(PackageIdMetadataKey, out var packageId)
            && hostMetadata.TryGetValue(PackageDisplayNameMetadataKey, out var packageDisplayName)
            && hostMetadata.TryGetValue(PackageGroupKeyMetadataKey, out var packageGroupKey))
        {
            metadata = new PackageScopedBackgroundProcessMetadata(
                packageId,
                packageDisplayName,
                packageGroupKey,
                hostMetadata
                    .Where(pair => pair.Key.StartsWith(PackageMetadataKeyPrefix, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(
                        pair => pair.Key[PackageMetadataKeyPrefix.Length..],
                        pair => pair.Value,
                        StringComparer.OrdinalIgnoreCase));
            return true;
        }

        metadata = null!;
        return false;
    }

    public IReadOnlyDictionary<string, string> ToHostMetadata()
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [PackageIdMetadataKey] = PackageId,
            [PackageDisplayNameMetadataKey] = PackageDisplayName,
            [PackageGroupKeyMetadataKey] = PackageGroupKey,
        };

        foreach (var pair in PackageMetadata)
        {
            metadata[$"{PackageMetadataKeyPrefix}{pair.Key}"] = pair.Value;
        }

        return metadata;
    }
}
