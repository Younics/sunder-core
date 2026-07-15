using Sunder.Sdk.Packaging;

[assembly: SunderPackage(Id = "test.authored.dynamicreflection", Name = "Authored Dynamic Reflection Fixture")]

namespace Sunder.Package.Build.Tests.Fixtures.AuthoredDynamicReflection;

public static class AuthoredSdkDynamicReflection
{
    public static Type? ResolveSdkContract(string assemblyQualifiedTypeName)
        => Type.GetType(assemblyQualifiedTypeName);

    public static async Task<Type?> ResolveSdkContractAsync(string assemblyQualifiedTypeName)
    {
        await Task.Yield();
        return Type.GetType(assemblyQualifiedTypeName);
    }

    public static Func<Type?> CreateSdkContractResolver(string assemblyQualifiedTypeName)
        => () => Type.GetType(assemblyQualifiedTypeName);

    public static IEnumerable<Type?> ResolveSdkContracts(string assemblyQualifiedTypeName)
    {
        yield return Type.GetType(assemblyQualifiedTypeName);
    }
}
