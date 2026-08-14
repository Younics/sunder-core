using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Compatibility;

namespace Sunder.Runtime.Host.Services;

internal static class PackageSensitiveCapabilityPolicy
{
    private static readonly HashSet<string> SensitiveCapabilities = new(StringComparer.Ordinal)
    {
        SunderSdkCapabilities.DockerEnvironmentV1,
    };

    public static IReadOnlyList<string> GetRequiredCapabilities(SunderPackageManifest manifest)
        => (manifest.Targets ?? [])
            .Where(static target => target is not null
                                    && string.Equals(target.Role, SunderPackageFormat.RuntimeHostRole, StringComparison.Ordinal)
                                    && string.Equals(target.Kind, SunderPackageFormat.WorkerTargetKind, StringComparison.Ordinal))
            .SelectMany(static target => target!.RequiredHostCapabilities ?? [])
            .Where(static capability => capability is not null && SensitiveCapabilities.Contains(capability))
            .Select(static capability => capability!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    public static IReadOnlyList<string> GetRequiredCapabilities(
        IReadOnlyList<RuntimeRegistryPackageTarget> targets)
        => targets
            .Where(static target => string.Equals(target.Role, SunderPackageFormat.RuntimeHostRole, StringComparison.Ordinal)
                                    && string.Equals(target.Kind, SunderPackageFormat.WorkerTargetKind, StringComparison.Ordinal))
            .SelectMany(static target => target.RequiredHostCapabilities)
            .Where(SensitiveCapabilities.Contains)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    public static string GetManifestSha256(SunderPackageContentIndex contentIndex)
        => contentIndex.Files?.Single(entry => string.Equals(
            entry?.Path,
            SunderPackageFormat.ManifestPath,
            StringComparison.Ordinal)).Sha256
           ?? throw new InvalidDataException("Package content index is missing its manifest hash.");

    public static string GetManifestSha256(InstalledPackageRecord package)
        => package.ContentInventory.Single(entry => string.Equals(
            entry.Path,
            SunderPackageFormat.ManifestPath,
            StringComparison.Ordinal)).Sha256;

    public static string? AuthorizeCandidate(
        PreparedPackageArchiveMutation candidate,
        InstalledPackageRecord? current,
        PackageCapabilityConsent? consent,
        out IReadOnlyList<InstalledPackageCapabilityGrant> grants)
    {
        var inherited = GetEffectiveCapabilities(current);
        var requiredConsent = candidate.SensitiveCapabilities
            .Where(capability => !inherited.Contains(capability))
            .ToArray();
        if (requiredConsent.Length > 0)
        {
            if (!Matches(
                    consent,
                    candidate.InstalledRecord.PackageId,
                    candidate.InstalledRecord.Version,
                    candidate.ConsentManifestSha256,
                    requiredConsent))
            {
                grants = [];
                return RequiredConsentMessage(
                    candidate.InstalledRecord.PackageId,
                    candidate.InstalledRecord.Version,
                    candidate.ConsentManifestSha256,
                    requiredConsent);
            }
        }
        else if (consent is not null && consent.Capabilities.Count > 0)
        {
            grants = [];
            return $"Sensitive capability consent for package '{candidate.InstalledRecord.PackageId}' is stale or was not required by this exact package change.";
        }

        grants = candidate.SensitiveCapabilities
            .Select(capability => new InstalledPackageCapabilityGrant(
                capability,
                candidate.InstalledRecord.Version,
                candidate.ActiveManifestSha256))
            .ToArray();
        return null;
    }

    public static IReadOnlyList<PackageCapabilityConsent> GetRequiredConsents(
        IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> planItems,
        IReadOnlyDictionary<string, InstalledPackageRecord> installed)
    {
        var requirements = new List<PackageCapabilityConsent>();
        foreach (var item in planItems)
        {
            var sensitive = GetRequiredCapabilities(item.Targets);
            if (sensitive.Count == 0)
            {
                continue;
            }
            installed.TryGetValue(item.PackageId, out var current);
            var inherited = GetEffectiveCapabilities(current);
            var required = sensitive.Where(capability => !inherited.Contains(capability)).ToArray();
            if (required.Length == 0)
            {
                continue;
            }
            var hashes = item.Artifacts
                .Select(static artifact => artifact.ManifestSha256)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (hashes.Length != 1 || !IsSha256(hashes[0]))
            {
                throw new InvalidDataException(
                    $"Registry plan for package '{item.PackageId}' does not identify one exact source manifest for sensitive capability consent.");
            }
            requirements.Add(new PackageCapabilityConsent(
                item.PackageId,
                item.Version,
                hashes[0],
                required));
        }
        return requirements
            .OrderBy(static requirement => requirement.PackageId, StringComparer.Ordinal)
            .ToArray();
    }

    public static string? ValidateProvidedConsents(
        IReadOnlyList<PackageCapabilityConsent> required,
        IReadOnlyList<PackageCapabilityConsent>? provided)
    {
        provided ??= [];
        if (provided.Count != required.Count)
        {
            return required.Count == 0
                ? "Sensitive capability consent was supplied, but this exact Registry plan does not require it."
                : "The Registry package plan requires explicit consent for sensitive worker capabilities.";
        }
        var unmatched = provided.ToList();
        foreach (var requirement in required)
        {
            var index = unmatched.FindIndex(candidate => Matches(
                candidate,
                requirement.PackageId,
                requirement.PackageVersion,
                requirement.ManifestSha256,
                requirement.Capabilities));
            if (index < 0)
            {
                return "Sensitive capability consent is missing, stale, or does not match the exact Registry package plan.";
            }
            unmatched.RemoveAt(index);
        }
        return null;
    }

    public static bool IsGranted(
        PreparedRuntimePackage package,
        string capability)
        => package.ApprovedSensitiveCapabilities.Contains(capability, StringComparer.Ordinal);

    public static bool IsSensitive(string capability) => SensitiveCapabilities.Contains(capability);

    internal static HashSet<string> GetEffectiveCapabilities(InstalledPackageRecord? package)
    {
        if (package is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
        var manifestSha256 = GetManifestSha256(package);
        return package.CapabilityGrants
            .Where(grant => string.Equals(grant.PackageVersion, package.Version, StringComparison.Ordinal)
                            && string.Equals(grant.ManifestSha256, manifestSha256, StringComparison.Ordinal)
                            && SensitiveCapabilities.Contains(grant.Capability))
            .Select(static grant => grant.Capability)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool Matches(
        PackageCapabilityConsent? consent,
        string packageId,
        string packageVersion,
        string manifestSha256,
        IReadOnlyList<string> capabilities)
        => consent is not null
           && string.Equals(consent.PackageId, packageId, StringComparison.Ordinal)
           && string.Equals(consent.PackageVersion, packageVersion, StringComparison.Ordinal)
           && string.Equals(consent.ManifestSha256, manifestSha256, StringComparison.Ordinal)
           && consent.Capabilities.Count == capabilities.Count
           && consent.Capabilities.Distinct(StringComparer.Ordinal).Count() == consent.Capabilities.Count
           && consent.Capabilities.All(capability => capabilities.Contains(capability, StringComparer.Ordinal));

    private static string RequiredConsentMessage(
        string packageId,
        string packageVersion,
        string manifestSha256,
        IReadOnlyList<string> capabilities)
        => $"Installing package '{packageId}' version '{packageVersion}' with source manifest '{manifestSha256}' requires explicit sensitive capability consent for: {string.Join(", ", capabilities)}.";

    internal static bool IsSha256(string? value)
        => value is { Length: 64 }
           && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
