using System.Text.Json.Serialization;

namespace Sunder.Runtime.Contracts;

public sealed record RuntimeStackImportPreviewRequest(
    string UploadId,
    IReadOnlyList<string> SelectedFragmentIds,
    IReadOnlyDictionary<string, string> InputValues,
    IReadOnlyDictionary<string, string> IdRemaps)
{
    public IReadOnlyList<string> SelectedFragmentIds { get; }
        = RuntimeContractCollections.Freeze(SelectedFragmentIds);
    public IReadOnlyDictionary<string, string> InputValues { get; }
        = RuntimeContractCollections.Freeze(InputValues);
    public IReadOnlyDictionary<string, string> IdRemaps { get; }
        = RuntimeContractCollections.Freeze(IdRemaps);
}

public sealed record RuntimeStackImportPreviewResponse(
    bool Success,
    string? PlanId,
    DateTimeOffset? ExpiresAtUtc,
    IReadOnlyList<RuntimeStackImportActionDescriptor> Actions,
    IReadOnlyList<RuntimeStackRequiredInputDescriptor> RequiredInputs,
    IReadOnlyList<RuntimeStackImportConflictDescriptor> Conflicts,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public IReadOnlyList<RuntimeStackImportActionDescriptor> Actions { get; }
        = RuntimeContractCollections.Freeze(Actions);
    public IReadOnlyList<RuntimeStackRequiredInputDescriptor> RequiredInputs { get; }
        = RuntimeContractCollections.Freeze(RequiredInputs);
    public IReadOnlyList<RuntimeStackImportConflictDescriptor> Conflicts { get; }
        = RuntimeContractCollections.Freeze(Conflicts);
    public IReadOnlyList<string> Warnings { get; } = RuntimeContractCollections.Freeze(Warnings);
    public IReadOnlyList<string> Errors { get; } = RuntimeContractCollections.Freeze(Errors);
}

public sealed record RuntimeStackImportRequest(
    string PlanId,
    IReadOnlyList<string> SelectedFragmentIds,
    IReadOnlyList<string> SelectedActionIds)
{
    public IReadOnlyList<string> SelectedFragmentIds { get; }
        = RuntimeContractCollections.Freeze(SelectedFragmentIds);
    public IReadOnlyList<string> SelectedActionIds { get; }
        = RuntimeContractCollections.Freeze(SelectedActionIds);
}

[JsonConverter(typeof(JsonStringEnumConverter<RuntimeStackImportOutcome>))]
public enum RuntimeStackImportOutcome
{
    Completed,
    Partial,
    Failed,
}

public sealed record RuntimeStackImportResponse(
    RuntimeStackImportOutcome Outcome,
    IReadOnlyList<RuntimeStackImportedItemDescriptor> ImportedItems,
    IReadOnlyDictionary<string, string> IdRemaps,
    IReadOnlyList<RuntimeStackImportContributorResultDescriptor> ContributorResults,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public IReadOnlyList<RuntimeStackImportedItemDescriptor> ImportedItems { get; }
        = RuntimeContractCollections.Freeze(ImportedItems);
    public IReadOnlyDictionary<string, string> IdRemaps { get; }
        = RuntimeContractCollections.Freeze(IdRemaps);
    public IReadOnlyList<RuntimeStackImportContributorResultDescriptor> ContributorResults { get; }
        = RuntimeContractCollections.Freeze(ContributorResults);
    public IReadOnlyList<string> Warnings { get; } = RuntimeContractCollections.Freeze(Warnings);
    public IReadOnlyList<string> Errors { get; } = RuntimeContractCollections.Freeze(Errors);
}

public sealed record RuntimeStackImportContributorResultDescriptor(
    string OwnerPackageId,
    string ContributorId,
    IReadOnlyList<string> FragmentIds,
    [property: JsonPropertyOrder(3)] RuntimeStackImportOutcome Outcome,
    IReadOnlyList<RuntimeStackImportedItemDescriptor> ImportedItems,
    IReadOnlyDictionary<string, string> IdRemaps,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    [JsonPropertyOrder(2)]
    public IReadOnlyList<string> FragmentIds { get; } = RuntimeContractCollections.Freeze(FragmentIds);
    [JsonPropertyOrder(4)]
    public IReadOnlyList<RuntimeStackImportedItemDescriptor> ImportedItems { get; }
        = RuntimeContractCollections.Freeze(ImportedItems);
    [JsonPropertyOrder(5)]
    public IReadOnlyDictionary<string, string> IdRemaps { get; }
        = RuntimeContractCollections.Freeze(IdRemaps);
    [JsonPropertyOrder(6)]
    public IReadOnlyList<string> Warnings { get; } = RuntimeContractCollections.Freeze(Warnings);
    [JsonPropertyOrder(7)]
    public IReadOnlyList<string> Errors { get; } = RuntimeContractCollections.Freeze(Errors);
}

public sealed record RuntimeStackImportAppliedContributionDescriptor(
    string OwnerPackageId,
    string ContributorId,
    IReadOnlyList<string> FragmentIds,
    IReadOnlyList<RuntimeStackImportedItemDescriptor> ImportedItems)
{
    public IReadOnlyList<string> FragmentIds { get; } = RuntimeContractCollections.Freeze(FragmentIds);
    public IReadOnlyList<RuntimeStackImportedItemDescriptor> ImportedItems { get; }
        = RuntimeContractCollections.Freeze(ImportedItems);
}

public sealed record RuntimeStackImportActionDescriptor(
    string ActionId,
    string OwnerPackageId,
    string ContributorId,
    string LocalActionId,
    string DisplayName,
    string Kind,
    bool DefaultSelected,
    string? Description = null);

public sealed record RuntimeStackRequiredInputDescriptor(
    string InputId,
    string OwnerPackageId,
    string ContributorId,
    string LocalInputId,
    string Label,
    bool Required,
    string? Description = null,
    string? DefaultValue = null);

public sealed record RuntimeStackImportConflictDescriptor(
    string ConflictId,
    string OwnerPackageId,
    string ContributorId,
    string Message,
    string Severity,
    string? FragmentId = null);

public sealed record RuntimeStackImportedItemDescriptor(
    string ItemId,
    string OwnerPackageId,
    string ContributorId,
    string DisplayName,
    string Kind);
