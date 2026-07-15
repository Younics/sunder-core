using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageSessionRolePruningTests
{
    [Fact]
    public async Task LoadInstalledAsync_PreHostRoleManifest_InfersRolesFromEntryAssembly()
    {
        var root = CreateTempDirectory();
        var packageFolder = Path.Combine(root, "installed", "test.package", "1.0.0");
        var manifestFolder = Path.Combine(packageFolder, "manifest");
        var libraryFolder = Path.Combine(packageFolder, "payload", "lib");
        Directory.CreateDirectory(manifestFolder);
        Directory.CreateDirectory(libraryFolder);
        var entryAssembly = typeof(PackageSessionOverlayTestPackageModule).Assembly.Location;
        var entryAssemblyName = Path.GetFileName(entryAssembly);
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
        {
            File.Copy(file, Path.Combine(libraryFolder, Path.GetFileName(file)), overwrite: true);
        }
        File.WriteAllText(Path.Combine(manifestFolder, "sunder-package.json"), JsonSerializer.Serialize(new
        {
            manifestVersion = 1,
            id = "test.package",
            name = "Test Package",
            version = "1.0.0",
            entryAssembly = entryAssemblyName,
            sdkApiVersion = 1,
            sdkPackageVersion = "1.1.0",
            requiredSdkCapabilities = new[] { "sdk-baseline-1-1.v1", "core.v1" },
        }));
        var record = new InstalledPackageRecord(
            "test.package",
            "Test Package",
            Summary: null,
            "1.0.0",
            entryAssemblyName,
            Icon: null,
            DependsOn: [],
            packageFolder,
            IsEnabled: true,
            DateTimeOffset.UtcNow);
        var loader = new PackageSessionLoadService(NullLogger.Instance, new RuntimePackagePaths(root));

        var result = await loader.LoadInstalledAsync([record]);

        Assert.Empty(result.Errors);
        var session = Assert.IsType<ActivePackageSession>(result.Session);
        Assert.Equal(
            PackageHostRoles.App | PackageHostRoles.Runtime,
            Assert.Single(session.GetActivePackages()).HostRoles);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task LoadInstalledWithDevOverlaysAsync_ContractOnlyPackageIsReadyWithoutRuntimeLoadContext()
    {
        var root = CreateTempDirectory();
        var packageFolder = Path.Combine(root, "contract-package");
        var libraryFolder = Path.Combine(packageFolder, "lib");
        Directory.CreateDirectory(libraryFolder);
        var entryAssembly = typeof(PackageSessionLoadService).Assembly.Location;
        var entryAssemblyName = Path.GetFileName(entryAssembly);
        File.Copy(entryAssembly, Path.Combine(libraryFolder, entryAssemblyName));
        File.WriteAllText(Path.Combine(packageFolder, "sunder-package.json"), JsonSerializer.Serialize(new SunderPackageManifest
        {
            ManifestVersion = 1,
            Id = "test.contracts",
            Name = "Test Contracts",
            Version = "1.0.0",
            EntryAssembly = entryAssemblyName,
            HostRoles = [SunderPackageFormat.ContractOnlyHostRole],
            SdkApiVersion = 1,
            SdkPackageVersion = "1.1.0",
            RequiredSdkCapabilities = ["sdk-baseline-1-1.v1", "core.v1"],
        }));
        var loader = new PackageSessionLoadService(NullLogger.Instance, new RuntimePackagePaths(root));

        var result = await loader.LoadInstalledWithDevOverlaysAsync([], [packageFolder]);

        Assert.Empty(result.Errors);
        var session = Assert.IsType<ActivePackageSession>(result.Session);
        var descriptor = Assert.Single(session.GetActivePackages());
        Assert.Equal(PackageHostRoles.ContractOnly, descriptor.HostRoles);
        Assert.Empty(session.LoadedPackageMap);
        Assert.Single(session.GetActivePackageSources());
        Assert.Empty(session.GetAppPackageSources());
        await session.DisposeAsync();
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-role-pruning-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
