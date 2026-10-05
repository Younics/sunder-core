using Sunder.Registry.Contracts;
using Sunder.Sdk.Packaging;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class RegistryActivePolicyClient
{
    internal static void ValidateRequest(RegistryPolicySynchronizationRequest request)
    {
        Require(Hex(request.Nonce, RegistryPolicyLimits.NonceBytes * 2), "Invalid policy nonce.");
        Set(request.Packages, RegistryPolicyLimits.MaximumPackageSubjects, PackageKey, PackageIdentity);
        Set(request.PlatformCatalogs, RegistryPolicyLimits.MaximumPlatformSubjects, PlatformKey, PlatformIdentity);
        Set(request.Runtimes, RegistryPolicyLimits.MaximumRuntimeSubjects, RuntimeKey, RuntimeIdentity);
        Require(request.Packages.Count + request.PlatformCatalogs.Count + request.Runtimes.Count > 0, "At least one policy subject is required.");
        Require(request.MinimumPackagePolicy is null || request.MinimumPackagePolicy.Revision > 0 && Hash(request.MinimumPackagePolicy.Fingerprint), "Invalid package policy floor.");
        Require(request.MinimumRuntimePolicyRevision >= 0, "Invalid runtime policy floor.");
        Require(request.MinimumCheckpoint is null || request.MinimumCheckpoint.Sequence > 0 && Hash(request.MinimumCheckpoint.Digest)
            && request.MinimumCheckpoint.TopologyGeneration > 0 && Hash(request.MinimumCheckpoint.TopologyFingerprint), "Invalid checkpoint floor.");
    }

    internal static void ValidatePackage(RegistryPackagePolicyDecision value)
    {
        Require(PackageIdentity(value.Package), "Invalid package identity.");
        Set(value.PlatformDependencies, RegistryPolicyLimits.MaximumPlatformSubjects, PlatformKey, PlatformIdentity);
        Require(value.PackageState is "Active" or "SecurityBlocked" or "Unknown"
            && value.VersionAvailability is "Listed" or "Yanked" or "SecurityBlocked" or "Unknown", "Unknown package policy state.");
        var blocked = value.PackageState == "SecurityBlocked" || value.VersionAvailability == "SecurityBlocked";
        if (blocked)
        {
            Require(value.Disposition == "Denied" && value.ErrorCode == PackagePolicyErrorCodes.SecurityBlocked, "Contradictory package security block.");
        }
        else if (value.PackageState == "Unknown" || value.VersionAvailability == "Unknown")
        {
            Require(value.PackageState == "Unknown" && value.VersionAvailability == "Unknown" && value.Disposition == "PolicyRequired"
                && value.ErrorCode == PackagePolicyErrorCodes.PolicyRequired && value.PlatformDependencies.Count == 0, "Contradictory unknown package policy.");
        }
        else
        {
            Require(value.Disposition switch
            {
                "Allowed" => value.ErrorCode is null,
                "Denied" => value.ErrorCode == PackagePolicyErrorCodes.ContractAuthorityRevoked && value.PlatformDependencies.Count > 0,
                "PolicyRequired" => value.ErrorCode == PackagePolicyErrorCodes.PolicyRequired && value.PlatformDependencies.Count > 0,
                _ => false,
            }, "Contradictory package permission.");
        }
    }

    internal static void ValidatePlatform(RegistryPlatformPolicyDecision value, DateTimeOffset observedAt)
    {
        Require(PlatformIdentity(value.Catalog), "Invalid platform identity.");
        Require(value.LifecycleState switch
        {
            "Supported" => value.Disposition == "Allowed" && value.ErrorCode is null && value.RevokedAtUtc is null,
            "Revoked" => value.Disposition == "Denied" && value.ErrorCode == PackagePolicyErrorCodes.ContractAuthorityRevoked
                && value.RevokedAtUtc is { Offset: var offset } time && offset == TimeSpan.Zero && time <= observedAt,
            "Unknown" => value.Disposition == "PolicyRequired" && value.ErrorCode == PackagePolicyErrorCodes.PolicyRequired && value.RevokedAtUtc is null,
            _ => false,
        }, "Contradictory platform policy.");
    }

    internal static void ValidateRuntime(RegistryRuntimePolicyDecision value, DateTimeOffset observedAt)
    {
        Require(RuntimeIdentity(value.Subject), "Invalid runtime identity.");
        Require(value.ActivationBlockedAtUtc is null || value.ActivationBlockedAtUtc.Value.Offset == TimeSpan.Zero, "Invalid runtime cutoff.");
        if (value.LifecycleState == "policy-required")
        {
            Require(value.Disposition == "PolicyRequired" && value.ErrorCode == ExecutionRuntimeErrorCodes.PolicyRequired
                && value.ActivationBlockedAtUtc is null && value.RevokedAtUtc is null, "Contradictory unknown runtime policy.");
            return;
        }
        if (value.LifecycleState == "revoked")
        {
            Require(value.Disposition == "Denied" && value.ErrorCode == ExecutionRuntimeErrorCodes.Revoked
                && value.RevokedAtUtc is { Offset: var offset } revoked && offset == TimeSpan.Zero && revoked <= observedAt, "Contradictory runtime revocation.");
            return;
        }
        Require(value.LifecycleState is "supported" or "deprecated" or "retired" && value.RevokedAtUtc is null
            && (value.LifecycleState != "supported" || value.ActivationBlockedAtUtc is null)
            && (value.LifecycleState != "retired" || value.ActivationBlockedAtUtc is not null), "Invalid runtime lifecycle.");
        var blocked = value.ActivationBlockedAtUtc <= observedAt;
        Require(blocked
            ? value.Disposition == "Denied" && value.ErrorCode == (value.LifecycleState == "retired" ? ExecutionRuntimeErrorCodes.Retired : ExecutionRuntimeErrorCodes.Deprecated)
            : value.Disposition == "Allowed" && value.ErrorCode is null, "Contradictory runtime execution permission.");
    }

    private static void Set<T>(IReadOnlyList<T> values, int limit, Func<T, string> key, Func<T, bool> valid)
    {
        Require(values.Count <= limit, "Policy subject count exceeds its bound.");
        string? previous = null;
        foreach (var value in values)
        {
            Require(value is not null && valid(value), "Invalid policy subject.");
            var current = key(value!);
            Require(previous is null || StringComparer.Ordinal.Compare(previous, current) < 0, "Policy subjects must be canonical, sorted and distinct.");
            previous = current;
        }
    }

    private static bool PackageIdentity(RegistryPackageVersionIdentity value) => Id(value.PackageId) && Version(value.Version) && Hash(value.PackageContentIdentity);
    private static bool PlatformIdentity(RegistryPlatformCatalogIdentity value) => value.AuthorityId == "sunder.core" && Version(value.ReleaseVersion) && Hash(value.CatalogSha256);
    private static bool RuntimeIdentity(RegistryRuntimePolicySubject value) => value.Runtime is { } runtime && Id(runtime.RuntimeId) && Version(runtime.Version)
        && runtime.PackagingRevision > 0 && runtime.Rid is "win-x64" or "win-arm64" or "linux-x64" or "linux-arm64" or "osx-x64" or "osx-arm64"
        && runtime.ModuleAbi is { Length: > 0 and <= 128 } && runtime.ModuleAbi.Split('.', '-').All(segment => segment.Length > 0
            && segment.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9')) && Hash(value.ManifestSha256) && Hash(value.ArtifactSha256);
    private static bool Id(string value) => PackageId.TryParse(value, out var parsed) && parsed.ToString() == value;
    private static bool Version(string value) => value is { Length: >= 5 and <= 256 } && SemanticVersion.TryParse(value, out var parsed) && parsed.ToString() == value;
}
