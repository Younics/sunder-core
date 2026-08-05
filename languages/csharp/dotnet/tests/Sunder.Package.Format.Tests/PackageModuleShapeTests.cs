using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed class PackageModuleShapeTests
{
    [Fact]
    public void Read_EnforcesInheritedRoleCountTopLevelVisibilityAndConstructorPolicy()
    {
        var shape = PackageModuleShapeReader.Read(typeof(InheritedRuntimeModule).Assembly.Location);

        var errors = shape.Validate();

        Assert.Contains(errors, error =>
            error.Contains("multiple ISunderRuntimePackageModule", StringComparison.Ordinal)
            && error.Contains(typeof(InheritedRuntimeModule).FullName!, StringComparison.Ordinal)
            && error.Contains(typeof(SecondRuntimeModule).FullName!, StringComparison.Ordinal));
        Assert.Contains(errors, error =>
            error.Contains(typeof(AppModuleWithoutDefaultConstructor).FullName!, StringComparison.Ordinal)
            && error.Contains("public parameterless constructor", StringComparison.Ordinal));
        Assert.Contains(errors, error =>
            error.Contains(typeof(ModuleContainer.NestedAppModule).FullName!, StringComparison.Ordinal)
            && error.Contains("top-level public class", StringComparison.Ordinal));
        Assert.Contains(errors, error =>
            error.Contains(typeof(OpenGenericRuntimeModule<>).FullName!, StringComparison.Ordinal)
            && error.Contains("non-generic", StringComparison.Ordinal));
        Assert.NotNull(shape.Resolve(PackageHostRoleMetadataValue.App).Error);
        Assert.NotNull(shape.Resolve(PackageHostRoleMetadataValue.Runtime).Error);
    }
}

public abstract class RuntimeModuleBase : ISunderRuntimePackageModule
{
    public abstract void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context);

    public abstract void RegisterRuntimeContributions(
        ISunderRuntimeContributionRegistry registry,
        IServiceProvider services);
}

public sealed class InheritedRuntimeModule : RuntimeModuleBase
{
    public override void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
    }

    public override void RegisterRuntimeContributions(
        ISunderRuntimeContributionRegistry registry,
        IServiceProvider services)
    {
    }
}

public sealed class SecondRuntimeModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
    }

    public void RegisterRuntimeContributions(
        ISunderRuntimeContributionRegistry registry,
        IServiceProvider services)
    {
    }
}

public sealed class OpenGenericRuntimeModule<T> : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
    }

    public void RegisterRuntimeContributions(
        ISunderRuntimeContributionRegistry registry,
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
