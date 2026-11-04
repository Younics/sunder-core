using Sunder.Sdk.Packaging;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;

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

public sealed class FixturePackageModule : ISunderAppPackageModule, Sunder.App.Bridge.IAppPackageEntryPointV1
{
    public ValueTask<Sunder.App.Bridge.IAppPackageActivationV1> CreateAsync(
        Sunder.App.Bridge.IAppPackageHostV1 host, Sunder.App.Bridge.IAppContributionSinkV1 contributions)
        => throw new NotSupportedException("Metadata-only fixture.");

    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
    }
}
