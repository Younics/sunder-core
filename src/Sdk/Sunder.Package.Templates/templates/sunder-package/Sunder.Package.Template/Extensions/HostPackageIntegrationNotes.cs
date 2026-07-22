namespace Sunder.Package.Template.Extensions;

internal static class HostPackageIntegrationNotes
{
    public const string HostPackageId = "SUNDER_HOST_PACKAGE_ID_CSHARP";

    public const string NextStep =
        "If the host package publishes a standard contracts package, generate with --withHostContracts and then add RegisterExtension(...) calls against that package's PackageExtensionPoints.";
}
