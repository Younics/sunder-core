using Sunder.App.Services;
using Sunder.Registry.Contracts;
using Sunder.Package.Format;
using Sunder.Sdk.Compatibility;

namespace Sunder.App.ViewModels;

public sealed class PackageCapabilityConsentViewModel
{
    public PackageCapabilityConsentViewModel(RegistryPackageChangePreflight preflight)
    {
        PackageDownloadText = RegistryPackageChangePreflight.FormatBytes(preflight.PackageDownloadBytes);
        SharedRuntimeDownloadText = RegistryPackageChangePreflight.FormatBytes(preflight.SharedRuntimeDownloadBytes);
        TotalDownloadText = RegistryPackageChangePreflight.FormatBytes(preflight.TotalDownloadBytes);
        SharedRuntimeReuseText = preflight.ExecutionRuntimes.Any(item => !item.AlreadyInstalled)
            ? "Missing runtimes install first; packages wait in the queue."
            : preflight.ExecutionRuntimes.Count > 0 ? "Verified installed runtimes will be reused." : "No package runtime downloads are required.";
        RegistryPlans = preflight.ExpectedPlans
            .Select(static plan => new RegistryPlanConfirmationItemViewModel(
                plan.RegistryOrigin,
                plan.IncludePrerelease ? "Prerelease versions allowed" : "Stable versions only",
                plan.ExpectedResolutionFingerprint))
            .ToArray();
        Changes = preflight.Plan.Items
            .Select(static item => new PackageChangeConfirmationItemViewModel(
                item.PackageId,
                item.Version ?? item.CurrentVersion ?? "-",
                FormatAction(item.ActionKind)))
            .ToArray();
        var files = preflight.Plan.Items.SelectMany(item => item.Artifacts.Select(file =>
            $"{item.PackageId} · {file.Kind} {file.Rid} · {file.Size:N0} bytes\nSHA-256: {file.Sha256}\nManifest SHA-256: {file.ManifestSha256}"));
        var runtimes = preflight.ExecutionRuntimes.Select(runtime =>
            $"{RuntimeManagementUi.Identity(runtime.Identity)} · {runtime.Identity.Rid} · {ExactBytes(runtime.DownloadSize)}\n"
            + $"Vendor checksum ({runtime.Identity.VendorChecksumAlgorithm}): {runtime.Identity.VendorChecksumDigest}");
        var plans = RegistryPlans.Select(plan => $"{plan.RegistryOrigin} · {plan.PrereleasePolicyText}\nResolution: {plan.ResolutionFingerprint}");
        ReviewDetails = $"Package files: {ExactBytes(preflight.PackageDownloadBytes)}\nShared runtimes: {ExactBytes(preflight.SharedRuntimeDownloadBytes)}\nTotal: {ExactBytes(preflight.TotalDownloadBytes)}\n\n"
            + string.Join("\n\n", plans.Concat(files).Concat(runtimes));
        SensitiveAccess = preflight.Plan.Items.Where(item => item.ActionKind != RegistryPackageActionKinds.Keep).SelectMany(item =>
            PackageInformationViewModel.DescribeFeatures(item.Targets.Where(target => item.Artifacts.Any(file => file.Kind == target.Role && file.Rid == target.Rid))
                .SelectMany(target => target.RequiredHostCapabilities.Select(capability => new SunderPackageFeature(target.Role, target.Rid, capability))))
            .Where(feature => feature.IsSensitive && !(feature.Identifiers == SunderSdkCapabilities.DockerEnvironmentV1
                && preflight.Plan.RequiredCapabilityConsents.Any(consent => consent.PackageId == item.PackageId)))
            .Select(feature => $"{item.PackageId} · {feature.Title}\n{feature.Description}")).ToArray();
        ExecutionRuntimes = preflight.ExecutionRuntimes
            .Select(static runtime => new ExecutionRuntimeDownloadItemViewModel(
                RuntimeManagementUi.Identity(runtime.Identity),
                $"{runtime.Identity.Rid} / {runtime.Identity.ModuleAbi}",
                runtime.SourceText,
                runtime.AlreadyInstalled
                    ? "Already installed; 0 bytes"
                    : $"{RegistryPackageChangePreflight.FormatBytes(runtime.DownloadSize)} download",
                string.Empty))
            .ToArray();
        Warnings = preflight.Plan.Warnings
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Items = preflight.Plan.RequiredCapabilityConsents
            .Select(static requirement => new PackageCapabilityConsentItemViewModel(
                requirement.PackageId,
                requirement.PackageVersion,
                requirement.ManifestSha256,
                string.Join(", ", requirement.Capabilities)))
            .ToArray();
        ApprovalScopeText = "Only the reviewed packages, runtimes and permissions will be applied.";
    }

    public string PackageDownloadText { get; }

    public string SharedRuntimeDownloadText { get; }

    public string TotalDownloadText { get; }

    public string SharedRuntimeReuseText { get; }

    public IReadOnlyList<RegistryPlanConfirmationItemViewModel> RegistryPlans { get; }

    public bool HasRegistryPlans => RegistryPlans.Count > 0;

    public IReadOnlyList<PackageChangeConfirmationItemViewModel> Changes { get; }

    public bool HasChanges => Changes.Count > 0;

    public IReadOnlyList<ExecutionRuntimeDownloadItemViewModel> ExecutionRuntimes { get; }

    public bool HasExecutionRuntimes => ExecutionRuntimes.Count > 0;

    public IReadOnlyList<string> Warnings { get; }

    public bool HasWarnings => Warnings.Count > 0;

    public IReadOnlyList<PackageCapabilityConsentItemViewModel> Items { get; }

    public bool HasCapabilityConsents => Items.Count > 0;

    public string ApprovalScopeText { get; }
    public string ReviewDetails { get; }
    public IReadOnlyList<string> SensitiveAccess { get; }

    private static string ExactBytes(long? size) => size is null ? "Size unavailable" : $"{size:N0} bytes";

    private static string FormatAction(string action)
        => action switch
        {
            RegistryPackageActionKinds.PackageInstall => "Install",
            RegistryPackageActionKinds.PackageUpdate => "Update",
            RegistryPackageActionKinds.PackageRemove => "Remove",
            RegistryPackageActionKinds.Keep => "Keep",
            _ => action,
        };
}

public sealed record RegistryPlanConfirmationItemViewModel(
    string RegistryOrigin,
    string PrereleasePolicyText,
    string ResolutionFingerprint);

public sealed record PackageChangeConfirmationItemViewModel(
    string PackageId,
    string PackageVersion,
    string ActionText);

public sealed record ExecutionRuntimeDownloadItemViewModel(
    string IdentityText,
    string TargetText,
    string SourceText,
    string DownloadText,
    string DetailText);

public sealed record PackageCapabilityConsentItemViewModel(
    string PackageId,
    string PackageVersion,
    string ManifestSha256,
    string CapabilitiesText);
