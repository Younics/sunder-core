using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Template;

public sealed partial class PackageModule
{
    partial void RegisterStackContributions(ISunderRuntimeContributionRegistry registry)
        => registry.RegisterExtension(SunderStackExtensionPoints.StackContributors, new PackageStackContributor());
}

public sealed class PackageStackContributor : IPackageStackContributor
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
        => ValueTask.FromResult(new StackImportResult(true, [], new Dictionary<string, string>(), [], []));
}
