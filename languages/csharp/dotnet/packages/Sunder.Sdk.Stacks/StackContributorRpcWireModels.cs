using Sunder.Sdk.Rpc;
using Local = Sunder.Sdk.Stacks;

namespace Sunder.Sdk.Stacks.Wire;

// These values retain the shipped aliases and absent/non-null members without changing the public local DTOs.
[SunderRpcTransparent, SunderRpcDefinition("Id"), SunderRpcFacet(MinLength = 1, MaxLength = 256)]
internal readonly record struct Id
{
    public required string Value { get; init; }
    public static implicit operator Id(string value) => new() { Value = value };
    public static implicit operator string(Id value) => value.Value;
    public static SunderRpcOptional<Id> Optional(string? value)
        => value is null ? default(SunderRpcOptional<Id>) : new Id { Value = value };
}

[SunderRpcTransparent, SunderRpcFacet(MinLength = 0, MaxLength = 4096)]
internal readonly record struct ShortText
{
    public required string Value { get; init; }
    public static implicit operator ShortText(string value) => new() { Value = value };
    public static implicit operator string(ShortText value) => value.Value;
    public static SunderRpcOptional<ShortText> Optional(string? value)
        => value is null ? default(SunderRpcOptional<ShortText>) : new ShortText { Value = value };
}

[SunderRpcTransparent, SunderRpcFacet(MinLength = 0, MaxLength = 1000000)]
internal readonly record struct Text
{
    public required string Value { get; init; }
    public static implicit operator Text(string value) => new() { Value = value };
    public static implicit operator string(Text value) => value.Value;
    public static SunderRpcOptional<Text> Optional(string? value)
        => value is null ? default(SunderRpcOptional<Text>) : new Text { Value = value };
}

[SunderRpcTransparent, SunderRpcFacet(MinLength = 2, MaxLength = 1000000)]
internal readonly record struct JsonText
{
    public required string Value { get; init; }
}

[SunderRpcTransparent, SunderRpcFacet(Minimum = "1", Maximum = "2147483647")]
internal readonly record struct SchemaVersion
{
    public required int Value { get; init; }
}

[SunderRpcTransparent, SunderRpcDefinition("SunderRpcContentReference")]
internal sealed class ContentReference
{
    public required SunderRpcContentReference Value { get; init; }
}

[SunderRpcTransparent, SunderRpcFacet(MinItems = 0, MaxItems = 10000)]
internal sealed class StringList
{
    public required IReadOnlyList<Text> Value { get; init; }
    public static StringList FromPublic(IReadOnlyList<string> value)
        => new() { Value = value.Select(item => (Text)item).ToArray() };
}

[SunderRpcTransparent, SunderRpcFacet(MinItems = 0, MaxItems = 10000)]
internal sealed class IdList
{
    public required IReadOnlyList<Id> Value { get; init; }
    public IReadOnlyList<string> ToPublic() => Value.Select(item => item.Value).ToArray();
}

[SunderRpcTransparent, SunderRpcFacet(MinProperties = 0, MaxProperties = 10000, MinLength = 1, MaxLength = 1024)]
internal sealed class StringMap
{
    public required IReadOnlyDictionary<string, Text> Value { get; init; }
    public static StringMap FromPublic(IReadOnlyDictionary<string, string> value)
        => new() { Value = value.ToDictionary(pair => pair.Key, pair => (Text)pair.Value, StringComparer.Ordinal) };
    public IReadOnlyDictionary<string, string> ToPublic()
        => Value.ToDictionary(pair => pair.Key, pair => pair.Value.Value, StringComparer.Ordinal);
}

internal sealed class StackRpcPayloadFile
{
    public required ShortText RelativePath { get; init; }
    public required ContentReference Content { get; init; }
}

[SunderRpcTransparent, SunderRpcFacet(MinItems = 0, MaxItems = 10000)]
internal sealed class StackRpcPayloadFileList
{
    public required IReadOnlyList<StackRpcPayloadFile> Value { get; init; }
}

internal sealed class StackContributorMetadata
{
    public required Id ContributorId { get; init; }
    public required ShortText DisplayName { get; init; }
    public required bool SupportsExport { get; init; }
    public required bool SupportsImport { get; init; }
    public required bool SupportsImportApplied { get; init; }
}

internal sealed class StackExportDiscoveryContext
{
    public required Id PackageId { get; init; }
    public Local.StackExportDiscoveryContext ToPublic() => new(PackageId);
}

