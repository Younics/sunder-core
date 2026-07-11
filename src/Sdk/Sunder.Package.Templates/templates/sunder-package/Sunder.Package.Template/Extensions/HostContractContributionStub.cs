using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Template;

public sealed partial class PackageModule
{
    partial void RegisterHostContractContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        // Add RegisterExtension(...) calls here after reviewing the host contracts package's
        // published PackageExtensionPoints.
    }
}
