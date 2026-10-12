namespace Sunder.Runtime.Host.Services;

internal sealed record ExecutionRuntimeStoreUsage(IReadOnlyList<ExecutionRuntimeProcessLeaseV1> Leases,
    IReadOnlyList<ExecutionRuntimeRetention> Retentions, int PendingTransactions);

internal sealed partial class ExecutionRuntimeStore
{
    internal async Task<ExecutionRuntimeStoreUsage> CaptureManagementUsageAsync(IReadOnlyList<ExecutionRuntimeIdentity> identities, CancellationToken token)
    {
        using var recovery = await EnterInitializedRecoveryReadAsync(token);
        using var fence = await _leases.EnterFenceAsync(token);
        var receipts = new List<ExecutionRuntimeReceiptV1>();
        foreach (var identity in identities)
        {
            var path = _layout.GetReceiptPath(identity);
            if (File.Exists(path)) receipts.Add(await ReadReceiptAsync(path, token));
        }
        var content = receipts.Select(value => value.ContentIdentity).ToHashSet(StringComparer.Ordinal);
        var leases = (await _leases.ReadLeasesUnderFenceAsync(token)).Where(value => content.Contains(value.ContentIdentity)).ToArray();
        var retentions = _retentions.SnapshotUnderFence().Where(value => identities.Contains(value.Identity)
            || value.ContentIdentity is not null && content.Contains(value.ContentIdentity)).ToArray();
        var pending = (await ReadPendingTransactionReceiptsAsync(token)).Count(value => content.Contains(value.ContentIdentity));
        return new(leases, retentions, pending);
    }

    // Caller holds RuntimeOperationGate and has captured package/pending/manual authority there.
    internal async Task<bool> RemoveUnreferencedAsync(ExecutionRuntimeIdentity identity,
        ExecutionRuntimeGarbageCollectionAuthoritySnapshot authority, Func<Task> removeIntent, CancellationToken token)
    {
        await InitializeAsync(token);
        using var recovery = await RuntimeFileLock.AcquireExclusiveAsync(_layout.RecoveryFencePath, token);
        using var fence = await _leases.EnterFenceAsync(token);
        using var pin = RuntimeFileLock.TryAcquireExclusive(_layout.GarbageCollectionRetentionFencePath);
        if (pin is null) throw new RuntimeConflictException("Runtime acquisition or activation retains this store; retry uninstall later.");
        var path = _layout.GetReceiptPath(identity);
        var receipt = File.Exists(path) ? await ReadReceiptAsync(path, token) : null;
        if (authority.PackageBindings.Any(value => RuntimePackageExecutionRuntimeBinding.FromInstalled(value).Identity == identity)
            || authority.Retentions.Any(value => value.Identity == identity)
            || _retentions.SnapshotUnderFence().Any(value => value.Identity == identity || receipt is not null && value.ContentIdentity == receipt.ContentIdentity)
            || receipt is not null && (_leases.HasActiveLeaseUnderFence(receipt.ContentIdentity)
                || (await ReadPendingTransactionReceiptsAsync(token)).Any(value => value.ContentIdentity == receipt.ContentIdentity)))
            throw new RuntimeConflictException("Runtime is referenced, leased or owned by a pending operation.");
        await removeIntent();
        return receipt is null || await TombstoneContentUnderFenceAsync(receipt.ContentIdentity, [(path, receipt)], token);
    }
}
