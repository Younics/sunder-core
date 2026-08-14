using Sunder.Sdk.Stacks;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Template.Worker;

internal static partial class WorkerConfiguration
{
    static partial void AddStackProviders(List<SunderWorkerProviderRegistration> providers)
    {
        var contributor = new PackageStackContributor();
        providers.Add(new SunderWorkerProviderRegistration(
            "SUNDER_PACKAGE_ID_TEXT.stack",
            SunderStackContributorRpc.ContractId,
            SunderStackContributorRpc.ContractVersion,
            SunderStackContributorRpc.DescriptorSha256,
            SunderStackContributorRpc.AbiSha256,
            SunderStackContributorRpc.CreateHandler(contributor)));
    }
}

public sealed class PackageStackContributor : IPackageStackExporter, IPackageStackImporter
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
