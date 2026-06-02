namespace Sunder.Protocol;

public sealed record RuntimeStackImportPreviewRequest(
    string StackPath,
    IReadOnlyList<string> SelectedFragmentIds,
    IReadOnlyDictionary<string, string> InputValues,
    IReadOnlyDictionary<string, string> IdRemaps);

public sealed record RuntimeStackImportPreviewResponse(
    bool Success,
    IReadOnlyList<RuntimeStackImportActionDescriptor> Actions,
    IReadOnlyList<RuntimeStackRequiredInputDescriptor> RequiredInputs,
    IReadOnlyList<RuntimeStackImportConflictDescriptor> Conflicts,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);

public sealed record RuntimeStackImportRequest(
    string StackPath,
    IReadOnlyList<string> SelectedFragmentIds,
    IReadOnlyDictionary<string, string> InputValues,
    IReadOnlyDictionary<string, string> IdRemaps,
    IReadOnlyList<string> SelectedActionIds);

public sealed record RuntimeStackImportResponse(
    bool Success,
    IReadOnlyList<RuntimeStackImportedItemDescriptor> ImportedItems,
    IReadOnlyDictionary<string, string> IdRemaps,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);

public sealed record RuntimeStackImportActionDescriptor(
    string ActionId,
    string ContributorId,
    string DisplayName,
    string Kind,
    bool DefaultSelected,
    string? Description = null);

public sealed record RuntimeStackRequiredInputDescriptor(
    string InputId,
    string ContributorId,
    string Kind,
    string Label,
    bool Required,
    string? Description = null,
    string? DefaultValue = null);

public sealed record RuntimeStackImportConflictDescriptor(
    string ConflictId,
    string ContributorId,
    string Message,
    string Severity,
    string? FragmentId = null);

public sealed record RuntimeStackImportedItemDescriptor(
    string ItemId,
    string ContributorId,
    string DisplayName,
    string Kind);
