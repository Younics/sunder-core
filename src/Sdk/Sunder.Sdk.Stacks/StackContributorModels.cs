using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Stacks;

/// <summary>Identifies the package whose contributor is discovering exportable items.</summary>
/// <param name="PackageId">Owning runtime package id.</param>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackExportDiscoveryContext(
    string PackageId);

/// <summary>Describes one user-selectable logical item that a contributor can export.</summary>
/// <param name="ItemId">Stable contributor-scoped item id.</param>
/// <param name="DisplayName">User-facing item name.</param>
/// <param name="Kind">Contributor-defined category used for display and result reporting.</param>
/// <param name="Description">Optional explanatory text.</param>
/// <param name="DefaultSelected">Whether export UI selects the item initially; defaults to true.</param>
/// <param name="Sensitivities">Optional sensitivities present in the item for disclosure UI.</param>
/// <param name="Details">Optional independently selectable or editable item details.</param>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackExportItemDescriptor(
    string ItemId,
    string DisplayName,
    string Kind,
    string? Description = null,
    bool DefaultSelected = true,
    IReadOnlyList<StackValueSensitivity>? Sensitivities = null,
    IReadOnlyList<StackExportItemDetail>? Details = null);

/// <summary>Describes one independently selectable value within an export item.</summary>
/// <param name="Label">User-facing value label.</param>
/// <param name="Value">Current export value.</param>
/// <param name="Sensitivity">Optional sensitivity; <see langword="null"/> means contributor-defined.</param>
/// <param name="Description">Optional value explanation.</param>
/// <param name="ValueWhenExcluded">Optional safe replacement used when the detail is excluded.</param>
/// <param name="DetailId">Stable id required for per-detail selection; <see langword="null"/> makes the detail informational.</param>
/// <param name="DefaultSelected">Whether the detail is initially included.</param>
/// <param name="IsEditable">Whether export UI may supply a value override.</param>
/// <param name="SupportsAskOnImport">Whether the exported value may become a required import input.</param>
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

/// <summary>Specifies selected items and optional detail-level export choices.</summary>
/// <param name="ItemIds">Stable ids selected for export.</param>
/// <param name="ItemSelections">Optional detail choices; <see langword="null"/> accepts descriptor defaults.</param>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackExportRequest(
    IReadOnlyList<string> ItemIds,
    IReadOnlyList<StackExportItemSelection>? ItemSelections = null);

/// <summary>Associates one selected export item with detail choices.</summary>
/// <param name="ItemId">Selected item id.</param>
/// <param name="Details">Optional detail choices; <see langword="null"/> selects all defaults.</param>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackExportItemSelection(
    string ItemId,
    IReadOnlyList<StackExportDetailSelection>? Details = null);

/// <summary>Overrides inclusion, value, or sensitivity for one export detail.</summary>
/// <param name="DetailId">Stable detail id from discovery.</param>
/// <param name="IsSelected">Whether the detail is included; defaults to true.</param>
/// <param name="ValueOverride">Optional user-edited value.</param>
/// <param name="SensitivityOverride">Optional user-selected sensitivity.</param>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackExportDetailSelection(
    string DetailId,
    bool IsSelected = true,
    string? ValueOverride = null,
    StackValueSensitivity? SensitivityOverride = null);