internal sealed class StackExportItemDetail
{
    public required ShortText Label { get; init; }
    public required Text Value { get; init; }
    public SunderRpcOptional<StackValueSensitivity> Sensitivity { get; init; }
    public SunderRpcOptional<Text> Description { get; init; }
    public SunderRpcOptional<Text> ValueWhenExcluded { get; init; }
    public SunderRpcOptional<Id> DetailId { get; init; }
    public required bool DefaultSelected { get; init; }
    public required bool IsEditable { get; init; }
    public required bool SupportsAskOnImport { get; init; }

    public static StackExportItemDetail FromPublic(Local.StackExportItemDetail value) => new()
    {
        Label = value.Label, Value = value.Value,
        Sensitivity = value.Sensitivity is { } sensitivity ? new SunderRpcOptional<StackValueSensitivity>(sensitivity) : default,
        Description = Text.Optional(value.Description), ValueWhenExcluded = Text.Optional(value.ValueWhenExcluded),
        DetailId = Id.Optional(value.DetailId), DefaultSelected = value.DefaultSelected,
        IsEditable = value.IsEditable, SupportsAskOnImport = value.SupportsAskOnImport,
    };

}

[SunderRpcTransparent, SunderRpcFacet(MinItems = 0, MaxItems = 10000)]
internal sealed class StackExportItemDetailList
{
    public required IReadOnlyList<StackExportItemDetail> Value { get; init; }
}

internal sealed class StackExportItemDescriptor
{
    public required Id ItemId { get; init; }
    public required ShortText DisplayName { get; init; }
    public required Id Kind { get; init; }
    public SunderRpcOptional<Text> Description { get; init; }
    public required bool DefaultSelected { get; init; }
    public SunderRpcOptional<StackExportItemDetailList> Details { get; init; }

    public static StackExportItemDescriptor FromPublic(Local.StackExportItemDescriptor value) => new()
    {
        ItemId = value.ItemId, DisplayName = value.DisplayName, Kind = value.Kind,
        Description = Text.Optional(value.Description), DefaultSelected = value.DefaultSelected,
        Details = value.Details is null ? default(SunderRpcOptional<StackExportItemDetailList>)
            : new StackExportItemDetailList { Value = value.Details.Select(StackExportItemDetail.FromPublic).ToArray() },
    };

}

[SunderRpcTransparent, SunderRpcFacet(MinItems = 0, MaxItems = 10000)]
internal sealed class StackExportItemDescriptorList
{
    public required IReadOnlyList<StackExportItemDescriptor> Value { get; init; }
}

internal sealed class StackExportItemList
{
    public required StackExportItemDescriptorList Items { get; init; }
}

internal sealed class StackExportDetailSelection
{
    public required Id DetailId { get; init; }
    public required bool IsSelected { get; init; }
    public SunderRpcOptional<Text> ValueOverride { get; init; }
    public SunderRpcOptional<StackValueSensitivity> SensitivityOverride { get; init; }

    public Local.StackExportDetailSelection ToPublic() => new(
        DetailId, IsSelected, ValueOverride.IsPresent ? ValueOverride.Value.Value : null,
        SensitivityOverride.IsPresent ? SensitivityOverride.Value : null);
}

[SunderRpcTransparent, SunderRpcFacet(MinItems = 0, MaxItems = 10000)]
internal sealed class StackExportDetailSelectionList
{
    public required IReadOnlyList<StackExportDetailSelection> Value { get; init; }
}

internal sealed class StackExportItemSelection
{
    public required Id ItemId { get; init; }
    public SunderRpcOptional<StackExportDetailSelectionList> Details { get; init; }

    public Local.StackExportItemSelection ToPublic() => new(
        ItemId, Details.IsPresent ? Details.Value.Value.Select(item => item.ToPublic()).ToArray() : null);
}

[SunderRpcTransparent, SunderRpcFacet(MinItems = 0, MaxItems = 10000)]
internal sealed class StackExportItemSelectionList
{
    public required IReadOnlyList<StackExportItemSelection> Value { get; init; }
}

internal sealed class StackExportRequest
{
    public required StackExportItemSelectionList ItemSelections { get; init; }
    public Local.StackExportRequest ToPublic() => new(ItemSelections.Value.Select(item => item.ToPublic()).ToArray());
}

internal sealed class StackRequiredInputDescriptor
{
    public required Id InputId { get; init; }
    public required ShortText Label { get; init; }
    public required StackValueSensitivity Sensitivity { get; init; }
    public required bool Required { get; init; }
    public SunderRpcOptional<Text> Description { get; init; }
    public SunderRpcOptional<Text> DefaultValue { get; init; }

