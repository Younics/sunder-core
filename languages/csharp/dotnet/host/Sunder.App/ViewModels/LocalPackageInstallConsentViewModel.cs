using Sunder.Package.Format;

namespace Sunder.App.ViewModels;

public sealed class LocalPackageInstallConsentViewModel
{
    private LocalPackageInstallConsentViewModel(
        string fileName,
        string packageName,
        string packageIdentity,
        string summary,
        string targetText,
        IReadOnlyList<PackageRpcAccessItemViewModel> rpcContractUses,
        string validationMessage,
        bool isValid)
    {
        FileName = fileName;
        PackageName = packageName;
        PackageIdentity = packageIdentity;
        Summary = summary;
        TargetText = targetText;
        RpcContractUses = rpcContractUses;
        ValidationMessage = validationMessage;
        IsValid = isValid;
    }

    public string FileName { get; }
    public string PackageName { get; }
    public string PackageIdentity { get; }
    public string Summary { get; }
    public string TargetText { get; }
    public IReadOnlyList<PackageRpcAccessItemViewModel> RpcContractUses { get; }
    public string RpcAccessSummary => PackageRpcAccessProjection.Summary(RpcContractUses);
    public bool HasRpcContractUses => RpcContractUses.Count > 0;
    public bool HasNoRpcContractUses => !HasRpcContractUses;
    public string ValidationMessage { get; }
    public bool IsValid { get; }
    public bool IsInvalid => !IsValid;
    public string DismissButtonText => IsValid ? "Cancel" : "Close";

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
        return new LocalPackageInstallConsentViewModel(
            fileName,
            manifest.Name!,
            $"{manifest.Id}  {manifest.Version}",
            string.IsNullOrWhiteSpace(manifest.Summary)
                ? "No package summary was provided."
                : manifest.Summary,
            roles.Length == 0 ? "Shared contract package" : string.Join(" + ", roles),
            PackageRpcAccessProjection.FromManifest(manifest.UsesContracts ?? []),
            validationMessage,
            isValid: true);
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
            [],
            string.Join(Environment.NewLine, errors),
            isValid: false);
}
