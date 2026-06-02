using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Stacks;

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackExportDiscoveryContext(
    string PackageId);

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackExportItemDescriptor(
    string ItemId,
    string DisplayName,
    string Kind,
    string? Description = null,
    bool DefaultSelected = true,
    IReadOnlyList<StackValueSensitivity>? Sensitivities = null,
    IReadOnlyList<StackExportItemDetail>? Details = null);

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackExportItemDetail(
    string Label,
    string Value,
    StackValueSensitivity? Sensitivity = null,
    string? Description = null,
    string? ValueWhenExcluded = null,
    string? DetailId = null,
    bool DefaultSelected = true,
    bool IsEditable = true,
    bool SupportsAskOnImport = false);

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackExportOptions(
    bool IncludePrivateText = true,
    bool IncludeMachineSpecificValues = false,
    bool IncludeExecutableCommands = true,
    bool IncludeNetworkEndpoints = true);

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackExportRequest(
    IReadOnlyList<string> ItemIds,
    StackExportOptions Options,
    IReadOnlyList<StackExportItemSelection>? ItemSelections = null);

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackExportItemSelection(
    string ItemId,
    IReadOnlyList<StackExportDetailSelection>? Details = null);

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackExportDetailSelection(
    string DetailId,
    bool IsSelected = true,
    string? ValueOverride = null,
    StackValueSensitivity? SensitivityOverride = null);

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public static class StackExportSelectionExtensions
{
    public static StackExportItemSelection? GetItemSelection(this StackExportRequest request, string itemId)
        => request.ItemSelections?.FirstOrDefault(selection => string.Equals(selection.ItemId, itemId, StringComparison.OrdinalIgnoreCase));

    public static bool IsDetailSelected(this StackExportRequest request, string itemId, string detailId)
    {
        var itemSelection = request.GetItemSelection(itemId);
        if (itemSelection?.Details is null)
        {
            return true;
        }

        return itemSelection.Details.Any(detail => detail.IsSelected && string.Equals(detail.DetailId, detailId, StringComparison.OrdinalIgnoreCase));
    }

    public static string GetDetailValue(this StackExportRequest request, string itemId, string detailId, string fallback)
    {
        var selected = request.GetItemSelection(itemId)?.Details?.FirstOrDefault(detail => string.Equals(detail.DetailId, detailId, StringComparison.OrdinalIgnoreCase));
        return selected is not null && !string.IsNullOrWhiteSpace(selected.ValueOverride)
            ? selected.ValueOverride!.Trim()
            : fallback;
    }

    public static StackValueSensitivity GetDetailSensitivity(this StackExportRequest request, string itemId, string detailId, StackValueSensitivity fallback)
    {
        var selected = request.GetItemSelection(itemId)?.Details?.FirstOrDefault(detail => string.Equals(detail.DetailId, detailId, StringComparison.OrdinalIgnoreCase));
        return selected?.SensitivityOverride ?? fallback;
    }
}

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackExportContribution(
    IReadOnlyList<StackFragmentExport> Fragments,
    IReadOnlyList<StackPackageRequirement> PackageRequirements,
    IReadOnlyList<string> Warnings);

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackFragmentExport(
    string FragmentId,
    string OwnerPackageId,
    string ContributorId,
    string SchemaId,
    int SchemaVersion,
    string DisplayName,
    string JsonPayload,
    StackSafetyDescriptor Safety,
    string? Description = null,
    bool DefaultSelected = true,
    IReadOnlyList<StackPackageRequirement>? RequiresPackages = null,
    IReadOnlyList<StackRequiredInputDescriptor>? RequiredInputs = null,
    IReadOnlyList<StackPayloadFile>? Files = null,
    string? SourceItemId = null);

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackFragmentImport(
    string FragmentId,
    string OwnerPackageId,
    string ContributorId,
    string SchemaId,
    int SchemaVersion,
    string DisplayName,
    string JsonPayload,
    string? Description = null,
    IReadOnlyList<StackPayloadFile>? Files = null);

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackPayloadFile(
    string RelativePath,
    string SourcePath);

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackPackageRequirement(
    string PackageId,
    string InstallTag = "latest",
    string? CreatedWithVersion = null,
    string? MinimumVersion = null,
    bool Required = true);

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackRequiredInputDescriptor(
    string InputId,
    StackRequiredInputKind Kind,
    string Label,
    bool Required = true,
    string? Description = null,
    string? DefaultValue = null);

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackSafetyDescriptor(
    bool ContainsSecrets = false,
    bool ContainsSecretReferences = false,
    bool ContainsLocalPaths = false,
    bool ContainsPrivateText = false,
    bool ContainsExecutableCommands = false,
    bool ContainsNetworkEndpoints = false,
    bool ContainsMachineSpecificValues = false);

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackImportPreviewRequest(
    IReadOnlyList<StackFragmentImport> Fragments,
    IReadOnlyDictionary<string, string> InputValues,
    IReadOnlyDictionary<string, string> IdRemaps);

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackImportPreview(
    IReadOnlyList<StackImportAction> Actions,
    IReadOnlyList<StackRequiredInputDescriptor> RequiredInputs,
    IReadOnlyList<StackImportConflict> Conflicts,
    IReadOnlyList<string> Warnings);

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackImportAction(
    string ActionId,
    string DisplayName,
    StackImportActionKind Kind,
    bool DefaultSelected = true,
    string? Description = null);

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackImportConflict(
    string ConflictId,
    string Message,
    StackImportConflictSeverity Severity,
    string? FragmentId = null);

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackImportRequest(
    IReadOnlyList<StackFragmentImport> Fragments,
    IReadOnlyDictionary<string, string> InputValues,
    IReadOnlyDictionary<string, string> IdRemaps,
    IReadOnlyList<string> SelectedActionIds);

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackImportResult(
    bool Success,
    IReadOnlyList<StackImportedItem> ImportedItems,
    IReadOnlyDictionary<string, string> IdRemaps,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackImportedItem(
    string ItemId,
    string DisplayName,
    string Kind);

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public enum StackValueSensitivity
{
    Public,
    PrivateText,
    Secret,
    AuthSession,
    LocalPath,
    NetworkEndpoint,
    ExecutableCommand,
    MachineSpecific,
}

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public enum StackRequiredInputKind
{
    Text,
    Secret,
    LocalPath,
    AuthSession,
    Choice,
}

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public enum StackImportActionKind
{
    Create,
    Update,
    Replace,
    Reuse,
    Skip,
}

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public enum StackImportConflictSeverity
{
    Warning,
    Error,
}