/// <summary>Provides consistent default handling for optional detail-level selections.</summary>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public static class StackExportSelectionExtensions
{
    /// <summary>Gets an item's explicit selection using case-insensitive ids, or <see langword="null"/> when defaults apply.</summary>
    public static StackExportItemSelection? GetItemSelection(this StackExportRequest request, string itemId)
        => request.ItemSelections?.FirstOrDefault(selection => string.Equals(selection.ItemId, itemId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Determines whether a detail is selected; absent detail choices default to selected.</summary>
    public static bool IsDetailSelected(this StackExportRequest request, string itemId, string detailId)
    {
        var itemSelection = request.GetItemSelection(itemId);
        if (itemSelection?.Details is null)
        {
            return true;
        }

        return itemSelection.Details.Any(detail => detail.IsSelected && string.Equals(detail.DetailId, detailId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Gets a nonblank value override or the supplied fallback.</summary>
    public static string GetDetailValue(this StackExportRequest request, string itemId, string detailId, string fallback)
    {
        var selected = request.GetItemSelection(itemId)?.Details?.FirstOrDefault(detail => string.Equals(detail.DetailId, detailId, StringComparison.OrdinalIgnoreCase));
        return selected is not null && !string.IsNullOrWhiteSpace(selected.ValueOverride)
            ? selected.ValueOverride!.Trim()
            : fallback;
    }

    /// <summary>Gets a sensitivity override or the supplied fallback.</summary>
    public static StackValueSensitivity GetDetailSensitivity(this StackExportRequest request, string itemId, string detailId, StackValueSensitivity fallback)
    {
        var selected = request.GetItemSelection(itemId)?.Details?.FirstOrDefault(detail => string.Equals(detail.DetailId, detailId, StringComparison.OrdinalIgnoreCase));
        return selected?.SensitivityOverride ?? fallback;
    }
}

/// <summary>Returns all fragments, package requirements, and nonfatal warnings produced by one contributor.</summary>
/// <param name="Fragments">Immutable exported fragments.</param>
/// <param name="PackageRequirements">Packages needed to consume those fragments.</param>
/// <param name="Warnings">User-facing nonfatal export warnings.</param>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackExportContribution(
    IReadOnlyList<StackFragmentExport> Fragments,
    IReadOnlyList<StackPackageRequirement> PackageRequirements,
    IReadOnlyList<string> Warnings);

/// <summary>Defines one contributor-owned versioned JSON fragment and optional payload files.</summary>
/// <param name="FragmentId">Stable id unique within the Stack archive.</param>
/// <param name="ContributorId">Contributor expected to import the fragment.</param>
/// <param name="SchemaId">Stable payload schema id.</param>
/// <param name="SchemaVersion">Positive contributor-defined schema version.</param>
/// <param name="DisplayName">User-facing fragment name.</param>
/// <param name="JsonPayload">UTF-8 JSON text owned and validated by the contributor.</param>
/// <param name="Description">Optional fragment explanation.</param>
/// <param name="DefaultSelected">Whether import UI initially selects the fragment.</param>
/// <param name="RequiredInputs">Optional values the importer must obtain from the user.</param>
/// <param name="Files">Optional files copied into the Stack payload.</param>
/// <param name="SourceItemId">Optional discovery item id that produced this fragment.</param>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackFragmentExport(
    string FragmentId,
    string ContributorId,
    string SchemaId,
    int SchemaVersion,
    string DisplayName,
    string JsonPayload,
    string? Description = null,
    bool DefaultSelected = true,
    IReadOnlyList<StackRequiredInputDescriptor>? RequiredInputs = null,
    IReadOnlyList<StackExportPayloadFile>? Files = null,
    string? SourceItemId = null);

/// <summary>Provides a validated Stack fragment and extracted payload files to an importer.</summary>
/// <param name="FragmentId">Stable archive fragment id.</param>
/// <param name="OwnerPackageId">Package that owns the target contributor.</param>
/// <param name="ContributorId">Contributor selected to import the fragment.</param>
/// <param name="SchemaId">Payload schema id.</param>
/// <param name="SchemaVersion">Contributor-defined schema version.</param>
/// <param name="DisplayName">User-facing fragment name.</param>
/// <param name="JsonPayload">Validated UTF-8 JSON text.</param>
/// <param name="Description">Optional fragment explanation.</param>
/// <param name="Files">Optional read-only extracted payload files valid for the import operation.</param>
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
    IReadOnlyList<StackImportPayloadFile>? Files = null);

/// <summary>Maps an archive-relative payload path to a local source file during export.</summary>
/// <param name="RelativePath">Safe forward-slash path within the fragment payload.</param>
/// <param name="SourcePath">Absolute readable local file path; the exporter retains ownership.</param>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackExportPayloadFile(
    string RelativePath,
    string SourcePath);

/// <summary>Maps a fragment-relative payload path to a temporary extracted file during import.</summary>
/// <param name="RelativePath">Safe forward-slash path from the archive.</param>
/// <param name="ExtractedPath">Absolute read-only temporary path owned by the host.</param>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackImportPayloadFile(
    string RelativePath,
    string ExtractedPath);

/// <summary>Declares a package needed to apply exported Stack content.</summary>
/// <param name="PackageId">Required runtime package id.</param>
/// <param name="InstallTag">Registry dist tag used when installation is needed; defaults to <c>latest</c>.</param>
/// <param name="CreatedWithVersion">Optional package version used when the Stack was exported.</param>
/// <param name="MinimumVersion">Optional minimum strict SemVer accepted for import.</param>
/// <param name="Required">Whether import must stop when the package is unavailable; defaults to true.</param>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackPackageRequirement(
    string PackageId,
    string InstallTag = "latest",
    string? CreatedWithVersion = null,
    string? MinimumVersion = null,
    bool Required = true);

/// <summary>Describes a value that must be resolved before a fragment can be imported.</summary>
/// <param name="InputId">Stable contributor-scoped input id.</param>
/// <param name="Label">User-facing input label.</param>
/// <param name="Required">Whether import rejects an omitted value; defaults to true.</param>
/// <param name="Description">Optional input guidance.</param>
/// <param name="DefaultValue">Optional initial value; it is not implicitly treated as secret.</param>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackRequiredInputDescriptor(
    string InputId,
    string Label,
    bool Required = true,
    string? Description = null,
    string? DefaultValue = null);

/// <summary>Requests a side-effect-free import plan for selected fragments.</summary>
/// <param name="Fragments">Validated immutable fragments and temporary payload files.</param>
/// <param name="InputValues">Resolved input values keyed by input id.</param>
/// <param name="IdRemaps">User-approved source-to-target identity mappings.</param>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackImportPreviewRequest(
    IReadOnlyList<StackFragmentImport> Fragments,
    IReadOnlyDictionary<string, string> InputValues,
    IReadOnlyDictionary<string, string> IdRemaps);

