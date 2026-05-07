using Sunder.Package.Template.Contracts.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Template.Contracts;

public static class PackageExtensionPoints
{
    public static readonly PackageExtensionPoint<IPackageSampleContribution> Sample =
        new("sunder.package.template:sample");
}
