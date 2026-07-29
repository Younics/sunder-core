using Sunder.Sdk.Packaging;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Runtime;
using Sunder.Sdk.Storage;

[assembly: SunderPackage(
    Id = "test.source.reference.package",
    Name = "Source Reference Package",
    Summary = "Exercises source-reference SDK build outputs.")]

namespace Sunder.Package.Build.Tests.Fixtures.SourceReferencePackage;

public static class PackageMarker
{
    public static IPackageStorageKeyMigrator? StorageMigrator => null;

    public static PackageStorageKeyMigration? StorageMigration => null;

    public static PackageRuntimeInvocationException? RuntimeFailure => null;

    public static IPackageViewNavigationPreparationTarget? NavigationPreparation => null;
}
