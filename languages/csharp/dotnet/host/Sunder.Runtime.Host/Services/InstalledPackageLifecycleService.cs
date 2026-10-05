using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal enum PackageAuthorityConfirmationPhase
{
    PreLaunch,
    PreCommit,
}

internal sealed partial class InstalledPackageLifecycleService
{
    private const int MaxPendingStages = 32;
    private readonly RuntimeSessionOwner _sessions;
    private readonly RuntimeOperationGate _gate;
    private readonly InstalledPackageStore _installedPackages;
    private readonly PackageStoreCoordinator _storeCoordinator;
    private readonly PackageSessionReconciler _reconciler;
    private readonly RuntimeContentTransferStore _transfers;
    private readonly PackageSessionPublisher _publisher;
    private readonly ILogger<InstalledPackageLifecycleService> _logger;
    private readonly IInstalledPackageLifecycleFaultInjector? _faultInjector;
    private readonly RuntimeLifecyclePolicyOptions _lifecyclePolicy;
    private readonly TimeProvider _timeProvider;
    private readonly RuntimeRpcCatalog? _rpcCatalog;
    private readonly LocalExecutionRuntimeBindingResolver? _localRuntimeBindings;
    private readonly ExecutionRuntimeLifecyclePolicyCache? _runtimeLifecyclePolicies;
    private readonly InstalledPackagePolicyCoordinator? _packagePolicies;
    private readonly Dictionary<string, PendingStoreStage> _stages = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reconciliationPendingStages = new(StringComparer.OrdinalIgnoreCase);

    public InstalledPackageLifecycleService(
        RuntimeSessionOwner sessions,
        RuntimeOperationGate gate,
        InstalledPackageStore installedPackages,
        PackageStoreCoordinator storeCoordinator,
        PackageSessionReconciler reconciler,
        RuntimeContentTransferStore transfers,
        PackageSessionPublisher publisher,
        ILogger<InstalledPackageLifecycleService> logger,
        IInstalledPackageLifecycleFaultInjector? faultInjector = null,
        RuntimeLifecyclePolicyOptions? lifecyclePolicy = null,
        TimeProvider? timeProvider = null,
        RuntimeRpcCatalog? rpcCatalog = null,
        LocalExecutionRuntimeBindingResolver? localRuntimeBindings = null,
        ExecutionRuntimeLifecyclePolicyCache? runtimeLifecyclePolicies = null,
        InstalledPackagePolicyCoordinator? packagePolicies = null)
    {
        _sessions = sessions;
        _gate = gate;
        _installedPackages = installedPackages;
        _storeCoordinator = storeCoordinator;
        _reconciler = reconciler;
        _transfers = transfers;
        _publisher = publisher;
        _logger = logger;
        _faultInjector = faultInjector;
        _lifecyclePolicy = lifecyclePolicy ?? new RuntimeLifecyclePolicyOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _rpcCatalog = rpcCatalog;
        _localRuntimeBindings = localRuntimeBindings;
        _runtimeLifecyclePolicies = runtimeLifecyclePolicies;
        _packagePolicies = packagePolicies;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var operation = await _gate.EnterAsync(cancellationToken);
        await _storeCoordinator.InitializeAsync(operation.CancellationToken);
        if (_packagePolicies is not null) await _packagePolicies.ReconcileUnderOperationGateAsync(operation.CancellationToken);
    }

    internal RuntimePackageStamp Stamp => _sessions.Stamp;

