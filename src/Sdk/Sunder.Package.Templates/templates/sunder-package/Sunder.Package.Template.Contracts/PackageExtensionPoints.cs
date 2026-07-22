using Sunder.Package.Template.Contracts.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Template.Contracts;

public static class PackageExtensionPoints
{
    public static readonly PackageExtensionPoint<IPackageSampleContribution> Sample =
        new("SUNDER_PACKAGE_ID_CSHARP:sample");
}
