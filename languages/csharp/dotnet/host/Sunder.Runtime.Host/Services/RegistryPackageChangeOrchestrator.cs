using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RegistryPackageChangeOrchestrator(
    RegistryPackagePlanResolver planResolver,
    RuntimeContentTransferStore transferStore,
    RegistryPackageArtifactDownloader artifactDownloader,
    InstalledPackageLifecycleService installedPackages,
    InstalledPackageStore installedPackageStore,
    ILogger<RegistryPackageChangeOrchestrator> logger)
{
    private static readonly IReadOnlySet<string> NoSourceAdoptions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<string, bool> NoPrereleasePolicyOverrides =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    public async Task<RuntimeRegistryResolveInstallPlanResponse> ResolveAsync(
        RuntimeRegistryPackageBatchRequest request,
        CancellationToken cancellationToken)
        => await planResolver.ResolveAsync(request, cancellationToken);

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
                request.Reinstall),
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
            var provenance = package.Provenance ?? InstalledPackageProvenanceRecord.Unknown;
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
            .Select(group => new PackageExecutionRequest(
                new RuntimeRegistryPackageBatchRequest(
                    group.Key.Origin,
                    group.Value.Select(selection => selection.Change).ToArray(),
                    group.Key.IncludePrerelease),
                NoSourceAdoptions,
                group.Value.ToDictionary(
                    selection => selection.Change.PackageId,
                    selection => selection.IncludePrereleasePolicy,
                    StringComparer.OrdinalIgnoreCase)))
            .ToArray();
        var result = await ExecuteAtomicAsync(executions, dryRun: false, cancellationToken);
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
            request.AllowDowngrade);
        var adoptedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            package.PackageId,
        };
        var result = await ExecuteAtomicAsync(
            [new PackageExecutionRequest(batch, adoptedPackageIds, NoPrereleasePolicyOverrides)],
            request.DryRun,
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
            cancellationToken);

    private async Task<RuntimeRegistryPackageChangeResult> ExecuteAtomicAsync(
        IReadOnlyList<PackageExecutionRequest> executions,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var resolved = new List<ResolvedPackageExecution>(executions.Count);
        IReadOnlyList<RegistryPackageStateExpectation> stateExpectations = [];
        try
        {
            var installed = (await installedPackageStore.ListAsync(cancellationToken))
                .ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
            foreach (var execution in executions)
            {
                var materialized = EnsureProvenanceChangesAreMaterialized(execution, installed);
                var resolution = await planResolver.ResolveForExecutionAsync(
                    materialized.Request,
                    cancellationToken);
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
            stateExpectations = BuildStateExpectations(installed, resolved);
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
        catch (Exception exception)
        {
            logger.LogWarning("Registry package plan failed: {ErrorType}", exception.GetType().Name);
            return Failed(exception.Message, RuntimeRegistryErrorCode.RegistryUnavailable, ResolvedWarnings(resolved), planItems: ResolvedPlanItems(resolved));
        }

        var planWarnings = ResolvedWarnings(resolved);
        var planItems = ResolvedPlanItems(resolved);
        if (dryRun)
        {
            return new RuntimeRegistryPackageChangeResult(
                true,
                RuntimeRegistryErrorCode.None,
                "Registry package plan resolved; no changes were applied.",
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
        var provenanceByUploadId = new Dictionary<string, InstalledPackageProvenanceRecord>(StringComparer.Ordinal);
        string? stageId = null;
        var artifactsPrepared = false;
        try
        {
            var mutations = new List<PackageStoreMutationRequest>(planItems.Count);
            foreach (var resolution in resolved)
            {
                foreach (var item in resolution.Plan.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var upload = await artifactDownloader.DownloadAsync(
                        resolution.Origin,
                        item,
                        resolution.Plan.TrustedArtifactOrigins,
                        cancellationToken);
                    uploadIds.Add(upload.UploadId);
                    provenanceByUploadId.Add(
                        upload.UploadId,
                        resolution.ProvenanceByPackageId[item.PackageId] with
                        {
                            SourceIdentity = upload.ContentHash,
                        });
                    mutations.Add(new PackageStoreMutationRequest(
                        item.CurrentVersion is null ? PackageStoreMutationKind.Install : PackageStoreMutationKind.Upgrade,
                        item.CurrentVersion is null ? null : item.PackageId,
                        upload.UploadId,
                        resolution.Execution.Request.AllowDowngrade,
                        RequiresReinstall(
                            item.CurrentVersion,
                            item.Version,
                            resolution.Execution.Request.Reinstall)));
                }
            }
            artifactsPrepared = true;

            var stage = await installedPackages.StageRegistryAsync(
                new PackageStoreStageRequest(mutations),
                provenanceByUploadId,
                stateExpectations,
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

            var commit = await installedPackages.CommitStageAsync(stageId, cancellationToken);
            stageId = null;
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
        catch (Exception exception)
        {
            if (stageId is not null)
            {
                await installedPackages.DiscardStageAsync(stageId, CancellationToken.None);
            }
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

            var provenance = package.Provenance ?? InstalledPackageProvenanceRecord.Unknown;
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

    private static IReadOnlyList<RegistryPackageStateExpectation> BuildStateExpectations(
        IReadOnlyDictionary<string, InstalledPackageRecord> installed,
        IReadOnlyList<ResolvedPackageExecution> resolved)
    {
        var expectations = installed.Values.ToDictionary(
            package => package.PackageId,
            package => new RegistryPackageStateExpectation(
                package.PackageId,
                package.Version,
                package.Provenance ?? InstalledPackageProvenanceRecord.Unknown),
            StringComparer.OrdinalIgnoreCase);
        foreach (var item in resolved.SelectMany(resolution => resolution.Plan.Items))
        {
            expectations.TryAdd(
                item.PackageId,
                new RegistryPackageStateExpectation(item.PackageId, Version: null, Provenance: null));
        }
        return expectations.Values
            .OrderBy(expectation => expectation.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
        var errors = plan.Errors
            .Concat(plan.Conflicts.Select(conflict => conflict.Message))
            .DefaultIfEmpty("Package plan resolution failed.")
            .ToArray();
        return new RuntimeRegistryPackageChangeResult(
            false,
            RuntimeRegistryErrorCode.Conflict,
            errors[0],
            false,
            false,
            ResolvedWarnings(resolved).Concat(plan.Warnings).ToArray(),
            errors,
            [],
            ResolvedPlanItems(resolved)
                .Concat(RuntimeRegistryContractMapper.ToRuntime(plan.Items))
                .ToArray());
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
            result.PlanItems)
        {
            CommittedStamp = result.CommittedStamp,
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
            result.PlanItems)
        {
            CommittedStamp = result.CommittedStamp,
            SkippedPackageIds = result.SkippedPackageIds,
        };

    private static RuntimeRegistryPackageChangeResult Failed(
        string message,
        RuntimeRegistryErrorCode errorCode,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<string>? errors = null,
        IReadOnlyList<RuntimeRegistryPackageInstallPlanItem>? planItems = null)
        => new(
            false,
            errorCode,
            message,
            false,
            false,
            warnings ?? [],
            errors ?? [message],
            [],
            planItems ?? []);

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
