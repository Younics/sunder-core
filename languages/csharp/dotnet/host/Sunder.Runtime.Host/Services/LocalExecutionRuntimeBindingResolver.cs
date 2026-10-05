using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Packaging;

namespace Sunder.Runtime.Host.Services;

internal enum LocalExecutionRuntimeBindingFailureKind
{
    Missing,
    Unsupported,
    PolicyRequired,
    Deprecated,
    Retired,
    Revoked,
    Corrupt,
}

internal sealed class LocalExecutionRuntimeBindingSnapshot(
    LocalExecutionRuntimeBindingResolver owner,
    ExecutionRuntimeLifecyclePolicySnapshot policy,
    IReadOnlyList<ExecutionRuntimeReceiptV1> receipts,
    DateTimeOffset capturedAtUtc) : IAsyncDisposable
{
    private readonly object _sync = new();
    private List<IAsyncDisposable>? _retentions = [];

    public long PolicyGeneration => policy.Generation;
    internal ExecutionRuntimeLifecyclePolicySnapshot Policy => policy;
    internal IReadOnlyList<ExecutionRuntimeReceiptV1> Receipts => receipts;
    internal DateTimeOffset CapturedAtUtc => capturedAtUtc;

    public Task<IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord>> ResolveInstalledAsync(
        SunderPackageManifest manifest,
        CancellationToken cancellationToken,
        IReadOnlyList<SunderPackageTargetKey>? acquiredTargets = null)
        => owner.ResolveInstalledAsync(this, manifest, policy, receipts, capturedAtUtc, cancellationToken, acquiredTargets);

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
    ExecutionRuntimeLifecyclePolicyCache lifecyclePolicies,
    TimeProvider timeProvider) : IEphemeralExecutionRuntimeBindingResolver
{
    public async Task<LocalExecutionRuntimeBindingSnapshot> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        await lifecyclePolicies.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var policy = lifecyclePolicies.CaptureSnapshot();
        var receipts = await store.ListReceiptsAsync(cancellationToken).ConfigureAwait(false);
        return new LocalExecutionRuntimeBindingSnapshot(
            this,
            policy,
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
                snapshot.Policy,
                snapshot.Receipts,
                snapshot.CapturedAtUtc,
                forActivation: true,
                allowDevelopment: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord>> ResolveInstalledAsync(
        LocalExecutionRuntimeBindingSnapshot snapshot,
        SunderPackageManifest manifest,
        ExecutionRuntimeLifecyclePolicySnapshot policy,
        IReadOnlyList<ExecutionRuntimeReceiptV1> receipts,
        DateTimeOffset resolvedAtUtc,
        CancellationToken cancellationToken,
        IReadOnlyList<SunderPackageTargetKey>? acquiredTargets = null)
    {
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
                    policy,
                    receipts,
                    resolvedAtUtc,
                    forActivation: false,
                    allowDevelopment: false,
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
                binding.RuntimeId,
                binding.RuntimeVersion,
                binding.RuntimePackagingRevision,
                binding.ModuleAbi,
                binding.RuntimeManifestSha256,
                binding.RuntimeArtifactSha256,
                binding.RuntimeArtifactSize,
                binding.RegistryOrigin,
                binding.AcquisitionSource,
                binding.SigningKeyId,
                binding.TrustStoreGeneration,
                binding.RuntimePolicyRevision,
                binding.RuntimeLifecycleState,
                binding.PolicyObservedAtUtc,
                binding.ResolvedAtUtc));
        }
        return Array.AsReadOnly(bindings.ToArray());
    }

    private async Task<ResolvedEphemeralExecutionRuntimeBinding> ResolveTargetAsync(
        SunderPackageTargetKey targetKey,
        SunderPackageExecutionRuntimeManifest requirement,
        ExecutionRuntimeLifecyclePolicySnapshot snapshotPolicy,
        IReadOnlyList<ExecutionRuntimeReceiptV1> receipts,
        DateTimeOffset resolvedAtUtc,
        bool forActivation,
        bool allowDevelopment,
        CancellationToken cancellationToken)
    {
        var compatible = receipts
            .Where(receipt => string.Equals(receipt.Identity.RuntimeId, requirement.Id, StringComparison.Ordinal)
                              && string.Equals(receipt.Identity.Rid, targetKey.Rid, StringComparison.Ordinal)
                              && string.Equals(receipt.Identity.ModuleAbi, requirement.ModuleAbi, StringComparison.Ordinal)
                              && PackageVersionRange.IsSatisfiedBy(receipt.Identity.Version, requirement.VersionRange!))
            .ToArray();
        var candidates = new List<Candidate>();
        var matchingPolicies = new List<ExecutionRuntimeLifecyclePolicyEntryV1>();
        foreach (var receipt in compatible)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasPermittedTrustAndSource(receipt, allowDevelopment)
                || !snapshotPolicy.TryGet(receipt.Identity, out var entry)
                || entry is null
                || !entry.MatchesReceipt(receipt))
            {
                continue;
            }
            matchingPolicies.Add(entry);
            if (!(forActivation
                    ? entry.PermitsActivation(resolvedAtUtc)
                    : entry.PermitsInstallation(resolvedAtUtc)))
            {
                continue;
            }
            candidates.Add(new Candidate(
                receipt,
                entry.RegistryOrigin,
                entry.RuntimePolicyRevision,
                entry.Lifecycle.State,
                entry.PolicyObservedAtUtc));
        }

        candidates.Sort(static (left, right) =>
        {
            var versionOrder = SemanticVersion.Parse(right.Receipt.Identity.Version)
                .CompareTo(SemanticVersion.Parse(left.Receipt.Identity.Version));
            return versionOrder != 0
                ? versionOrder
                : right.Receipt.Identity.PackagingRevision.CompareTo(left.Receipt.Identity.PackagingRevision);
        });
        var corruptCandidates = new List<ExecutionRuntimeReceiptV1>();
        foreach (var candidate in candidates)
        {
            var retention = await store.RetainAsync(
                    new ExecutionRuntimeRetention(Identity: candidate.Receipt.Identity),
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var verification = await store.VerifyAsync(
                        candidate.Receipt.Identity,
                        deep: false,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!verification.Healthy)
                {
                    corruptCandidates.Add(candidate.Receipt);
                    await retention.DisposeAsync().ConfigureAwait(false);
                    continue;
                }
                var receipt = candidate.Receipt;
                return new ResolvedEphemeralExecutionRuntimeBinding(
                    new RuntimePackageExecutionRuntimeBinding(
                        targetKey.Role,
                        targetKey.Rid,
                        receipt.Identity.RuntimeId,
                        receipt.Identity.Version,
                        receipt.Identity.PackagingRevision,
                        receipt.Identity.ModuleAbi,
                        receipt.ManifestSha256,
                        receipt.ArtifactSha256,
                        receipt.ArtifactSize,
                        candidate.RegistryOrigin,
                        receipt.AcquisitionSource,
                        receipt.SigningKeyId,
                        receipt.TrustGeneration,
                        candidate.RuntimePolicyRevision,
                        candidate.RuntimeLifecycleState,
                        candidate.PolicyObservedAtUtc,
                        resolvedAtUtc),
                    retention);
            }
            catch
            {
                await retention.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        var kind = ClassifyFailure(
            requirement,
            receipts,
            compatible,
            matchingPolicies,
            candidates,
            resolvedAtUtc,
            forActivation);
        var code = kind switch
        {
            LocalExecutionRuntimeBindingFailureKind.Missing => ExecutionRuntimeErrorCodes.Missing,
            LocalExecutionRuntimeBindingFailureKind.Unsupported => ExecutionRuntimeErrorCodes.Unsupported,
            LocalExecutionRuntimeBindingFailureKind.PolicyRequired => ExecutionRuntimeErrorCodes.PolicyRequired,
            LocalExecutionRuntimeBindingFailureKind.Deprecated => ExecutionRuntimeErrorCodes.Deprecated,
            LocalExecutionRuntimeBindingFailureKind.Retired => ExecutionRuntimeErrorCodes.Retired,
            LocalExecutionRuntimeBindingFailureKind.Revoked => ExecutionRuntimeErrorCodes.Revoked,
            _ => ExecutionRuntimeErrorCodes.IntegrityFailed,
        };
        var requirementText = $"{requirement.Id} {requirement.VersionRange} {requirement.ModuleAbi}";
        var ensureCommand =
            $"sunder runtime ensure {requirement.Id} '{requirement.VersionRange}' {requirement.ModuleAbi}";
        var recovery = kind switch
        {
            LocalExecutionRuntimeBindingFailureKind.Missing =>
                $"Run \"{ensureCommand}\" or 'sunder runtime import --file <artifact.sunderruntime>', then retry.",
            LocalExecutionRuntimeBindingFailureKind.Unsupported =>
                $"Run \"{ensureCommand}\". If Registry reports the line as unsupported, use a package version that requires a supported runtime line.",
            LocalExecutionRuntimeBindingFailureKind.PolicyRequired =>
                $"Run \"{ensureCommand}\" to resolve trusted Registry policy, then retry.",
            LocalExecutionRuntimeBindingFailureKind.Deprecated =>
                $"Run \"{ensureCommand}\" to select a supported release, or update the package before runtime installation is blocked.",
            LocalExecutionRuntimeBindingFailureKind.Retired =>
                $"The required runtime line is retired. Update the package or run \"{ensureCommand}\" to resolve a supported replacement.",
            LocalExecutionRuntimeBindingFailureKind.Revoked =>
                $"The installed runtime is revoked and cannot be used. Update the package or run \"{ensureCommand}\" to resolve a trusted replacement.",
            _ => BuildCorruptRuntimeRecovery(corruptCandidates, ensureCommand),
        };
        throw new InvalidDataException(
            $"{code}: no installed execution runtime with authoritative local policy satisfies target '{targetKey}' requirement '{requirementText}'. {recovery}");
    }

    private static LocalExecutionRuntimeBindingFailureKind ClassifyFailure(
        SunderPackageExecutionRuntimeManifest requirement,
        IReadOnlyList<ExecutionRuntimeReceiptV1> receipts,
        IReadOnlyList<ExecutionRuntimeReceiptV1> compatible,
        IReadOnlyList<ExecutionRuntimeLifecyclePolicyEntryV1> matchingPolicies,
        IReadOnlyList<Candidate> candidates,
        DateTimeOffset resolvedAtUtc,
        bool forActivation)
    {
        if (compatible.Count == 0)
        {
            return receipts.Any(receipt => string.Equals(
                    receipt.Identity.RuntimeId,
                    requirement.Id,
                    StringComparison.Ordinal))
                ? LocalExecutionRuntimeBindingFailureKind.Unsupported
                : LocalExecutionRuntimeBindingFailureKind.Missing;
        }
        if (candidates.Count > 0)
        {
            return LocalExecutionRuntimeBindingFailureKind.Corrupt;
        }

        if (matchingPolicies.Any(static policy =>
                policy.Lifecycle.State == ExecutionRuntimeLifecycleStates.Revoked))
        {
            return LocalExecutionRuntimeBindingFailureKind.Revoked;
        }
        if (matchingPolicies.Any(policy =>
                policy.Lifecycle.State == ExecutionRuntimeLifecycleStates.Retired
                && !(forActivation
                    ? policy.PermitsActivation(resolvedAtUtc)
                    : policy.PermitsInstallation(resolvedAtUtc))))
        {
            return LocalExecutionRuntimeBindingFailureKind.Retired;
        }
        if (matchingPolicies.Any(policy =>
                policy.Lifecycle.State == ExecutionRuntimeLifecycleStates.Deprecated
                && !(forActivation
                    ? policy.PermitsActivation(resolvedAtUtc)
                    : policy.PermitsInstallation(resolvedAtUtc))))
        {
            return LocalExecutionRuntimeBindingFailureKind.Deprecated;
        }
        return LocalExecutionRuntimeBindingFailureKind.PolicyRequired;
    }

    private static string BuildCorruptRuntimeRecovery(
        IReadOnlyList<ExecutionRuntimeReceiptV1> corruptCandidates,
        string ensureCommand)
    {
        var receipt = corruptCandidates.FirstOrDefault();
        if (receipt is null)
        {
            return $"Run \"{ensureCommand}\" to restore a healthy trusted runtime, then retry.";
        }
        var identity = receipt.Identity;
        var repairCommand =
            $"sunder runtime repair {identity.RuntimeId} {identity.Version} {identity.PackagingRevision} {identity.Rid} {identity.ModuleAbi} --yes";
        return $"Run '{repairCommand}' to repair the exact corrupt runtime, then retry.";
    }

    private static bool HasPermittedTrustAndSource(
        ExecutionRuntimeReceiptV1 receipt,
        bool allowDevelopment)
        => receipt.TrustEnvironment switch
        {
            ExecutionRuntimeTrustEnvironment.Production => receipt.AcquisitionSource is (
                RuntimeExecutionRuntimeAcquisitionSources.Registry
                or RuntimeExecutionRuntimeAcquisitionSources.LocalImport),
            ExecutionRuntimeTrustEnvironment.Development => allowDevelopment
                                                            && receipt.AcquisitionSource
                                                            == RuntimeExecutionRuntimeAcquisitionSources.Development,
            _ => false,
        };

    private sealed record Candidate(
        ExecutionRuntimeReceiptV1 Receipt,
        string RegistryOrigin,
        long RuntimePolicyRevision,
        string RuntimeLifecycleState,
        DateTimeOffset PolicyObservedAtUtc);
}
