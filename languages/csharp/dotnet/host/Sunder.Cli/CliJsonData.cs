using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli;

internal static class CliJsonData
{
    public static object PackageSummaries(IEnumerable<RegistryPackageSummary> packages)
        => packages.Select(package => new
        {
            packageId = package.PackageId,
            name = package.Name,
            summary = package.Summary,
            latestVersion = package.LatestVersion,
            yanked = package.IsYanked,
            createdAtUtc = package.CreatedAtUtc,
            updatedAtUtc = package.UpdatedAtUtc,
        }).ToArray();

    public static object PackageDetails(RegistryPackageDetails package) => new
    {
        packageId = package.PackageId,
        name = package.Name,
        summary = package.Summary,
        latestVersion = package.LatestVersion,
        versions = package.Versions.Select(version => new
        {
            version = version.Version,
            yanked = version.IsYanked,
            deprecatedMessage = version.DeprecatedMessage,
            publishedAtUtc = version.PublishedAtUtc,
        }).ToArray(),
        createdAtUtc = package.CreatedAtUtc,
        updatedAtUtc = package.UpdatedAtUtc,
    };

    public static object PackageVersion(RegistryPackageVersionDetails version) => new
    {
        packageId = version.PackageId,
        name = version.Name,
        summary = version.Summary,
        version = version.Version,
        yanked = version.IsYanked,
        deprecatedMessage = version.DeprecatedMessage,
        manifestFormatVersion = version.ManifestFormatVersion,
        archiveFormatVersion = version.ArchiveFormatVersion,
        dependencies = version.DependsOn.Select(item => new { packageId = item.PackageId, versionRange = item.VersionRange }).ToArray(),
        targets = version.Targets.Select(target => new
        {
            role = target.Role,
            rid = target.Rid,
            kind = target.Kind,
            entryPoint = target.EntryPoint,
            targetFramework = target.TargetFramework,
        }).ToArray(),
        contracts = new
        {
            bundles = version.ContractBundles.Select(item => new
            {
                contractId = item.ContractId,
                version = item.Version,
                descriptorPath = item.DescriptorPath,
                descriptorSha256 = item.DescriptorSha256,
                abiSha256 = item.AbiSha256,
                exposure = item.Exposure,
            }).ToArray(),
            uses = version.UsesContracts.Select(item => new
            {
                role = item.Role,
                contractId = item.ContractId,
                contractVersion = item.ContractVersion,
                abiSha256 = item.AbiSha256,
                required = item.Required,
                actions = item.Actions,
            }).ToArray(),
            providers = version.Providers.Select(item => new
            {
                providerId = item.ProviderId,
                role = item.Role,
                contractId = item.ContractId,
                contractVersion = item.ContractVersion,
                descriptorSha256 = item.DescriptorSha256,
                abiSha256 = item.AbiSha256,
                audience = item.Audience,
            }).ToArray(),
        },
        artifact = new { sha256 = version.CanonicalArtifact.Sha256, size = version.CanonicalArtifact.Size },
        projectionCount = version.Projections.Count,
        publishedAtUtc = version.PublishedAtUtc,
    };

    public static object StackSummaries(IEnumerable<RegistryStackSummary> stacks)
        => stacks.Select(stack => new
        {
            stackId = stack.StackId,
            name = stack.Name,
            summary = stack.Summary,
            packageCount = stack.PackageCount,
            fragmentCount = stack.FragmentCount,
            createdAtUtc = stack.CreatedAtUtc,
            updatedAtUtc = stack.UpdatedAtUtc,
        }).ToArray();

    public static object StackDetails(RegistryStackDetails stack) => new
    {
        stackId = stack.StackId,
        name = stack.Name,
        summary = stack.Summary,
        packages = stack.Packages.Select(package => new
        {
            packageId = package.PackageId,
            installTag = package.InstallTag,
            minimumVersion = package.MinimumVersion,
            createdWithVersion = package.CreatedWithVersion,
            required = package.Required,
        }).ToArray(),
        fragments = stack.Fragments.Select(fragment => new
        {
            fragmentId = fragment.FragmentId,
            ownerPackageId = fragment.OwnerPackageId,
            displayName = fragment.DisplayName,
            defaultSelected = fragment.DefaultSelected,
        }).ToArray(),
        requiredInputs = stack.RequiredInputs.Select(input => new
        {
            inputId = input.InputId,
            ownerPackageId = input.OwnerPackageId,
            label = input.Label,
            required = input.Required,
        }).ToArray(),
        artifact = new { sha256 = stack.Artifact.Sha256, size = stack.Artifact.Size },
        createdAtUtc = stack.CreatedAtUtc,
        updatedAtUtc = stack.UpdatedAtUtc,
    };

