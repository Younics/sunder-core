using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RegistryPackageChangeOrchestrator(
    RegistryPackagePlanResolver planResolver,
    RuntimeContentTransferStore transferStore,
    IHostExecutionRuntimeBindingPreparer runtimeBindingPreparer,
    RegistryPackageArtifactDownloader artifactDownloader,
    InstalledPackageLifecycleService installedPackages,
    InstalledPackageStore installedPackageStore,
    TimeProvider timeProvider,
    ILogger<RegistryPackageChangeOrchestrator> logger,
    IHostApplicationLifetime? applicationLifetime = null,
    PackageRuntimePrerequisiteService? prerequisites = null)
{
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
        if (!plan.Success)
        {
            return plan;
        }
        return plan with
        {
            RuntimePrerequisites = prerequisites is null ? null : await prerequisites.PreviewAsync(
                new(Registry: materialized.Request with { ExpectedResolutionFingerprint = plan.ResolutionFingerprint }), cancellationToken),
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
                request.ExpectedResolutionFingerprint) { RuntimePrerequisiteExpectation = request.RuntimePrerequisiteExpectation },
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
            // An empty authoritative selection is an explicit no-op, not an incomplete
            // preview. Use the same inspector/fingerprint rules as nonempty batches.
            var emptyPrerequisites = prerequisites is null ? null
                : await prerequisites.InspectRegistryAsync([], cancellationToken);
            if (emptyPrerequisites is not null)
            {
                try { PackageRuntimePrerequisiteService.CheckExpectation(emptyPrerequisites, request.RuntimePrerequisiteExpectation); }
                catch (RuntimeExecutionRuntimeException error) when (error.Code == ExecutionRuntimeErrorCodes.PlanStale)
                {
                    return AddContext(Failed(StalePreviewMessage, RuntimeRegistryErrorCode.StalePlan)
                        with { RuntimePrerequisites = emptyPrerequisites }, warnings, skipped);
                }
            }
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
                RuntimePrerequisites = emptyPrerequisites,
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
                        ExpectedResolutionFingerprint: expectedPlans.GetValueOrDefault(key)) { RuntimePrerequisiteExpectation = request.RuntimePrerequisiteExpectation },
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

        var batch = RegistrySourceAdoptionSelection.Create(request, package);
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
                    return StalePreview(resolved, resolution.Plan);
                }
                if (!resolution.Plan.Success)
                {
                    return PlanFailed(resolved, resolution.Plan);
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
        var planExpectations = resolved.Select(static resolution => new RuntimeRegistryPackagePlanExpectation(
            resolution.Origin.AbsoluteUri, resolution.Execution.Request.IncludePrerelease,
            resolution.Plan.ResolutionFingerprint)).ToArray();
        RuntimePackageRuntimePrerequisitePlan? runtimePrerequisites = null;

        // Carry the reviewed evidence on successes and rejections alike. The outcome still
        // determines authority: diagnostic evidence on a stale/failed result is not a permit.
        RuntimeRegistryPackageChangeResult ToResult(RuntimeRegistryPackageChangeResult result) => result with
        {
            PlanExpectations = result.PlanExpectations.Count == 0 ? planExpectations : result.PlanExpectations,
            RequiredCapabilityConsents = result.RequiredCapabilityConsents.Count == 0 ? requiredCapabilityConsents : result.RequiredCapabilityConsents,
            RuntimePrerequisites = result.RuntimePrerequisites ?? runtimePrerequisites,
        };

        if (prerequisites is not null)
        {
            runtimePrerequisites = await prerequisites.InspectRegistryAsync(
                resolved.Select(value => new RegistryPackagePlanResolution(value.Origin, value.Plan)).ToArray(), cancellationToken,
                executions.SelectMany(value => value.AdoptedPackageIds).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(packageId => installed[packageId]).ToArray());
            try
            {
                foreach (var execution in executions)
                    PackageRuntimePrerequisiteService.CheckExpectation(runtimePrerequisites, execution.Request.RuntimePrerequisiteExpectation);
            }
            catch (RuntimeExecutionRuntimeException error) when (error.Code == ExecutionRuntimeErrorCodes.PlanStale)
            {
                return ToResult(Failed("Package source or target OS changed while runtime prerequisites were being installed. Re-preview before resuming.",
                    RuntimeRegistryErrorCode.StalePlan, planWarnings, planItems: planItems));
            }
            if (!runtimePrerequisites.Satisfied)
                return ToResult(Failed(runtimePrerequisites.Message, RuntimeRegistryErrorCode.RuntimePrerequisitesRequired,
                    planWarnings, planItems: planItems, requiredCapabilityConsents: requiredCapabilityConsents));
        }
        if (dryRun)
        {
            var runtimePlans = resolved
                .Select(static resolution => RuntimeRegistryContractMapper.ToRuntime(resolution.Plan))
                .ToArray();
            return ToResult(new RuntimeRegistryPackageChangeResult(
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
            });
        }
        var consentError = PackageSensitiveCapabilityPolicy.ValidateProvidedConsents(
            requiredCapabilityConsents,
            providedCapabilityConsents);
        if (consentError is not null)
        {
            return ToResult(Failed(
                consentError,
                RuntimeRegistryErrorCode.ConsentRequired,
                planWarnings,
                planItems: planItems,
                requiredCapabilityConsents: requiredCapabilityConsents));
        }
        if (planItems.Count == 0)
        {
            return ToResult(new RuntimeRegistryPackageChangeResult(
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
            });
        }

        var uploadIds = new List<string>();
        var uploadMetadataById = new Dictionary<string, RegistryPackageUploadMetadata>(StringComparer.Ordinal);
        string? stageId = null;
        var artifactsPrepared = false;
        try
        {
            if (!PackageStorePolicy.CatalogBindingsEqual(
                    await installedPackageStore.ListAsync(cancellationToken).ConfigureAwait(false),
                    expectedCatalog))
            {
                return ToResult(Failed(
                    "Registry package staging is stale because the installed package catalog changed after resolution.",
                    RuntimeRegistryErrorCode.Conflict,
                    planWarnings,
                    planItems: planItems));
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

            if (mutations.Count == 0)
            {
                await ReconfirmResolutionAuthorityAsync(resolved, cancellationToken)
                    .ConfigureAwait(false);
                return ToResult(new RuntimeRegistryPackageChangeResult(
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
                });
            }

            var stage = await installedPackages.StageRegistryAsync(
                new PackageStoreStageRequest(mutations),
                uploadMetadataById,
                expectedCatalog,
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
                return ToResult(new RuntimeRegistryPackageChangeResult(
                    false,
                    RuntimeRegistryErrorCode.Conflict,
                    errors[0],
                    false,
                    false,
                    planWarnings.Concat(stage.Warnings).ToArray(),
                    errors,
                    stage.ImpactedPackageIds,
                    planItems) { RuntimePrerequisites = stage.OperationResult.RuntimePrerequisites });
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
            return ToResult(new RuntimeRegistryPackageChangeResult(
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
                RuntimePrerequisites = commit.RuntimePrerequisites,
            });
        }
        catch (OperationCanceledException)
        {
            if (stageId is not null)
            {
                await installedPackages.DiscardStageAsync(stageId, CancellationToken.None);
            }
            throw;
        }
        catch (InvalidDataException exception)
        {
            if (stageId is not null)
            {
                await installedPackages.DiscardStageAsync(stageId, CancellationToken.None);
            }
            return ToResult(Failed(
                exception.Message,
                artifactsPrepared ? RuntimeRegistryErrorCode.Conflict : RuntimeRegistryErrorCode.ArtifactVerificationFailed,
                planWarnings,
                planItems: planItems));
        }
        catch (RegistryArtifactTooLargeException exception)
        {
            if (stageId is not null)
            {
                await installedPackages.DiscardStageAsync(stageId, CancellationToken.None);
            }
            return ToResult(Failed(
                exception.Message,
                RuntimeRegistryErrorCode.DownloadTooLarge,
                planWarnings,
                planItems: planItems));
        }
        catch (PackagePolicyDeniedException exception)
        {
            if (stageId is not null) await installedPackages.DiscardStageAsync(stageId, CancellationToken.None);
            return ToResult(Failed(exception.Code, exception.Code == PackagePolicyErrorCodes.PlanStale ? RuntimeRegistryErrorCode.StalePlan : RuntimeRegistryErrorCode.Conflict,
                planWarnings, planItems: planItems));
        }
        catch (Exception exception)
        {
            if (stageId is not null)
            {
                await installedPackages.DiscardStageAsync(stageId, CancellationToken.None);
            }
            if (exception is RuntimeRequiredProviderException) throw;
            logger.LogWarning("Registry package transaction failed: {ErrorType}", exception.GetType().Name);
            return ToResult(Failed(
                exception.Message,
                RuntimeRegistryErrorCode.RegistryUnavailable,
                planWarnings,
                planItems: planItems));
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
            if (!plan.Success
                || plan.Errors.Count != 0
                || plan.Conflicts.Count != 0
                || !string.Equals(
                    plan.ResolutionFingerprint,
                    expectedFingerprint,
                    StringComparison.Ordinal)
                || plan.ExpiresAtUtc <= timeProvider.GetUtcNow())
            {
                throw new InvalidDataException(
                    "Registry package staging is stale because its resolution authority could not be reconfirmed.");
            }
        }
    }

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
        RegistryResolveInstallPlanResponse plan)
    {
        var runtimePlan = RuntimeRegistryContractMapper.ToRuntime(plan);
        var errors = plan.Errors
            .Concat(plan.Conflicts.Select(static conflict => string.IsNullOrWhiteSpace(conflict.Message)
                ? $"Package '{conflict.PackageId}' conflicts with the requested change."
                : conflict.Message))
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
                .ToArray(),
            errors,
            [],
            ResolvedPlanItems(resolved)
                .Concat(runtimePlan.Items)
                .ToArray())
        {
            Conflicts = runtimePlan.Conflicts,
        };
    }

    private static RuntimeRegistryPackageChangeResult StalePreview(
        IReadOnlyList<ResolvedPackageExecution> resolved,
        RegistryResolveInstallPlanResponse plan)
    {
        var runtimePlan = RuntimeRegistryContractMapper.ToRuntime(plan);
        return Failed(
            StalePreviewMessage,
            RuntimeRegistryErrorCode.StalePlan,
            ResolvedWarnings(resolved)
                .Concat(plan.Warnings)
                .ToArray(),
            planItems: ResolvedPlanItems(resolved)
                .Concat(runtimePlan.Items)
                .ToArray(),
            conflicts: runtimePlan.Conflicts);
    }

    private static bool ConfirmsExpectedPreview(
        RegistryResolveInstallPlanResponse plan,
        string expectedResolutionFingerprint,
        DateTimeOffset now)
        => plan.Success
           && plan.Errors.Count == 0
           && plan.Conflicts.Count == 0
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
        => result with
        {
            Warnings = warnings.Concat(result.Warnings).ToArray(),
            SkippedPackageIds = skippedPackageIds
                .Concat(result.SkippedPackageIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };

    private static RuntimeRegistryPackageChangeResult WithMessage(
        RuntimeRegistryPackageChangeResult result,
        string message)
        => result with { Message = message };

    private static RuntimeRegistryPackageChangeResult Failed(
        string message,
        RuntimeRegistryErrorCode errorCode,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<string>? errors = null,
        IReadOnlyList<RuntimeRegistryPackageInstallPlanItem>? planItems = null,
        IReadOnlyList<PackageCapabilityConsent>? requiredCapabilityConsents = null,
        IReadOnlyList<RuntimeRegistryPackageInstallPlanConflict>? conflicts = null)
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

}
