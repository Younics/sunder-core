namespace Sunder.Runtime.Host.Services;

internal sealed class ExecutionRuntimeGarbageCollectionAuthoritySnapshot
{
    public ExecutionRuntimeGarbageCollectionAuthoritySnapshot(
        IEnumerable<InstalledPackageExecutionRuntimeBindingRecord> packageBindings,
        IReadOnlyList<ExecutionRuntimeRetention>? retentions = null)
    {
        ArgumentNullException.ThrowIfNull(packageBindings);
        PackageBindings = Array.AsReadOnly(packageBindings.ToArray());
        Retentions = Array.AsReadOnly((retentions ?? []).ToArray());
    }

    public IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord> PackageBindings { get; }
    public IReadOnlyList<ExecutionRuntimeRetention> Retentions { get; }
}

internal interface IExecutionRuntimeGarbageCollectionAuthority
{
    Task<TResult> ExecuteAsync<TResult>(
        Func<ExecutionRuntimeGarbageCollectionAuthoritySnapshot, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken);
}

internal sealed class PackageExecutionRuntimeGarbageCollectionAuthority(
    RuntimeOperationGate operationGate,
    PackageStoreCoordinator packageStore,
    ExecutionRuntimeManagementState? managementState = null) : IExecutionRuntimeGarbageCollectionAuthority
{
    public async Task<TResult> ExecuteAsync<TResult>(
        Func<ExecutionRuntimeGarbageCollectionAuthoritySnapshot, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using var operationLease = await operationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        var packageSnapshot = await packageStore.CaptureExecutionRuntimeGarbageCollectionAuthorityAsync(
                operationLease.CancellationToken)
            .ConfigureAwait(false);
        return await operation(
                new ExecutionRuntimeGarbageCollectionAuthoritySnapshot(
                    packageSnapshot.PackageBindings,
                    managementState is null ? [] : ExecutionRuntimeManagementState.Retentions(await managementState.ReadAsync(operationLease.CancellationToken))),
                operationLease.CancellationToken)
            .ConfigureAwait(false);
    }
}

internal sealed record ExecutionRuntimeRetention(
    ExecutionRuntimeIdentity? Identity = null,
    string? ContentIdentity = null,
    string? ArchiveSha256 = null,
    long? ArchiveSize = null);

internal interface IExecutionRuntimeRetentionManager
{
    ValueTask<IAsyncDisposable> RetainAsync(
        ExecutionRuntimeRetention retention,
        CancellationToken cancellationToken = default);
}

internal sealed class ExecutionRuntimeRetentionRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, ExecutionRuntimeRetention> _retentions = [];

    internal IAsyncDisposable RetainUnderFence(
        ExecutionRuntimeRetention retention,
        RuntimeFileLock crossProcessPin)
    {
        ArgumentNullException.ThrowIfNull(crossProcessPin);
        var retentionId = Guid.NewGuid();
        lock (_sync)
        {
            _retentions.Add(retentionId, retention);
        }
        return new RetentionLease(this, retentionId, crossProcessPin);
    }

    internal IReadOnlyList<ExecutionRuntimeRetention> SnapshotUnderFence()
    {
        lock (_sync)
        {
            return Array.AsReadOnly(_retentions.Values.ToArray());
        }
    }

    private void Release(Guid retentionId)
    {
        lock (_sync)
        {
            _retentions.Remove(retentionId);
        }
    }

    private sealed class RetentionLease(
        ExecutionRuntimeRetentionRegistry owner,
        Guid retentionId,
        RuntimeFileLock crossProcessPin) : IAsyncDisposable
    {
        private ExecutionRuntimeRetentionRegistry? _owner = owner;
        private RuntimeFileLock? _crossProcessPin = crossProcessPin;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(retentionId);
            Interlocked.Exchange(ref _crossProcessPin, null)?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
