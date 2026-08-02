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
            bundles = version.ContractBundles.Select(item => new { contractId = item.ContractId, version = item.Version }).ToArray(),
            uses = version.UsesContracts.Select(item => new
            {
                contractId = item.ContractId,
                versionRange = item.VersionRange,
                required = item.Required,
            }).ToArray(),
            providers = version.Providers.Select(item => new
            {
                providerId = item.ProviderId,
                contractId = item.ContractId,
                contractVersion = item.ContractVersion,
                role = item.Role,
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
        plan = result.PlanItems.Select(item => new
        {
            packageId = item.PackageId,
            currentVersion = item.CurrentVersion,
            version = item.Version,
            update = item.IsUpdate,
            deprecatedMessage = item.DeprecatedMessage,
        }).ToArray(),
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
}