    public static StackRequiredInputDescriptor FromPublic(Local.StackRequiredInputDescriptor value) => new()
    {
        InputId = value.InputId, Label = value.Label, Sensitivity = value.Sensitivity, Required = value.Required,
        Description = Text.Optional(value.Description), DefaultValue = Text.Optional(value.DefaultValue),
    };

}

[SunderRpcTransparent, SunderRpcFacet(MinItems = 0, MaxItems = 10000)]
internal sealed class StackRequiredInputDescriptorList
{
    public required IReadOnlyList<StackRequiredInputDescriptor> Value { get; init; }
}

internal sealed class StackPackageRequirement
{
    public required Id PackageId { get; init; }
    public required Id InstallTag { get; init; }
    public SunderRpcOptional<ShortText> CreatedWithVersion { get; init; }
    public SunderRpcOptional<ShortText> MinimumVersion { get; init; }
    public required bool Required { get; init; }

    public static StackPackageRequirement FromPublic(Local.StackPackageRequirement value) => new()
    {
        PackageId = value.PackageId, InstallTag = value.InstallTag, Required = value.Required,
        CreatedWithVersion = ShortText.Optional(value.CreatedWithVersion), MinimumVersion = ShortText.Optional(value.MinimumVersion),
    };

}

[SunderRpcTransparent, SunderRpcFacet(MinItems = 0, MaxItems = 10000)]
internal sealed class StackPackageRequirementList
{
    public required IReadOnlyList<StackPackageRequirement> Value { get; init; }
}

internal sealed class StackRpcFragmentExport
{
    public required Id FragmentId { get; init; }
    public required Id SchemaId { get; init; }
    public required SchemaVersion SchemaVersion { get; init; }
    public required ShortText DisplayName { get; init; }
    public required JsonText JsonPayload { get; init; }
    public SunderRpcOptional<Text> Description { get; init; }
    public required bool DefaultSelected { get; init; }
    public SunderRpcOptional<StackRequiredInputDescriptorList> RequiredInputs { get; init; }
    public SunderRpcOptional<StackRpcPayloadFileList> Files { get; init; }
    public SunderRpcOptional<Id> SourceItemId { get; init; }

    public static StackRpcFragmentExport FromPublic(Local.StackRpcFragmentExport value) => new()
    {
        FragmentId = value.FragmentId, SchemaId = value.SchemaId, SchemaVersion = new() { Value = value.SchemaVersion },
        DisplayName = value.DisplayName, JsonPayload = new() { Value = value.JsonPayload },
        Description = Text.Optional(value.Description), DefaultSelected = value.DefaultSelected,
        RequiredInputs = value.RequiredInputs is null ? default(SunderRpcOptional<StackRequiredInputDescriptorList>)
            : new StackRequiredInputDescriptorList { Value = value.RequiredInputs.Select(StackRequiredInputDescriptor.FromPublic).ToArray() },
        Files = value.Files is null ? default(SunderRpcOptional<StackRpcPayloadFileList>) : new StackRpcPayloadFileList
        { Value = value.Files.Select(file => new StackRpcPayloadFile { RelativePath = file.RelativePath, Content = new() { Value = file.Content } }).ToArray() },
        SourceItemId = Id.Optional(value.SourceItemId),
    };

}

[SunderRpcTransparent, SunderRpcFacet(MinItems = 0, MaxItems = 10000)]
internal sealed class StackRpcFragmentExportList
{
    public required IReadOnlyList<StackRpcFragmentExport> Value { get; init; }
}

internal sealed class StackRpcExportContribution
{
    public required StackRpcFragmentExportList Fragments { get; init; }
    public required StackPackageRequirementList PackageRequirements { get; init; }
    public required StringList Warnings { get; init; }
}

internal sealed class StackRpcFragmentImport
{
    public required Id FragmentId { get; init; }
    public required Id OwnerPackageId { get; init; }
    public required Id ContributorId { get; init; }
    public required Id SchemaId { get; init; }
    public required SchemaVersion SchemaVersion { get; init; }
    public required ShortText DisplayName { get; init; }
    public required JsonText JsonPayload { get; init; }
    public SunderRpcOptional<Text> Description { get; init; }
    public SunderRpcOptional<StackRpcPayloadFileList> Files { get; init; }
    public Local.StackRpcFragmentImport ToPublic() => new(FragmentId, OwnerPackageId, ContributorId, SchemaId, SchemaVersion.Value,
        DisplayName, JsonPayload.Value, Description.IsPresent ? Description.Value.Value : null,
        Files.IsPresent ? Files.Value.Value.Select(file => new Local.StackRpcPayloadFile(file.RelativePath, file.Content.Value)).ToArray() : null);
}

