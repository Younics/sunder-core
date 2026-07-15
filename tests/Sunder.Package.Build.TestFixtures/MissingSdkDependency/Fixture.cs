using Sunder.Sdk.Packaging;
using Sunder.Sdk.Stacks;

[assembly: SunderPackage(Id = "test.missing.sdk.dependency", Name = "Missing SDK Dependency Fixture")]

namespace Sunder.Package.Build.Tests.Fixtures.MissingSdkDependency;

public static class StackContractReference
{
    public static Type ContractType => typeof(IPackageStackExporter);
}
