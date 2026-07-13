using Sunder.Sdk.Packaging;

[assembly: SunderPackage(Id = "test.authored.reflection", Name = "Authored Reflection Fixture")]

namespace Sunder.Package.Build.Tests.Fixtures.AuthoredReflection;

public static class AuthoredSdkReflection
{
    public static Type? ResolveSdkContract()
        => Type.GetType("Sunder.Sdk.Abstractions.IPackageContext, Sunder.Sdk");
}
