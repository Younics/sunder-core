using System.Collections.ObjectModel;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Packaging;

namespace Sunder.Runtime.Host.Services;

internal sealed class PreparedExecutionRuntimeBindings(
    IReadOnlyDictionary<string, IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord>> bindings,
    IReadOnlyList<IAsyncDisposable> retentions,
    ExecutionRuntimeLifecyclePolicyCache? lifecyclePolicies,
    IReadOnlyList<ExecutionRuntimeLifecyclePolicyObservation> lifecycleObservations,
    PreparedExecutionRuntimeLifecyclePolicyUpdate? lifecyclePolicyUpdate) : IAsyncDisposable
{
    private IAsyncDisposable[]? _retentions = retentions.ToArray();

    public IReadOnlyDictionary<string, IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord>> Bindings { get; }
        = bindings;

    public IReadOnlyList<ExecutionRuntimeLifecyclePolicyEntryV1> LifecyclePolicyEntries { get; }
        = lifecyclePolicyUpdate?.Entries ?? [];

    public ExecutionRuntimeLifecyclePolicySnapshot? LifecyclePolicyAuthority { get; }
        = lifecyclePolicyUpdate?.Authority;

    public Task ObserveLifecyclePoliciesAsync(CancellationToken cancellationToken)
        => lifecyclePolicies is null
            ? Task.CompletedTask
            : lifecyclePolicyUpdate is null
                ? lifecyclePolicies.ObserveAsync(lifecycleObservations, cancellationToken)
                : lifecyclePolicies.ApplyEntriesAsync(lifecyclePolicyUpdate.Entries, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        var retained = Interlocked.Exchange(ref _retentions, null);
        if (retained is null)
        {
            return;
        }
        for (var index = retained.Length - 1; index >= 0; index--)
        {
            await retained[index].DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal sealed class RegistryExecutionRuntimeBindingPreparer
{
    private readonly Func<
        Uri,
        RegistryResolvedExecutionRuntimeArtifact,
        IReadOnlyList<string>,
        CancellationToken,
        Task<ExecutionRuntimeEnsureResult>> _ensureArtifact;
    private readonly IExecutionRuntimeRetentionManager _retentionManager;
    private readonly TimeProvider _timeProvider;
    private readonly ExecutionRuntimeLifecyclePolicyCache? _lifecyclePolicies;
    private readonly ExecutionRuntimePhaseTelemetry _telemetry;

    public RegistryExecutionRuntimeBindingPreparer(
        ExecutionRuntimeArtifactDownloader artifactDownloader,
        IExecutionRuntimeRetentionManager retentionManager,
        TimeProvider timeProvider,
        ExecutionRuntimeLifecyclePolicyCache lifecyclePolicies,
        ExecutionRuntimePhaseTelemetry? telemetry = null)
        : this(
            (artifactDownloader ?? throw new ArgumentNullException(nameof(artifactDownloader))).DownloadAsync,
            retentionManager,
            timeProvider,
            lifecyclePolicies,
            telemetry)
    {
    }

    internal RegistryExecutionRuntimeBindingPreparer(
        Func<
            Uri,
            RegistryResolvedExecutionRuntimeArtifact,
            IReadOnlyList<string>,
            CancellationToken,
        Task<ExecutionRuntimeEnsureResult>> ensureArtifact,
        IExecutionRuntimeRetentionManager retentionManager,
        TimeProvider timeProvider,
        ExecutionRuntimeLifecyclePolicyCache? lifecyclePolicies = null,
        ExecutionRuntimePhaseTelemetry? telemetry = null)
    {
        _ensureArtifact = ensureArtifact ?? throw new ArgumentNullException(nameof(ensureArtifact));
        _retentionManager = retentionManager ?? throw new ArgumentNullException(nameof(retentionManager));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _lifecyclePolicies = lifecyclePolicies;
        _telemetry = telemetry ?? ExecutionRuntimePhaseTelemetry.Disabled;
    }

    public async Task<PreparedExecutionRuntimeBindings> PrepareAsync(
        IReadOnlyList<RegistryPackagePlanResolution> planContexts,
        IReadOnlyDictionary<string, InstalledPackageRecord> initiallyInstalledPackages,
        CancellationToken cancellationToken)
    {
        using var operation = _telemetry.BeginOperation();
        ArgumentNullException.ThrowIfNull(planContexts);
        ArgumentNullException.ThrowIfNull(initiallyInstalledPackages);

        var planPhase = _telemetry.BeginPhase(ExecutionRuntimePhaseNames.Plan);
        Dictionary<RuntimeIdentityKey, ArtifactAcquisition> artifacts;
        List<ValidatedItem> validatedItems;
        try
        {
        var installed = CaptureInstalledCatalog(initiallyInstalledPackages);
        var acceptedPlans = new List<AcceptedPlan>(planContexts.Count);
        foreach (var context in planContexts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context is null || context.Plan is null)
            {
                throw InvalidPlan("a resolved plan context is missing");
            }
            if (context.Origin is null || !context.Origin.IsAbsoluteUri)
            {
                throw InvalidPlan("a resolved plan has an invalid Registry origin");
            }

            Uri origin;
            try
            {
                origin = RegistryOrigin.Normalize(context.Origin.AbsoluteUri);
            }
            catch (ArgumentException exception)
            {
                throw InvalidPlan("a resolved plan has an invalid Registry origin", exception);
            }

            var observedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
            ValidatePlanHeader(context.Plan, observedAtUtc);
            acceptedPlans.Add(new AcceptedPlan(origin, context.Plan, observedAtUtc));
        }

        var currentRid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        artifacts = new Dictionary<RuntimeIdentityKey, ArtifactAcquisition>();
        validatedItems = new List<ValidatedItem>();
        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var installedProjections = new Dictionary<string, InstalledRuntimeProjection>(StringComparer.OrdinalIgnoreCase);

        foreach (var accepted in acceptedPlans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var planArtifacts = ValidateArtifacts(accepted, artifacts);
            var referencedArtifacts = new HashSet<RuntimeIdentityKey>();

            foreach (var item in accepted.Plan.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item is null
                    || !PackageId.TryParse(item.PackageId, out _)
                    || !packageIds.Add(item.PackageId))
                {
                    throw InvalidPlan("plan items must identify unique canonical packages across the transaction");
                }

                var targets = ValidateTargets(item);
                var bindings = ValidateBindings(
                    item,
                    targets,
                    planArtifacts,
                    referencedArtifacts,
                    currentRid,
                    accepted);
                await ValidateActionShapeAsync(
                        item,
                        bindings,
                        installed,
                        installedProjections,
                        cancellationToken)
                    .ConfigureAwait(false);
                validatedItems.Add(new ValidatedItem(item.PackageId, bindings));
            }

            if (planArtifacts.Keys.Any(key => !referencedArtifacts.Contains(key)))
            {
                throw InvalidPlan("a plan contains an unreferenced execution-runtime artifact");
            }
        }

        SetSingleIdentity(planPhase, artifacts);
        planPhase.Succeeded();
        }
        catch (OperationCanceledException)
        {
            planPhase.Canceled();
            throw;
        }
        catch (Exception exception)
        {
            planPhase.Failed(exception);
            throw;
        }
        finally
        {
            planPhase.Dispose();
        }

        using var bindPhase = _telemetry.BeginPhase(ExecutionRuntimePhaseNames.Bind);
        SetSingleIdentity(bindPhase, artifacts);
        var retentions = new List<IAsyncDisposable>(artifacts.Count);
        try
        {
            foreach (var acquisition in artifacts.Values)
            {
                var identity = acquisition.Artifact.Identity;
                retentions.Add(await _retentionManager.RetainAsync(
                        new ExecutionRuntimeRetention(
                            new ExecutionRuntimeIdentity(
                                identity.RuntimeId,
                                identity.Version,
                                identity.PackagingRevision,
                                identity.Rid,
                                identity.ModuleAbi),
                            ArtifactSha256: acquisition.Artifact.ArtifactSha256,
                            ArtifactSize: acquisition.Artifact.ArtifactSize,
                            ManifestSha256: acquisition.Artifact.ManifestSha256),
                        cancellationToken)
                    .ConfigureAwait(false));
            }

            var ensured = new Dictionary<RuntimeIdentityKey, EnsuredArtifact>(artifacts.Count);
            foreach (var (key, acquisition) in artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await _ensureArtifact(
                        acquisition.Origin,
                        acquisition.Artifact,
                        acquisition.TrustedArtifactOrigins,
                        cancellationToken)
                    .ConfigureAwait(false);
                var resolvedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
                ValidateReceipt(acquisition, result, resolvedAtUtc);
                ensured.Add(key, new EnsuredArtifact(result.Receipt, resolvedAtUtc));
            }
            var lifecycleObservations = artifacts.Select(item =>
            {
                var ensuredArtifact = ensured[item.Key];
                return new ExecutionRuntimeLifecyclePolicyObservation(
                    ensuredArtifact.Receipt,
                    item.Value.Origin.AbsoluteUri,
                    item.Value.Artifact.Lifecycle,
                    item.Value.Artifact.ObservedRuntimePolicyRevision,
                    item.Value.Artifact.MinimumHostTrustGeneration,
                    ensuredArtifact.ResolvedAtUtc);
            }).ToArray();
            var lifecyclePolicyUpdate = _lifecyclePolicies is null
                ? null
                : await _lifecyclePolicies.PrepareUpdateAsync(lifecycleObservations, cancellationToken)
                    .ConfigureAwait(false);

            var resultByPackageId = new Dictionary<string, IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord>>(
                validatedItems.Count,
                StringComparer.OrdinalIgnoreCase);
            foreach (var item in validatedItems)
            {
                var records = item.Bindings.Select(binding =>
                {
                    var ensuredArtifact = ensured[binding.ArtifactKey];
                    if (ensuredArtifact.ResolvedAtUtc < binding.Plan.ObservedAtUtc)
                    {
                        throw InvalidPlan(
                            "the Host clock moved backwards while resolving an execution-runtime artifact");
                    }
                    var receipt = ensuredArtifact.Receipt;
                    return new InstalledPackageExecutionRuntimeBindingRecord(
                        binding.Binding.Role,
                        binding.Binding.Rid,
                        receipt.Identity.RuntimeId,
                        receipt.Identity.Version,
                        receipt.Identity.PackagingRevision,
                        receipt.Identity.ModuleAbi,
                        receipt.ManifestSha256,
                        receipt.ArtifactSha256,
                        receipt.ArtifactSize,
                        binding.Plan.Origin.AbsoluteUri,
                        receipt.AcquisitionSource,
                        receipt.SigningKeyId,
                        receipt.TrustGeneration,
                        binding.Plan.Plan.RuntimePolicyRevision,
                        binding.Artifact.Lifecycle.State,
                        binding.Plan.ObservedAtUtc,
                        ensuredArtifact.ResolvedAtUtc);
                }).ToArray();
                resultByPackageId.Add(item.PackageId, Array.AsReadOnly(records));
            }

            var preparedBindings = new PreparedExecutionRuntimeBindings(
                new ReadOnlyDictionary<string, IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord>>(
                    resultByPackageId),
                retentions,
                _lifecyclePolicies,
                Array.AsReadOnly(lifecycleObservations),
                lifecyclePolicyUpdate);
            bindPhase.Succeeded();
            return preparedBindings;
        }
        catch (Exception exception)
        {
            if (exception is OperationCanceledException)
            {
                bindPhase.Canceled();
            }
            else
            {
                bindPhase.Failed(exception);
            }
            for (var index = retentions.Count - 1; index >= 0; index--)
            {
                await retentions[index].DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    private static void SetSingleIdentity(
        ExecutionRuntimePhaseTelemetry.PhaseScope phase,
        IReadOnlyDictionary<RuntimeIdentityKey, ArtifactAcquisition> artifacts)
    {
        if (artifacts.Count != 1)
        {
            return;
        }
        var identity = artifacts.Values.Single().Artifact.Identity;
        phase.SetIdentity(new ExecutionRuntimeIdentity(
            identity.RuntimeId,
            identity.Version,
            identity.PackagingRevision,
            identity.Rid,
            identity.ModuleAbi));
    }

    private static Dictionary<string, InstalledPackageRecord> CaptureInstalledCatalog(
        IReadOnlyDictionary<string, InstalledPackageRecord> initiallyInstalledPackages)
    {
        var installed = new Dictionary<string, InstalledPackageRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var (packageId, package) in initiallyInstalledPackages)
        {
            if (package is null
                || !string.Equals(packageId, package.PackageId, StringComparison.Ordinal)
                || !installed.TryAdd(package.PackageId, package))
            {
                throw InvalidPlan("the initially captured installed package catalog is inconsistent");
            }
        }
        return installed;
    }

    private static void ValidatePlanHeader(
        RegistryResolveInstallPlanResponse plan,
        DateTimeOffset observedAtUtc)
    {
        if (!plan.Success
            || plan.Errors is null
            || plan.Errors.Count != 0
            || plan.Conflicts is null
            || plan.Conflicts.Count != 0)
        {
            throw InvalidPlan("only a successful conflict-free Registry plan can authorize execution runtimes");
        }
        if (plan.Items is null
            || plan.RuntimeArtifacts is null
            || plan.RuntimeConflicts is null
            || plan.TrustedArtifactOrigins is null)
        {
            throw InvalidPlan("a successful plan has an incomplete execution-runtime inventory");
        }
        if (plan.RuntimeConflicts.Count != 0)
        {
            throw InvalidPlan("a successful plan contains execution-runtime conflicts");
        }
        if (plan.RuntimePolicyRevision <= 0)
        {
            throw InvalidPlan("the observed execution-runtime policy revision must be positive");
        }
        if (string.IsNullOrWhiteSpace(plan.ResolutionFingerprint))
        {
            throw InvalidPlan("the execution-runtime resolution fingerprint is required");
        }
        if (plan.ExpiresAtUtc == default || plan.ExpiresAtUtc <= observedAtUtc)
        {
            throw InvalidPlan("the execution-runtime resolution has expired");
        }
    }

    private static Dictionary<RuntimeIdentityKey, RegistryResolvedExecutionRuntimeArtifact> ValidateArtifacts(
        AcceptedPlan accepted,
        IDictionary<RuntimeIdentityKey, ArtifactAcquisition> allArtifacts)
    {
        var planArtifacts = new Dictionary<RuntimeIdentityKey, RegistryResolvedExecutionRuntimeArtifact>();
        foreach (var artifact in accepted.Plan.RuntimeArtifacts)
        {
            if (artifact is null || artifact.Identity is null || artifact.Lifecycle is null)
            {
                throw InvalidPlan("an execution-runtime artifact descriptor is incomplete");
            }
            if (artifact.ObservedRuntimePolicyRevision != accepted.Plan.RuntimePolicyRevision)
            {
                throw InvalidPlan("an execution-runtime artifact was observed under a different policy revision");
            }

            var key = RuntimeIdentityKey.From(artifact.Identity);
            if (planArtifacts.TryGetValue(key, out var duplicate)
                && !ArtifactsEquivalent(duplicate, artifact))
            {
                throw InvalidPlan("one exact execution-runtime identity has conflicting artifact descriptors");
            }
            planArtifacts.TryAdd(key, artifact);

            if (allArtifacts.TryGetValue(key, out var existing))
            {
                if (!string.Equals(
                        existing.Origin.AbsoluteUri,
                        accepted.Origin.AbsoluteUri,
                        StringComparison.Ordinal)
                    || !ArtifactsEquivalent(existing.Artifact, artifact))
                {
                    throw InvalidPlan(
                        "one exact execution-runtime identity was resolved from different Registry origins or with conflicting artifact bytes");
                }
                continue;
            }

            allArtifacts.Add(
                key,
                new ArtifactAcquisition(
                    accepted.Origin,
                    artifact,
                    Array.AsReadOnly(accepted.Plan.TrustedArtifactOrigins.ToArray())));
        }
        return planArtifacts;
    }

    private static Dictionary<(string Role, string Rid), RegistryPackageTarget> ValidateTargets(
        RegistryPackageInstallPlanItem item)
    {
        if (item.Targets is null || item.Targets.Count > SunderPackageFormat.MaxTargets)
        {
            throw InvalidPlan($"package '{item.PackageId}' has an invalid target inventory");
        }

        var targets = new Dictionary<(string Role, string Rid), RegistryPackageTarget>();
        foreach (var target in item.Targets)
        {
            if (target is null
                || !SunderPackageFormat.IsHostRole(target.Role)
                || !SunderPackageFormat.IsRuntimeIdentifier(target.Rid)
                || !targets.TryAdd((target.Role, target.Rid), target))
            {
                throw InvalidPlan($"package '{item.PackageId}' has an invalid or duplicate target");
            }

            var isWorker = string.Equals(target.Role, SunderPackageFormat.RuntimeHostRole, StringComparison.Ordinal)
                           && string.Equals(target.Kind, SunderPackageFormat.WorkerTargetKind, StringComparison.Ordinal);
            if (isWorker != (target.ExecutionRuntime is not null))
            {
                throw InvalidPlan(
                    $"package '{item.PackageId}' target '{target.Role}/{target.Rid}' has an invalid execution-runtime declaration");
            }
            if (target.ExecutionRuntime is { } requirement
                && (!IsExecutionRuntimePair(requirement.RuntimeId, requirement.ModuleAbi)
                    || !PackageVersionRange.TryParse(requirement.VersionRange, out _)))
            {
                throw InvalidPlan(
                    $"package '{item.PackageId}' target '{target.Role}/{target.Rid}' has an invalid execution-runtime requirement");
            }
        }
        return targets;
    }

    private static IReadOnlyList<ValidatedBinding> ValidateBindings(
        RegistryPackageInstallPlanItem item,
        IReadOnlyDictionary<(string Role, string Rid), RegistryPackageTarget> targets,
        IReadOnlyDictionary<RuntimeIdentityKey, RegistryResolvedExecutionRuntimeArtifact> artifacts,
        ISet<RuntimeIdentityKey> referencedArtifacts,
        string currentRid,
        AcceptedPlan accepted)
    {
        if (item.RuntimeBindings is null || item.RuntimeBindings.Count > SunderPackageFormat.MaxTargets)
        {
            throw InvalidPlan($"package '{item.PackageId}' has an invalid execution-runtime binding inventory");
        }
        var orderedBindings = item.RuntimeBindings
            .OrderBy(static binding => binding.Role, StringComparer.Ordinal)
            .ThenBy(static binding => binding.Rid, StringComparer.Ordinal)
            .ToArray();
        if (!item.RuntimeBindings.SequenceEqual(orderedBindings))
        {
            throw InvalidPlan(
                $"package '{item.PackageId}' execution-runtime bindings are not in canonical role/RID order");
        }

        var keys = new HashSet<(string Role, string Rid)>();
        var result = new List<ValidatedBinding>(item.RuntimeBindings.Count);
        foreach (var binding in item.RuntimeBindings)
        {
            if (binding is null
                || binding.Requirement is null
                || binding.Runtime is null
                || !keys.Add((binding.Role, binding.Rid))
                || !string.Equals(binding.Role, SunderPackageFormat.RuntimeHostRole, StringComparison.Ordinal)
                || !string.Equals(binding.Rid, currentRid, StringComparison.Ordinal)
                || !targets.TryGetValue((binding.Role, binding.Rid), out var target)
                || target.ExecutionRuntime is null
                || target.ExecutionRuntime != binding.Requirement)
            {
                throw InvalidPlan(
                    $"package '{item.PackageId}' has a duplicate or non-current execution-runtime binding that does not match one exact target declaration");
            }

            var requirement = binding.Requirement;
            var identity = binding.Runtime;
            if (!IsExecutionRuntimePair(requirement.RuntimeId, requirement.ModuleAbi)
                || !string.Equals(identity.RuntimeId, requirement.RuntimeId, StringComparison.Ordinal)
                || !string.Equals(identity.ModuleAbi, requirement.ModuleAbi, StringComparison.Ordinal)
                || !string.Equals(identity.Rid, binding.Rid, StringComparison.Ordinal)
                || !SemanticVersion.TryParse(identity.Version, out var version)
                || version.Prerelease is not null
                || identity.PackagingRevision is <= 0 or > SunderExecutionRuntimeFormat.MaxPackagingRevision
                || !PackageVersionRange.IsSatisfiedBy(identity.Version, requirement.VersionRange))
            {
                throw InvalidPlan(
                    $"package '{item.PackageId}' selected an execution runtime that does not satisfy its exact target requirement, RID, or module ABI");
            }

            var artifactKey = RuntimeIdentityKey.From(identity);
            if (!artifacts.TryGetValue(artifactKey, out var artifact))
            {
                throw InvalidPlan(
                    $"package '{item.PackageId}' references a missing exact execution-runtime artifact");
            }
            referencedArtifacts.Add(artifactKey);
            result.Add(new ValidatedBinding(binding, artifactKey, artifact, accepted));
        }
        var expectedKeys = targets.Values
            .Where(target => target.ExecutionRuntime is not null
                             && string.Equals(target.Rid, currentRid, StringComparison.Ordinal))
            .Select(target => (target.Role, target.Rid))
            .ToHashSet();
        if (!keys.SetEquals(expectedKeys))
        {
            throw InvalidPlan(
                $"package '{item.PackageId}' does not bind every current-RID execution-runtime target exactly once");
        }
        return result;
    }

    private static async Task ValidateActionShapeAsync(
        RegistryPackageInstallPlanItem item,
        IReadOnlyList<ValidatedBinding> bindings,
        IReadOnlyDictionary<string, InstalledPackageRecord> installed,
        IDictionary<string, InstalledRuntimeProjection> installedProjections,
        CancellationToken cancellationToken)
    {
        if (item.Artifacts is null || item.Artifacts.Any(static artifact => artifact is null))
        {
            throw InvalidPlan($"package '{item.PackageId}' has an invalid package artifact inventory");
        }
        if (!PackageContentSignatureValidator.IsLowercaseSha256(item.PackageContentIdentity))
        {
            throw InvalidPlan($"package '{item.PackageId}' has an invalid package content identity");
        }

        installed.TryGetValue(item.PackageId, out var current);
        switch (item.ActionKind)
        {
            case RegistryPackageActionKinds.PackageInstall:
                if (current is not null
                    || item.CurrentVersion is not null
                    || !IsPackageVersion(item.Version)
                    || item.Artifacts.Count == 0
                    || item.RuntimeOnlyRebind is not null)
                {
                    throw InvalidAction(item);
                }
                break;

            case RegistryPackageActionKinds.PackageUpdate:
                if (current is null
                    || !string.Equals(item.CurrentVersion, current.Version, StringComparison.Ordinal)
                    || !IsPackageVersion(item.Version)
                    || item.Artifacts.Count == 0
                    || item.RuntimeOnlyRebind is not null)
                {
                    throw InvalidAction(item);
                }
                break;

            case RegistryPackageActionKinds.Keep:
            {
                if (current is null
                    || !string.Equals(item.CurrentVersion, current.Version, StringComparison.Ordinal)
                    || !string.Equals(item.Version, current.Version, StringComparison.Ordinal)
                    || !MatchesInstalledRegistryIdentity(item, current)
                    || item.Artifacts.Count != 0
                    || item.RuntimeOnlyRebind is not null)
                {
                    throw InvalidAction(item);
                }

                var projected = await GetInstalledProjectionAsync(
                        current,
                        installedProjections,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!item.RuntimeBindings.SequenceEqual(projected.Bindings))
                {
                    throw InvalidPlan(
                        $"keep action for package '{item.PackageId}' does not preserve its current manifest-projected execution-runtime bindings");
                }
                ValidateBindingsAgainstInstalledManifest(item, bindings, projected.Bindings);
                break;
            }

            case RegistryPackageActionKinds.RuntimeOnlyRebind:
            {
                if (current is null
                    || !string.Equals(item.CurrentVersion, current.Version, StringComparison.Ordinal)
                    || !string.Equals(item.Version, current.Version, StringComparison.Ordinal)
                    || !MatchesInstalledRegistryIdentity(item, current)
                    || item.Artifacts.Count != 0
                    || item.RuntimeOnlyRebind is null)
                {
                    throw InvalidAction(item);
                }

                var projected = await GetInstalledProjectionAsync(
                        current,
                        installedProjections,
                        cancellationToken)
                    .ConfigureAwait(false);
                var rebind = item.RuntimeOnlyRebind;
                if (rebind.PreviousBindings is null
                    || rebind.NewBindings is null
                    || !string.Equals(rebind.PackageVersion, current.Version, StringComparison.Ordinal)
                    || !string.Equals(rebind.PackageContentIdentity, item.PackageContentIdentity, StringComparison.Ordinal)
                    || !rebind.PreviousBindings.SequenceEqual(projected.Bindings)
                    || !rebind.NewBindings.SequenceEqual(item.RuntimeBindings)
                    || rebind.PreviousBindings.SequenceEqual(rebind.NewBindings))
                {
                    throw InvalidPlan(
                        $"runtime-only-rebind details for package '{item.PackageId}' do not exactly match its current package identity and binding transition");
                }
                ValidateBindingsAgainstInstalledManifest(item, bindings, projected.Bindings);
                break;
            }

            case RegistryPackageActionKinds.PackageRemove:
                if (current is null
                    || !string.Equals(item.CurrentVersion, current.Version, StringComparison.Ordinal)
                    || item.Version is not null
                    || item.Targets.Count != 0
                    || item.Artifacts.Count != 0
                    || item.RuntimeBindings.Count != 0
                    || item.RuntimeOnlyRebind is not null)
                {
                    throw InvalidAction(item);
                }
                break;

            default:
                throw InvalidAction(item);
        }
    }

    private static bool MatchesInstalledRegistryIdentity(
        RegistryPackageInstallPlanItem item,
        InstalledPackageRecord package)
        => package.Provenance.SourceKind == InstalledPackageSourceKind.Registry
           && string.Equals(
               item.PackageContentIdentity,
               package.Provenance.SourceIdentity,
               StringComparison.Ordinal);

    private static async Task<InstalledRuntimeProjection> GetInstalledProjectionAsync(
        InstalledPackageRecord package,
        IDictionary<string, InstalledRuntimeProjection> cache,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(package.PackageId, out var cached))
        {
            return cached;
        }

        var validation = await InstalledPackageProjection.ValidateAsync(package, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.Success
            || validation.Manifest is null
            || validation.ContentIndex is null
            || !string.Equals(validation.Manifest.Id, package.PackageId, StringComparison.Ordinal)
            || !string.Equals(validation.Manifest.Version, package.Version, StringComparison.Ordinal)
            || !PackageSessionPreparer.InventoryMatches(validation.ContentIndex, package.ContentInventory)
            || !string.Equals(
                PackageSessionPreparer.ComputeContentIdentity(package.InstallPath),
                package.ContentIdentity,
                StringComparison.Ordinal))
        {
            throw InvalidPlan(
                $"installed package '{package.PackageId}' cannot authorize a keep or runtime rebind because its strict manifest is invalid");
        }

        var projected = new InstalledRuntimeProjection(
            RegistryPackagePlanResolver.ProjectRuntimeBindings(package, validation.Manifest));
        cache.Add(package.PackageId, projected);
        return projected;
    }

    private static void ValidateBindingsAgainstInstalledManifest(
        RegistryPackageInstallPlanItem item,
        IReadOnlyList<ValidatedBinding> bindings,
        IReadOnlyList<RegistryPackageTargetRuntimeBinding> projected)
    {
        if (bindings.Count != projected.Count)
        {
            throw InvalidPlan(
                $"action '{item.ActionKind}' for package '{item.PackageId}' does not bind every current manifest-projected execution-runtime target");
        }

        for (var index = 0; index < bindings.Count; index++)
        {
            var planned = bindings[index].Binding;
            var current = projected[index];
            if (!string.Equals(planned.Role, current.Role, StringComparison.Ordinal)
                || !string.Equals(planned.Rid, current.Rid, StringComparison.Ordinal)
                || planned.Requirement != current.Requirement)
            {
                throw InvalidPlan(
                    $"action '{item.ActionKind}' for package '{item.PackageId}' does not match its current strict manifest execution-runtime requirements");
            }
        }
    }

    private static void ValidateReceipt(
        ArtifactAcquisition acquisition,
        ExecutionRuntimeEnsureResult result,
        DateTimeOffset resolvedAtUtc)
    {
        if (result is null || result.Receipt is null)
        {
            throw InvalidPlan("execution-runtime acquisition did not return an immutable receipt");
        }

        var artifact = acquisition.Artifact;
        var receipt = result.Receipt;
        var hasRegistryProvenance = receipt.TrustEnvironment == ExecutionRuntimeTrustEnvironment.Production
                                    && receipt.AcquisitionSource
                                    == RuntimeExecutionRuntimeAcquisitionSources.Registry
                                    && string.Equals(
                                        receipt.RegistryOrigin,
                                        acquisition.Origin.AbsoluteUri,
                                        StringComparison.Ordinal);
        var hasLocalImportProvenance = receipt.TrustEnvironment == ExecutionRuntimeTrustEnvironment.Production
                                       && receipt.AcquisitionSource
                                       == RuntimeExecutionRuntimeAcquisitionSources.LocalImport
                                       && receipt.RegistryOrigin is null;
        var hasDevelopmentProvenance = receipt.TrustEnvironment == ExecutionRuntimeTrustEnvironment.Development
                                       && receipt.AcquisitionSource
                                       == RuntimeExecutionRuntimeAcquisitionSources.Development
                                       && string.Equals(
                                           receipt.RegistryOrigin,
                                           acquisition.Origin.AbsoluteUri,
                                           StringComparison.Ordinal);
        if (!RuntimeIdentitiesEqual(receipt.Identity, artifact.Identity)
            || !string.Equals(receipt.ManifestSha256, artifact.ManifestSha256, StringComparison.Ordinal)
            || !string.Equals(receipt.ArtifactSha256, artifact.ArtifactSha256, StringComparison.Ordinal)
            || receipt.ArtifactSize != artifact.ArtifactSize
            || !string.Equals(receipt.SigningKeyId, artifact.SigningKeyId, StringComparison.Ordinal)
            || !string.Equals(
                receipt.NativeAttestationSha256,
                artifact.NativeAttestationSha256,
                StringComparison.Ordinal)
            || !string.Equals(
                receipt.NativeAttestationKeyId,
                artifact.NativeAttestationKeyId,
                StringComparison.Ordinal)
            || receipt.TrustGeneration < artifact.MinimumHostTrustGeneration
            || !hasRegistryProvenance && !hasLocalImportProvenance && !hasDevelopmentProvenance
            || resolvedAtUtc == default)
        {
            throw InvalidPlan("the ensured execution-runtime receipt does not match its exact Registry artifact");
        }
    }

    private static bool ArtifactsEquivalent(
        RegistryResolvedExecutionRuntimeArtifact left,
        RegistryResolvedExecutionRuntimeArtifact right)
        => RuntimeIdentitiesEqual(left.Identity, right.Identity)
           && string.Equals(left.ManifestSha256, right.ManifestSha256, StringComparison.Ordinal)
           && string.Equals(left.ArtifactSha256, right.ArtifactSha256, StringComparison.Ordinal)
           && left.ArtifactSize == right.ArtifactSize
           && string.Equals(left.SigningKeyId, right.SigningKeyId, StringComparison.Ordinal)
           && left.MinimumHostTrustGeneration == right.MinimumHostTrustGeneration
           && string.Equals(
               left.NativeAttestationSha256,
               right.NativeAttestationSha256,
               StringComparison.Ordinal)
           && string.Equals(left.NativeAttestationKeyId, right.NativeAttestationKeyId, StringComparison.Ordinal)
           && LifecyclesEquivalent(left.Lifecycle, right.Lifecycle)
           && left.ObservedRuntimePolicyRevision == right.ObservedRuntimePolicyRevision
           && string.Equals(left.DownloadUrl, right.DownloadUrl, StringComparison.Ordinal);

    private static bool RuntimeIdentitiesEqual(
        RegistryExecutionRuntimeIdentity left,
        RegistryExecutionRuntimeIdentity right)
        => left is not null
           && right is not null
           && string.Equals(left.RuntimeId, right.RuntimeId, StringComparison.Ordinal)
           && string.Equals(left.Version, right.Version, StringComparison.Ordinal)
           && left.PackagingRevision == right.PackagingRevision
           && string.Equals(left.Rid, right.Rid, StringComparison.Ordinal)
           && string.Equals(left.ModuleAbi, right.ModuleAbi, StringComparison.Ordinal);

    private static bool RuntimeIdentitiesEqual(
        ExecutionRuntimeIdentity left,
        RegistryExecutionRuntimeIdentity right)
        => left is not null
           && right is not null
           && string.Equals(left.RuntimeId, right.RuntimeId, StringComparison.Ordinal)
           && string.Equals(left.Version, right.Version, StringComparison.Ordinal)
           && left.PackagingRevision == right.PackagingRevision
           && string.Equals(left.Rid, right.Rid, StringComparison.Ordinal)
           && string.Equals(left.ModuleAbi, right.ModuleAbi, StringComparison.Ordinal);

    private static bool LifecyclesEquivalent(
        RegistryExecutionRuntimeLifecycle left,
        RegistryExecutionRuntimeLifecycle right)
        => left is not null
           && right is not null
           && string.Equals(left.State, right.State, StringComparison.Ordinal)
           && left.StagedAtUtc.EqualsExact(right.StagedAtUtc)
           && DateTimesEqualExact(left.SupportedFromUtc, right.SupportedFromUtc)
           && DateTimesEqualExact(left.DeprecatedAtUtc, right.DeprecatedAtUtc)
           && DateTimesEqualExact(left.InstallBlockedAtUtc, right.InstallBlockedAtUtc)
           && DateTimesEqualExact(left.ActivationBlockedAtUtc, right.ActivationBlockedAtUtc)
           && DateTimesEqualExact(left.RevokedAtUtc, right.RevokedAtUtc)
           && string.Equals(left.Message, right.Message, StringComparison.Ordinal);

    private static bool DateTimesEqualExact(DateTimeOffset? left, DateTimeOffset? right)
        => left.HasValue == right.HasValue
           && (!left.HasValue || left.Value.EqualsExact(right!.Value));

    private static bool IsExecutionRuntimePair(string? runtimeId, string? moduleAbi)
        => runtimeId switch
        {
            SunderExecutionRuntimeFormat.DotNetRuntimeId => string.Equals(
                moduleAbi,
                SunderExecutionRuntimeFormat.DotNetModuleAbi,
                StringComparison.Ordinal),
            SunderExecutionRuntimeFormat.NodeRuntimeId => string.Equals(
                moduleAbi,
                SunderExecutionRuntimeFormat.NodeModuleAbi,
                StringComparison.Ordinal),
            _ => false,
        };

    private static bool IsPackageVersion(string? version)
        => SemanticVersion.TryParse(version, out _);

    private static InvalidDataException InvalidAction(RegistryPackageInstallPlanItem item)
        => InvalidPlan(
            $"package '{item.PackageId}' has an invalid '{item.ActionKind}' action shape for the initially captured catalog");

    private static InvalidDataException InvalidPlan(string reason, Exception? innerException = null)
        => new($"Registry execution-runtime plan is invalid: {reason}.", innerException);

    private sealed record AcceptedPlan(
        Uri Origin,
        RegistryResolveInstallPlanResponse Plan,
        DateTimeOffset ObservedAtUtc);

    private sealed record ArtifactAcquisition(
        Uri Origin,
        RegistryResolvedExecutionRuntimeArtifact Artifact,
        IReadOnlyList<string> TrustedArtifactOrigins);

    private sealed record ValidatedBinding(
        RegistryPackageTargetRuntimeBinding Binding,
        RuntimeIdentityKey ArtifactKey,
        RegistryResolvedExecutionRuntimeArtifact Artifact,
        AcceptedPlan Plan);

    private sealed record ValidatedItem(
        string PackageId,
        IReadOnlyList<ValidatedBinding> Bindings);

    private sealed record InstalledRuntimeProjection(
        IReadOnlyList<RegistryPackageTargetRuntimeBinding> Bindings);

    private sealed record EnsuredArtifact(
        ExecutionRuntimeReceiptV1 Receipt,
        DateTimeOffset ResolvedAtUtc);

    private readonly record struct RuntimeIdentityKey(
        string RuntimeId,
        string Version,
        int PackagingRevision,
        string Rid,
        string ModuleAbi)
    {
        public static RuntimeIdentityKey From(RegistryExecutionRuntimeIdentity identity)
        {
            if (identity is null
                || string.IsNullOrEmpty(identity.RuntimeId)
                || string.IsNullOrEmpty(identity.Version)
                || string.IsNullOrEmpty(identity.Rid)
                || string.IsNullOrEmpty(identity.ModuleAbi))
            {
                throw InvalidPlan("an exact execution-runtime identity is incomplete");
            }
            return new RuntimeIdentityKey(
                identity.RuntimeId,
                identity.Version,
                identity.PackagingRevision,
                identity.Rid,
                identity.ModuleAbi);
        }
    }
}
