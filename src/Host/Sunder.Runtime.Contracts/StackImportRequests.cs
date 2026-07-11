namespace Sunder.Runtime.Contracts;

public sealed record RuntimeStackImportPreviewRequest(
    string UploadId,
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
    string UploadId,
    IReadOnlyList<string> SelectedFragmentIds,
    IReadOnlyDictionary<string, string> InputValues,
    IReadOnlyDictionary<string, string> IdRemaps,
    IReadOnlyList<string> SelectedActionIds);

public sealed record RuntimeStackImportResponse(
    bool Success,
    IReadOnlyList<RuntimeStackImportedItemDescriptor> ImportedItems,
    IReadOnlyDictionary<string, string> IdRemaps,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public IReadOnlyList<RuntimeStackImportAppliedContributionDescriptor> AppliedContributions { get; init; } = [];
}

public sealed record RuntimeStackImportAppliedContributionDescriptor(
    string OwnerPackageId,
    string ContributorId,
    IReadOnlyList<string> FragmentIds,
    IReadOnlyList<RuntimeStackImportedItemDescriptor> ImportedItems);

public sealed record RuntimeStackImportActionDescriptor(
    string ActionId,
    string ContributorId,
    string DisplayName,
    string Kind,
    bool DefaultSelected,
    string? Description = null)
{
    public string? OwnerPackageId { get; init; }
}

public sealed record RuntimeStackRequiredInputDescriptor(
    string InputId,
    string ContributorId,
    string Label,
    bool Required,
    string? Description = null,
    string? DefaultValue = null)
{
    public string? OwnerPackageId { get; init; }
}

public sealed record RuntimeStackImportConflictDescriptor(
    string ConflictId,
    string ContributorId,
    string Message,
    string Severity,
    string? FragmentId = null)
{
    public string? OwnerPackageId { get; init; }
}

public sealed record RuntimeStackImportedItemDescriptor(
    string ItemId,
    string ContributorId,
    string DisplayName,
    string Kind)
{
    public string? OwnerPackageId { get; init; }
}
