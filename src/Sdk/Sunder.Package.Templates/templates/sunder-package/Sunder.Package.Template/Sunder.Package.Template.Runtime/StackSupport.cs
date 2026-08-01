using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;

namespace Sunder.Package.Template.Runtime;

public sealed partial class PackageModule
{
    partial void RegisterStackContributions(
        ISunderRuntimeContributionRegistry registry,
        IServiceProvider services)
    {
        var contributor = new PackageStackContributor();
        registry.RegisterStackContributor("SUNDER_PACKAGE_ID_TEXT.stack", contributor, services);
    }
}

public sealed class PackageStackContributor : IPackageStackExporter, IPackageStackImporter
{
    public string ContributorId => "SUNDER_PACKAGE_ID_CSHARP.stacks";

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
