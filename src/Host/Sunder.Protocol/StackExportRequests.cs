namespace Sunder.Protocol;

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
    string OutputPath,
    IReadOnlyList<RuntimeStackExportSelection> SelectedItems,
    RuntimeStackExportOptions Options);

public sealed record RuntimeStackExportSelection(
    string ContributorId,
    string ItemId,
    IReadOnlyList<RuntimeStackExportDetailSelection>? Details = null);

public sealed record RuntimeStackExportDetailSelection(
    string DetailId,
    bool IsSelected = true,
    string? ValueOverride = null,
    string? SensitivityOverride = null);

public sealed record RuntimeStackExportOptions(
    bool IncludePrivateText = true,
    bool IncludeMachineSpecificValues = false,
    bool IncludeExecutableCommands = true,
    bool IncludeNetworkEndpoints = true);

public sealed record RuntimeStackExportResponse(
    bool Success,
    string? StackPath,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);
