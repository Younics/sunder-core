using Sunder.App.Services;
using Sunder.Registry.Contracts;

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
            .Where(static item => item.ActionKind != RegistryPackageActionKinds.Keep)
            .Select(static item => new PackageChangeConfirmationItemViewModel(
                item.PackageId,
                item.Version ?? item.CurrentVersion ?? "-",
                FormatAction(item.ActionKind)))
            .ToArray();
        ExecutionRuntimes = preflight.ExecutionRuntimes
            .Select(static runtime => new ExecutionRuntimeDownloadItemViewModel(
                RuntimeManagementUi.Identity(runtime.Identity),
                $"{runtime.Identity.Rid} / {runtime.Identity.ModuleAbi}",
                runtime.SourceText,
                runtime.AlreadyInstalled
                    ? $"Already installed; 0 bytes"
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
        ApprovalScopeText = Items.Count == 0
            ? "Sunder will apply only the package changes shown above."
            : "Capability approval is fenced to the package IDs, versions, and manifest hashes shown above.";
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

    private static string FormatAction(string action)
        => action switch
        {
            RegistryPackageActionKinds.PackageInstall => "Install",
            RegistryPackageActionKinds.PackageUpdate => "Update",
            RegistryPackageActionKinds.PackageRemove => "Remove",
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
