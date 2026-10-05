using Sunder.Sdk.Stacks;
using Sunder.Sdk.Rpc;
namespace Sunder.Package.Template;

[SunderRpcProvider(typeof(IPackageStackContributor), ProviderId = "SUNDER_PACKAGE_ID_TEXT.stack")]
public sealed partial class PackageStackContributor : IPackageStackExporter, IPackageStackImporter
{
    public string ContributorId => "SUNDER_PACKAGE_ID_CSHARP.stack";

    public string DisplayName => "SUNDER_PACKAGE_NAME_CSHARP";

    public ValueTask<IReadOnlyList<StackExportItemDescriptor>> ListExportItemsAsync(
        StackExportDiscoveryContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<StackExportItemDescriptor>>([]);

    public ValueTask<StackExportContribution> ExportAsync(
        StackExportRequest request,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new StackExportContribution([], [], []));

    public ValueTask<StackImportPreview> PreviewImportAsync(
        StackImportPreviewRequest request,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new StackImportPreview([], [], [], []));

    public ValueTask<StackImportResult> ImportAsync(
        StackImportRequest request,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new StackImportResult(StackImportOutcome.Completed, [], new Dictionary<string, string>(), [], []));
}
