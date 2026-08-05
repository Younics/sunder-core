namespace Sunder.Runtime.Contracts;

public sealed record RuntimeStackExportDiscoveryResponse(
    IReadOnlyList<RuntimeStackExportItemDescriptor> Items,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public IReadOnlyList<RuntimeStackExportItemDescriptor> Items { get; }
        = RuntimeContractCollections.Freeze(Items);
    public IReadOnlyList<string> Warnings { get; } = RuntimeContractCollections.Freeze(Warnings);
    public IReadOnlyList<string> Errors { get; } = RuntimeContractCollections.Freeze(Errors);
}

public sealed record RuntimeStackExportItemDescriptor(
    string ContributorId,
    string OwnerPackageId,
    string ItemId,
    string DisplayName,
    string Kind,
    bool DefaultSelected,
    string? Description = null,
    IReadOnlyList<RuntimeStackExportItemDetail>? Details = null)
{
    public IReadOnlyList<RuntimeStackExportItemDetail>? Details { get; }
        = RuntimeContractCollections.FreezeNullable(Details);
}

public sealed record RuntimeStackExportItemDetail(
    string Label,
    string Value,
    string? Sensitivity = null,
    string? Description = null,
    string? ValueWhenExcluded = null,
    string? DetailId = null,
    bool DefaultSelected = true,
    bool IsEditable = true,
    bool SupportsAskOnImport = false);

public sealed record RuntimeStackExportRequest(
    string StackId,
    string Name,
    string? Summary,
    IReadOnlyList<RuntimeStackExportSelection> SelectedItems,
    string? ReadmeMarkdown = null,
    IReadOnlyList<RuntimeStackMediaInput>? Media = null,
    IReadOnlyList<string>? SelectedPackages = null)
{
    public IReadOnlyList<RuntimeStackExportSelection> SelectedItems { get; }
        = RuntimeContractCollections.Freeze(SelectedItems);
    public IReadOnlyList<RuntimeStackMediaInput>? Media { get; }
        = RuntimeContractCollections.FreezeNullable(Media);
    public IReadOnlyList<string>? SelectedPackages { get; }
        = RuntimeContractCollections.FreezeNullable(SelectedPackages);
}

public sealed record RuntimeStackMediaInput(
    string UploadId,
    string ContentPath,
    string FileName,
    string ContentType,
    string? AltText = null,
    int SortOrder = 0);

public sealed record RuntimeStackExportSelection(
    string OwnerPackageId,
    string ContributorId,
    string ItemId,
    IReadOnlyList<RuntimeStackExportDetailSelection>? Details = null)
{
    public IReadOnlyList<RuntimeStackExportDetailSelection>? Details { get; }
        = RuntimeContractCollections.FreezeNullable(Details);
}

public sealed record RuntimeStackExportDetailSelection(
    string DetailId,
    bool IsSelected = true,
    string? ValueOverride = null,
    string? SensitivityOverride = null);

public sealed record RuntimeStackExportResponse(
    bool Success,
    ContentDownloadDescriptor? Download,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public IReadOnlyList<string> Warnings { get; } = RuntimeContractCollections.Freeze(Warnings);
    public IReadOnlyList<string> Errors { get; } = RuntimeContractCollections.Freeze(Errors);
}
