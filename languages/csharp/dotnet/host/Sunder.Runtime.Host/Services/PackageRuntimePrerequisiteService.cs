using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using static Sunder.Runtime.Host.Services.ExecutionRuntimeManagementSupport;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageRuntimePrerequisiteService(
    RegistryPackagePlanResolver registryPlans, RuntimeContentTransferStore transfers, RuntimeSessionOwner sessions,
    SunderPackageArchiveInstaller installer, LocalExecutionRuntimeBindingResolver local,
    ExecutionRuntimeInstallationService installation, InstalledPackageStore installed,
    DevelopmentImportAuthorityStore developmentImports)
{
    public async Task<RuntimePackageRuntimePrerequisitePlan> PreviewAsync(RuntimePackageRuntimePrerequisiteRequest request, CancellationToken token)
    {
        if (request is null || (request.Registry is null ? 0 : 1) + (request.Local is null ? 0 : 1) + (request.Adoption is null ? 0 : 1)
            + (request.DevelopmentFolder is null ? 0 : 1) != 1)
            throw new RuntimeValidationException("Select exactly one Registry, adoption, uploaded package, or development folder source.");
        RuntimePackageRuntimePrerequisitePlan result;
        if (request.Adoption is { } adoption)
        {
            var package = (await installed.ListAsync(token)).SingleOrDefault(value => string.Equals(value.PackageId, adoption.PackageId, StringComparison.OrdinalIgnoreCase))
                ?? throw new RuntimeNotFoundException("The package selected for source adoption is not installed.");
            var batch = RegistrySourceAdoptionSelection.Create(adoption, package);
            var resolution = await registryPlans.ResolveAsync(batch, token);
            if (!resolution.Plan.Success) throw new RuntimeConflictException(string.Join("; ", resolution.Plan.Errors));
            result = await InspectRegistryAsync([resolution], token, [package]);
            CheckExpectation(result, adoption.RuntimePrerequisiteExpectation);
        }
        else if (request.Registry is { } registry)
        {
            var resolution = await registryPlans.ResolveAsync(registry, token);
            if (!resolution.Plan.Success) throw new RuntimeConflictException(string.Join("; ", resolution.Plan.Errors));
            result = await InspectRegistryAsync([resolution], token);
        }
        else if (request.DevelopmentFolder is { } folder) result = await InspectDevelopmentAsync(folder, token);
        else result = await InspectUploadsAsync(request.Local!, token);
        CheckExpectation(result, request.ExpectedSourceFingerprint is null ? null : new(request.ExpectedSourceFingerprint, Rid));
        return result;
    }

    internal async Task<RuntimePackageRuntimePrerequisitePlan> InspectDevelopmentAsync(string folder, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder.Length > 4096 || !Path.IsPathFullyQualified(folder))
            throw new RuntimeValidationException("Select an absolute built canonical development folder on the Host.");
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(folder, token);
        if (!validation.Success || validation.Manifest is null || validation.ContentIndex is null)
            throw new RuntimePackageValidationException("Build the canonical development folder before loading: " + string.Join(" | ", validation.Errors));
        var manifest = validation.Manifest;
        var manifestHash = RuntimeRpcPackageMetadata.GetManifestSha256(validation.ContentIndex);
        DevelopmentImportAuthority? imports = null;
        if (PackageCompatibilityAuthority.NeedsEvidence(manifest))
        {
            try
            {
                imports = await developmentImports.ResolveAsync(new(manifest.Id!, PackageSourceKind.Dev, folder,
                    Manifest: manifest, ContentIndex: validation.ContentIndex), token);
            }
            catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException)
            {
                throw new RuntimePackageValidationException("Development import evidence is missing or invalid. Run 'sunder dev package prepare-imports <folder>' "
                    + "with exact local --candidate/--platform-catalog inputs or an explicit --registry, then load again.");
            }
        }
        if ((manifest.Targets ?? []).Any(value => value?.Role == SunderPackageFormat.RuntimeHostRole)
            && !(manifest.Targets ?? []).Any(value => value?.Role == SunderPackageFormat.RuntimeHostRole && value.Rid == Rid))
            throw new RuntimePackageValidationException($"Build the canonical development package for the Host's exact RID '{Rid}' before loading.");
        var contentIdentity = Hash(validation.ContentIndex);
        var requirements = new List<RuntimePackageRuntimePrerequisite>();
        foreach (var target in (manifest.Targets ?? []).OfType<SunderPackageTargetManifest>().Where(value => value.Rid == Rid))
        {
            var errors = SunderSdkCompatibilityProfile.Validate(manifest.Id, new(target.Role!, target.Rid!), target);
            if (errors.Count != 0) throw new RuntimePackageValidationException(string.Join(" | ", errors));
            if (target.ExecutionRuntime is not { } runtime) continue;
            var requirement = new RuntimeExecutionRuntimeRequirement(runtime.Id!, runtime.VersionRange!, runtime.ModuleAbi!);
            var receipt = await local.FindHealthyAsync(requirement, Rid, token).ConfigureAwait(false);
            var install = receipt is null ? await installation.PreviewAsync(new(requirement, true), token).ConfigureAwait(false) : null;
            requirements.Add(new(new(manifest.Id!, manifest.Version!, contentIdentity, target.Role!, Rid), manifestHash, requirement,
                receipt is not null, receipt is null ? ExecutionRuntimeErrorCodes.Missing : null, receipt?.Identity.ToContract(), install));
        }
        return Result(Hash(new { Folder = folder, Content = contentIdentity, Imports = imports, Rid }), requirements)
            with { DevelopmentPackageId = manifest.Id, Message = requirements.All(value => value.Satisfied)
                ? "Development package runtimes are ready. Revalidate this source before acquiring its owner lease."
                : "Review and install the required runtimes before loading this development folder." };
    }

    internal async Task<RuntimePackageRuntimePrerequisitePlan> InspectRegistryAsync(
        IReadOnlyList<RegistryPackagePlanResolution> contexts, CancellationToken token,
        IReadOnlyList<InstalledPackageRecord>? sourceAdoptions = null)
    {
        var requirements = new List<RuntimePackageRuntimePrerequisite>();
        var sources = new List<object>();
        var kept = contexts.Any(context => context.Plan.Items.Any(item => item.ActionKind == RegistryPackageActionKinds.Keep))
            ? (await installed.ListAsync(token)).ToDictionary(package => package.PackageId, StringComparer.Ordinal) : null;
        foreach (var context in contexts.OrderBy(value => value.Origin.AbsoluteUri, StringComparer.Ordinal))
        foreach (var item in context.Plan.Items.OrderBy(value => value.PackageId, StringComparer.Ordinal))
        {
            InstalledPackageRecord? existing = null;
            RegistryAcquisition acquisition;
            if (item.ActionKind == RegistryPackageActionKinds.Keep)
            {
                if (!kept!.TryGetValue(item.PackageId, out existing) || existing.Version != item.Version
                    || InstalledPackagePolicyRecord.Subject(existing).PackageContentIdentity != item.PackageContentIdentity) throw Stale();
                var validated = await InstalledPackageProjection.ValidateAsync(existing, token);
                if (!validated.Success || validated.ContentIndex is null || validated.Manifest is null) throw Stale();
                acquisition = new(existing.Provenance.RegistryOrigin, existing.Provenance.ProjectionFingerprint,
                    PackageSensitiveCapabilityPolicy.GetManifestSha256(validated.ContentIndex),
                    InstalledPackageProjection.Keys(existing) ?? SunderPackageTargetResolver.EnumerateTargets(validated.Manifest),
                    PackageSensitiveCapabilityPolicy.GetEffectiveCapabilities(existing).Order(StringComparer.Ordinal).ToArray());
            }
            else
            {
                var keys = item.Artifacts.Where(value => value.Rid is not null)
                    .Select(value => new SunderPackageTargetKey(value.Kind, value.Rid!)).ToArray();
                var capabilities = item.Targets.Where(target => keys.Contains(new(target.Role, target.Rid))
                        && target.Role == SunderPackageFormat.RuntimeHostRole && target.Kind == SunderPackageFormat.WorkerTargetKind)
                    .SelectMany(target => target.RequiredHostCapabilities).Where(PackageSensitiveCapabilityPolicy.IsSensitive)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                acquisition = new(context.Origin.AbsoluteUri, ProjectionFingerprint(item), item.Artifacts.FirstOrDefault()?.ManifestSha256,
                    keys, capabilities);
            }
            acquisition = acquisition with { Targets = acquisition.Targets.OrderBy(key => key.Role, StringComparer.Ordinal).ThenBy(key => key.Rid, StringComparer.Ordinal).ToArray() };
            sources.Add(new { Origin = context.Origin.AbsoluteUri, Rid, item.PackageId, item.Version, item.PackageContentIdentity, item.Targets, acquisition });
            foreach (var target in item.Targets.Where(target => target.ExecutionRuntime is not null && target.Rid == Rid
                         && acquisition.Targets.Contains(new(target.Role, target.Rid))))
            {
                var runtime = target.ExecutionRuntime!;
                var requirement = new RuntimeExecutionRuntimeRequirement(runtime.RuntimeId, runtime.VersionRange, runtime.ModuleAbi);
                var reference = new RuntimeExecutionRuntimePackageReference(item.PackageId, item.Version!, item.PackageContentIdentity, target.Role, target.Rid);
                var receipt = existing is null ? await local.FindHealthyAsync(requirement, target.Rid, token)
                    : await local.VerifyKeptBindingAsync(existing.ExecutionRuntimeBindings.SingleOrDefault(binding =>
                        binding.Role == target.Role && binding.Rid == target.Rid && binding.Requirement == requirement) ?? throw Stale(), token);
                var install = receipt is null ? await installation.PreviewAsync(new(requirement, true), token) : null;
                requirements.Add(new(reference, acquisition.ManifestSha256 ?? throw Stale(), requirement,
                    receipt is not null, receipt is null ? ExecutionRuntimeErrorCodes.Missing : null, receipt?.Identity.ToContract(), install));
            }
        }
        // Runtime availability/policy refresh does not change source identity. Adoption also
        // pins the actual installed origin/version/content; clients cannot manufacture it.
        var fingerprint = Hash(new
        {
            Plans = sources,
            Runtimes = requirements.Select(value => new { value.Package, Identity = value.InstalledRuntime ?? value.Installation?.Resolution?.Artifact.Identity }),
            SourceAdoptions = (sourceAdoptions ?? []).OrderBy(value => value.PackageId, StringComparer.Ordinal)
                .Select(value => new { value.PackageId, value.Version, value.ContentIdentity, value.Provenance }).ToArray(),
        });
        return Result(fingerprint, requirements);
    }

    private sealed record RegistryAcquisition(string? Origin, string? Projections, string? ManifestSha256,
        IReadOnlyList<SunderPackageTargetKey> Targets, IReadOnlyList<string> Capabilities);

    internal static string ProjectionFingerprint(RegistryPackageInstallPlanItem item)
        => Hash(item.Artifacts.OrderBy(value => value.Kind, StringComparer.Ordinal).ThenBy(value => value.Rid, StringComparer.Ordinal)
            .Select(value => new { value.Kind, value.Rid, value.Size, value.Sha256, value.SourceArchiveSha256,
                value.ManifestSha256, value.ProjectionContentIdentity, value.ProjectionFormatVersion }).ToArray());

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
