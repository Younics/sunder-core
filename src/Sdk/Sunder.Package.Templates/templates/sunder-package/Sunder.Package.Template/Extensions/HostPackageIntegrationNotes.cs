namespace Sunder.Package.Template.Extensions;

internal static class HostPackageIntegrationNotes
{
    public const string HostPackageId = "sunder.host.package";

    public const string HostContractsPackageId = "Sunder.Host.Package.Contracts";

    public const string HostContractsVersion = "1.0.0-HOST-CONTRACTS";

    public const string NextStep =
        "If the host package publishes a standard contracts package, generate with --withHostContracts and then replace the placeholder extension stub with real RegisterExtension(...) calls against that package's PackageExtensionPoints.";
}