[SunderRpcTransparent, SunderRpcFacet(MinItems = 0, MaxItems = 10000)]
internal sealed class StackRpcFragmentImportList
{
    public required IReadOnlyList<StackRpcFragmentImport> Value { get; init; }
}

internal sealed class StackRpcImportPreviewRequest
{
    public required StackRpcFragmentImportList Fragments { get; init; }
    public required StringMap InputValues { get; init; }
    public required StringMap IdRemaps { get; init; }
}

internal sealed class StackImportAction
{
    public required Id ActionId { get; init; }
    public required ShortText DisplayName { get; init; }
    public required StackImportActionKind Kind { get; init; }
    public required bool DefaultSelected { get; init; }
    public SunderRpcOptional<Text> Description { get; init; }
    public static StackImportAction FromPublic(Local.StackImportAction value) => new()
    {
        ActionId = value.ActionId, DisplayName = value.DisplayName, Kind = value.Kind,
        DefaultSelected = value.DefaultSelected, Description = Text.Optional(value.Description),
    };
}

[SunderRpcTransparent, SunderRpcFacet(MinItems = 0, MaxItems = 10000)]
internal sealed class StackImportActionList
{
    public required IReadOnlyList<StackImportAction> Value { get; init; }
}

internal sealed class StackImportConflict
{
    public required Id ConflictId { get; init; }
    public required Text Message { get; init; }
    public required StackImportConflictSeverity Severity { get; init; }
    public SunderRpcOptional<Id> FragmentId { get; init; }
    public static StackImportConflict FromPublic(Local.StackImportConflict value) => new()
    {
        ConflictId = value.ConflictId, Message = value.Message, Severity = value.Severity, FragmentId = Id.Optional(value.FragmentId),
    };
}

[SunderRpcTransparent, SunderRpcFacet(MinItems = 0, MaxItems = 10000)]
internal sealed class StackImportConflictList
{
    public required IReadOnlyList<StackImportConflict> Value { get; init; }
}

internal sealed class StackImportPreview
{
    public required StackImportActionList Actions { get; init; }
    public required StackRequiredInputDescriptorList RequiredInputs { get; init; }
    public required StackImportConflictList Conflicts { get; init; }
    public required StringList Warnings { get; init; }
    public static StackImportPreview FromPublic(Local.StackImportPreview value) => new()
    {
        Actions = new() { Value = value.Actions.Select(StackImportAction.FromPublic).ToArray() },
        RequiredInputs = new() { Value = value.RequiredInputs.Select(StackRequiredInputDescriptor.FromPublic).ToArray() },
        Conflicts = new() { Value = value.Conflicts.Select(StackImportConflict.FromPublic).ToArray() },
        Warnings = StringList.FromPublic(value.Warnings),
    };
}

internal sealed class StackRpcImportRequest
{
    public required StackRpcFragmentImportList Fragments { get; init; }
    public required StringMap InputValues { get; init; }
    public required StringMap IdRemaps { get; init; }
    public required IdList SelectedActionIds { get; init; }
}

internal sealed class StackImportedItem
{
    public required Id ItemId { get; init; }
    public required ShortText DisplayName { get; init; }
    public required Id Kind { get; init; }
    public static StackImportedItem FromPublic(Local.StackImportedItem value)
        => new() { ItemId = value.ItemId, DisplayName = value.DisplayName, Kind = value.Kind };
    public Local.StackImportedItem ToPublic() => new(ItemId, DisplayName, Kind);
}

[SunderRpcTransparent, SunderRpcFacet(MinItems = 0, MaxItems = 10000)]
internal sealed class StackImportedItemList
{
    public required IReadOnlyList<StackImportedItem> Value { get; init; }
}

internal sealed class StackImportResult
{
    public required StackImportOutcome Outcome { get; init; }
    public required StackImportedItemList ImportedItems { get; init; }
    public required StringMap IdRemaps { get; init; }
    public required StringList Warnings { get; init; }
    public required StringList Errors { get; init; }
    public static StackImportResult FromPublic(Local.StackImportResult value) => new()
    {
        Outcome = value.Outcome, ImportedItems = new() { Value = value.ImportedItems.Select(StackImportedItem.FromPublic).ToArray() },
        IdRemaps = StringMap.FromPublic(value.IdRemaps), Warnings = StringList.FromPublic(value.Warnings), Errors = StringList.FromPublic(value.Errors),
    };
}

internal sealed class StackImportAppliedContext
{
    public required Id OwnerPackageId { get; init; }
    public required Id ContributorId { get; init; }
    public required IdList FragmentIds { get; init; }
    public required StackImportedItemList ImportedItems { get; init; }
}
