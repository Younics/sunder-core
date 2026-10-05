namespace Sunder.Package.Projections;

public enum PackageProjectionRole
{
    App,
    Runtime,
}

public enum PackageProjectionCacheOutcome
{
    Hit,
    Miss,
    CorruptRebuild,
}

public enum PackageProjectionProcessProbe
{
    Matching,
    Missing,
    BirthMismatch,
    Unavailable,
}

public sealed record PackageProjectionProcessIdentity(
    int ProcessId,
    string BirthIdentity,
    DateTimeOffset StartedAtUtc);

public interface IPackageProjectionProcessIdentityProvider
{
    PackageProjectionProcessIdentity ObserveCurrent();

    PackageProjectionProcessProbe Probe(int processId, string birthIdentity);
}

public sealed record PackageProjectionSourceFile(
    string SourceRelativePath,
    string ProjectionRelativePath,
    string Sha256,
    long Size,
    bool IsExecutable = false);

public sealed record PackageProjectionRequest(
    string SourceRootPath,
    string PackageId,
    string SourceContentIdentity,
    PackageProjectionRole ConsumerRole,
    string ProjectionRulesIdentity,
    IReadOnlyList<string> TargetKeys,
    IReadOnlyList<PackageProjectionSourceFile> Files,
    IReadOnlyList<string>? WarmValidationPaths = null);

public sealed record PackageProjectionOwner(
    string OwnerKind,
    Guid HostInstanceId,
    string PackageId,
    string GenerationId);

public sealed record PackageProjectionReference(
    int FormatRevision,
    string CacheKey,
    PackageProjectionRole ConsumerRole,
    string SourceContentIdentity,
    string ProjectionRulesIdentity,
    IReadOnlyList<string> TargetKeys);

public sealed record PackageProjectionInventoryEntry(
    string Path,
    string Sha256,
    long Size,
    bool IsExecutable);

public sealed record PackageProjectionReceipt(
    int SchemaVersion,
    int FormatRevision,
    string CacheKey,
    PackageProjectionRole ConsumerRole,
    string PackageId,
    string SourceContentIdentity,
    string ProjectionRulesIdentity,
    IReadOnlyList<string> TargetKeys,
    IReadOnlyList<PackageProjectionInventoryEntry> Inventory,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset VerifiedAtUtc);

public sealed record PackageProjectionCacheMetrics(
    long BytesRead,
    long BytesHashed,
    long BytesCopied,
    int FilesHashed,
    int FilesCopied)
{
    public static PackageProjectionCacheMetrics Empty { get; } = new(0, 0, 0, 0, 0);
}

public sealed record PackageProjectionGarbageCollectionResult(
    int ActiveLeaseCount,
    int RecoveredLeaseCount,
    int MarkedEntryCount,
    int TombstonedEntryCount,
    int PreservedEntryCount);

public sealed record PackageProjectionLeaseRecoveryResult(
    int ActiveLeaseCount,
    int RecoveredLeaseCount);

public sealed record PackageProjectionCacheOptions
{
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan GarbageCollectionGracePeriod { get; init; } = TimeSpan.FromDays(7);

    public TimeSpan StagingRetention { get; init; } = TimeSpan.FromDays(1);

    public TimeSpan QuarantineRetention { get; init; } = TimeSpan.FromDays(30);
}

public sealed class PackageProjectionOfflineResetScope : IDisposable, IAsyncDisposable
{
    private readonly PackageProjectionCache _cache;
    private PackageProjectionFamilyLock? _familyLock;
    private PackageProjectionOfflineResetInspection? _inspection;
    private PackageProjectionLeaseRecoveryResult? _preparationResult;

    internal PackageProjectionOfflineResetScope(
        PackageProjectionCache cache,
        PackageProjectionFamilyLock familyLock)
    {
        _cache = cache;
        _familyLock = familyLock;
    }

