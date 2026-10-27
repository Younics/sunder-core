using Sunder.Package.Format;
using Sunder.Sdk.Compatibility;
using Sunder.Runtime.Contracts;
using Sunder.App.Services;

namespace Sunder.App.ViewModels;

public sealed class LocalPackageInstallConsentViewModel
{
    private LocalPackageInstallConsentViewModel(
        string fileName,
        string packageName,
        string packageIdentity,
        string summary,
        string targetText,
        bool hasDockerEnvironmentAccess,
        string? sensitiveCapabilityManifestSha256,
        string validationMessage,
        bool isValid)
    {
        FileName = fileName;
        PackageName = packageName;
        PackageIdentity = packageIdentity;
        Summary = summary;
        TargetText = targetText;
        HasDockerEnvironmentAccess = hasDockerEnvironmentAccess;
        SensitiveCapabilityManifestSha256 = sensitiveCapabilityManifestSha256;
        ValidationMessage = validationMessage;
        IsValid = isValid;
    }

    public string FileName { get; }
    public string PackageName { get; }
    public string PackageIdentity { get; }
    public string PackageId => PackageIdentity.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    public string Summary { get; }
    public string TargetText { get; }
    public bool HasDockerEnvironmentAccess { get; }
    public string? SensitiveCapabilityManifestSha256 { get; }
    public string ValidationMessage { get; }
    public bool IsValid { get; }
    public bool IsInvalid => !IsValid;
    public string DismissButtonText => IsValid ? "Cancel" : "Close";
    public IReadOnlyList<ExecutionRuntimeDownloadItemViewModel> RuntimeDownloads { get; private init; } = [];
    public PackageInformationViewModel Information { get; private init; } = PackageInformationViewModel.Unavailable();
    public bool HasRuntimeDownloads => RuntimeDownloads.Count != 0;

    internal LocalPackageInstallConsentViewModel WithRuntimes(RuntimePackageRuntimePrerequisitePlan plan)
        => new(FileName, PackageName, PackageIdentity, Summary, TargetText, HasDockerEnvironmentAccess,
            SensitiveCapabilityManifestSha256, ValidationMessage, IsValid)
        {
            Information = Information,
            RuntimeDownloads = plan.Requirements.Select(item =>
            {
                var identity = item.Installation?.Resolution?.Artifact.Identity ?? item.InstalledRuntime;
                return identity is null ? null : new ExecutionRuntimeDownloadItemViewModel(RuntimeManagementUi.Identity(identity),
                    $"{identity.Rid} / {identity.ModuleAbi}", RuntimeManagementUi.Source(identity),
                    item.Satisfied ? "Already installed; 0 bytes" : RegistryPackageChangePreflight.FormatBytes(item.Installation?.Resolution?.DownloadSize),
                     item.Satisfied ? "The verified installed runtime will be reused." : "Runtime installation runs first; this package waits in the queue.");
            }).OfType<ExecutionRuntimeDownloadItemViewModel>().Distinct().ToArray(),
        };

    internal static LocalPackageInstallConsentViewModel Valid(
        string fileName,
        SunderPackageArchiveValidationResult validation)
    {
        var manifest = validation.Manifest!;
        var roles = (manifest.Targets ?? [])
            .Where(static target => target is not null)
            .Select(static target => target!.Role switch
            {
                SunderPackageFormat.AppHostRole => "App",
                SunderPackageFormat.RuntimeHostRole => "Runtime",
                _ => target.Role!,
            })
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var validationMessage = validation.Warnings.Count == 0
            ? "Archive structure and content hashes passed validation."
            : string.Join(Environment.NewLine, validation.Warnings);
        var hasDockerEnvironmentAccess = (manifest.Targets ?? []).Any(target =>
            target?.RequiredHostCapabilities?.Contains(
                SunderSdkCapabilities.DockerEnvironmentV1,
                StringComparer.Ordinal) == true);
        return new LocalPackageInstallConsentViewModel(
            fileName,
            manifest.Name!,
            $"{manifest.Id}  {manifest.Version}",
            string.IsNullOrWhiteSpace(manifest.Summary)
                ? "No package summary was provided."
                : manifest.Summary,
            roles.Length == 0 ? "Shared contract package" : string.Join(" + ", roles),
            hasDockerEnvironmentAccess,
            hasDockerEnvironmentAccess
                ? validation.ContentIndex!.Files!.Single(entry => string.Equals(
                    entry?.Path,
                    SunderPackageFormat.ManifestPath,
                    StringComparison.Ordinal)).Sha256
                : null,
            validationMessage,
            isValid: true)
        {
            Information = PackageInformationViewModel.Create(SunderPackageInformation.FromManifest(manifest), AppPackageTargetEnvironment.CurrentRid),
        };
    }

    internal static LocalPackageInstallConsentViewModel Invalid(
        string fileName,
        IEnumerable<string> errors)
        => new(
            fileName,
            "Package cannot be installed",
            fileName,
            "The selected archive did not pass Sunder package validation.",
            "Invalid archive",
            false,
            null,
            string.Join(Environment.NewLine, errors),
            isValid: false);
}
