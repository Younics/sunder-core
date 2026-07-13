using Microsoft.Extensions.DependencyInjection;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeHostCompositionTests
{
    [Fact]
    public async Task ProductionComposition_ValidatesAndResolvesEveryRuntimeService()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = new RuntimePackagePaths(root);
            var services = new ServiceCollection();
            services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("test-runtime-token"));

            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

            Assert.NotNull(provider.GetRequiredService<PackageSessionLifecycleService>());
            Assert.NotNull(provider.GetRequiredService<InstalledPackageLifecycleService>());
            Assert.NotNull(provider.GetRequiredService<DevPackageWatchService>());
            Assert.NotNull(provider.GetRequiredService<PackageLogStreamService>());
            Assert.NotNull(provider.GetRequiredService<RuntimeEventStreamService>());
            Assert.NotNull(provider.GetRequiredService<RuntimeContentTransferService>());
            Assert.NotNull(provider.GetRequiredService<RuntimePackageUiService>());
            Assert.NotNull(provider.GetRequiredService<PackageSessionCommandService>());
            Assert.NotNull(provider.GetRequiredService<RuntimePackageDataService>());
            Assert.NotNull(provider.GetRequiredService<PackageSettingsAccessService>());
            Assert.NotNull(provider.GetRequiredService<PackageAuthAccessService>());
            Assert.NotNull(provider.GetRequiredService<PackageFaultService>());
            Assert.NotNull(provider.GetRequiredService<RuntimeStackExportService>());
            Assert.NotNull(provider.GetRequiredService<RuntimeStackImportService>());
            Assert.NotNull(provider.GetRequiredService<PackageCallbackAccessService>());
            Assert.NotNull(provider.GetRequiredService<PackageCallbackServer>());
            Assert.NotNull(provider.GetRequiredService<RegistryCredentialStore>());
            Assert.NotNull(provider.GetRequiredService<RegistryAuthCoordinator>());
            Assert.NotNull(provider.GetRequiredService<RegistryPackagePlanResolver>());
            Assert.NotNull(provider.GetRequiredService<RegistryPackageChangeOrchestrator>());
            Assert.NotNull(provider.GetRequiredService<RegistryAuthenticatedOperations>());

            Assert.Same(
                provider.GetRequiredService<RuntimeSessionOwner>().State,
                provider.GetRequiredService<PackageSessionState>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RegistryProductionServices_DoNotDependOnCompatibilityFacade()
    {
        var productionTypes = new[]
        {
            typeof(RegistryPackageChangeOrchestrator),
            typeof(RegistryAuthenticatedOperations),
        };

        var facadeDependencies = productionTypes
            .SelectMany(type => type.GetConstructors())
            .SelectMany(constructor => constructor.GetParameters())
            .Where(parameter => parameter.ParameterType.Name.Contains("SessionService", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(facadeDependencies);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "SunderRuntimeCompositionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