    public static object PackageStatus(
        InstalledPackageDescriptor? installed,
        SessionPackageDescriptor? session,
        bool active,
        RuntimePackageSnapshot snapshot,
        IReadOnlyList<CliPackageRuntimeStatus> executionRuntimes) => new
    {
        packageId = session?.PackageId ?? installed!.PackageId,
        installed = installed is null ? null : new
        {
            name = installed.Name,
            version = installed.Version,
            enabled = installed.IsEnabled,
            installedAtUtc = installed.InstalledAtUtc,
            statusMessage = installed.StatusMessage,
            source = installed.Provenance.SourceKind.ToString().ToLowerInvariant(),
            versionPolicy = installed.Provenance.VersionPolicy.ToString().ToLowerInvariant(),
            registryOrigin = installed.Provenance.RegistryOrigin,
            tag = installed.Provenance.RequestedTag,
            requestedVersion = installed.Provenance.RequestedVersion,
        },
        session = session is null ? null : new
        {
            displayName = session.DisplayName,
            version = session.Version,
            enabled = session.IsEnabled,
            active,
            readiness = session.Readiness.ToString().ToLowerInvariant(),
            failureOrigin = session.FailureOrigin?.ToString().ToLowerInvariant(),
            lastError = session.LastError,
            lastFailureAtUtc = session.LastFailureAtUtc,
            failureCount = session.FailureCount,
        },
        runtime = new
        {
            instanceId = snapshot.RuntimeInstanceId,
            sessionGeneration = snapshot.SessionGeneration,
            bootstrapState = snapshot.BootstrapState.ToString().ToLowerInvariant(),
        },
        executionRuntimes = executionRuntimes.Select(status => new
        {
            target = new
            {
                role = status.Binding.Role,
                rid = status.Binding.Rid,
            },
            requirement = new
            {
                runtimeId = status.Binding.Requirement.RuntimeId,
                versionRange = status.Binding.Requirement.VersionRange,
                moduleAbi = status.Binding.Requirement.ModuleAbi,
            },
            binding = new
            {
                runtimeId = status.Binding.Runtime.RuntimeId,
                sourceId = status.Binding.Runtime.SourceId.ToString().ToLowerInvariant(),
                vendorVersion = status.Binding.Runtime.VendorVersion,
                rid = status.Binding.Runtime.Rid,
                moduleAbi = status.Binding.Runtime.ModuleAbi,
                vendorChecksumAlgorithm = status.Binding.Runtime.VendorChecksumAlgorithm.ToString().ToLowerInvariant(),
                vendorChecksumDigest = status.Binding.Runtime.VendorChecksumDigest,
            },
            installed = status.InstalledRuntime is not null,
            status = status.InstalledRuntime?.Status ?? "missing",
            health = status.InstalledRuntime is null ? null : new
            {
                status = status.InstalledRuntime.Health.Status,
                checkedAtUtc = status.InstalledRuntime.Health.CheckedAtUtc,
                repairRecommended = status.InstalledRuntime.Health.RepairRecommended,
                message = status.InstalledRuntime.Health.Message,
            },
            support = status.InstalledRuntime?.Support,
            actions = new
            {
                ensureRequired = status.EnsureRequired,
                repairRequired = status.RepairRequired,
                rebindRequired = status.RebindRequired,
                updateRecommended = status.UpdateRecommended,
                ensureCommand = status.EnsureCommand,
                repairCommand = status.RepairCommand,
                rebindPreflightCommand = status.RebindPreflightCommand,
                rebindApplyCommand = status.RebindApplyCommand,
                impactedPackageIds = status.ImpactedPackageIds,
                message = status.ActionMessage,
            },
        }).ToArray(),
    };

