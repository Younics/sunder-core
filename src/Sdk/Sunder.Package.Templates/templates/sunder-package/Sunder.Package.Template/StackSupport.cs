using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Template;

public sealed partial class PackageModule
{
    partial void RegisterStackContributions(ISunderRuntimeContributionRegistry registry)
    {
        var contributor = new PackageStackContributor();
        registry.RegisterExtension(SunderStackExtensionPoints.StackExporters, contributor);
        registry.RegisterExtension(SunderStackExtensionPoints.StackImporters, contributor);
    }
}

public sealed class PackageStackContributor : IPackageStackExporter, IPackageStackImporter
{
    public string ContributorId => "sunder.package.template.stacks";

    public string DisplayName => "Sunder Package Template";

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