    public async Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledAsync(CancellationToken cancellationToken = default)
        => (await _installedPackages.ListAsync(cancellationToken))
            .Select(package => _installedPackages.ToDescriptor(_packagePolicies?.ProjectCurrent(package) ?? package))
            .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal async Task<RuntimeExecutionRuntimeImpact> GetExecutionRuntimeImpactAsync(
        ExecutionRuntimeIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await using var operation = await _gate.EnterAsync(cancellationToken);
        return await CaptureExecutionRuntimeImpactAsync(identity, operation.CancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<int> EnforceExecutionRuntimePolicyAsync(
        IReadOnlyList<ExecutionRuntimeIdentity> blockedIdentities,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(blockedIdentities);
        if (blockedIdentities.Count == 0)
        {
            return 0;
        }
        await using var operation = await _gate.EnterAsync(cancellationToken);
        return await EnforceExecutionRuntimePolicyCoreAsync(
                blockedIdentities,
                operation.CancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<int> EnforceExecutionRuntimePolicyCoreAsync(
        IReadOnlyList<ExecutionRuntimeIdentity> blockedIdentities,
        CancellationToken cancellationToken)
    {
        var blockedKeys = blockedIdentities
            .Select(static identity => identity.SingleFlightKey)
            .ToHashSet(StringComparer.Ordinal);
        var packageIds = _sessions.State.GetExecutionRuntimeDependentPackageIds(blockedKeys);
        var faulted = 0;
        var cleanups = new List<Task>(packageIds.Count);
        foreach (var packageId in packageIds)
        {
            if (_sessions.EnforceExecutionRuntimePolicy(
                    packageId,
                    "The package is blocked by exact execution-runtime lifecycle policy.",
                    out var cleanupCompletion))
            {
                faulted++;
                cleanups.Add(cleanupCompletion);
            }
        }
        await Task.WhenAll(cleanups).WaitAsync(cancellationToken).ConfigureAwait(false);
        return faulted;
    }

    internal async Task<(
        ExecutionRuntimeVerificationResult Verification,
        ExecutionRuntimeEnsureResult? Repaired,
        PackageLifecycleOperationResult? Reload)> ExecuteExecutionRuntimeRepairAsync(
        ExecutionRuntimeIdentity identity,
        Func<CancellationToken, Task<ExecutionRuntimeVerificationResult>> verify,
        Func<CancellationToken, Task<ExecutionRuntimeEnsureResult?>> repair,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteExecutionRuntimeRepairAsync(
                identity,
                expectedImpactFingerprint: null,
                verify,
                repair,
                cancellationToken)
            .ConfigureAwait(false);
        return (result.Verification, result.Repaired, result.Reload);
    }

    internal async Task<(
        RuntimeExecutionRuntimeImpact Impact,
        ExecutionRuntimeVerificationResult Verification,
        ExecutionRuntimeEnsureResult? Repaired,
        PackageLifecycleOperationResult? Reload)> ExecuteExecutionRuntimeRepairAsync(
        ExecutionRuntimeIdentity identity,
        string? expectedImpactFingerprint,
        Func<CancellationToken, Task<ExecutionRuntimeVerificationResult>> verify,
        Func<CancellationToken, Task<ExecutionRuntimeEnsureResult?>> repair,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(verify);
        ArgumentNullException.ThrowIfNull(repair);

        await using var operation = await _gate.EnterAsync(cancellationToken);
        var operationToken = operation.CancellationToken;
        var impact = await CaptureExecutionRuntimeImpactAsync(identity, operationToken)
            .ConfigureAwait(false);
        ValidateExecutionRuntimeImpact(impact, expectedImpactFingerprint);
        var verification = await verify(operationToken).ConfigureAwait(false);
        if (verification.Healthy)
        {
            return (impact, verification, null, null);
        }

        _ = await EnforceExecutionRuntimePolicyCoreAsync([identity], operationToken)
            .ConfigureAwait(false);
        var repaired = await repair(operationToken).ConfigureAwait(false);
        if (repaired is null)
        {
            return (impact, verification, null, null);
        }

        var reload = await LoadPackagesCoreAsync(
                preserveDevelopmentOverlays: true,
                failOnLoadErrors: true,
                operationToken)
            .ConfigureAwait(false);
        return (impact, verification, repaired, reload);
    }

    internal async Task<(
        RuntimeExecutionRuntimeImpact Impact,
        ExecutionRuntimeEnsureResult Recovered,
        PackageLifecycleOperationResult Reload)> ExecuteExecutionRuntimeRecoveryAsync(
        ExecutionRuntimeIdentity identity,
        string expectedImpactFingerprint,
        Func<CancellationToken, Task<ExecutionRuntimeEnsureResult>> recover,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedImpactFingerprint);
        ArgumentNullException.ThrowIfNull(recover);

        await using var operation = await _gate.EnterAsync(cancellationToken);
        var operationToken = operation.CancellationToken;
        var impact = await CaptureExecutionRuntimeImpactAsync(identity, operationToken)
            .ConfigureAwait(false);
        ValidateExecutionRuntimeImpact(impact, expectedImpactFingerprint);
        _ = await EnforceExecutionRuntimePolicyCoreAsync([identity], operationToken)
            .ConfigureAwait(false);
        var recovered = await recover(operationToken).ConfigureAwait(false);
        var reload = await LoadPackagesCoreAsync(
                preserveDevelopmentOverlays: true,
                failOnLoadErrors: true,
                operationToken)
            .ConfigureAwait(false);
        return (impact, recovered, reload);
    }

    private async Task<RuntimeExecutionRuntimeImpact> CaptureExecutionRuntimeImpactAsync(
        ExecutionRuntimeIdentity identity,
        CancellationToken cancellationToken)
    {
        var references = (await _installedPackages.ListAsync(cancellationToken).ConfigureAwait(false))
            .SelectMany(package => package.ExecutionRuntimeBindings
                .Where(binding => ExecutionRuntimeIdentityMatches(binding, identity))
                .Select(binding => new
                {
                    package.PackageId,
                    PackageVersion = package.Version,
                    package.ContentIdentity,
                    Binding = binding,
                }))
            .OrderBy(static reference => reference.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static reference => reference.Binding.Role, StringComparer.Ordinal)
            .ThenBy(static reference => reference.Binding.Rid, StringComparer.Ordinal)
            .ToArray();
        var packageIds = references
            .Select(static reference => reference.PackageId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var fingerprintInput = new StringBuilder(identity.SingleFlightKey);
        foreach (var reference in references)
        {
            var binding = reference.Binding;
            fingerprintInput.Append('\n').Append(reference.PackageId)
                .Append('\n').Append(reference.PackageVersion)
                .Append('\n').Append(reference.ContentIdentity)
                .Append('\n').Append(binding.Role)
                .Append('\n').Append(binding.Rid)
                .Append('\n').Append(binding.RuntimeManifestSha256)
                .Append('\n').Append(binding.RuntimeArtifactSha256)
                .Append('\n').Append(binding.RuntimeArtifactSize.ToString(CultureInfo.InvariantCulture))
                .Append('\n').Append(binding.RegistryOrigin)
                .Append('\n').Append(binding.AcquisitionSource)
                .Append('\n').Append(binding.SigningKeyId)
                .Append('\n').Append(binding.TrustStoreGeneration.ToString(CultureInfo.InvariantCulture))
                .Append('\n').Append(binding.RuntimePolicyRevision.ToString(CultureInfo.InvariantCulture))
                .Append('\n').Append(binding.RuntimeLifecycleState)
                .Append('\n').Append(binding.PolicyObservedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
                .Append('\n').Append(binding.ResolvedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }
        var fingerprint = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput.ToString())))
            .ToLowerInvariant();
        return new RuntimeExecutionRuntimeImpact(packageIds.Length, packageIds, fingerprint);
    }

    private static void ValidateExecutionRuntimeImpact(
        RuntimeExecutionRuntimeImpact impact,
        string? expectedImpactFingerprint)
    {
        if (expectedImpactFingerprint is null)
        {
            return;
        }
        if (!string.Equals(expectedImpactFingerprint, impact.Fingerprint, StringComparison.Ordinal))
        {
            throw new ExecutionRuntimePlanMismatchException(
                "Execution-runtime package impact changed after confirmation.");
        }
    }

    private static bool ExecutionRuntimeIdentityMatches(
        InstalledPackageExecutionRuntimeBindingRecord binding,
        ExecutionRuntimeIdentity identity)
        => string.Equals(binding.RuntimeId, identity.RuntimeId, StringComparison.Ordinal)
           && string.Equals(binding.RuntimeVersion, identity.Version, StringComparison.Ordinal)
           && binding.RuntimePackagingRevision == identity.PackagingRevision
           && string.Equals(binding.Rid, identity.Rid, StringComparison.Ordinal)
           && string.Equals(binding.ModuleAbi, identity.ModuleAbi, StringComparison.Ordinal);

    internal async Task<PackageOperationResult> InstallFromRuntimePathAsync(string path, CancellationToken cancellationToken = default)
        => await ExecutePreparedMutationsAsync(
            [new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: path)],
            cancellationToken);

    internal async Task<PackageOperationResult> UpgradeFromRuntimePathAsync(
        string packageId,
        string path,
        bool allowDowngrade = false,
        bool reinstall = false,
        CancellationToken cancellationToken = default)
        => await ExecutePreparedMutationsAsync(
            [new PackageStoreMutation(PackageStoreMutationKind.Upgrade, packageId, path, allowDowngrade, reinstall)],
            cancellationToken);

    public async Task<PackageLifecycleOperationResult> LoadInstalledPackagesAsync(CancellationToken cancellationToken = default)
        => await LoadPackagesAsync(preserveDevelopmentOverlays: false, cancellationToken);

    private async Task<PackageLifecycleOperationResult> LoadPackagesAsync(
        bool preserveDevelopmentOverlays,
        CancellationToken cancellationToken)
    {
        await using var operation = await _gate.EnterAsync(cancellationToken);
        return await LoadPackagesCoreAsync(
                preserveDevelopmentOverlays,
                operation.CancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<PackageLifecycleOperationResult> LoadPackagesCoreAsync(
        bool preserveDevelopmentOverlays,
        CancellationToken operationToken)
        => await LoadPackagesCoreAsync(
                preserveDevelopmentOverlays,
                failOnLoadErrors: false,
                operationToken)
            .ConfigureAwait(false);

    private async Task<PackageLifecycleOperationResult> LoadPackagesCoreAsync(
        bool preserveDevelopmentOverlays,
        bool failOnLoadErrors,
        CancellationToken operationToken)
    {
        if (_packagePolicies is not null) await _packagePolicies.ReconcileUnderOperationGateAsync(operationToken);
        var baseGeneration = _sessions.Generation;
        var currentPackages = _sessions.State.GetActivePackages();
        var sources = _sessions.Sources.Snapshot();
        if (!preserveDevelopmentOverlays)
        {
            sources.RemoveDevOverlaysOwnedBy(
                PackageSessionOverlayOwner.Startup,
                PackageSessionOverlayOwner.HotReload,
                PackageSessionOverlayOwner.Sdk);
        }
        var loaded = await _reconciler.LoadMergedSessionAsync(
            sources.ActiveDevOverlays,
            baseGeneration,
            operationToken);
        var warnings = loaded.Warnings.Concat(loaded.Errors.Select(error => $"Installed package session loaded with package errors: {error}")).ToList();
        if (loaded.Session is null)
        {
            return PackageLifecycleOperationResult.Failed(
                loaded.Errors.FirstOrDefault() ?? "Installed package lifecycle load failed.",
                currentPackages,
                warnings,
                loaded.Errors);
        }
        if (failOnLoadErrors && loaded.Errors.Count > 0)
        {
            loaded.Session.StartupTrace?.TryFail("strict-reload-failed");
            await loaded.Session.DisposeAsync().ConfigureAwait(false);
            return PackageLifecycleOperationResult.Failed(
                "Dependent packages could not be reloaded; the previous healthy generation was retained.",
                currentPackages,
                warnings,
                loaded.Errors);
        }
        var pendingRuntimeCutover = await _storeCoordinator.GetPendingRuntimeCutoverAsync(operationToken)
            .ConfigureAwait(false);

        if (loaded.Session.IsEmpty && _sessions.State.ActiveSession.IsEmpty)
        {
            try
            {
                _rpcCatalog?.DeactivateAll(_sessions.Generation);
                _rpcCatalog?.VerifySessionGeneration(_sessions.Generation);
                ResolvePendingReconciliations(_sessions.Stamp);
                loaded.Session.StartupTrace?.TryCommitGeneration(_sessions.Generation, "no-op");
                warnings.AddRange(await _storeCoordinator.CompleteRecoveredCutoversAsync(
                        operationToken,
                        acknowledgeEmptyGeneration: true)
                    .ConfigureAwait(false));
                return new PackageLifecycleOperationResult(
                    true,
                    "No installed packages to load.",
                    currentPackages,
                    warnings,
                    [],
                    [])
                {
                    CommittedStamp = _sessions.Stamp,
                };
            }
            catch (OperationCanceledException)
            {
                loaded.Session.StartupTrace?.TryFail("canceled");
                throw;
            }
            catch
            {
                loaded.Session.StartupTrace?.TryFail();
                throw;
            }
        }

        var packages = loaded.Session.GetActivePackages();
        try
        {
            Func<Task>? generationCommitted = null;
            Func<Task>? previousWorkersStopped = null;
            Func<CancellationToken, Task>? storageActivationPreparing = null;
            if (pendingRuntimeCutover is { } pending)
            {
                generationCommitted = () => _storeCoordinator.AdvanceCutoverPhaseAsync(
                    pending.TransactionId,
                    PackageStoreCutoverPhase.GenerationCommitAcknowledged);
                previousWorkersStopped = () => _storeCoordinator.AdvanceCutoverPhaseAsync(
                    pending.TransactionId,
                    PackageStoreCutoverPhase.OldGenerationStopped);
                storageActivationPreparing = _ => _storeCoordinator.PrepareStorageActivationAsync(
                    pending.TransactionId,
                    loaded.Session.LoadedPackageMap.Keys.ToArray());
            }
            var publication = await _publisher.PublishAsync(
                loaded.Session,
                sources,
                warnings,
                loaded.Errors,
                baseGeneration,
                operationToken,
                committedStoreTransactionId: pendingRuntimeCutover?.TransactionId,
                candidateGenerationCommitted: generationCommitted,
                previousWorkersQuiesced: previousWorkersStopped,
                storageActivationPreparing: storageActivationPreparing);
            warnings.AddRange(publication.CleanupWarnings);
            var impacted = currentPackages.Select(package => package.PackageId)
                .Concat(packages.Select(package => package.PackageId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var committed = _sessions.GetSnapshot();
            ResolvePendingReconciliations(publication.Stamp);
            warnings.AddRange(await _storeCoordinator.CompleteRecoveredCutoversAsync(operationToken)
                .ConfigureAwait(false));
            return new PackageLifecycleOperationResult(
                true,
                "Installed packages loaded.",
                committed.ActivePackages,
                warnings,
                loaded.Errors,
                impacted)
            {
                CommittedStamp = publication.Stamp,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (exception is RuntimeRequiredProviderException) throw;
            _logger.LogError(exception, "Failed to start background services for installed package lifecycle load");
            const string message = "Installed package lifecycle load failed while starting background services.";
            return PackageLifecycleOperationResult.Failed(message, currentPackages, warnings, [message]);
        }
        throw new InvalidOperationException("Installed package publication did not complete.");
    }

    public Task<PackageOperationResult> SetEnabledAsync(string packageId, bool enabled, CancellationToken cancellationToken = default)
        => ExecutePreparedMutationsAsync(
            [new PackageStoreMutation(enabled ? PackageStoreMutationKind.Enable : PackageStoreMutationKind.Disable, packageId)],
            cancellationToken);

    public Task<PackageOperationResult> UninstallAsync(
        string packageId,
        PackageUninstallRequest request,
        CancellationToken cancellationToken = default)
        => ExecutePreparedMutationsAsync(
            [new PackageStoreMutation(
                PackageStoreMutationKind.Uninstall,
                packageId,
                AllowCascade: request.AllowCascade,
                ConfirmationToken: request.ConfirmationToken)],
            cancellationToken);

    public async Task<PackageUninstallPlan?> GetUninstallPlanAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        var packages = await _installedPackages.ListAsync(cancellationToken);
        return packages.Any(package => string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
            ? PackageStorePolicy.CreateUninstallPlan(packageId, packages)
            : null;
    }

    public Task<PackageStoreStageResult> StageAsync(
        PackageStoreStageRequest request,
        CancellationToken cancellationToken = default)
        => StageCoreAsync(
            request,
            registryUploadMetadataById: null,
            registryExpectedCatalog: null,
            registryRuntimeRebinds: null,
            registryRuntimePolicyUpdates: null,
            registryRuntimePolicyEntries: null,
            registryRuntimePolicyAuthority: null,
            cancellationToken);

    internal Task<PackageStoreStageResult> StageRegistryAsync(
        PackageStoreStageRequest request,
        IReadOnlyDictionary<string, RegistryPackageUploadMetadata> registryUploadMetadataById,
        IReadOnlyList<InstalledPackageRecord> registryExpectedCatalog,
        IReadOnlyList<PackageExecutionRuntimeRebindMutation> registryRuntimeRebinds,
        CancellationToken cancellationToken = default)
        => StageRegistryAsync(
            request,
            registryUploadMetadataById,
            registryExpectedCatalog,
            registryRuntimeRebinds,
            registryRuntimePolicyUpdates: [],
            registryRuntimePolicyEntries: [],
            registryRuntimePolicyAuthority: null,
            cancellationToken);

    internal Task<PackageStoreStageResult> StageRegistryAsync(
        PackageStoreStageRequest request,
        IReadOnlyDictionary<string, RegistryPackageUploadMetadata> registryUploadMetadataById,
        IReadOnlyList<InstalledPackageRecord> registryExpectedCatalog,
        IReadOnlyList<PackageExecutionRuntimeRebindMutation> registryRuntimeRebinds,
        IReadOnlyList<PackageExecutionRuntimePolicyUpdateMutation> registryRuntimePolicyUpdates,
        IReadOnlyList<ExecutionRuntimeLifecyclePolicyEntryV1> registryRuntimePolicyEntries,
        ExecutionRuntimeLifecyclePolicySnapshot? registryRuntimePolicyAuthority,
        CancellationToken cancellationToken = default)
        => StageCoreAsync(
            request,
            registryUploadMetadataById,
            registryExpectedCatalog,
            registryRuntimeRebinds,
            registryRuntimePolicyUpdates,
            registryRuntimePolicyEntries,
            registryRuntimePolicyAuthority,
            cancellationToken);

    private async Task<PackageStoreStageResult> StageCoreAsync(
        PackageStoreStageRequest request,
        IReadOnlyDictionary<string, RegistryPackageUploadMetadata>? registryUploadMetadataById,
        IReadOnlyList<InstalledPackageRecord>? registryExpectedCatalog,
        IReadOnlyList<PackageExecutionRuntimeRebindMutation>? registryRuntimeRebinds,
        IReadOnlyList<PackageExecutionRuntimePolicyUpdateMutation>? registryRuntimePolicyUpdates,
        IReadOnlyList<ExecutionRuntimeLifecyclePolicyEntryV1>? registryRuntimePolicyEntries,
        ExecutionRuntimeLifecyclePolicySnapshot? registryRuntimePolicyAuthority,
        CancellationToken cancellationToken)
    {
        await using var operation = await _gate.EnterAsync(cancellationToken);
        var operationToken = operation.CancellationToken;
        await SweepStagesCoreAsync(_timeProvider.GetUtcNow());
        if (_stages.Count >= MaxPendingStages)
        {
            return PackageStoreStageResult.Failed(
                $"The Runtime already has {MaxPendingStages} pending package store stages.",
                _sessions.State.GetActivePackages());
        }
        var baseSessionGeneration = _sessions.Generation;
        var currentPackages = _sessions.State.GetActivePackages();
        var sources = _sessions.Sources.Snapshot();
        var leases = new List<RuntimeUploadLease>();
        PackageStoreStagePreparation preparation;
        try
        {
            if (registryExpectedCatalog is not null)
            {
                await ValidateRegistryStateExpectationAsync(
                    registryExpectedCatalog,
                    operationToken);
            }
            var changes = ResolveMutations(
                request.Mutations,
                leases,
                registryUploadMetadataById,
                operationToken);
            if (registryRuntimeRebinds is { Count: > 0 })
            {
                changes.AddRange(registryRuntimeRebinds);
            }
            if (registryRuntimePolicyUpdates is { Count: > 0 })
            {
                changes.AddRange(registryRuntimePolicyUpdates);
            }
            var localBindingAuthority = await CaptureLocalBindingAuthorityAsync(
                    changes,
                    operationToken)
                .ConfigureAwait(false);
            preparation = await _storeCoordinator.PrepareStageAsync(
                changes,
                localBindingAuthority,
                registryRuntimePolicyEntries ?? [],
                registryRuntimePolicyAuthority,
                operationToken);
        }
        catch (InvalidDataException exception)
        {
            return PackageStoreStageResult.Failed(exception.Message, currentPackages);
        }
        finally
        {
            foreach (var lease in leases) _transfers.ReleaseUpload(lease);
        }
        return await PrepareCandidateAsync(
            preparation,
            baseSessionGeneration,
            currentPackages,
            sources,
            operationToken);
    }

    private async Task ValidateRegistryStateExpectationAsync(
        IReadOnlyList<InstalledPackageRecord> expectedCatalog,
        CancellationToken cancellationToken)
    {
        var current = await _installedPackages.ListAsync(cancellationToken);
        if (!PackageStorePolicy.CatalogBindingsEqual(current, expectedCatalog))
        {
            throw StaleRegistryPlan(packageId: null);
        }
    }

    private static InvalidDataException StaleRegistryPlan(string? packageId)
        => new(packageId is null
            ? "Registry package staging is stale because the installed package catalog changed after resolution."
            : $"Registry package staging is stale because installed state for package '{packageId}' changed after resolution.");

    private async Task<PackageStoreStageResult> PrepareCandidateAsync(
        PackageStoreStagePreparation preparation,
        long baseSessionGeneration,
        IReadOnlyList<ActivePackageDescriptor> currentPackages,
        PackageSessionSourceSnapshot sources,
        CancellationToken cancellationToken)
    {
        if (!preparation.Success || preparation.Stage is null)
        {
            var failure = preparation.Failure ?? PackageOperationResults.Failure("Package store stage failed.");
            return PackageStoreStageResult.Failed(failure.Message ?? "Package store stage failed.", currentPackages, failure.Warnings, failure.Errors, failure.ImpactedPackageIds);
        }

        if (preparation.Stage.Result.ImpactedPackageIds.Count == 0)
        {
            var createdAtUtc = _timeProvider.GetUtcNow();
            var expiresAtUtc = createdAtUtc + _lifecyclePolicy.PendingStageLifetime;
            _stages[preparation.Stage.StageId] = new PendingStoreStage(
                Candidate: null,
                StoreResult: preparation.Stage.Result,
                preparation.Stage.BaseCatalogGeneration,
                baseSessionGeneration,
                createdAtUtc,
                expiresAtUtc,
                preparation.Stage.LocalRuntimePolicyAuthority);
            _sessions.RegisterStage(
                preparation.Stage.StageId,
                baseSessionGeneration,
                baseSessionGeneration,
                RuntimePackageStageKind.PackageStore,
                createdAtUtc,
                expiresAtUtc);
            return new PackageStoreStageResult(
                preparation.Stage.StageId,
                preparation.Stage.Result,
                currentPackages);
        }

        PackageSessionLoadResult loaded;
        try
        {
            loaded = await _reconciler.LoadMergedSessionAsync(
                preparation.Stage.ProspectivePackages,
                sources.ActiveDevOverlays,
                baseSessionGeneration,
                preparation.Stage.PreparationSourcePaths,
                preparation.Stage.RegistryRuntimePolicyAuthority,
                cancellationToken);
        }
        catch
        {
            await _storeCoordinator.DiscardStageAsync(preparation.Stage.StageId);
            throw;
        }
        if (loaded.Session is null || loaded.Errors.Count > 0)
        {
            try
            {
                if (loaded.Session is not null) await loaded.Session.DisposeAsync();
            }
            finally
            {
                await _storeCoordinator.DiscardStageAsync(preparation.Stage.StageId);
            }
            return PackageStoreStageResult.Failed(
                loaded.Errors.FirstOrDefault() ?? "Package store stage failed while loading the prospective package session.",
                currentPackages,
                preparation.Stage.Result.Warnings.Concat(loaded.Warnings).ToArray(),
                loaded.Errors,
                preparation.Stage.Result.ImpactedPackageIds);
        }
        if (_sessions.Generation != baseSessionGeneration)
        {
            try
            {
                await loaded.Session.DisposeAsync();
            }
            finally
            {
                await _storeCoordinator.DiscardStageAsync(preparation.Stage.StageId);
            }
            return PackageStoreStageResult.Failed(
                "Package store stage is stale because the active package session changed while it was being prepared.",
                currentPackages,
                impactedPackageIds: preparation.Stage.Result.ImpactedPackageIds);
        }

        var result = preparation.Stage.Result with
        {
            Warnings = preparation.Stage.Result.Warnings
                .Concat(loaded.Warnings)
                .Concat(loaded.Errors.Select(error => $"Staged installed package session loaded with package errors: {error}"))
                .ToArray(),
        };
        PreparedPackageSession candidate;
        try
        {
            candidate = _publisher.Prepare(
                loaded.Session,
                sources,
                result.Warnings,
                loaded.Errors,
                baseSessionGeneration,
                preparation.Stage.StageId,
                requiresPackageStoreCommit: true);
        }
        catch (Exception exception)
        {
            try
            {
                await loaded.Session.DisposeAsync();
            }
            finally
            {
                await _storeCoordinator.DiscardStageAsync(preparation.Stage.StageId);
            }
            if (exception is RuntimeRequiredProviderException) throw;
            _logger.LogError(exception, "Failed to prepare package store stage {StageId}", preparation.Stage.StageId);
            return PackageStoreStageResult.Failed(
                "Package store stage candidate preparation failed.",
                currentPackages,
                result.Warnings,
                impactedPackageIds: result.ImpactedPackageIds);
        }
        var candidateCreatedAtUtc = _timeProvider.GetUtcNow();
        var candidateExpiresAtUtc = candidateCreatedAtUtc + _lifecyclePolicy.PendingStageLifetime;
        _stages[preparation.Stage.StageId] = new PendingStoreStage(
            candidate,
            preparation.Stage.Result,
            preparation.Stage.BaseCatalogGeneration,
            baseSessionGeneration,
            candidateCreatedAtUtc,
            candidateExpiresAtUtc,
            preparation.Stage.LocalRuntimePolicyAuthority);
        _sessions.RegisterStage(
            preparation.Stage.StageId,
            baseSessionGeneration + 1,
            baseSessionGeneration,
            RuntimePackageStageKind.PackageStore,
            candidateCreatedAtUtc,
            candidateExpiresAtUtc);
        return new PackageStoreStageResult(
            preparation.Stage.StageId,
            result,
            loaded.Session.GetActivePackages());
    }

    public async Task<PackageOperationResult> CommitStageAsync(string stageId, CancellationToken cancellationToken = default)
        => await CommitStageAsync(
            stageId,
            authorityConfirmation: null,
            cancellationToken);

    internal async Task<PackageOperationResult> CommitRegistryStageAsync(
        string stageId,
        Func<PackageAuthorityConfirmationPhase, CancellationToken, Task> authorityConfirmation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorityConfirmation);
        return await CommitStageAsync(stageId, authorityConfirmation, cancellationToken);
    }

    private async Task<PackageOperationResult> CommitStageAsync(
        string stageId,
        Func<PackageAuthorityConfirmationPhase, CancellationToken, Task>? authorityConfirmation,
        CancellationToken cancellationToken)
    {
        await using var operation = await _gate.EnterAsync(cancellationToken);
        await SweepStagesCoreAsync(_timeProvider.GetUtcNow());
        return await CommitStageCoreAsync(
            stageId,
            authorityConfirmation,
            operation.CancellationToken);
    }

    private async Task<PackageOperationResult> CommitStageCoreAsync(
        string stageId,
        Func<PackageAuthorityConfirmationPhase, CancellationToken, Task>? authorityConfirmation,
        CancellationToken operationToken)
    {
        if (!_stages.Remove(stageId, out var stage))
        {
            return PackageOperationResults.Failure(
                _sessions.GetStageStatus(stageId)?.Message
                ?? $"Package store stage '{stageId}' was not found.");
        }
        if ((stage.Candidate is not null && stage.BaseSessionGeneration != _sessions.Generation)
            || stage.BaseCatalogGeneration != _storeCoordinator.CatalogGeneration)
        {
            try
            {
                if (stage.Candidate is not null)
                {
                    await _publisher.DiscardAsync(stage.Candidate);
                }
                else
                {
                    _sessions.RemoveStage(stageId);
                }
            }
            finally
            {
                await _storeCoordinator.DiscardStageAsync(stageId);
            }
            _sessions.MarkStageFailed(stageId, "The package store stage became stale before commit.");
            return PackageOperationResults.Failure($"Package store stage '{stageId}' is stale because the active package session changed before commit.");
        }

        PendingPackageSessionPublication? publication = null;
        PackageOperationResult? storeResult = null;
        var storeCommitted = false;
        var storeCommitAttempted = false;
        PackageStoreCommitDisposition? storeDisposition = null;
        var authorityConfirmationFailed = false;
        _sessions.MarkStageCommitting(stageId);
        try
        {
            if (stage.Candidate is not null)
            {
                publication = await _publisher.BeginPublishAsync(stage.Candidate, operationToken);
                if (stage.BaseSessionGeneration != _sessions.Generation
                    || stage.BaseCatalogGeneration != _storeCoordinator.CatalogGeneration)
                {
                    try
                    {
                        await _publisher.DiscardAsync(publication);
                        publication = null;
                    }
                    finally
                    {
                        await _storeCoordinator.DiscardStageAsync(stageId);
                    }
                    _sessions.MarkStageFailed(stageId, "The package store stage became stale before commit.");
                    return PackageOperationResults.Failure($"Package store stage '{stageId}' is stale because the active package session changed before commit.");
                }

                await ExecuteUnderAuthorityFenceAsync(PackageAuthorityConfirmationPhase.PreLaunch, async token =>
                {
                    await _storeCoordinator.AdvanceCutoverPhaseAsync(
                        stageId,
                        PackageStoreCutoverPhase.PreLaunchPolicyConfirmed).ConfigureAwait(false);
                    _faultInjector?.Hit(
                        InstalledPackageLifecycleFaultPoint.PreLaunchPolicyConfirmedBeforeCandidateStart);
                    await _publisher.StartCandidateAsync(publication, token).ConfigureAwait(false);
                    await _storeCoordinator.AdvanceCutoverPhaseAsync(
                        stageId,
                        PackageStoreCutoverPhase.CandidateReady).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
                _faultInjector?.Hit(InstalledPackageLifecycleFaultPoint.CandidateReadyBeforeStoreCommit);
            }
            else
            {
                await ExecuteUnderAuthorityFenceAsync(
                        PackageAuthorityConfirmationPhase.PreLaunch,
                        async _ =>
                        {
                            await _storeCoordinator.AdvanceCutoverPhaseAsync(
                                stageId,
                                PackageStoreCutoverPhase.PreLaunchPolicyConfirmed).ConfigureAwait(false);
                            _faultInjector?.Hit(
                                InstalledPackageLifecycleFaultPoint.PreLaunchPolicyConfirmedBeforeCandidateStart);
                            await _storeCoordinator.AdvanceCutoverPhaseAsync(
                                stageId,
                                PackageStoreCutoverPhase.CandidateReady).ConfigureAwait(false);
                            return true;
                        })
                    .ConfigureAwait(false);
                _faultInjector?.Hit(InstalledPackageLifecycleFaultPoint.CandidateReadyBeforeStoreCommit);
            }

            operationToken.ThrowIfCancellationRequested();
            var storeCommit = await ExecuteUnderAuthorityFenceAsync(
                PackageAuthorityConfirmationPhase.PreCommit,
                async _ =>
                {
                    await _storeCoordinator.AdvanceCutoverPhaseAsync(
                        stageId,
                        PackageStoreCutoverPhase.PreCommitPolicyConfirmed).ConfigureAwait(false);
                    _faultInjector?.Hit(
                        InstalledPackageLifecycleFaultPoint.PreCommitPolicyConfirmedBeforeStoreCommit);
                    return await _storeCoordinator.CommitStageWithOutcomeAsync(
                        stageId,
                        CancellationToken.None,
                        retainCutoverForPublication: true,
                        durableCommitStarted: () => storeCommitAttempted = true,
                        packagePolicyFenceHeld: true).ConfigureAwait(false);
                }).ConfigureAwait(false);
            storeResult = storeCommit.OperationResult;
            storeDisposition = storeCommit.Disposition;
            if (storeDisposition == PackageStoreCommitDisposition.Indeterminate)
            {
                return await FailIndeterminateStoreCommitAsync(
                    stageId,
                    stage,
                    publication,
                    storeResult,
                    exception: null).ConfigureAwait(false);
            }
            if (!storeResult.Success)
            {
                if (publication is not null)
                {
                    await _publisher.DiscardAsync(publication);
                    publication = null;
                }
                else
                {
                    _sessions.RemoveStage(stageId);
                }
                _sessions.MarkStageFailed(stageId, storeResult.Message);
                return storeResult;
            }
            if (storeDisposition != PackageStoreCommitDisposition.Committed)
            {
                throw new InvalidOperationException(
                    $"Package store stage '{stageId}' reported success without a durable commit.");
            }
            storeCommitted = true;
            if (publication is not null)
            {
                _publisher.MarkPackageStoreCommitted(publication);
            }
            if (publication is null)
            {
                await _storeCoordinator.AdvanceCutoverPhaseAsync(
                    stageId,
                    PackageStoreCutoverPhase.GenerationCommitAcknowledged).ConfigureAwait(false);
                await _storeCoordinator.AdvanceCutoverPhaseAsync(
                    stageId,
                    PackageStoreCutoverPhase.OldGenerationStopped).ConfigureAwait(false);
                var noOpCutoverWarnings = await _storeCoordinator.CompleteCutoverAsync(stageId)
                    .ConfigureAwait(false);
                _sessions.RemoveStage(stageId);
                var stamp = _sessions.Stamp;
                var noOpResult = storeResult with
                {
                    StoreCommitted = true,
                    CommittedStamp = stamp,
                    Warnings = storeResult.Warnings.Concat(noOpCutoverWarnings).ToArray(),
                };
                _sessions.MarkStageCommitted(
                    stageId,
                    stamp,
                    runtimeSessionApplied: false,
                    reconciliationPending: false,
                    noOpResult.Message);
                return noOpResult;
            }

            stage.Candidate!.Session.FinalizeCommittedInstalledSources();
            _faultInjector?.Hit(InstalledPackageLifecycleFaultPoint.StoreCommittedBeforeSessionPublication);
            var committed = await _publisher.CommitAsync(
                publication,
                CancellationToken.None,
                candidateGenerationCommitted: async () =>
                {
                    await _storeCoordinator.AdvanceCutoverPhaseAsync(
                        stageId,
                        PackageStoreCutoverPhase.GenerationCommitAcknowledged).ConfigureAwait(false);
                    _faultInjector?.Hit(
                        InstalledPackageLifecycleFaultPoint.CandidateGenerationCommittedBeforePreviousWorkersQuiesced);
                },
                previousWorkersQuiesced: async () =>
                {
                    await _storeCoordinator.AdvanceCutoverPhaseAsync(
                        stageId,
                        PackageStoreCutoverPhase.OldGenerationStopped).ConfigureAwait(false);
                    _faultInjector?.Hit(
                        InstalledPackageLifecycleFaultPoint.PreviousWorkersQuiescedBeforeStorageActivation);
                },
                storageActivationPreparing: _ => _storeCoordinator.PrepareStorageActivationAsync(
                    stageId,
                    stage.Candidate!.Session.LoadedPackageMap.Keys.ToArray()));
            publication = null;
            var publicationCutoverWarnings = await _storeCoordinator.CompleteCutoverAsync(stageId)
                .ConfigureAwait(false);
            var result = storeResult with
            {
                RuntimeSessionApplied = true,
                RequiresAppRestart = false,
                Warnings = storeResult.Warnings
                    .Concat(committed.CleanupWarnings)
                    .Concat(publicationCutoverWarnings)
                    .ToArray(),
                CommittedStamp = committed.Stamp,
                StoreCommitted = true,
                RuntimeSessionReconciliationPending = committed.ReconciliationPending,
            };
            _sessions.MarkStageCommitted(
                stageId,
                committed.Stamp,
                runtimeSessionApplied: true,
                committed.ReconciliationPending,
                result.Message);
            if (committed.ReconciliationPending)
            {
                _reconciliationPendingStages.Add(stageId);
            }
            else
            {
                ResolvePendingReconciliations(committed.Stamp);
            }
            return result;

            async Task<TResult> ExecuteUnderAuthorityFenceAsync<TResult>(
                PackageAuthorityConfirmationPhase phase,
                Func<CancellationToken, Task<TResult>> operation)
            {
                if (authorityConfirmation is not null)
                {
                    try
                    {
                        await authorityConfirmation(phase, operationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        authorityConfirmationFailed = true;
                        throw;
                    }
                }
                if (stage.LocalRuntimePolicyAuthority is not { } localAuthority)
                {
                    try { return await _storeCoordinator.ExecuteUnderPackagePolicyFenceAsync(stageId, operation, operationToken).ConfigureAwait(false); }
                    catch (PackagePolicyDeniedException) { authorityConfirmationFailed = true; throw; }
                }
                if (_runtimeLifecyclePolicies is null)
                {
                    authorityConfirmationFailed = true;
                    throw new InvalidOperationException(
                        "Local execution-runtime policy authority is unavailable during package store commit.");
                }

                var operationStarted = false;
                try
                {
                    return await _storeCoordinator.ExecuteUnderPackagePolicyFenceAsync(stageId, policyToken => _runtimeLifecyclePolicies.ExecuteUnderGenerationFenceAsync(
                            localAuthority.Generation,
                            localAuthority.InstallationIdentities,
                            localAuthority.ActivationIdentities,
                            token =>
                            {
                                operationStarted = true;
                                return operation(token);
                            },
                            policyToken), operationToken)
                        .ConfigureAwait(false);
                }
                catch when (!operationStarted)
                {
                    authorityConfirmationFailed = true;
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (
            !storeCommitted
            && !storeCommitAttempted
            && !authorityConfirmationFailed)
        {
            try
            {
                if (publication is not null && !publication.Publication.Completed)
                {
                    await _publisher.DiscardAsync(publication);
                }
                else
                {
                    _sessions.RemoveStage(stageId);
                }
            }
            finally
            {
                await _storeCoordinator.DiscardStageAsync(stageId);
            }
            _sessions.MarkStageFailed(stageId, "The package store stage was cancelled before commit.");
            throw;
        }
        catch (Exception exception) when (!storeCommitted && authorityConfirmationFailed)
        {
            try
            {
                if (publication is not null && !publication.Publication.Completed)
                {
                    await _publisher.DiscardAsync(publication);
                }
                else
                {
                    _sessions.RemoveStage(stageId);
                }
            }
            finally
            {
                await _storeCoordinator.DiscardStageAsync(stageId);
            }
            _sessions.MarkStageDiscarded(stageId);
            _logger.LogWarning(
                exception,
                "Package authority confirmation rejected package store stage {StageId}",
                stageId);
            throw;
        }
        catch (Exception exception)
        {
            if (!storeCommitted
                && _storeCoordinator.CatalogGeneration > stage.BaseCatalogGeneration)
            {
                storeCommitted = true;
                storeResult = stage.StoreResult;
                if (publication is not null)
                {
                    _publisher.MarkPackageStoreCommitted(publication);
                }
            }

            if (!storeCommitted
                && storeCommitAttempted
                && storeDisposition != PackageStoreCommitDisposition.RolledBack)
            {
                return await FailIndeterminateStoreCommitAsync(
                    stageId,
                    stage,
                    publication,
                    storeResult,
                    exception).ConfigureAwait(false);
            }

            if (!storeCommitted)
            {
                try
                {
                    if (publication is not null && !publication.Publication.Completed)
                    {
                        await _publisher.DiscardAsync(publication);
                    }
                    else
                    {
                        _sessions.RemoveStage(stageId);
                    }
                }
                finally
                {
                    await _storeCoordinator.DiscardStageAsync(stageId);
                }
                _sessions.MarkStageFailed(stageId, "The package store stage could not be committed.");
                if (exception is RuntimeRequiredProviderException) throw;
                _logger.LogError(exception, "Failed to commit package store stage {StageId}", stageId);
                return PackageOperationResults.Failure("The package store stage could not be committed.");
            }

            if (publication is not null && !publication.Publication.Completed)
            {
                try
                {
                    await _publisher.DiscardAsync(publication);
                }
                catch (Exception cleanupException)
                {
                    _logger.LogWarning(
                        cleanupException,
                        "Failed to discard package session candidate after committed store stage {StageId}",
                        stageId);
                }
            }
            else
            {
                _sessions.RemoveStage(stageId);
            }

            const string warning = "The installed package store was committed, but the Runtime session was not updated; reconciliation is pending.";
            _logger.LogError(exception, "Package store stage {StageId} failed after its durable store commit", stageId);
            var stamp = _sessions.Stamp;
            var degraded = storeResult! with
            {
                Success = true,
                RuntimeSessionApplied = false,
                RequiresAppRestart = false,
                Warnings = storeResult.Warnings.Append(warning).ToArray(),
                Errors = [],
                CommittedStamp = stamp,
                StoreCommitted = true,
                RuntimeSessionReconciliationPending = true,
            };
            _sessions.MarkStageCommitted(
                stageId,
                stamp,
                runtimeSessionApplied: false,
                reconciliationPending: true,
                degraded.Message);
            _reconciliationPendingStages.Add(stageId);
            return degraded;
        }
    }

    private async Task<PackageOperationResult> FailIndeterminateStoreCommitAsync(
        string stageId,
        PendingStoreStage stage,
        PendingPackageSessionPublication? publication,
        PackageOperationResult? storeResult,
        Exception? exception)
    {
        if (publication is not null && !publication.Publication.Completed)
        {
            try
            {
                _publisher.MarkPackageStoreIndeterminate(publication);
                await _publisher.DiscardAsync(publication).ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                _logger.LogCritical(
                    cleanupException,
                    "Failed to abandon package sessions after indeterminate store stage {StageId}",
                    stageId);
            }
        }
        else
        {
            _sessions.RemoveStage(stageId);
        }

        _gate.FenceForIntegrityFailure();
        const string message =
            "The package store outcome is indeterminate; the Runtime is unavailable until startup recovery reconciles the authoritative catalog.";
        _sessions.MarkStageFailed(stageId, message);
        if (exception is null)
        {
            _logger.LogCritical(
                "Package store stage {StageId} has an indeterminate durable outcome; further Runtime operations are fenced",
                stageId);
        }
        else
        {
            _logger.LogCritical(
                exception,
                "Package store stage {StageId} failed with an indeterminate durable outcome; further Runtime operations are fenced",
                stageId);
        }

        var warnings = (storeResult?.Warnings ?? stage.StoreResult.Warnings)
            .Append("Transaction evidence was preserved for startup recovery.")
            .ToArray();
        return PackageOperationResults.Failure(message, warnings: warnings) with
        {
            ImpactedPackageIds = stage.StoreResult.ImpactedPackageIds,
            ChangeSet = stage.StoreResult.ChangeSet,
            RuntimeSessionReconciliationPending = true,
        };
    }

    public async Task<bool> DiscardStageAsync(string stageId, CancellationToken cancellationToken = default)
    {
        await using var operation = await _gate.EnterAsync(cancellationToken);
        operation.CancellationToken.ThrowIfCancellationRequested();
        await SweepStagesCoreAsync(_timeProvider.GetUtcNow());
        if (!_stages.Remove(stageId, out var stage)) return false;
        var discarded = false;
        try
        {
            if (stage.Candidate is not null)
            {
                await _publisher.DiscardAsync(stage.Candidate);
            }
            else
            {
                _sessions.RemoveStage(stageId);
            }
        }
        finally
        {
            discarded = await _storeCoordinator.DiscardStageAsync(stageId);
        }
        if (discarded)
        {
            _sessions.MarkStageDiscarded(stageId);
        }
        return discarded;
    }

    internal async Task<PackageOperationResult> ExecutePreparedMutationsAsync(
        IReadOnlyList<PackageStoreMutation> mutations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        await using (var operation = await _gate.EnterAsync(cancellationToken))
        {
            var operationToken = operation.CancellationToken;
            var baseSessionGeneration = _sessions.Generation;
            var currentPackages = _sessions.State.GetActivePackages();
            var sources = _sessions.Sources.Snapshot();
            var localBindingAuthority = await CaptureLocalBindingAuthorityAsync(
                    mutations,
                    operationToken)
                .ConfigureAwait(false);
            var preparation = await _storeCoordinator.PrepareStageAsync(
                mutations,
                localBindingAuthority,
                operationToken);
            var stage = await PrepareCandidateAsync(
                preparation,
                baseSessionGeneration,
                currentPackages,
                sources,
                operationToken);
            return !stage.Success || stage.StageId is null
                ? stage.OperationResult
                : await CommitStageCoreAsync(
                    stage.StageId,
                    authorityConfirmation: null,
                    operationToken: operationToken);
        }
    }

    private List<PackageStoreChange> ResolveMutations(
        IReadOnlyList<PackageStoreMutationRequest> requests,
        ICollection<RuntimeUploadLease> leases,
        IReadOnlyDictionary<string, RegistryPackageUploadMetadata>? registryUploadMetadataById,
        CancellationToken cancellationToken)
    {
        var mutations = new List<PackageStoreChange>(requests.Count);
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? archivePath = null;
            if (request.Kind is PackageStoreMutationKind.Install or PackageStoreMutationKind.Upgrade)
            {
                if (string.IsNullOrWhiteSpace(request.UploadId)) throw new InvalidDataException("An upload id is required for package install and upgrade mutations.");
                var lease = _transfers.AcquireUpload(request.UploadId, RuntimeUploadKind.Package, _sessions.Generation, consume: true)
                    ?? throw new InvalidDataException("The package upload was not found or belongs to a stale Runtime generation.");
                leases.Add(lease);
                archivePath = lease.FilePath;
                var registryMetadata = registryUploadMetadataById is null
                    ? null
                    : registryUploadMetadataById.TryGetValue(request.UploadId, out var metadata)
                        ? metadata
                        : throw new InvalidDataException(
                            $"Registry metadata is missing for package upload '{request.UploadId}'.");
                var provenance = registryMetadata?.Provenance
                    ?? InstalledPackageProvenanceRecord.LocalArchive(lease.ContentHash);
                mutations.Add(new PackageStoreMutation(
                    request.Kind,
                    request.PackageId,
                    archivePath,
                    request.AllowDowngrade,
                    request.Reinstall,
                    provenance,
                    request.AllowCascade,
                    request.ConfirmationToken,
                    request.CapabilityConsent,
                    registryMetadata?.ConsentManifestSha256,
                    registryMetadata?.ExecutionRuntimeBindings)
                {
                    ResolutionPolicy = registryMetadata?.ResolutionPolicy,
                    RegistryProjectionTargets = registryMetadata?.AcquiredTargets,
                });
                continue;
            }
            mutations.Add(new PackageStoreMutation(
                request.Kind,
                request.PackageId,
                archivePath,
                request.AllowDowngrade,
                request.Reinstall,
                AllowCascade: request.AllowCascade,
                ConfirmationToken: request.ConfirmationToken,
                CapabilityConsent: request.CapabilityConsent));
        }
        return mutations;
    }

    private async Task<LocalExecutionRuntimeBindingSnapshot?> CaptureLocalBindingAuthorityAsync(
        IReadOnlyList<PackageStoreChange> changes,
        CancellationToken cancellationToken)
    {
        if (_localRuntimeBindings is null
            || !changes.OfType<PackageStoreMutation>().Any(static mutation =>
                mutation.Kind is PackageStoreMutationKind.Install or PackageStoreMutationKind.Upgrade
                && mutation.ExecutionRuntimeBindings is null))
        {
            return null;
        }
        return await _localRuntimeBindings.CaptureAsync(cancellationToken).ConfigureAwait(false);
    }

    internal ActivePackageSession? GetStagedSession(string stageId)
        => _stages.TryGetValue(stageId, out var stage) ? stage.Candidate?.Session : null;

    private void ResolvePendingReconciliations(RuntimePackageStamp stamp)
    {
        foreach (var stageId in _reconciliationPendingStages)
        {
            _sessions.MarkStageCommitted(
                stageId,
                stamp,
                runtimeSessionApplied: true,
                reconciliationPending: false,
                "The committed package store stage was reconciled to the Runtime session.");
        }
        _reconciliationPendingStages.Clear();
    }

    private sealed record PendingStoreStage(
        PreparedPackageSession? Candidate,
        PackageOperationResult StoreResult,
        long BaseCatalogGeneration,
        long BaseSessionGeneration,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset ExpiresAtUtc,
        LocalExecutionRuntimePolicyAuthority? LocalRuntimePolicyAuthority);
}

internal enum InstalledPackageLifecycleFaultPoint
{
    PreLaunchPolicyConfirmedBeforeCandidateStart,
    CandidateReadyBeforeStoreCommit,
    PreCommitPolicyConfirmedBeforeStoreCommit,
    StoreCommittedBeforeSessionPublication,
    CandidateGenerationCommittedBeforePreviousWorkersQuiesced,
    PreviousWorkersQuiescedBeforeStorageActivation,
}

internal interface IInstalledPackageLifecycleFaultInjector
{
    void Hit(InstalledPackageLifecycleFaultPoint point);
}
