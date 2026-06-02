using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Stacks;

[SunderSdkCapability(SunderSdkCapabilities.StacksV1)]
[SunderSdkCapability(SunderSdkCapabilities.StackContributionsV1)]
public interface IPackageStackContributor
{
    string ContributorId { get; }

    string DisplayName { get; }

    ValueTask<IReadOnlyList<StackExportItemDescriptor>> ListExportItemsAsync(
        StackExportDiscoveryContext context,
        CancellationToken cancellationToken = default);

    ValueTask<StackExportContribution> ExportAsync(
        StackExportRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<StackImportPreview> PreviewImportAsync(
        StackImportPreviewRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<StackImportResult> ImportAsync(
        StackImportRequest request,
        CancellationToken cancellationToken = default);
}
