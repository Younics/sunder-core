using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed class PackageModuleShapeTests
{
    [Fact]
    public void Read_EnforcesInheritedRoleCountTopLevelVisibilityAndConstructorPolicy()
    {
        var shape = PackageModuleShapeReader.Read(typeof(InheritedAppModule).Assembly.Location);

        var errors = shape.Validate();

        Assert.Contains(errors, error =>
            error.Contains("multiple ISunderAppPackageModule", StringComparison.Ordinal)
            && error.Contains(typeof(InheritedAppModule).FullName!, StringComparison.Ordinal)
            && error.Contains(typeof(SecondAppModule).FullName!, StringComparison.Ordinal));
        Assert.Contains(errors, error =>
            error.Contains(typeof(AppModuleWithoutDefaultConstructor).FullName!, StringComparison.Ordinal)
            && error.Contains("public parameterless constructor", StringComparison.Ordinal));
        Assert.Contains(errors, error =>
            error.Contains(typeof(ModuleContainer.NestedAppModule).FullName!, StringComparison.Ordinal)
            && error.Contains("top-level public class", StringComparison.Ordinal));
        Assert.Contains(errors, error =>
            error.Contains(typeof(OpenGenericAppModule<>).FullName!, StringComparison.Ordinal)
            && error.Contains("non-generic", StringComparison.Ordinal));
        Assert.NotNull(shape.Resolve(PackageHostRoleMetadataValue.App).Error);
    }
}

public abstract class AppModuleBase : ISunderAppPackageModule
{
    public abstract void ConfigureAppServices(IServiceCollection services, IPackageContext context);

    public abstract void RegisterAppContributions(
        ISunderAppContributionRegistry registry,
        IServiceProvider services);
}

public sealed class InheritedAppModule : AppModuleBase
{
    public override void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
    }

    public override void RegisterAppContributions(
        ISunderAppContributionRegistry registry,
        IServiceProvider services)
    {
    }
}

public sealed class SecondAppModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
    }

    public void RegisterAppContributions(
        ISunderAppContributionRegistry registry,
        IServiceProvider services)
    {
    }
}

public sealed class OpenGenericAppModule<T> : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
    }

    public void RegisterAppContributions(
        ISunderAppContributionRegistry registry,
        IServiceProvider services)
    {
    }
}

public sealed class AppModuleWithoutDefaultConstructor(string value) : ISunderAppPackageModule
{
    public string Value { get; } = value;

    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
    }
}

public static class ModuleContainer
{
    public sealed class NestedAppModule : ISunderAppPackageModule
    {
        public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
        {
        }

        public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
        {
        }
    }
}
