using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using static Sunder.Runtime.Host.Services.ExecutionRuntimeManagementSupport;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageRuntimePrerequisiteService(
    RegistryPackagePlanResolver registryPlans, RuntimeContentTransferStore transfers, RuntimeSessionOwner sessions,
    SunderPackageArchiveInstaller installer, LocalExecutionRuntimeBindingResolver local,
    ExecutionRuntimeInstallationService installation, InstalledPackageStore installed)
{
    public async Task<RuntimePackageRuntimePrerequisitePlan> PreviewAsync(RuntimePackageRuntimePrerequisiteRequest request, CancellationToken token)
    {
        if (request is null || (request.Registry is null ? 0 : 1) + (request.Local is null ? 0 : 1) + (request.Adoption is null ? 0 : 1) != 1)
            throw new RuntimeValidationException("Select exactly one Registry, adoption, or uploaded package source.");
        RuntimePackageRuntimePrerequisitePlan result;
        if (request.Adoption is { } adoption)
        {
            var package = (await installed.ListAsync(token)).SingleOrDefault(value => string.Equals(value.PackageId, adoption.PackageId, StringComparison.OrdinalIgnoreCase))
                ?? throw new RuntimeNotFoundException("The package selected for source adoption is not installed.");
            var batch = RegistrySourceAdoptionSelection.Create(adoption, package);
            var resolution = await registryPlans.ResolveForExecutionAsync(batch, batch.ExpectedResolutionFingerprint, token);
            if (!resolution.Plan.Success) throw new RuntimeConflictException(string.Join("; ", resolution.Plan.Errors));
            result = await InspectRegistryAsync([resolution], token, [package]);
            CheckExpectation(result, adoption.RuntimePrerequisiteExpectation);
        }
        else if (request.Registry is { } registry)
        {
            var resolution = await registryPlans.ResolveForExecutionAsync(registry, registry.ExpectedResolutionFingerprint, token);
            if (!resolution.Plan.Success) throw new RuntimeConflictException(string.Join("; ", resolution.Plan.Errors));
            result = await InspectRegistryAsync([resolution], token);
        }
        else result = await InspectUploadsAsync(request.Local!, token);
        CheckExpectation(result, request.ExpectedSourceFingerprint is null ? null : new(request.ExpectedSourceFingerprint, Rid));
        return result;
    }

    internal async Task<RuntimePackageRuntimePrerequisitePlan> InspectRegistryAsync(
        IReadOnlyList<RegistryPackagePlanResolution> contexts, CancellationToken token,
        IReadOnlyList<InstalledPackageRecord>? sourceAdoptions = null)
    {
        var requirements = new List<RuntimePackageRuntimePrerequisite>();
        foreach (var context in contexts)
        foreach (var item in context.Plan.Items)
        foreach (var target in item.Targets.Where(value => value.ExecutionRuntime is not null && value.Rid == Rid))
        {
            var requirement = new RuntimeExecutionRuntimeRequirement(
                target.ExecutionRuntime!.RuntimeId,
                target.ExecutionRuntime.VersionRange,
                target.ExecutionRuntime.ModuleAbi);
            var reference = new RuntimeExecutionRuntimePackageReference(item.PackageId, item.Version!, item.PackageContentIdentity, target.Role, target.Rid);
            var receipt = await local.FindHealthyAsync(requirement, target.Rid, token).ConfigureAwait(false);
            RuntimeExecutionRuntimeInstallPlan? install = null;
            if (receipt is null) install = await installation.PreviewAsync(new(requirement, true), token).ConfigureAwait(false);
            var manifestHash = item.Artifacts.FirstOrDefault()?.ManifestSha256;
            if (manifestHash is null)
            {
                var existing = (await installed.ListAsync(token)).Single(value => value.PackageId == item.PackageId);
                var validated = await InstalledPackageProjection.ValidateAsync(existing, token);
                if (!validated.Success || validated.ContentIndex is null) throw Stale();
                manifestHash = PackageSensitiveCapabilityPolicy.GetManifestSha256(validated.ContentIndex);
            }
            requirements.Add(new(reference, manifestHash, requirement,
                receipt is not null, receipt is null ? ExecutionRuntimeErrorCodes.Missing : null,
                receipt?.Identity.ToContract(), install));
        }
        var sources = contexts.OrderBy(value => value.Origin.AbsoluteUri, StringComparer.Ordinal).Select(value => new
        {
            Origin = value.Origin.AbsoluteUri, Rid,
            Packages = value.Plan.Items.Select(item => new { item.PackageId, item.Version, item.PackageContentIdentity, item.Targets,
                Artifacts = item.Artifacts.Select(artifact => new { artifact.Kind, artifact.Rid, artifact.Sha256, artifact.Size, artifact.ManifestSha256 }) }),
        }).ToArray();
        // Runtime availability/policy refresh does not change source identity. Adoption also
        // pins the actual installed origin/version/content; clients cannot manufacture it.
        var fingerprint = sourceAdoptions is not { Count: > 0 } ? Hash(sources) : Hash(new
        {
            Plans = sources,
            SourceAdoptions = sourceAdoptions.OrderBy(value => value.PackageId, StringComparer.Ordinal)
                .Select(value => new { value.PackageId, value.Version, value.ContentIdentity, value.Provenance }).ToArray(),
        });
        return Result(fingerprint, requirements);
    }

    internal async Task<RuntimePackageRuntimePrerequisitePlan> InspectUploadsAsync(PackageStoreStageRequest request,
        CancellationToken token)
    {
        if (request.Mutations.Count > RuntimeExecutionRuntimeManagementLimits.MaximumPackageSources)
            throw new RuntimeValidationException("Too many package sources.");
        var mutations = new List<PackageStoreMutation>();
        foreach (var mutation in request.Mutations.Where(value => value.Kind is PackageStoreMutationKind.Install or PackageStoreMutationKind.Upgrade))
        {
            // Non-consuming lookup does not transfer ownership. ReleaseUpload would delete
            // the input needed for resumption; its existing expiry/cancel path owns cleanup.
            var lease = transfers.AcquireUpload(mutation.UploadId!, RuntimeUploadKind.Package, sessions.Generation, consume: false, mutation.UploadRetention)
                ?? throw new RuntimeValidationException("Package upload expired; upload again before resuming.");
            mutations.Add(new(mutation.Kind, mutation.PackageId, lease.FilePath, mutation.AllowDowngrade, mutation.Reinstall,
                InstalledPackageProvenanceRecord.LocalArchive(lease.ContentHash)));
        }
        var result = await InspectLocalAsync(mutations, token);
        CheckExpectation(result, request.RuntimePrerequisiteExpectation);
        return result;
    }

    internal async Task<RuntimePackageRuntimePrerequisitePlan> InspectLocalAsync(IReadOnlyList<PackageStoreMutation> mutations,
        CancellationToken token)
    {
        var requirements = new List<RuntimePackageRuntimePrerequisite>();
        var sourceEvidence = new List<object>();
        foreach (var mutation in mutations.Where(value => value.Kind is PackageStoreMutationKind.Install or PackageStoreMutationKind.Upgrade))
        {
            var preparation = await installer.PrepareAsync(mutation.ArchiveFilePath!, true, token, mutation.Provenance,
                mutation.ConsentManifestSha256, mutation.RegistryProjectionTargets);
            var archive = preparation.Mutation ?? throw new RuntimeValidationException(preparation.Failure?.Message ?? "Package inspection failed.");
            try
            {
                sourceEvidence.Add(new { mutation.Kind, mutation.PackageId, mutation.AllowDowngrade, mutation.Reinstall,
                    archive.InstalledRecord.SourcePackageContentIdentity, archive.ConsentManifestSha256, Rid, archive.InstalledRecord.Provenance });
                foreach (var target in archive.Manifest.Targets!.Where(value => value?.ExecutionRuntime is not null && value.Rid == Rid).Select(value => value!))
                {
                    var requirement = new RuntimeExecutionRuntimeRequirement(target.ExecutionRuntime!.Id!, target.ExecutionRuntime.VersionRange!, target.ExecutionRuntime.ModuleAbi!);
                    RuntimeExecutionRuntimeIdentity? installedRuntime = null;
                    string? error = null;
                    try
                    {
                        var receipt = await local.FindHealthyAsync(requirement, target.Rid!, token).ConfigureAwait(false);
                        installedRuntime = receipt?.Identity.ToContract();
                    }
                    catch (Exception exception) when (exception is InvalidDataException)
                    {
                        error = ExecutionRuntimeErrorCatalog.All.FirstOrDefault(value => exception.Message.StartsWith(value.Code + ":", StringComparison.Ordinal))?.Code
                            ?? ExecutionRuntimeErrorCodes.IntegrityFailed;
                    }
                    var install = installedRuntime is null ? await installation.PreviewAsync(new(requirement, true), token).ConfigureAwait(false) : null;
                    requirements.Add(new(new(archive.Manifest.Id!, archive.Manifest.Version!, archive.InstalledRecord.SourcePackageContentIdentity!, target.Role!, target.Rid!),
                        archive.ConsentManifestSha256, requirement, installedRuntime is not null, error ?? (installedRuntime is null ? ExecutionRuntimeErrorCodes.Missing : null), installedRuntime, install));
                }
            }
            finally { SunderPackageArchiveInstaller.TryDeleteDirectory(archive.StagingPath); }
        }
        return Result(Hash(sourceEvidence), requirements);
    }

    internal static void CheckExpectation(RuntimePackageRuntimePrerequisitePlan result, RuntimePackageRuntimePrerequisiteExpectation? expectation)
    {
        if (expectation is not null && (expectation.Rid != Rid || expectation.SourceFingerprint != result.SourceFingerprint)) throw Stale();
    }
    internal static PackageOperationResult Blocked(RuntimePackageRuntimePrerequisitePlan result)
        => PackageOperationResults.Failure(result.Message, [result.ErrorCode ?? ExecutionRuntimeErrorCodes.Missing]) with { RuntimePrerequisites = result };
    private static RuntimePackageRuntimePrerequisitePlan Result(string fingerprint, IReadOnlyList<RuntimePackageRuntimePrerequisite> requirements)
        => new(true, requirements.All(value => value.Satisfied), requirements.FirstOrDefault(value => !value.Satisfied)?.ErrorCode,
            requirements.All(value => value.Satisfied) ? "Required runtimes are installed. Revalidate package consent and install."
                : "Download and install the required .NET or Node runtime first. Open Settings → Runtimes, then resume this installation, or cancel.",
            fingerprint, Rid, requirements, []);
}