    public static object RegistryPackageChange(RuntimeRegistryPackageChangeResult result) => new
    {
        success = result.Success,
        errorCode = RegistryErrorCode(result.ErrorCode),
        message = result.Message,
        runtimeSessionApplied = result.RuntimeSessionApplied,
        requiresAppRestart = result.RequiresAppRestart,
        warnings = result.Warnings,
        errors = result.Errors,
        impactedPackageIds = result.ImpactedPackageIds,
        plan = result.PlanItems.Select(PlanItem).ToArray(),
        requiredCapabilityConsents = result.RequiredCapabilityConsents.Select(consent => new
        {
            packageId = consent.PackageId,
            packageVersion = consent.PackageVersion,
            manifestSha256 = consent.ManifestSha256,
            capabilities = consent.Capabilities,
        }).ToArray(),
    };

    public static object RegistryPackageExecution(
        CliPackageChangePreflight preflight,
        bool dryRun,
        RuntimeRegistryPackageChangeResult? result) => new
    {
        mode = dryRun ? "dry-run" : "apply",
        applied = result is not null,
        preflight = new
        {
            success = preflight.Success,
            packageDownloadBytes = preflight.PackageDownloadBytes,
            fullRuntimeDownloadBytes = preflight.FullRuntimeDownloadBytes,
            incrementalRuntimeDownloadBytes = preflight.IncrementalRuntimeDownloadBytes,
            reusedRuntimeBytes = preflight.ReusedRuntimeBytes,
            fullDownloadBytes = preflight.FullDownloadBytes,
            incrementalDownloadBytes = preflight.IncrementalDownloadBytes,
            planExpectations = preflight.PlanExpectations.Select(expectation => new
            {
                registryOrigin = expectation.RegistryOrigin,
                includePrerelease = expectation.IncludePrerelease,
                expectedResolutionFingerprint = expectation.ExpectedResolutionFingerprint,
            }).ToArray(),
            plan = preflight.Items.Select(PlanItem).ToArray(),
            runtimeDownloads = preflight.RuntimeDownloads.Select(download => new
            {
                identity = new
                {
                    runtimeId = download.Resolution.Artifact.Identity.RuntimeId,
                    sourceId = download.Resolution.Artifact.Identity.SourceId.ToString().ToLowerInvariant(),
                    vendorVersion = download.Resolution.Artifact.Identity.VendorVersion,
                    rid = download.Resolution.Artifact.Identity.Rid,
                    moduleAbi = download.Resolution.Artifact.Identity.ModuleAbi,
                    vendorChecksumAlgorithm = download.Resolution.Artifact.Identity.VendorChecksumAlgorithm.ToString().ToLowerInvariant(),
                    vendorChecksumDigest = download.Resolution.Artifact.Identity.VendorChecksumDigest,
                },
                fullBytes = download.FullBytes,
                incrementalBytes = download.IncrementalBytes,
                alreadyInstalledAndHealthy = download.AlreadyInstalled,
                support = download.Resolution.Artifact.Support,
            }).ToArray(),
            conflicts = preflight.Conflicts.Select(conflict => new
            {
                packageId = conflict.PackageId,
                currentVersion = conflict.CurrentVersion,
                requestedVersionRange = conflict.RequestedVersionRange,
                requiredByPackageId = conflict.RequiredByPackageId,
                code = conflict.ErrorCode,
                message = conflict.Message,
            }).ToArray(),
            requiredCapabilityConsents = preflight.RequiredCapabilityConsents.Select(consent => new
            {
                packageId = consent.PackageId,
                packageVersion = consent.PackageVersion,
                manifestSha256 = consent.ManifestSha256,
                capabilities = consent.Capabilities,
            }).ToArray(),
            skippedPackageIds = preflight.SkippedPackageIds,
            warnings = preflight.Warnings,
            errors = preflight.Errors,
        },
        result = result is null ? null : RegistryPackageChange(result),
    };

    public static object PackageOperation(PackageOperationResult result) => new
    {
        success = result.Success,
        message = result.Message,
        runtimeSessionApplied = result.RuntimeSessionApplied,
        appShellApplied = result.AppShellApplied,
        requiresAppRestart = result.RequiresAppRestart,
        impactedPackageIds = result.ImpactedPackageIds,
        warnings = result.Warnings,
        errors = result.Errors,
    };

