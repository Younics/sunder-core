using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Packaging;

namespace Sunder.Runtime.Host.Services;

internal sealed class LocalExecutionRuntimeBindingSnapshot(
    LocalExecutionRuntimeBindingResolver owner,
    IReadOnlyList<ExecutionRuntimeReceiptV1> receipts,
    DateTimeOffset capturedAtUtc) : IAsyncDisposable
{
    private readonly object _sync = new();
    private List<IAsyncDisposable>? _retentions = [];

    internal IReadOnlyList<ExecutionRuntimeReceiptV1> Receipts => receipts;
    internal DateTimeOffset CapturedAtUtc => capturedAtUtc;

    public Task<IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord>> ResolveInstalledAsync(
        SunderPackageManifest manifest,
        CancellationToken cancellationToken,
        IReadOnlyList<SunderPackageTargetKey>? acquiredTargets = null)
        => owner.ResolveInstalledAsync(this, manifest, receipts, capturedAtUtc, cancellationToken, acquiredTargets);

    internal void Adopt(ResolvedEphemeralExecutionRuntimeBinding binding)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_retentions is null, this);
            _retentions.Add(binding);
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<IAsyncDisposable>? retained;
        lock (_sync)
        {
            retained = _retentions;
            _retentions = null;
        }
        if (retained is null)
        {
            return;
        }
        for (var index = retained.Count - 1; index >= 0; index--)
        {
            await retained[index].DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal sealed class LocalExecutionRuntimeBindingResolver(
    ExecutionRuntimeStore store,
    TimeProvider timeProvider) : IEphemeralExecutionRuntimeBindingResolver
{
    internal async Task<ExecutionRuntimeReceiptV1?> FindHealthyAsync(
        RuntimeExecutionRuntimeRequirement requirement,
        string rid,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentException.ThrowIfNullOrWhiteSpace(rid);
        var receipts = await store.ListReceiptsAsync(cancellationToken).ConfigureAwait(false);
        return await FindHealthyAsync(requirement, rid, receipts, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ResolvedEphemeralExecutionRuntimeBinding> ResolveInstallationRequirementAsync(
        SunderPackageTargetKey target,
        SunderPackageExecutionRuntimeManifest requirement,
        CancellationToken token)
    {
        await using var snapshot = await CaptureAsync(token).ConfigureAwait(false);
        return await ResolveTargetAsync(target, requirement, snapshot.Receipts, snapshot.CapturedAtUtc, token)
            .ConfigureAwait(false);
    }

    public async Task<LocalExecutionRuntimeBindingSnapshot> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        var receipts = await store.ListReceiptsAsync(cancellationToken).ConfigureAwait(false);
        return new LocalExecutionRuntimeBindingSnapshot(
            this,
            receipts,
            timeProvider.GetUtcNow().ToUniversalTime());
    }

    public async Task<ResolvedEphemeralExecutionRuntimeBinding> ResolveEphemeralBindingAsync(
        SunderPackageTargetKey targetKey,
        SunderPackageTargetManifest target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        await using var snapshot = await CaptureAsync(cancellationToken).ConfigureAwait(false);
        return await ResolveTargetAsync(
                targetKey,
                target.ExecutionRuntime
                ?? throw new InvalidDataException(
                    $"Development target '{targetKey}' does not declare an execution-runtime requirement."),
                snapshot.Receipts,
                snapshot.CapturedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord>> ResolveInstalledAsync(
        LocalExecutionRuntimeBindingSnapshot snapshot,
        SunderPackageManifest manifest,
        IReadOnlyList<ExecutionRuntimeReceiptV1> receipts,
        DateTimeOffset resolvedAtUtc,
        CancellationToken cancellationToken,
        IReadOnlyList<SunderPackageTargetKey>? acquiredTargets = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(manifest);
        var currentRid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        var targets = (manifest.Targets ?? [])
            .Where(target => target?.ExecutionRuntime is not null
                             && string.Equals(target.Rid, currentRid, StringComparison.Ordinal)
                             && InstalledPackageProjection.Includes(acquiredTargets, target.Role, target.Rid))
            .Select(static target => target!)
            .OrderBy(static target => target.Role, StringComparer.Ordinal)
            .ThenBy(static target => target.Rid, StringComparer.Ordinal)
            .ToArray();
        var bindings = new List<InstalledPackageExecutionRuntimeBindingRecord>(targets.Length);
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetKey = new SunderPackageTargetKey(target.Role!, target.Rid!);
            var resolved = await ResolveTargetAsync(
                    targetKey,
                    target.ExecutionRuntime!,
                    receipts,
                    resolvedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                snapshot.Adopt(resolved);
            }
            catch
            {
                await resolved.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            var binding = resolved.Binding;
            bindings.Add(new InstalledPackageExecutionRuntimeBindingRecord(
                binding.Role,
                binding.Rid,
                binding.Requirement,
                binding.Runtime,
                binding.InstalledArtifact,
                binding.ResolvedAtUtc));
        }
        return Array.AsReadOnly(bindings.ToArray());
    }

    private async Task<ExecutionRuntimeReceiptV1?> FindHealthyAsync(
        RuntimeExecutionRuntimeRequirement requirement,
        string rid,
        IReadOnlyList<ExecutionRuntimeReceiptV1> receipts,
        CancellationToken cancellationToken)
    {
        var candidates = receipts
            .Where(receipt => string.Equals(receipt.Identity.RuntimeId, requirement.RuntimeId, StringComparison.Ordinal)
                              && string.Equals(receipt.Identity.Rid, rid, StringComparison.Ordinal)
                              && string.Equals(receipt.Identity.ModuleAbi, requirement.ModuleAbi, StringComparison.Ordinal)
                              && PackageVersionRange.IsSatisfiedBy(receipt.Identity.VendorVersion, requirement.VersionRange))
            .OrderByDescending(static receipt => SemanticVersion.Parse(receipt.Identity.VendorVersion))
            .ThenBy(static receipt => receipt.Identity.VendorChecksumDigest, StringComparer.Ordinal)
            .ToArray();

        foreach (var receipt in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var verification = await store.VerifyAsync(receipt.Identity, deep: false, cancellationToken).ConfigureAwait(false);
            if (verification.Healthy)
            {
                return receipt;
            }
        }

        return null;
    }

    private async Task<ResolvedEphemeralExecutionRuntimeBinding> ResolveTargetAsync(
        SunderPackageTargetKey targetKey,
        SunderPackageExecutionRuntimeManifest requirement,
        IReadOnlyList<ExecutionRuntimeReceiptV1> receipts,
        DateTimeOffset resolvedAtUtc,
        CancellationToken cancellationToken)
    {
        var contractRequirement = new RuntimeExecutionRuntimeRequirement(
            requirement.Id!,
            requirement.VersionRange!,
            requirement.ModuleAbi!);
        var receipt = await FindHealthyAsync(contractRequirement, targetKey.Rid, receipts, cancellationToken)
            .ConfigureAwait(false);
        if (receipt is null)
        {
            var requirementText = $"{requirement.Id} {requirement.VersionRange} {requirement.ModuleAbi}";
            var ensureCommand = $"sunder runtime ensure {requirement.Id} '{requirement.VersionRange}' {requirement.ModuleAbi}";
            throw new InvalidDataException(
                $"{ExecutionRuntimeErrorCodes.Missing}: no healthy installed official execution runtime satisfies target '{targetKey}' requirement '{requirementText}'. Run \"{ensureCommand}\", then retry.");
        }

        var retention = await store.RetainAsync(
                new ExecutionRuntimeRetention(Identity: receipt.Identity),
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return new ResolvedEphemeralExecutionRuntimeBinding(
                new RuntimePackageExecutionRuntimeBinding(
                    targetKey.Role,
                    targetKey.Rid,
                    contractRequirement,
                    receipt.Identity.ToContract(),
                    receipt.InstalledArtifact,
                    resolvedAtUtc),
                retention);
        }
        catch
        {
            await retention.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