/// <summary>Describes proposed import actions, unresolved inputs, conflicts, and warnings without applying changes.</summary>
/// <param name="Actions">Selectable side effects that import can perform.</param>
/// <param name="RequiredInputs">Inputs still needed from the user.</param>
/// <param name="Conflicts">Identity or state conflicts requiring review.</param>
/// <param name="Warnings">Nonfatal user-facing concerns.</param>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackImportPreview(
    IReadOnlyList<StackImportAction> Actions,
    IReadOnlyList<StackRequiredInputDescriptor> RequiredInputs,
    IReadOnlyList<StackImportConflict> Conflicts,
    IReadOnlyList<string> Warnings);

/// <summary>Describes one independently selectable import side effect.</summary>
/// <param name="ActionId">Stable id echoed in the import request.</param>
/// <param name="DisplayName">User-facing action name.</param>
/// <param name="Kind">Expected mutation behavior.</param>
/// <param name="DefaultSelected">Whether import UI selects the action initially.</param>
/// <param name="Description">Optional action detail.</param>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackImportAction(
    string ActionId,
    string DisplayName,
    StackImportActionKind Kind,
    bool DefaultSelected = true,
    string? Description = null);

/// <summary>Describes a conflict discovered while planning import.</summary>
/// <param name="ConflictId">Stable id for UI correlation.</param>
/// <param name="Message">User-facing conflict explanation.</param>
/// <param name="Severity">Whether import may continue.</param>
/// <param name="FragmentId">Affected fragment id, or <see langword="null"/> for a Stack-wide conflict.</param>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackImportConflict(
    string ConflictId,
    string Message,
    StackImportConflictSeverity Severity,
    string? FragmentId = null);

/// <summary>Requests application of the user-approved import plan.</summary>
/// <param name="Fragments">Validated immutable fragments and temporary payload files.</param>
/// <param name="InputValues">Resolved inputs keyed by input id.</param>
/// <param name="IdRemaps">Approved source-to-target identity mappings.</param>
/// <param name="SelectedActionIds">Action ids selected from the latest preview.</param>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackImportRequest(
    IReadOnlyList<StackFragmentImport> Fragments,
    IReadOnlyDictionary<string, string> InputValues,
    IReadOnlyDictionary<string, string> IdRemaps,
    IReadOnlyList<string> SelectedActionIds);

/// <summary>Reports committed import effects and diagnostics.</summary>
/// <param name="Success">Whether all required selected actions completed.</param>
/// <param name="ImportedItems">Items created, updated, replaced, or reused.</param>
/// <param name="IdRemaps">Final source-to-target identity mappings.</param>
/// <param name="Warnings">Nonfatal diagnostics.</param>
/// <param name="Errors">Failure diagnostics; empty on successful import.</param>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackImportResult(
    bool Success,
    IReadOnlyList<StackImportedItem> ImportedItems,
    IReadOnlyDictionary<string, string> IdRemaps,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);

/// <summary>Identifies one logical item affected by import.</summary>
/// <param name="ItemId">Final target item id.</param>
/// <param name="DisplayName">User-facing item name.</param>
/// <param name="Kind">Contributor-defined item category.</param>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackImportedItem(
    string ItemId,
    string DisplayName,
    string Kind);

/// <summary>Summarizes a successfully applied import for post-import handlers.</summary>
/// <param name="OwnerPackageId">Package that owns the contributor.</param>
/// <param name="ContributorId">Contributor that applied the fragments.</param>
/// <param name="FragmentIds">Applied fragment ids.</param>
/// <param name="ImportedItems">Final imported item snapshots.</param>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public sealed record StackImportAppliedContext(
    string OwnerPackageId,
    string ContributorId,
    IReadOnlyList<string> FragmentIds,
    IReadOnlyList<StackImportedItem> ImportedItems);

/// <summary>Classifies whether an exported value may be disclosed in a Stack archive.</summary>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public enum StackValueSensitivity
{
    /// <summary>The value may be stored directly in an exported Stack.</summary>
    Public,
    /// <summary>The value is sensitive and should be excluded, replaced, or requested during import.</summary>
    Secret,
}

/// <summary>Describes the target mutation represented by an import action.</summary>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public enum StackImportActionKind
{
    /// <summary>Creates a new target item.</summary>
    Create,
    /// <summary>Modifies an existing target item in place.</summary>
    Update,
    /// <summary>Removes and recreates an existing target item.</summary>
    Replace,
    /// <summary>Uses an existing target item without modifying it.</summary>
    Reuse,
    /// <summary>Intentionally performs no mutation.</summary>
    Skip,
}

/// <summary>Specifies whether an import conflict blocks application.</summary>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
public enum StackImportConflictSeverity
{
    /// <summary>Import may continue after user review.</summary>
    Warning,
    /// <summary>Import must not continue until the conflict is resolved.</summary>
    Error,
}
