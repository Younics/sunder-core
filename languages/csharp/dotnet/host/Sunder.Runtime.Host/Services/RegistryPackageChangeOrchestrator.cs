using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RegistryPackageChangeOrchestrator(
    RegistryPackagePlanResolver planResolver,
    RuntimeContentTransferStore transferStore,
    RegistryExecutionRuntimeBindingPreparer runtimeBindingPreparer,
    RegistryPackageArtifactDownloader artifactDownloader,
    InstalledPackageLifecycleService installedPackages,
    InstalledPackageStore installedPackageStore,
    ExecutionRuntimeLifecyclePolicyRefresher lifecyclePolicyRefresher,
    ExecutionRuntimeLifecyclePolicyCache lifecyclePolicies,
    TimeProvider timeProvider,
    ILogger<RegistryPackageChangeOrchestrator> logger,
    IHostApplicationLifetime? applicationLifetime = null)
{
    private const string HardDenyEnforcementFailureMessage =
        "Registry reported an exact execution-runtime denial, but durable catalog and policy evidence could not be committed; Runtime shutdown was requested.";
    private const string StalePreviewMessage =
        "The Registry package plan changed after it was previewed. Review the current plan and retry; no package changes were applied.";
    private static readonly IReadOnlySet<string> NoSourceAdoptions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<string, bool> NoPrereleasePolicyOverrides =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    public async Task<RuntimeRegistryResolveInstallPlanResponse> ResolveAsync(
        RuntimeRegistryPackageBatchRequest request,
        CancellationToken cancellationToken)
    {
        var installedRecords = await installedPackageStore.ListAsync(cancellationToken);
        var installed = installedRecords.ToDictionary(
            static package => package.PackageId,
            StringComparer.OrdinalIgnoreCase);
        var materialized = EnsureProvenanceChangesAreMaterialized(
            new PackageExecutionRequest(request, NoSourceAdoptions, NoPrereleasePolicyOverrides),
            installed);
        var plan = await planResolver.ResolveAsync(materialized.Request, cancellationToken);
        if (plan.RuntimeConflicts.Any(static conflict => IsHardDenyCode(conflict.Code)))
        {
            IReadOnlyList<string> enforcementWarnings;
            try
            {
                var origin = RegistryOrigin.Normalize(request.RegistryOrigin);
                enforcementWarnings = await EnforceKnownBlockedExecutionRuntimesAsync(
                        CreateHardDenyEvidence(origin, plan),
                        installedRecords,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                applicationLifetime?.StopApplication();
                logger.LogWarning(
                    "Registry package plan hard-denial enforcement failed: {ErrorType}",
                    exception.GetType().Name);
                enforcementWarnings = [HardDenyEnforcementFailureMessage];
            }
            plan = AddPlanWarnings(plan, enforcementWarnings);
        }
        if (!plan.Success)
        {
            return plan;
        }
        return plan with
        {
            RequiredCapabilityConsents = PackageSensitiveCapabilityPolicy.GetRequiredConsents(
                plan.Items,
                installed),
        };
    }

    public Task<RuntimeRegistryPackageChangeResult> InstallAsync(
        RuntimeRegistryPackageRequest request,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            new RuntimeRegistryPackageBatchRequest(
                request.RegistryOrigin,
                [new RuntimeRegistryPackageChangeRequest(
                    request.PackageId,
                    request.Version,
                    request.DesiredTargets ?? [],
                    request.Version is null ? request.Tag : null,
                    request.VersionRange,
                    request.Required)],
                request.IncludePrerelease,
                request.AllowDowngrade,
                request.Reinstall,
                request.CapabilityConsents,
                request.ExpectedResolutionFingerprint),
            cancellationToken);

    public async Task<RuntimeRegistryPackageChangeResult> UpdateAsync(
        RuntimeRegistryUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var installed = await installedPackageStore.ListAsync(cancellationToken);
        var selected = string.IsNullOrWhiteSpace(request.PackageId)
            ? installed.ToArray()
            : installed.Where(package => string.Equals(
                    package.PackageId,
                    request.PackageId,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
        if (selected.Length == 0)
        {
            return Failed(
                string.IsNullOrWhiteSpace(request.PackageId)
                    ? "No packages are installed."
                    : $"Package '{request.PackageId}' is not installed.",
                RuntimeRegistryErrorCode.NotFound);
        }

        string? requestedOrigin = null;
        if (!string.IsNullOrWhiteSpace(request.RegistryOrigin))
        {
            try
            {
                requestedOrigin = RegistryOrigin.Normalize(request.RegistryOrigin).AbsoluteUri;
            }
            catch (ArgumentException exception)
            {
                return Failed(exception.Message, RuntimeRegistryErrorCode.InvalidRequest);
            }
        }

        var skipped = new List<string>();
        var warnings = new List<string>();
        var groups = new Dictionary<(string Origin, bool IncludePrerelease), List<PackageUpdateSelection>>();
        foreach (var package in selected)
        {
            var provenance = package.Provenance;
            if (provenance.SourceKind != InstalledPackageSourceKind.Registry
                || string.IsNullOrWhiteSpace(provenance.RegistryOrigin))
            {
                skipped.Add(package.PackageId);
                warnings.Add($"Skipped unmanaged package '{package.PackageId}'; explicitly adopt a Registry source to update it.");
                continue;
            }
            if (requestedOrigin is not null
                && !string.Equals(requestedOrigin, provenance.RegistryOrigin, StringComparison.Ordinal))
            {
                skipped.Add(package.PackageId);
                warnings.Add($"Skipped package '{package.PackageId}' because its recorded Registry origin is '{provenance.RegistryOrigin}', not '{requestedOrigin}'.");
                continue;
            }
            if (provenance.VersionPolicy == InstalledPackageVersionPolicy.ExplicitVersion)
            {
                skipped.Add(package.PackageId);
                warnings.Add($"Skipped package '{package.PackageId}' because it is pinned to explicit version '{provenance.RequestedVersion}'.");
                continue;
            }
            if (provenance.VersionPolicy == InstalledPackageVersionPolicy.TransitiveDependency)
            {
                skipped.Add(package.PackageId);
                warnings.Add($"Skipped resolver-managed dependency '{package.PackageId}'; it is updated through a package that depends on it.");
                continue;
            }
            if (provenance.VersionPolicy != InstalledPackageVersionPolicy.FollowTag
                || string.IsNullOrWhiteSpace(provenance.RequestedTag))
            {
                skipped.Add(package.PackageId);
                warnings.Add($"Skipped package '{package.PackageId}' because its Registry update policy is incomplete.");
                continue;
            }

            var groupKey = (
                provenance.RegistryOrigin,
                provenance.IncludePrerelease || request.IncludePrerelease);
            if (!groups.TryGetValue(groupKey, out var changes))
            {
                changes = [];
                groups.Add(groupKey, changes);
            }
            changes.Add(new PackageUpdateSelection(
                new RuntimeRegistryPackageChangeRequest(
                    provenance.SourcePackageId ?? package.PackageId,
                    null,
                    request.DesiredTargets ?? [],
                    provenance.RequestedTag,
                    provenance.VersionRange),
                provenance.IncludePrerelease));
        }

        IReadOnlyDictionary<(string Origin, bool IncludePrerelease), string> expectedPlans;
        try
        {
            expectedPlans = NormalizeExpectedPlans(request.ExpectedPlans);
        }
        catch (ArgumentException exception)
        {
            return Failed(exception.Message, RuntimeRegistryErrorCode.InvalidRequest);
        }

        if (expectedPlans.Count > 0 && !ExpectedPlansMatchGroups(expectedPlans, groups.Keys))
        {
            return AddContext(
                Failed(StalePreviewMessage, RuntimeRegistryErrorCode.StalePlan),
                warnings,
                skipped);
        }

        if (groups.Count == 0)
        {
            return new RuntimeRegistryPackageChangeResult(
                true,
                RuntimeRegistryErrorCode.None,
                "No managed package updates were eligible.",
                true,
                false,
                warnings,
                [],
                [],
                [])
            {
                CommittedStamp = installedPackages.Stamp,
                SkippedPackageIds = skipped,
            };
        }

        var executions = groups
            .OrderBy(group => group.Key.Origin, StringComparer.Ordinal)
            .ThenBy(group => group.Key.IncludePrerelease)
            .Select(group =>
            {
                var key = (RegistryOrigin.Normalize(group.Key.Origin).AbsoluteUri, group.Key.IncludePrerelease);
                return new PackageExecutionRequest(
                    new RuntimeRegistryPackageBatchRequest(
                        group.Key.Origin,
                        group.Value.Select(selection => selection.Change).ToArray(),
                        group.Key.IncludePrerelease,
                        ExpectedResolutionFingerprint: expectedPlans.GetValueOrDefault(key)),
                    NoSourceAdoptions,
                    group.Value.ToDictionary(
                        selection => selection.Change.PackageId,
                        selection => selection.IncludePrereleasePolicy,
                        StringComparer.OrdinalIgnoreCase));
            })
            .ToArray();
        var result = await ExecuteAtomicAsync(
            executions,
            request.DryRun,
            request.CapabilityConsents,
            cancellationToken);
        return AddContext(result, warnings, skipped);
    }

    public async Task<RuntimeRegistryPackageChangeResult> AdoptSourceAsync(
        RuntimeRegistrySourceAdoptionRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.PackageId)
            || string.IsNullOrWhiteSpace(request.RegistryOrigin))
        {
            return Failed(
                "Adopting a package source requires one package id and an explicit Registry origin.",
                RuntimeRegistryErrorCode.InvalidRequest);
        }
        if (string.IsNullOrWhiteSpace(request.Version) == string.IsNullOrWhiteSpace(request.Tag))
        {
            return Failed(
                "A Registry source adoption must select exactly one explicit version or tag.",
                RuntimeRegistryErrorCode.InvalidRequest);
        }
        if (request.Confirm == request.DryRun)
        {
            return Failed(
                "A Registry source adoption requires exactly one of confirmation or dry run.",
                RuntimeRegistryErrorCode.InvalidRequest);
        }
        if (request.Confirm
            && !PackageSensitiveCapabilityPolicy.IsSha256(request.ExpectedResolutionFingerprint))
        {
            return Failed(
                "A confirmed Registry source adoption requires the lower-case SHA-256 resolution fingerprint returned by its dry-run preview.",
                RuntimeRegistryErrorCode.InvalidRequest);
        }
        if (request.DryRun
            && request.ExpectedResolutionFingerprint is not null
            && !PackageSensitiveCapabilityPolicy.IsSha256(request.ExpectedResolutionFingerprint))
        {
            return Failed(
                "A Registry source adoption resolution fingerprint must be a lower-case SHA-256 digest.",
                RuntimeRegistryErrorCode.InvalidRequest);
        }
        if (!string.IsNullOrWhiteSpace(request.Version) && request.IncludePrerelease)
        {
            return Failed(
                "Prerelease inclusion is a tag policy and cannot be combined with an explicit version.",
                RuntimeRegistryErrorCode.InvalidRequest);
        }

        var installed = await installedPackageStore.ListAsync(cancellationToken);
        var package = installed.FirstOrDefault(candidate => string.Equals(
            candidate.PackageId,
            request.PackageId,
            StringComparison.OrdinalIgnoreCase));
        if (package is null)
        {
            return Failed($"Package '{request.PackageId}' is not installed.", RuntimeRegistryErrorCode.NotFound);
        }

        var batch = new RuntimeRegistryPackageBatchRequest(
            request.RegistryOrigin,
            [new RuntimeRegistryPackageChangeRequest(
                package.PackageId,
                request.Version,
                request.DesiredTargets ?? [],
                request.Version is null ? request.Tag : null)],
            request.IncludePrerelease,
            request.AllowDowngrade,
            ExpectedResolutionFingerprint: request.ExpectedResolutionFingerprint);
        var adoptedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            package.PackageId,
        };
        var result = await ExecuteAtomicAsync(
            [new PackageExecutionRequest(batch, adoptedPackageIds, NoPrereleasePolicyOverrides)],
            request.DryRun,
            request.CapabilityConsents,
            cancellationToken);
        if (!result.Success)
        {
            return result;
        }

        return WithMessage(
            result,
            request.DryRun
                ? $"Resolved Registry source adoption for '{package.PackageId}'; no changes were applied."
                : $"Adopted Registry source for '{package.PackageId}'.");
    }

    public Task<RuntimeRegistryPackageChangeResult> ExecuteAsync(
        RuntimeRegistryPackageBatchRequest request,
        CancellationToken cancellationToken)
        => ExecuteAtomicAsync(
            [new PackageExecutionRequest(request, NoSourceAdoptions, NoPrereleasePolicyOverrides)],
            dryRun: false,
            providedCapabilityConsents: request.CapabilityConsents,
            cancellationToken: cancellationToken);

    private async Task<RuntimeRegistryPackageChangeResult> ExecuteAtomicAsync(
        IReadOnlyList<PackageExecutionRequest> executions,
        bool dryRun,
        IReadOnlyList<PackageCapabilityConsent>? providedCapabilityConsents,
        CancellationToken cancellationToken)
    {
        var resolved = new List<ResolvedPackageExecution>(executions.Count);
        IReadOnlyDictionary<string, InstalledPackageRecord> installed =
            new Dictionary<string, InstalledPackageRecord>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<InstalledPackageRecord> expectedCatalog = [];
        IReadOnlyList<PackageCapabilityConsent> requiredCapabilityConsents = [];
        try
        {
            installed = (await installedPackageStore.ListAsync(cancellationToken))
                .ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
            expectedCatalog = installed.Values
                .OrderBy(package => package.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var execution in executions)
            {
                var materialized = EnsureProvenanceChangesAreMaterialized(execution, installed);
                var expectedResolutionFingerprint = materialized.Request.ExpectedResolutionFingerprint;
                var resolution = await planResolver.ResolveForExecutionAsync(
                    materialized.Request,
                    expectedResolutionFingerprint,
                    cancellationToken);
                if (expectedResolutionFingerprint is not null
                    && !ConfirmsExpectedPreview(resolution.Plan, expectedResolutionFingerprint, timeProvider.GetUtcNow()))
                {
                    IReadOnlyList<string> enforcementWarnings;
                    try
                    {
                        enforcementWarnings = await EnforceKnownBlockedExecutionRuntimesAsync(
                                CreateHardDenyEvidence(resolution.Origin, resolution.Plan),
                                expectedCatalog,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (InvalidDataException exception)
                    {
                        return PlanEnforcementFailed(resolved, resolution.Plan, exception.Message);
                    }
                    return StalePreview(
                        resolved,
                        resolution.Plan,
                        enforcementWarnings);
                }
                if (!resolution.Plan.Success)
                {
                    IReadOnlyList<string> enforcementWarnings;
                    try
                    {
                        enforcementWarnings = await EnforceKnownBlockedExecutionRuntimesAsync(
                                CreateHardDenyEvidence(resolution.Origin, resolution.Plan),
                                expectedCatalog,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (InvalidDataException exception)
                    {
                        return PlanEnforcementFailed(resolved, resolution.Plan, exception.Message);
                    }
                    return PlanFailed(resolved, resolution.Plan, enforcementWarnings);
                }

                var provenance = BuildProvenanceByPackageId(
                    resolution.Origin,
                    materialized.Request,
                    materialized.AdoptedPackageIds,
                    resolution.Plan.Items,
                    installed,
                    materialized.IncludePrereleasePolicyByPackageId);
                foreach (var packageId in materialized.AdoptedPackageIds)
                {
                    if (!resolution.Plan.Items.Any(item => string.Equals(
                            item.PackageId,
                            packageId,
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new InvalidDataException(
                            $"Registry source adoption for '{packageId}' did not produce a materialized package change.");
                    }
                }
                resolved.Add(new ResolvedPackageExecution(
                    materialized,
                    resolution.Origin,
                    resolution.Plan,
                    provenance));
            }
            ValidateUniquePlanPackages(resolved);
            requiredCapabilityConsents = PackageSensitiveCapabilityPolicy.GetRequiredConsents(
                ResolvedPlanItems(resolved),
                installed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException exception)
        {
            return Failed(exception.Message, RuntimeRegistryErrorCode.InvalidRequest);
        }
        catch (InvalidDataException exception)
        {
            return Failed(exception.Message, RuntimeRegistryErrorCode.Conflict, ResolvedWarnings(resolved), planItems: ResolvedPlanItems(resolved));
        }
        catch (PackagePolicyDeniedException exception)
        {
            return Failed(exception.Code, exception.Code == PackagePolicyErrorCodes.PlanStale ? RuntimeRegistryErrorCode.StalePlan : RuntimeRegistryErrorCode.Conflict,
                ResolvedWarnings(resolved), planItems: ResolvedPlanItems(resolved));
        }
        catch (Exception exception)
        {
            logger.LogWarning("Registry package plan failed: {ErrorType}", exception.GetType().Name);
            return Failed(exception.Message, RuntimeRegistryErrorCode.RegistryUnavailable, ResolvedWarnings(resolved), planItems: ResolvedPlanItems(resolved));
        }

        var planWarnings = ResolvedWarnings(resolved);
        var planItems = ResolvedPlanItems(resolved);
        if (dryRun)
        {
            var runtimePlans = resolved
                .Select(static resolution => RuntimeRegistryContractMapper.ToRuntime(resolution.Plan))
                .ToArray();
            return new RuntimeRegistryPackageChangeResult(
                true,
                RuntimeRegistryErrorCode.None,
                "Registry package plan resolved; no changes were applied.",
                false,
                false,
                planWarnings,
                [],
                [],
                planItems,
                requiredCapabilityConsents)
            {
                CommittedStamp = installedPackages.Stamp,
                Conflicts = runtimePlans.SelectMany(static plan => plan.Conflicts).ToArray(),
                PlanExpectations = resolved.Select(static resolution =>
                        new RuntimeRegistryPackagePlanExpectation(
                            resolution.Origin.AbsoluteUri,
                            resolution.Execution.Request.IncludePrerelease,
                            resolution.Plan.ResolutionFingerprint))
                    .ToArray(),
                RuntimeArtifacts = runtimePlans
                    .SelectMany(static plan => plan.RuntimeArtifacts)
                    .DistinctBy(static artifact =>
                        $"{artifact.Identity.RuntimeId}\0{artifact.Identity.Version}\0{artifact.Identity.PackagingRevision}\0{artifact.Identity.Rid}\0{artifact.Identity.ModuleAbi}\0{artifact.ArtifactSha256}",
                    StringComparer.Ordinal)
                    .ToArray(),
                RuntimeConflicts = runtimePlans.SelectMany(static plan => plan.RuntimeConflicts).ToArray(),
            };
        }
        var consentError = PackageSensitiveCapabilityPolicy.ValidateProvidedConsents(
            requiredCapabilityConsents,
            providedCapabilityConsents);
        if (consentError is not null)
        {
            return Failed(
                consentError,
                RuntimeRegistryErrorCode.ConsentRequired,
                planWarnings,
                planItems: planItems,
                requiredCapabilityConsents: requiredCapabilityConsents);
        }
        if (planItems.Count == 0)
        {
            return new RuntimeRegistryPackageChangeResult(
                true,
                RuntimeRegistryErrorCode.None,
                "No package changes required.",
                true,
                false,
                planWarnings,
                [],
                [],
                [])
            {
                CommittedStamp = installedPackages.Stamp,
            };
        }

        var uploadIds = new List<string>();
        var uploadMetadataById = new Dictionary<string, RegistryPackageUploadMetadata>(StringComparer.Ordinal);
        var runtimeRebinds = new List<PackageExecutionRuntimeRebindMutation>();
        var runtimePolicyUpdates = new List<PackageExecutionRuntimePolicyUpdateMutation>();
        string? stageId = null;
        var artifactsPrepared = false;
        try
        {
            if (!PackageStorePolicy.CatalogBindingsEqual(
                    await installedPackageStore.ListAsync(cancellationToken).ConfigureAwait(false),
                    expectedCatalog))
            {
                return Failed(
                    "Registry package staging is stale because the installed package catalog changed after resolution.",
                    RuntimeRegistryErrorCode.Conflict,
                    planWarnings,
                    planItems: planItems);
            }
            await using var preparedExactBindings = await runtimeBindingPreparer.PrepareAsync(
                resolved.Select(resolution => new RegistryPackagePlanResolution(
                        resolution.Origin,
                        resolution.Plan))
                    .ToArray(),
                installed,
                cancellationToken);
            var exactBindingsByPackageId = preparedExactBindings.Bindings;
            var mutations = new List<PackageStoreMutationRequest>(planItems.Count);
            foreach (var resolution in resolved)
            {
                var selectedTargets = RuntimeRegistryContractMapper.ToRegistry(
                        resolution.Execution.Request.Packages)
                    .SelectMany(static change => change.DesiredTargets)
                    .Distinct()
                    .ToArray();
                foreach (var item in resolution.Plan.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!exactBindingsByPackageId.TryGetValue(item.PackageId, out var exactBindings))
                    {
                        throw new InvalidDataException(
                            $"Registry plan did not prepare exact execution-runtime bindings for package '{item.PackageId}'.");
                    }

                    if (item.ActionKind == RegistryPackageActionKinds.Keep)
                    {
                        if (!installed.TryGetValue(item.PackageId, out var current))
                        {
                            throw new InvalidDataException(
                                $"Registry keep package '{item.PackageId}' is not installed.");
                        }
                        if (RequiresLifecycleEvidenceUpdate(
                                item.PackageId,
                                current.ExecutionRuntimeBindings,
                                exactBindings))
                        {
                            runtimePolicyUpdates.Add(new PackageExecutionRuntimePolicyUpdateMutation(
                                item.PackageId,
                                current,
                                exactBindings));
                        }
                        continue;
                    }
                    if (item.ActionKind == RegistryPackageActionKinds.RuntimeOnlyRebind)
                    {
                        if (!installed.TryGetValue(item.PackageId, out var current))
                        {
                            throw new InvalidDataException(
                                $"Registry execution-runtime rebind package '{item.PackageId}' is not installed.");
                        }
                        runtimeRebinds.Add(new PackageExecutionRuntimeRebindMutation(
                            item.PackageId,
                            current,
                            exactBindings));
                        continue;
                    }
                    if (item.ActionKind is not (
                        RegistryPackageActionKinds.PackageInstall
                        or RegistryPackageActionKinds.PackageUpdate))
                    {
                        throw new InvalidDataException(
                            $"Registry action '{item.ActionKind}' for package '{item.PackageId}' is not supported by package transactions.");
                    }

                    var upload = await artifactDownloader.DownloadAsync(
                        resolution.Origin,
                        item,
                        selectedTargets,
                        resolution.Plan.TrustedArtifactOrigins,
                        cancellationToken);
                    uploadIds.Add(upload.UploadId);
                    uploadMetadataById.Add(
                        upload.UploadId,
                        new RegistryPackageUploadMetadata(
                            resolution.ProvenanceByPackageId[item.PackageId] with
                            {
                                SourceIdentity = item.PackageContentIdentity,
                            },
                            item.Artifacts[0].ManifestSha256,
                            exactBindings)
                        {
                            ResolutionPolicy = resolution.Plan.ResolutionPolicy,
                            AcquiredTargets = item.Artifacts.Where(artifact => artifact.Rid is not null)
                                .Select(artifact => new Sunder.Package.Format.SunderPackageTargetKey(artifact.Kind, artifact.Rid!)).ToArray(),
                        });
                    mutations.Add(new PackageStoreMutationRequest(
                        item.CurrentVersion is null ? PackageStoreMutationKind.Install : PackageStoreMutationKind.Upgrade,
                        item.CurrentVersion is null ? null : item.PackageId,
                        upload.UploadId,
                        resolution.Execution.Request.AllowDowngrade,
                        RequiresReinstall(
                            item.CurrentVersion,
                            item.Version ?? throw new InvalidDataException(
                                $"Registry action '{item.ActionKind}' for package '{item.PackageId}' does not identify package content."),
                            resolution.Execution.Request.Reinstall),
                        CapabilityConsent: providedCapabilityConsents?.FirstOrDefault(consent =>
                            string.Equals(consent.PackageId, item.PackageId, StringComparison.Ordinal))));
                }
            }
            artifactsPrepared = true;

            if (mutations.Count == 0 && runtimeRebinds.Count == 0 && runtimePolicyUpdates.Count == 0)
            {
                await ReconfirmResolutionAuthorityAsync(resolved, cancellationToken)
                    .ConfigureAwait(false);
                await preparedExactBindings.ObserveLifecyclePoliciesAsync(cancellationToken)
                    .ConfigureAwait(false);
                return new RuntimeRegistryPackageChangeResult(
                    true,
                    RuntimeRegistryErrorCode.None,
                    "No package changes required.",
                    false,
                    false,
                    planWarnings,
                    [],
                    [],
                    planItems)
                {
                    CommittedStamp = installedPackages.Stamp,
                };
            }

            var stage = await installedPackages.StageRegistryAsync(
                new PackageStoreStageRequest(mutations),
                uploadMetadataById,
                expectedCatalog,
                runtimeRebinds,
                runtimePolicyUpdates,
                preparedExactBindings.LifecyclePolicyEntries,
                preparedExactBindings.LifecyclePolicyAuthority,
                cancellationToken);
            stageId = stage.StageId;
            if (!stage.Success || stageId is null)
            {
                if (stageId is not null)
                {
                    await installedPackages.DiscardStageAsync(stageId, CancellationToken.None);
                    stageId = null;
                }
                var errors = stage.Errors
                    .DefaultIfEmpty(stage.OperationResult.Message ?? "Package transaction staging failed.")
                    .ToArray();
                return new RuntimeRegistryPackageChangeResult(
                    false,
                    RuntimeRegistryErrorCode.Conflict,
                    errors[0],
                    false,
                    false,
                    planWarnings.Concat(stage.Warnings).ToArray(),
                    errors,
                    stage.ImpactedPackageIds,
                    planItems);
            }

            var commit = await installedPackages.CommitRegistryStageAsync(
                stageId,
                (_, token) => ReconfirmResolutionAuthorityAsync(resolved, token),
                cancellationToken);
            stageId = null;
            if (!commit.Success && commit.RuntimeSessionReconciliationPending)
            {
                applicationLifetime?.StopApplication();
            }
            var commitErrors = commit.Errors
                .DefaultIfEmpty(commit.Message ?? "Package transaction commit failed.")
                .ToArray();
            return new RuntimeRegistryPackageChangeResult(
                commit.Success,
                commit.Success ? RuntimeRegistryErrorCode.None : RuntimeRegistryErrorCode.InternalError,
                commit.Message ?? (commit.Success
                    ? $"Applied {planItems.Count} package change(s)."
                    : commitErrors[0]),
                commit.RuntimeSessionApplied,
                commit.RequiresAppRestart,
                planWarnings.Concat(commit.Warnings).ToArray(),
                commit.Success ? [] : commitErrors,
                commit.ImpactedPackageIds,
                planItems)
            {
                CommittedStamp = commit.CommittedStamp,
            };
        }
        catch (OperationCanceledException)
        {
            if (stageId is not null)
            {
                await installedPackages.DiscardStageAsync(stageId, CancellationToken.None);
            }
            throw;
        }
        catch (RegistryExecutionRuntimeHardDenyException exception)
        {
            if (stageId is not null)
            {
                await installedPackages.DiscardStageAsync(stageId, CancellationToken.None);
                stageId = null;
            }
            var runtimeConflicts = exception.Evidence
                .SelectMany(static evidence => evidence.Conflicts)
                .Select(RuntimeRegistryContractMapper.ToRuntime)
                .ToArray();
            try
            {
                var enforcementWarnings = await EnforceKnownBlockedExecutionRuntimesAsync(
                        exception.Evidence,
                        expectedCatalog,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return Failed(
                    exception.Message,
                    RuntimeRegistryErrorCode.Conflict,
                    planWarnings.Concat(enforcementWarnings).ToArray(),
                    planItems: planItems,
                    runtimeConflicts: runtimeConflicts);
            }
            catch (Exception enforcementException)
            {
                return Failed(
                    enforcementException.Message,
                    RuntimeRegistryErrorCode.Conflict,
                    planWarnings,
                    planItems: planItems,
                    runtimeConflicts: runtimeConflicts);
            }
        }
        catch (InvalidDataException exception)
        {
            if (stageId is not null)
            {
                await installedPackages.DiscardStageAsync(stageId, CancellationToken.None);
            }
            return Failed(
                exception.Message,
                artifactsPrepared ? RuntimeRegistryErrorCode.Conflict : RuntimeRegistryErrorCode.ArtifactVerificationFailed,
                planWarnings,
                planItems: planItems);
        }
        catch (RegistryArtifactTooLargeException exception)
        {
            if (stageId is not null)
            {
                await installedPackages.DiscardStageAsync(stageId, CancellationToken.None);
            }
            return Failed(
                exception.Message,
                RuntimeRegistryErrorCode.DownloadTooLarge,
                planWarnings,
                planItems: planItems);
        }
        catch (PackagePolicyDeniedException exception)
        {
            if (stageId is not null) await installedPackages.DiscardStageAsync(stageId, CancellationToken.None);
            return Failed(exception.Code, exception.Code == PackagePolicyErrorCodes.PlanStale ? RuntimeRegistryErrorCode.StalePlan : RuntimeRegistryErrorCode.Conflict,
                planWarnings, planItems: planItems);
        }
        catch (Exception exception)
        {
            if (stageId is not null)
            {
                await installedPackages.DiscardStageAsync(stageId, CancellationToken.None);
            }
            if (exception is RuntimeRequiredProviderException) throw;
            logger.LogWarning("Registry package transaction failed: {ErrorType}", exception.GetType().Name);
            return Failed(
                exception.Message,
                RuntimeRegistryErrorCode.RegistryUnavailable,
                planWarnings,
                planItems: planItems);
        }
        finally
        {
            foreach (var uploadId in uploadIds)
            {
                transferStore.DiscardUpload(uploadId);
            }
        }
    }

    private async Task ReconfirmResolutionAuthorityAsync(
        IReadOnlyList<ResolvedPackageExecution> resolved,
        CancellationToken cancellationToken)
    {
        foreach (var original in resolved)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedFingerprint = original.Plan.ResolutionFingerprint;
            var confirmation = await planResolver.ResolveForExecutionAsync(
                original.Execution.Request,
                expectedFingerprint,
                cancellationToken);
            var plan = confirmation.Plan;
            var hardDenyEvidence = CreateHardDenyEvidence(confirmation.Origin, plan);
            if (hardDenyEvidence.Count > 0)
            {
                throw new RegistryExecutionRuntimeHardDenyException(hardDenyEvidence);
            }
            if (!plan.Success
                || plan.Errors.Count != 0
                || plan.Conflicts.Count != 0
                || plan.RuntimeConflicts.Count != 0
                || !string.Equals(
                    plan.ResolutionFingerprint,
                    expectedFingerprint,
                    StringComparison.Ordinal)
                || plan.RuntimePolicyRevision != original.Plan.RuntimePolicyRevision
                || plan.ExpiresAtUtc <= timeProvider.GetUtcNow())
            {
                throw new InvalidDataException(
                    "Registry package staging is stale because its resolution authority could not be reconfirmed.");
            }
        }
    }

    private async Task<IReadOnlyList<string>> EnforceKnownBlockedExecutionRuntimesAsync(
        IReadOnlyList<RegistryRuntimeHardDenyEvidence> evidence,
        IReadOnlyList<InstalledPackageRecord> expectedCatalog,
        CancellationToken cancellationToken)
    {
        if (evidence.Count == 0)
        {
            return [];
        }
        var conflicts = evidence.SelectMany(static item => item.Conflicts).ToArray();
        var identities = conflicts
            .Where(static conflict => conflict.CurrentRuntime is not null)
            .Select(static conflict => conflict.CurrentRuntime!)
            .Distinct()
            .Select(static identity => new ExecutionRuntimeIdentity(
                identity.RuntimeId,
                identity.Version,
                identity.PackagingRevision,
                identity.Rid,
                identity.ModuleAbi))
            .OrderBy(static identity => identity.SingleFlightKey, StringComparer.Ordinal)
            .ToArray();
        try
        {
            var refreshedEntries = new List<ExecutionRuntimeLifecyclePolicyEntryV1>();
            foreach (var authority in evidence)
            {
                var refresh = await lifecyclePolicyRefresher.RefreshHardDenialsAsync(
                        authority.RegistryOrigin,
                        authority.RuntimePolicyRevision,
                        authority.Conflicts,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (refresh.ReleasesUnavailable > 0)
                {
                    throw new InvalidDataException(
                        "Registry reported a blocked execution runtime, but its exact lifecycle evidence could not be refreshed.");
                }
                if (refresh.PreparedUpdate is null
                    || refresh.PreparedUpdate.Entries.Count != refresh.ReceiptsObserved)
                {
                    throw new InvalidDataException(
                        "Registry hard-denial lifecycle evidence was not prepared for durable commit.");
                }
                refreshedEntries.AddRange(refresh.PreparedUpdate.Entries);
            }

            var preparedUpdate = await lifecyclePolicies.PrepareEntriesAsync(
                    refreshedEntries,
                    cancellationToken)
                .ConfigureAwait(false);
            await lifecyclePolicies.ValidatePreparedUpdatePersistenceAsync(
                    preparedUpdate,
                    cancellationToken)
                .ConfigureAwait(false);

            var authoritativeEntries = identities.Select(identity =>
                {
                    if (!preparedUpdate.Authority.TryGet(identity, out var policy)
                        || policy is null)
                    {
                        throw new InvalidDataException(
                            $"Authoritative lifecycle policy is missing exact runtime '{identity.SingleFlightKey}'.");
                    }
                    return policy;
                })
                .ToArray();
            var policyByIdentity = authoritativeEntries.ToDictionary(
                static policy => policy.Identity.SingleFlightKey,
                StringComparer.Ordinal);
            var matchedPolicyIdentities = new HashSet<string>(StringComparer.Ordinal);
            var desiredCatalog = expectedCatalog.ToDictionary(
                static package => package.PackageId,
                StringComparer.OrdinalIgnoreCase);
            var policyUpdates = new List<PackageExecutionRuntimePolicyUpdateMutation>();
            var affectedBindingCount = 0;
            foreach (var package in expectedCatalog)
            {
                var changed = false;
                var desiredBindings = package.ExecutionRuntimeBindings.Select(binding =>
                {
                    var identity = new ExecutionRuntimeIdentity(
                        binding.RuntimeId,
                        binding.RuntimeVersion,
                        binding.RuntimePackagingRevision,
                        binding.Rid,
                        binding.ModuleAbi);
                    if (!policyByIdentity.TryGetValue(identity.SingleFlightKey, out var policy))
                    {
                        return binding;
                    }

                    matchedPolicyIdentities.Add(identity.SingleFlightKey);
                    affectedBindingCount++;
                    if (!string.Equals(policy.RegistryOrigin, binding.RegistryOrigin, StringComparison.Ordinal)
                        || !string.Equals(policy.ManifestSha256, binding.RuntimeManifestSha256, StringComparison.Ordinal)
                        || !string.Equals(policy.ArtifactSha256, binding.RuntimeArtifactSha256, StringComparison.Ordinal)
                        || policy.ArtifactSize != binding.RuntimeArtifactSize
                        || !string.Equals(policy.SigningKeyId, binding.SigningKeyId, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Authoritative lifecycle policy does not match installed binding '{identity.SingleFlightKey}'.");
                    }
                    if (policy.RuntimePolicyRevision < binding.RuntimePolicyRevision
                        || policy.RuntimePolicyRevision == binding.RuntimePolicyRevision
                        && !string.Equals(
                            policy.Lifecycle.State,
                            binding.RuntimeLifecycleState,
                            StringComparison.Ordinal)
                        || policy.RuntimePolicyRevision > binding.RuntimePolicyRevision
                        && !IsForwardLifecycleTransition(
                            binding.RuntimeLifecycleState,
                            policy.Lifecycle.State)
                        || policy.PolicyObservedAtUtc < binding.PolicyObservedAtUtc)
                    {
                        throw new InvalidDataException(
                            $"Authoritative lifecycle policy conflicts with installed binding '{identity.SingleFlightKey}'.");
                    }

                    var desired = binding with
                    {
                        RuntimePolicyRevision = policy.RuntimePolicyRevision,
                        RuntimeLifecycleState = policy.Lifecycle.State,
                        PolicyObservedAtUtc = policy.PolicyObservedAtUtc,
                        ResolvedAtUtc = policy.PolicyObservedAtUtc > binding.ResolvedAtUtc
                            ? policy.PolicyObservedAtUtc
                            : binding.ResolvedAtUtc,
                    };
                    changed |= desired != binding;
                    return desired;
                }).ToArray();
                if (!changed)
                {
                    continue;
                }

                var desiredPackage = package with { ExecutionRuntimeBindings = desiredBindings };
                desiredCatalog[package.PackageId] = desiredPackage;
                policyUpdates.Add(new PackageExecutionRuntimePolicyUpdateMutation(
                    package.PackageId,
                    package,
                    desiredBindings));
            }
            if (!matchedPolicyIdentities.SetEquals(policyByIdentity.Keys))
            {
                throw new InvalidDataException(
                    "Registry hard-denial evidence does not match every affected installed execution-runtime binding.");
            }

            var desiredPackages = desiredCatalog.Values
                .OrderBy(static package => package.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var stage = await installedPackages.StageRegistryAsync(
                    new PackageStoreStageRequest([]),
                    new Dictionary<string, RegistryPackageUploadMetadata>(StringComparer.Ordinal),
                    expectedCatalog,
                    registryRuntimeRebinds: [],
                    registryRuntimePolicyUpdates: policyUpdates,
                    registryRuntimePolicyEntries: authoritativeEntries,
                    registryRuntimePolicyAuthority: preparedUpdate.Authority,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!stage.Success || stage.StageId is null)
            {
                if (stage.StageId is not null)
                {
                    await installedPackages.DiscardStageAsync(stage.StageId, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                throw new InvalidDataException(
                    stage.OperationResult.Message
                    ?? "Execution-runtime hard-denial binding evidence could not be staged.");
            }

            var commit = await installedPackages.CommitStageAsync(
                    stage.StageId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!commit.Success)
            {
                throw new InvalidDataException(
                    commit.Message
                    ?? "Execution-runtime hard-denial binding evidence could not be committed.");
            }
            var committedCatalog = await installedPackageStore.ListAsync(CancellationToken.None)
                .ConfigureAwait(false);
            if (!PackageStorePolicy.CatalogsEqual(committedCatalog.Select(package => package with { Policy = null }).ToArray(),
                    desiredPackages.Select(package => package with { Policy = null }).ToArray()))
            {
                throw new InvalidDataException(
                    "Committed execution-runtime hard-denial binding evidence does not match the authoritative catalog.");
            }
            var committedPolicies = lifecyclePolicies.CaptureSnapshot();
            if (authoritativeEntries.Any(expected =>
                    !committedPolicies.TryGet(expected.Identity, out var actual)
                    || actual != expected))
            {
                throw new InvalidDataException(
                    "Committed execution-runtime hard-denial policy does not match installed binding evidence.");
            }

            var now = timeProvider.GetUtcNow();
            var blocked = authoritativeEntries
                .Where(policy => !policy.PermitsActivation(now))
                .Select(static policy => policy.Identity)
                .ToArray();
            var faultedPackageCount = await installedPackages.EnforceExecutionRuntimePolicyAsync(
                    blocked,
                    CancellationToken.None)
                .ConfigureAwait(false);
            var warnings = new List<string>
            {
                $"Persisted authoritative lifecycle policy for {affectedBindingCount} installed execution-runtime binding(s).",
            };
            if (blocked.Length > 0)
            {
                warnings.Add(
                    $"Stopped and faulted {faultedPackageCount} active package(s) bound to {blocked.Length} blocked execution runtime(s).");
            }
            return warnings;
        }
        catch (Exception exception)
        {
            applicationLifetime?.StopApplication();
            throw new InvalidDataException(
                HardDenyEnforcementFailureMessage,
                exception);
        }
    }

    private static IReadOnlyList<RegistryRuntimeHardDenyEvidence> CreateHardDenyEvidence(
        Uri origin,
        RuntimeRegistryResolveInstallPlanResponse plan)
    {
        var conflicts = plan.RuntimeConflicts
            .Where(static conflict => IsHardDenyCode(conflict.Code))
            .Select(static conflict => new RegistryExecutionRuntimeResolutionConflict(
                conflict.Code,
                conflict.Message,
                new RegistryExecutionRuntimeRequirement(
                    conflict.Requirement.RuntimeId,
                    conflict.Requirement.VersionRange,
                    conflict.Requirement.ModuleAbi),
                conflict.Rid,
                conflict.PackageId,
                conflict.TargetRole,
                conflict.CurrentRuntime is null
                    ? null
                    : new RegistryExecutionRuntimeIdentity(
                        conflict.CurrentRuntime.RuntimeId,
                        conflict.CurrentRuntime.Version,
                        conflict.CurrentRuntime.PackagingRevision,
                        conflict.CurrentRuntime.Rid,
                        conflict.CurrentRuntime.ModuleAbi),
                conflict.RequiredHostTrustGeneration))
            .ToArray();
        return conflicts.Length == 0
            ? []
            : [new RegistryRuntimeHardDenyEvidence(origin, plan.RuntimePolicyRevision, conflicts)];
    }

    private static IReadOnlyList<RegistryRuntimeHardDenyEvidence> CreateHardDenyEvidence(
        Uri origin,
        RegistryResolveInstallPlanResponse plan)
    {
        var conflicts = plan.RuntimeConflicts
            .Where(static conflict => IsHardDenyCode(conflict.Code))
            .ToArray();
        return conflicts.Length == 0
            ? []
            : [new RegistryRuntimeHardDenyEvidence(origin, plan.RuntimePolicyRevision, conflicts)];
    }

    private static bool IsHardDenyCode(string code)
        => code is ExecutionRuntimeErrorCodes.Revoked
            or ExecutionRuntimeErrorCodes.Retired
            or ExecutionRuntimeErrorCodes.CurrentRuntimeTrustDenied;

    private static RuntimeRegistryResolveInstallPlanResponse AddPlanWarnings(
        RuntimeRegistryResolveInstallPlanResponse plan,
        IReadOnlyList<string> warnings)
        => warnings.Count == 0
            ? plan
            : new RuntimeRegistryResolveInstallPlanResponse(
                plan.Success,
                plan.Items,
                plan.Warnings.Concat(warnings).ToArray(),
                plan.Errors,
                plan.Conflicts,
                plan.TrustedArtifactOrigins,
                plan.RequiredCapabilityConsents,
                plan.RuntimeArtifacts,
                plan.RuntimeConflicts,
                plan.ResolutionFingerprint,
                plan.RuntimePolicyRevision,
                plan.ExpiresAtUtc);

    private static bool RequiresLifecycleEvidenceUpdate(
        string packageId,
        IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord> currentBindings,
        IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord> desiredBindings)
    {
        if (currentBindings.Count != desiredBindings.Count)
        {
            throw InvalidKeepBinding(packageId);
        }
        var currentByTarget = currentBindings.ToDictionary(
            static binding => $"{binding.Role}\0{binding.Rid}",
            StringComparer.Ordinal);
        var changed = false;
        foreach (var desired in desiredBindings)
        {
            if (!currentByTarget.TryGetValue($"{desired.Role}\0{desired.Rid}", out var current)
                || !SameImmutableBinding(current, desired))
            {
                throw InvalidKeepBinding(packageId);
            }
            if (desired.RuntimePolicyRevision < current.RuntimePolicyRevision
                || desired.RuntimePolicyRevision == current.RuntimePolicyRevision
                && !string.Equals(
                    desired.RuntimeLifecycleState,
                    current.RuntimeLifecycleState,
                    StringComparison.Ordinal)
                || desired.RuntimePolicyRevision > current.RuntimePolicyRevision
                && !IsForwardLifecycleTransition(
                    current.RuntimeLifecycleState,
                    desired.RuntimeLifecycleState))
            {
                throw new InvalidDataException(
                    $"Registry keep action for package '{packageId}' carries stale or conflicting execution-runtime policy evidence.");
            }
            changed |= desired.RuntimePolicyRevision != current.RuntimePolicyRevision
                       || !string.Equals(
                           desired.RuntimeLifecycleState,
                           current.RuntimeLifecycleState,
                           StringComparison.Ordinal);
        }
        return changed;
    }

    private static bool IsForwardLifecycleTransition(string current, string desired)
        => current == desired
           || current == ExecutionRuntimeLifecycleStates.Supported
           || current == ExecutionRuntimeLifecycleStates.Deprecated
           && desired is ExecutionRuntimeLifecycleStates.Retired or ExecutionRuntimeLifecycleStates.Revoked
           || current == ExecutionRuntimeLifecycleStates.Retired
           && desired == ExecutionRuntimeLifecycleStates.Revoked;

    private static bool SameImmutableBinding(
        InstalledPackageExecutionRuntimeBindingRecord current,
        InstalledPackageExecutionRuntimeBindingRecord desired)
        => string.Equals(current.Role, desired.Role, StringComparison.Ordinal)
           && string.Equals(current.Rid, desired.Rid, StringComparison.Ordinal)
           && string.Equals(current.RuntimeId, desired.RuntimeId, StringComparison.Ordinal)
           && string.Equals(current.RuntimeVersion, desired.RuntimeVersion, StringComparison.Ordinal)
           && current.RuntimePackagingRevision == desired.RuntimePackagingRevision
           && string.Equals(current.ModuleAbi, desired.ModuleAbi, StringComparison.Ordinal)
           && string.Equals(current.RuntimeManifestSha256, desired.RuntimeManifestSha256, StringComparison.Ordinal)
           && string.Equals(current.RuntimeArtifactSha256, desired.RuntimeArtifactSha256, StringComparison.Ordinal)
           && current.RuntimeArtifactSize == desired.RuntimeArtifactSize
           && string.Equals(current.RegistryOrigin, desired.RegistryOrigin, StringComparison.Ordinal)
           && string.Equals(current.AcquisitionSource, desired.AcquisitionSource, StringComparison.Ordinal)
           && string.Equals(current.SigningKeyId, desired.SigningKeyId, StringComparison.Ordinal)
           && current.TrustStoreGeneration == desired.TrustStoreGeneration;

    private static InvalidDataException InvalidKeepBinding(string packageId)
        => new(
            $"Registry keep action for package '{packageId}' changed its immutable exact execution-runtime binding; a runtime-only rebind is required.");

    private static PackageExecutionRequest EnsureProvenanceChangesAreMaterialized(
        PackageExecutionRequest execution,
        IReadOnlyDictionary<string, InstalledPackageRecord> installed)
    {
        if (execution.Request.Reinstall)
        {
            return execution;
        }

        var origin = RegistryOrigin.Normalize(execution.Request.RegistryOrigin).AbsoluteUri;
        foreach (var change in execution.Request.Packages)
        {
            if (!installed.TryGetValue(change.PackageId, out var package))
            {
                continue;
            }

            var provenance = package.Provenance;
            var includePrereleasePolicy = execution.IncludePrereleasePolicyByPackageId.TryGetValue(
                package.PackageId,
                out var persistedIncludePrerelease)
                ? persistedIncludePrerelease
                : execution.Request.IncludePrerelease;
            if (execution.AdoptedPackageIds.Contains(package.PackageId)
                || provenance.SourceKind == InstalledPackageSourceKind.Registry
                && string.Equals(provenance.RegistryOrigin, origin, StringComparison.Ordinal)
                && !SelectionMatches(provenance, change, includePrereleasePolicy))
            {
                return execution with
                {
                    Request = execution.Request with { Reinstall = true },
                };
            }
        }
        return execution;
    }

    internal static IReadOnlyDictionary<string, InstalledPackageProvenanceRecord> BuildProvenanceByPackageId(
        Uri origin,
        RuntimeRegistryPackageBatchRequest request,
        IReadOnlySet<string> adoptedPackageIds,
        IReadOnlyList<RegistryPackageInstallPlanItem> items,
        IReadOnlyDictionary<string, InstalledPackageRecord> installed,
        IReadOnlyDictionary<string, bool>? includePrereleasePolicyByPackageId = null)
    {
        Dictionary<string, RuntimeRegistryPackageChangeRequest> requested;
        try
        {
            requested = request.Packages.ToDictionary(
                change => change.PackageId,
                StringComparer.OrdinalIgnoreCase);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "A Registry package transaction cannot request the same package more than once.",
                exception);
        }

        var normalizedOrigin = origin.AbsoluteUri;
        var result = new Dictionary<string, InstalledPackageProvenanceRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            installed.TryGetValue(item.PackageId, out var current);
            if (current is null && item.CurrentVersion is not null
                || current is not null && !string.Equals(current.Version, item.CurrentVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Registry plan state for package '{item.PackageId}' does not match the Runtime's installed catalog.");
            }

            requested.TryGetValue(item.PackageId, out var directRequest);
            var sourceAdoptionRequested = directRequest is not null
                && adoptedPackageIds.Contains(item.PackageId);
            var currentProvenance = current?.Provenance ?? InstalledPackageProvenanceRecord.Unknown;
            var sameRegistrySource = current is not null
                && currentProvenance.SourceKind == InstalledPackageSourceKind.Registry
                && string.Equals(currentProvenance.RegistryOrigin, normalizedOrigin, StringComparison.Ordinal)
                && string.Equals(currentProvenance.SourcePackageId, item.PackageId, StringComparison.Ordinal);
            if (current is not null && !sameRegistrySource && !sourceAdoptionRequested)
            {
                throw new InvalidDataException(
                    $"Package '{item.PackageId}' is managed by a different or unknown source. Explicit source adoption is required before replacing it from '{normalizedOrigin}'.");
            }
            if (directRequest is null
                && sameRegistrySource
                && currentProvenance.VersionPolicy == InstalledPackageVersionPolicy.ExplicitVersion
                && !string.Equals(current!.Version, item.Version, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Registry plan would change explicitly pinned package '{item.PackageId}' from version '{current.Version}' to '{item.Version}'.");
            }

            InstalledPackageProvenanceRecord provenance;
            if (directRequest is not null)
            {
                var includePrereleasePolicy = includePrereleasePolicyByPackageId is not null
                    && includePrereleasePolicyByPackageId.TryGetValue(item.PackageId, out var persistedIncludePrerelease)
                        ? persistedIncludePrerelease
                        : request.IncludePrerelease;
                provenance = CreateRegistryProvenance(
                    normalizedOrigin,
                    item,
                    directRequest,
                    includePrereleasePolicy);
            }
            else if (sameRegistrySource)
            {
                provenance = currentProvenance;
            }
            else
            {
                provenance = new InstalledPackageProvenanceRecord(
                    InstalledPackageSourceKind.Registry,
                    InstalledPackageVersionPolicy.TransitiveDependency,
                    normalizedOrigin,
                    item.PackageId,
                    SourceIdentity: new string('0', 64));
            }

            if (!result.TryAdd(item.PackageId, provenance))
            {
                throw new InvalidDataException($"Registry plan contains duplicate package '{item.PackageId}'.");
            }
        }
        return result;
    }

    private static InstalledPackageProvenanceRecord CreateRegistryProvenance(
        string origin,
        RegistryPackageInstallPlanItem item,
        RuntimeRegistryPackageChangeRequest request,
        bool includePrerelease)
    {
        if (!string.IsNullOrWhiteSpace(request.Version))
        {
            if (!string.Equals(request.Version, item.Version, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Registry resolved package '{item.PackageId}' to '{item.Version}' instead of explicitly requested version '{request.Version}'.");
            }
            return new InstalledPackageProvenanceRecord(
                InstalledPackageSourceKind.Registry,
                InstalledPackageVersionPolicy.ExplicitVersion,
                origin,
                item.PackageId,
                RequestedVersion: request.Version,
                SourceIdentity: new string('0', 64),
                IncludePrerelease: includePrerelease);
        }

        return new InstalledPackageProvenanceRecord(
            InstalledPackageSourceKind.Registry,
            InstalledPackageVersionPolicy.FollowTag,
            origin,
            item.PackageId,
            RequestedTag: string.IsNullOrWhiteSpace(request.Tag) ? "latest" : request.Tag.Trim(),
            VersionRange: request.VersionRange,
            SourceIdentity: new string('0', 64),
            IncludePrerelease: includePrerelease);
    }

    private static bool SelectionMatches(
        InstalledPackageProvenanceRecord provenance,
        RuntimeRegistryPackageChangeRequest request,
        bool includePrerelease)
        => provenance.IncludePrerelease == includePrerelease
           && (!string.IsNullOrWhiteSpace(request.Version)
               ? provenance.VersionPolicy == InstalledPackageVersionPolicy.ExplicitVersion
                 && string.Equals(provenance.RequestedVersion, request.Version, StringComparison.Ordinal)
               : provenance.VersionPolicy == InstalledPackageVersionPolicy.FollowTag
                 && string.Equals(
                     provenance.RequestedTag,
                     string.IsNullOrWhiteSpace(request.Tag) ? "latest" : request.Tag.Trim(),
                     StringComparison.Ordinal)
                 && string.Equals(provenance.VersionRange, request.VersionRange, StringComparison.Ordinal));

    private static void ValidateUniquePlanPackages(IReadOnlyList<ResolvedPackageExecution> resolved)
    {
        var originsByPackageId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resolution in resolved)
        {
            foreach (var item in resolution.Plan.Items)
            {
                if (originsByPackageId.TryAdd(item.PackageId, resolution.Origin.AbsoluteUri))
                {
                    continue;
                }
                throw new InvalidDataException(
                    $"Package '{item.PackageId}' was selected by more than one Registry plan; no package changes were committed.");
            }
        }
    }

    private static RuntimeRegistryPackageChangeResult PlanFailed(
        IReadOnlyList<ResolvedPackageExecution> resolved,
        RegistryResolveInstallPlanResponse plan,
        IReadOnlyList<string>? enforcementWarnings = null)
    {
        var runtimePlan = RuntimeRegistryContractMapper.ToRuntime(plan);
        var errors = plan.Errors
            .Concat(plan.Conflicts.Select(static conflict => string.IsNullOrWhiteSpace(conflict.Message)
                ? $"Package '{conflict.PackageId}' conflicts with the requested change."
                : conflict.Message))
            .Concat(plan.RuntimeConflicts.Select(conflict => conflict.Message))
            .DefaultIfEmpty("Package plan resolution failed.")
            .ToArray();
        return new RuntimeRegistryPackageChangeResult(
            false,
            RuntimeRegistryErrorCode.Conflict,
            errors[0],
            false,
            false,
            ResolvedWarnings(resolved)
                .Concat(plan.Warnings)
                .Concat(enforcementWarnings ?? [])
                .ToArray(),
            errors,
            [],
            ResolvedPlanItems(resolved)
                .Concat(runtimePlan.Items)
                .ToArray())
        {
            Conflicts = runtimePlan.Conflicts,
            RuntimeArtifacts = resolved
                .SelectMany(static resolution =>
                    RuntimeRegistryContractMapper.ToRuntime(resolution.Plan).RuntimeArtifacts)
                .Concat(runtimePlan.RuntimeArtifacts)
                .ToArray(),
            RuntimeConflicts = runtimePlan.RuntimeConflicts,
        };
    }

    private static RuntimeRegistryPackageChangeResult PlanEnforcementFailed(
        IReadOnlyList<ResolvedPackageExecution> resolved,
        RegistryResolveInstallPlanResponse plan,
        string message)
    {
        var runtimePlan = RuntimeRegistryContractMapper.ToRuntime(plan);
        return Failed(
            message,
            RuntimeRegistryErrorCode.Conflict,
            ResolvedWarnings(resolved).Concat(plan.Warnings).ToArray(),
            planItems: ResolvedPlanItems(resolved).Concat(runtimePlan.Items).ToArray(),
            conflicts: runtimePlan.Conflicts,
            runtimeConflicts: runtimePlan.RuntimeConflicts,
            runtimeArtifacts: runtimePlan.RuntimeArtifacts);
    }

    private static RuntimeRegistryPackageChangeResult StalePreview(
        IReadOnlyList<ResolvedPackageExecution> resolved,
        RegistryResolveInstallPlanResponse plan,
        IReadOnlyList<string>? enforcementWarnings = null)
    {
        var runtimePlan = RuntimeRegistryContractMapper.ToRuntime(plan);
        return Failed(
            StalePreviewMessage,
            RuntimeRegistryErrorCode.StalePlan,
            ResolvedWarnings(resolved)
                .Concat(plan.Warnings)
                .Concat(enforcementWarnings ?? [])
                .ToArray(),
            planItems: ResolvedPlanItems(resolved)
                .Concat(runtimePlan.Items)
                .ToArray(),
            conflicts: runtimePlan.Conflicts,
            runtimeConflicts: runtimePlan.RuntimeConflicts,
            runtimeArtifacts: runtimePlan.RuntimeArtifacts);
    }

    private static bool ConfirmsExpectedPreview(
        RegistryResolveInstallPlanResponse plan,
        string expectedResolutionFingerprint,
        DateTimeOffset now)
        => plan.Success
           && plan.Errors.Count == 0
           && plan.Conflicts.Count == 0
           && plan.RuntimeConflicts.Count == 0
           && string.Equals(
               plan.ResolutionFingerprint,
               expectedResolutionFingerprint,
               StringComparison.Ordinal)
           && plan.ExpiresAtUtc > now;

    private static IReadOnlyDictionary<(string Origin, bool IncludePrerelease), string> NormalizeExpectedPlans(
        IReadOnlyList<RuntimeRegistryPackagePlanExpectation> expectations)
    {
        var result = new Dictionary<(string Origin, bool IncludePrerelease), string>();
        foreach (var expectation in expectations)
        {
            if (expectation is null
                || !PackageSensitiveCapabilityPolicy.IsSha256(expectation.ExpectedResolutionFingerprint))
            {
                throw new ArgumentException(
                    "Expected Registry package plan fingerprints must be lower-case SHA-256 digests.");
            }
            var key = (
                RegistryOrigin.Normalize(expectation.RegistryOrigin).AbsoluteUri,
                expectation.IncludePrerelease);
            if (!result.TryAdd(key, expectation.ExpectedResolutionFingerprint))
            {
                throw new ArgumentException(
                    $"An expected Registry package plan is declared more than once for '{key.Item1}'.");
            }
        }
        return result;
    }

    private static bool ExpectedPlansMatchGroups(
        IReadOnlyDictionary<(string Origin, bool IncludePrerelease), string> expectedPlans,
        IEnumerable<(string Origin, bool IncludePrerelease)> groupKeys)
    {
        var actual = groupKeys
            .Select(static key => (RegistryOrigin.Normalize(key.Origin).AbsoluteUri, key.IncludePrerelease))
            .ToHashSet();
        return actual.Count == expectedPlans.Count && actual.SetEquals(expectedPlans.Keys);
    }

    private static IReadOnlyList<string> ResolvedWarnings(IReadOnlyList<ResolvedPackageExecution> resolved)
        => resolved.SelectMany(resolution => resolution.Plan.Warnings).ToArray();

    private static IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> ResolvedPlanItems(
        IReadOnlyList<ResolvedPackageExecution> resolved)
        => RuntimeRegistryContractMapper.ToRuntime(
            resolved.SelectMany(resolution => resolution.Plan.Items).ToArray());

    private static RuntimeRegistryPackageChangeResult AddContext(
        RuntimeRegistryPackageChangeResult result,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> skippedPackageIds)
        => new(
            result.Success,
            result.ErrorCode,
            result.Message,
            result.RuntimeSessionApplied,
            result.RequiresAppRestart,
            warnings.Concat(result.Warnings).ToArray(),
            result.Errors,
            result.ImpactedPackageIds,
            result.PlanItems,
            result.RequiredCapabilityConsents)
        {
            CommittedStamp = result.CommittedStamp,
            Conflicts = result.Conflicts,
            PlanExpectations = result.PlanExpectations,
            RuntimeArtifacts = result.RuntimeArtifacts,
            RuntimeConflicts = result.RuntimeConflicts,
            SkippedPackageIds = skippedPackageIds
                .Concat(result.SkippedPackageIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };

    private static RuntimeRegistryPackageChangeResult WithMessage(
        RuntimeRegistryPackageChangeResult result,
        string message)
        => new(
            result.Success,
            result.ErrorCode,
            message,
            result.RuntimeSessionApplied,
            result.RequiresAppRestart,
            result.Warnings,
            result.Errors,
            result.ImpactedPackageIds,
            result.PlanItems,
            result.RequiredCapabilityConsents)
        {
            CommittedStamp = result.CommittedStamp,
            Conflicts = result.Conflicts,
            PlanExpectations = result.PlanExpectations,
            RuntimeArtifacts = result.RuntimeArtifacts,
            RuntimeConflicts = result.RuntimeConflicts,
            SkippedPackageIds = result.SkippedPackageIds,
        };

    private static RuntimeRegistryPackageChangeResult Failed(
        string message,
        RuntimeRegistryErrorCode errorCode,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<string>? errors = null,
        IReadOnlyList<RuntimeRegistryPackageInstallPlanItem>? planItems = null,
        IReadOnlyList<PackageCapabilityConsent>? requiredCapabilityConsents = null,
        IReadOnlyList<RuntimeRegistryPackageInstallPlanConflict>? conflicts = null,
        IReadOnlyList<RuntimeRegistryExecutionRuntimeResolutionConflict>? runtimeConflicts = null,
        IReadOnlyList<RuntimeRegistryResolvedExecutionRuntimeArtifact>? runtimeArtifacts = null)
        => new(
            false,
            errorCode,
            message,
            false,
            false,
            warnings ?? [],
            errors ?? [message],
            [],
            planItems ?? [],
            requiredCapabilityConsents ?? [])
        {
            Conflicts = conflicts ?? [],
            RuntimeArtifacts = runtimeArtifacts ?? [],
            RuntimeConflicts = runtimeConflicts ?? [],
        };

    internal static bool RequiresReinstall(string? currentVersion, string version, bool requested)
        => requested
           || currentVersion is not null
           && string.Equals(currentVersion, version, StringComparison.Ordinal);

    private sealed record PackageExecutionRequest(
        RuntimeRegistryPackageBatchRequest Request,
        IReadOnlySet<string> AdoptedPackageIds,
        IReadOnlyDictionary<string, bool> IncludePrereleasePolicyByPackageId);

    private sealed record PackageUpdateSelection(
        RuntimeRegistryPackageChangeRequest Change,
        bool IncludePrereleasePolicy);

    private sealed record ResolvedPackageExecution(
        PackageExecutionRequest Execution,
        Uri Origin,
        RegistryResolveInstallPlanResponse Plan,
        IReadOnlyDictionary<string, InstalledPackageProvenanceRecord> ProvenanceByPackageId);

    private sealed record RegistryRuntimeHardDenyEvidence(
        Uri RegistryOrigin,
        long RuntimePolicyRevision,
        IReadOnlyList<RegistryExecutionRuntimeResolutionConflict> Conflicts);

    private sealed class RegistryExecutionRuntimeHardDenyException(
        IReadOnlyList<RegistryRuntimeHardDenyEvidence> evidence)
        : Exception(
            evidence.SelectMany(static item => item.Conflicts)
                .Select(static conflict => conflict.Message)
                .FirstOrDefault()
            ?? "Registry denied an exact currently bound execution runtime.")
    {
        public IReadOnlyList<RegistryRuntimeHardDenyEvidence> Evidence { get; } = evidence;
    }
}
