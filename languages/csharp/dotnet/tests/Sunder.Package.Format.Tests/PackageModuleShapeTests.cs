using Sunder.App.Bridge;
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
            error.Contains("multiple IAppPackageEntryPointV1", StringComparison.Ordinal)
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

public abstract class AppModuleBase : IAppPackageEntryPointV1
{
    public abstract ValueTask<IAppPackageActivationV1> CreateAsync(IAppPackageHostV1 host, IAppContributionSinkV1 contributions);
}

public sealed class InheritedAppModule : AppModuleBase
{
    public override ValueTask<IAppPackageActivationV1> CreateAsync(IAppPackageHostV1 host, IAppContributionSinkV1 contributions) => throw new NotSupportedException();
}

public sealed class SecondAppModule : IAppPackageEntryPointV1
{
    public ValueTask<IAppPackageActivationV1> CreateAsync(IAppPackageHostV1 host, IAppContributionSinkV1 contributions) => throw new NotSupportedException();
}

public sealed class OpenGenericAppModule<T> : IAppPackageEntryPointV1
{
    public ValueTask<IAppPackageActivationV1> CreateAsync(IAppPackageHostV1 host, IAppContributionSinkV1 contributions) => throw new NotSupportedException();
}

public sealed class AppModuleWithoutDefaultConstructor(string value) : IAppPackageEntryPointV1
{
    public string Value { get; } = value;

    public ValueTask<IAppPackageActivationV1> CreateAsync(IAppPackageHostV1 host, IAppContributionSinkV1 contributions) => throw new NotSupportedException();
}

public static class ModuleContainer
{
    public sealed class NestedAppModule : IAppPackageEntryPointV1
    {
        public ValueTask<IAppPackageActivationV1> CreateAsync(IAppPackageHostV1 host, IAppContributionSinkV1 contributions) => throw new NotSupportedException();
    }
}
