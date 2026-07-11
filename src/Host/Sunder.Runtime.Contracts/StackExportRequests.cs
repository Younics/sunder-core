namespace Sunder.Runtime.Contracts;

public sealed record RuntimeStackExportDiscoveryResponse(
    IReadOnlyList<RuntimeStackExportItemDescriptor> Items,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);

public sealed record RuntimeStackExportItemDescriptor(
    string ContributorId,
    string OwnerPackageId,
    string ItemId,
    string DisplayName,
    string Kind,
    bool DefaultSelected,
    IReadOnlyList<string> Sensitivities,
    string? Description = null,
    IReadOnlyList<RuntimeStackExportItemDetail>? Details = null);

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
    IReadOnlyList<string>? SelectedPackages = null);

public sealed record RuntimeStackMediaInput(
    string UploadId,
    string ContentPath,
    string FileName,
    string ContentType,
    string? AltText = null,
    int SortOrder = 0);

public sealed record RuntimeStackExportSelection(
    string ContributorId,
    string ItemId,
    IReadOnlyList<RuntimeStackExportDetailSelection>? Details = null)
{
    public string? OwnerPackageId { get; init; }
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
    IReadOnlyList<string> Errors);