    public static object PackageValidation(SunderPackageArchiveValidationResult result) => new
    {
        valid = result.Success,
        package = result.Manifest is null ? null : new
        {
            id = result.Manifest.Id,
            name = result.Manifest.Name,
            version = result.Manifest.Version,
            summary = result.Manifest.Summary,
            manifestVersion = result.Manifest.ManifestVersion,
            archiveFormatVersion = result.Manifest.ArchiveFormatVersion,
        },
        warnings = result.Warnings,
        errors = result.Errors,
    };

    public static object StackValidation(SunderStackArchiveValidationResult result) => new
    {
        valid = result.Success,
        stack = result.Manifest is null ? null : new
        {
            stackId = result.Manifest.StackId,
            name = result.Manifest.Name,
            summary = result.Manifest.Summary,
            schemaVersion = result.Manifest.SchemaVersion,
            minReaderVersion = result.Manifest.MinReaderVersion,
            packageCount = result.Manifest.Packages?.Count ?? 0,
            fragmentCount = result.Manifest.Fragments?.Count ?? 0,
        },
        warnings = result.Warnings,
        errors = result.Errors,
    };

    public static object PackagePublish(RegistryPublishPackageResponse result) => new
    {
        success = result.Success,
        packageId = result.PackageId,
        version = result.Version,
        message = result.Message,
        warnings = result.Warnings,
        errors = result.Errors,
    };

    public static object StackPublish(RegistryPublishStackResponse result) => new
    {
        success = result.Success,
        stackId = result.StackId,
        message = result.Message,
        warnings = result.Warnings,
        errors = result.Errors,
    };

    public static object Management(bool success, string? message, IReadOnlyList<string> errors)
        => new { success, message, errors };

    public static object RegistryAuthSession(RuntimeRegistryAuthSessionStatus? status)
        => status is null ? new { found = false } : new
        {
            found = true,
            state = status.State.ToString().ToLowerInvariant(),
            user = User(status.User),
            expiresAtUtc = status.CredentialExpiresAtUtc,
            errorCode = RegistryErrorCode(status.ErrorCode),
            message = status.Message,
        };

    public static object RegistryAuthStatus(RuntimeRegistryAuthStatus status) => new
    {
        signedIn = status.IsSignedIn,
        user = User(status.User),
        expiresAtUtc = status.ExpiresAtUtc,
        errorCode = RegistryErrorCode(status.ErrorCode),
        message = status.Message,
    };

    public static string RegistryErrorCode(RuntimeRegistryErrorCode code) => code switch
    {
        RuntimeRegistryErrorCode.None => "none",
        RuntimeRegistryErrorCode.InvalidRequest => "invalid_request",
        RuntimeRegistryErrorCode.AuthenticationRequired => "authentication_required",
        RuntimeRegistryErrorCode.Forbidden => "forbidden",
        RuntimeRegistryErrorCode.NotFound => "not_found",
        RuntimeRegistryErrorCode.Conflict => "conflict",
        RuntimeRegistryErrorCode.RegistryUnavailable => "registry_unavailable",
        RuntimeRegistryErrorCode.DownloadTooLarge => "download_too_large",
        RuntimeRegistryErrorCode.ArtifactVerificationFailed => "artifact_verification_failed",
        RuntimeRegistryErrorCode.ConsentRequired => "consent_required",
        RuntimeRegistryErrorCode.StalePlan => "stale_plan",
        RuntimeRegistryErrorCode.Cancelled => "cancelled",
        _ => "internal_error",
    };

    private static object? User(RuntimeRegistryUser? user)
        => user is null ? null : new
        {
            userId = user.UserId,
            username = user.Username,
            displayName = user.DisplayName,
        };

    private static object PlanItem(RuntimeRegistryPackageInstallPlanItem item) => new
    {
        packageId = item.PackageId,
        currentVersion = item.CurrentVersion,
        version = item.Version,
        actionKind = item.ActionKind,
        deprecatedMessage = item.DeprecatedMessage,
    };
}
