using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageSessionRolePruningTests
{
    [Fact]
    public async Task LoadInstalledWithDevOverlaysAsync_AppOnlyPackageIsReadyWithoutRuntimeLoadContext()
    {
        var root = CreateTempDirectory();
        var packageFolder = Path.Combine(root, "contract-package");
        var entryAssembly = typeof(PackageSessionLoadService).Assembly.Location;
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            packageFolder,
            "test.contracts",
            "1.0.0",
            entryAssembly,
            roles: [SunderPackageFormat.AppHostRole]);
        var loader = new PackageSessionLoadService(NullLogger.Instance, new RuntimePackagePaths(root));

        var result = await loader.LoadInstalledWithDevOverlaysAsync([], [packageFolder]);

        Assert.Empty(result.Errors);
        var session = Assert.IsType<ActivePackageSession>(result.Session);
        var descriptor = Assert.Single(session.GetActivePackages());
        Assert.Equal(PackageHostRoles.App, descriptor.HostRoles);
        Assert.Empty(session.LoadedPackageMap);
        Assert.Single(session.GetActivePackageSources());
        Assert.Single(session.GetAppPackageSources());
        await session.DisposeAsync();
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-role-pruning-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
