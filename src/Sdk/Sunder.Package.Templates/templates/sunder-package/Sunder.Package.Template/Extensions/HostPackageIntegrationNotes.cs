namespace Sunder.Package.Template.Extensions;

internal static class HostPackageIntegrationNotes
{
    public const string HostPackageId = "sunder.host.package";

    public const string NextStep =
        "If the host package publishes a standard contracts package, generate with --withHostContracts and then add RegisterExtension(...) calls against that package's PackageExtensionPoints.";
}
