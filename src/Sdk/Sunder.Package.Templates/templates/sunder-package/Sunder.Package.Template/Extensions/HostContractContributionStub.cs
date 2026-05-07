using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Template;

public sealed partial class PackageModule
{
    partial void RegisterHostContractContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
        // TODO: Replace this placeholder with the actual host contracts types from
        // Sunder.Host.Package.Contracts after reviewing that package's published PackageExtensionPoints.
        //
        // Example shape:
        // registry.RegisterExtension(PackageExtensionPoints.Sample, new MyContribution());
    }
}
