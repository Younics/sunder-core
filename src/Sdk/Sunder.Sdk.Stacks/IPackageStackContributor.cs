using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Stacks;

/// <summary>Discovers, exports, previews, and imports Stack content owned by one package feature.</summary>
/// <remarks>Contributor instances are activation-scoped and may be called concurrently. Preview must be side-effect free. The host binds a preview to an expiring, single-use import plan. Import owns committed mutations but not host-provided fragment collections or temporary files. Imports are atomic only within the guarantees made by each contributor; the host cannot roll back another contributor.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
[SunderSdkCapability(SunderSdkCapabilities.StackContributionsV1)]
public interface IPackageStackContributor
{
    /// <summary>Gets a stable globally unique id used in Stack fragments.</summary>
    string ContributorId { get; }

    /// <summary>Gets the user-facing contributor name.</summary>
    string DisplayName { get; }

    /// <summary>Discovers an immutable snapshot of exportable items without mutating package state.</summary>
    ValueTask<IReadOnlyList<StackExportItemDescriptor>> ListExportItemsAsync(
        StackExportDiscoveryContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Exports selected items; cancellation must leave source package state unchanged.</summary>
    ValueTask<StackExportContribution> ExportAsync(
        StackExportRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Builds a side-effect-free import plan and reports required inputs and conflicts.</summary>
    ValueTask<StackImportPreview> PreviewImportAsync(
        StackImportPreviewRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Applies selected actions and reports committed effects; cancellation should stop before additional mutations where safe.</summary>
    ValueTask<StackImportResult> ImportAsync(
        StackImportRequest request,
        CancellationToken cancellationToken = default);
}