    public async Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_inspection is not null)
        {
            return;
        }

        var inspection = await _cache.InspectOfflineResetLeasesAsync(cancellationToken).ConfigureAwait(false);
        if (inspection.ActiveLeaseCount != 0)
        {
            throw new InvalidOperationException(
                $"The package projection cache has {inspection.ActiveLeaseCount} live App or Runtime lease(s). Stop those processes before resetting startup caches.");
        }
        _inspection = inspection;
    }

    public async Task<PackageProjectionLeaseRecoveryResult> PrepareForDeletionAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_inspection is null)
        {
            throw new InvalidOperationException("The package projection cache reset scope must be validated before deletion preparation.");
        }
        if (_preparationResult is not null)
        {
            return _preparationResult;
        }

        _preparationResult = await _cache.PrepareOfflineResetForDeletionAsync(_inspection, cancellationToken)
            .ConfigureAwait(false);
        return _preparationResult;
    }

    public void Dispose()
        => Interlocked.Exchange(ref _familyLock, null)?.Dispose();

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_familyLock is null, this);
}

public sealed class PackageProjectionAcquisition : IDisposable, IAsyncDisposable
{
    private PackageProjectionLease? _lease;

    internal PackageProjectionAcquisition(
        string rootPath,
        PackageProjectionReference reference,
        PackageProjectionReceipt receipt,
        PackageProjectionCacheOutcome outcome,
        PackageProjectionCacheMetrics metrics,
        PackageProjectionLease lease)
    {
        RootPath = rootPath;
        Reference = reference;
        Receipt = receipt;
        Outcome = outcome;
        Metrics = metrics;
        _lease = lease;
    }

    public string RootPath { get; }

    public PackageProjectionReference Reference { get; }

    public PackageProjectionReceipt Receipt { get; }

    public PackageProjectionCacheOutcome Outcome { get; }

    public PackageProjectionCacheMetrics Metrics { get; }

    public Task AttachToProcessTreeAsync(
        IReadOnlyList<PackageProjectionProcessIdentity> processes,
        CancellationToken cancellationToken = default)
        => (Volatile.Read(ref _lease)
            ?? throw new ObjectDisposedException(nameof(PackageProjectionAcquisition)))
            .AttachToProcessTreeAsync(processes, processTreeLeaseId: null, cancellationToken);

    public Task AttachToProcessTreeAsync(
        string processTreeLeaseId,
        IReadOnlyList<PackageProjectionProcessIdentity> processes,
        CancellationToken cancellationToken = default)
        => (Volatile.Read(ref _lease)
            ?? throw new ObjectDisposedException(nameof(PackageProjectionAcquisition)))
            .AttachToProcessTreeAsync(processes, processTreeLeaseId, cancellationToken);

    public Task BindToProcessTreeLeaseAsync(
        string processTreeLeaseId,
        CancellationToken cancellationToken = default)
        => (Volatile.Read(ref _lease)
            ?? throw new ObjectDisposedException(nameof(PackageProjectionAcquisition)))
            .BindToProcessTreeLeaseAsync(processTreeLeaseId, cancellationToken);

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        var lease = Volatile.Read(ref _lease);
        if (lease is not null)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            Interlocked.CompareExchange(ref _lease, null, lease);
        }
    }
}

internal sealed record PackageProjectionOfflineResetInspection(
    int ActiveLeaseCount,
    IReadOnlyList<string> StaleLeasePaths);

internal sealed class PackageProjectionLease(
    PackageProjectionCache cache,
    PackageProjectionRole role,
    string cacheKey,
    string leaseId) : IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _released;

    public Task AttachToProcessTreeAsync(
        IReadOnlyList<PackageProjectionProcessIdentity> processes,
        string? processTreeLeaseId,
        CancellationToken cancellationToken)
        => AttachCoreAsync(processes, processTreeLeaseId, cancellationToken);

    public Task BindToProcessTreeLeaseAsync(
        string processTreeLeaseId,
        CancellationToken cancellationToken)
        => BindCoreAsync(processTreeLeaseId, cancellationToken);

    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_released)
            {
                return;
            }
            await cache.ReleaseLeaseAsync(role, cacheKey, leaseId, CancellationToken.None)
                .ConfigureAwait(false);
            _released = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task AttachCoreAsync(
        IReadOnlyList<PackageProjectionProcessIdentity> processes,
        string? processTreeLeaseId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_released, this);
            await cache.AttachLeaseToProcessTreeAsync(
                    role,
                    cacheKey,
                    leaseId,
                    processes,
                    processTreeLeaseId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task BindCoreAsync(
        string processTreeLeaseId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_released, this);
            await cache.BindLeaseToProcessTreeAsync(
                    role,
                    cacheKey,
                    leaseId,
                    processTreeLeaseId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
