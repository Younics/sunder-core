using Sunder.Sdk.Packaging;
using Sunder.Sdk.Abstractions;
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

    public static IPackageViewNavigationPreparationTarget? NavigationPreparation => null;

    public static IPackageSettings? UnannotatedSettings => null;

    public static IPackageSettings ReadUnannotatedSettingsTwice(IPackageContext context)
    {
        _ = context.Settings;
        return context.Settings;
    }

    public static object ReadUnresolvedMemberTwice(IPackageContext context)
    {
        _ = context.UnresolvedMember;
        return context.UnresolvedMember;
    }

    public static Type? ResolveUnannotatedContract()
        => Type.GetType("Sunder.Sdk.Abstractions.IPackageSettings, Sunder.Sdk");

    public static Type? ResolveUnresolvedContract()
        => Type.GetType("Sunder.Sdk.Abstractions.UnresolvedSdkContract, Sunder.Sdk");

    public static Type? ResolveUnresolvedContractAgain()
        => Type.GetType("Sunder.Sdk.Abstractions.UnresolvedSdkContract, Sunder.Sdk");
}

public sealed class FixturePackageModule : ISunderAppPackageModule;
