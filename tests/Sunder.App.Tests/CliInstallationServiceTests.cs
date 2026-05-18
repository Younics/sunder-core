using Sunder.App.Services;
using static Sunder.App.Tests.TestSupport.TestPaths;
using Xunit;

namespace Sunder.App.Tests;

public sealed class CliInstallationServiceTests
{
    [Fact]
    public async Task GetStatusAsync_MissingBundledCliReportsNotInstallable()
    {
        var testRoot = CreateTempDirectory();
        var environment = new FakeCliEnvironmentVariableStore();
        var service = CreateService(testRoot, CliInstallPlatform.Windows, environment);

        var status = await service.GetStatusAsync();

        Assert.False(status.IsBundledCliAvailable);
        Assert.False(status.CanInstallOrRepair);
        Assert.False(status.IsInstalled);
        Assert.Contains("Bundled Sunder CLI", status.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetStatusAsync_UnixMissingBundledCliReportsMissingCliBeforePathWarning()
    {
        var testRoot = CreateTempDirectory();
        var environment = new FakeCliEnvironmentVariableStore { ProcessPath = "/usr/local/bin" };
        var service = CreateService(testRoot, CliInstallPlatform.Linux, environment);

        var status = await service.GetStatusAsync();

        Assert.False(status.IsBundledCliAvailable);
        Assert.Contains("Bundled Sunder CLI", status.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureInstalledAsync_WindowsCopiesCliCreatesShimAndAddsUserPath()
    {
        var testRoot = CreateTempDirectory();
        CreateBundledCli(testRoot, CliInstallPlatform.Windows, "v1");
        var environment = new FakeCliEnvironmentVariableStore
        {
            ProcessPath = @"C:\Windows\System32",
            UserPath = @"C:\Users\test\bin",
        };
        var service = CreateService(testRoot, CliInstallPlatform.Windows, environment);

        var result = await service.EnsureInstalledAsync();

        Assert.True(result.InstalledOrUpdatedCli);
        Assert.True(result.CreatedOrUpdatedShim);
        Assert.True(result.UpdatedUserPath);
        Assert.True(result.Status.IsInstalled);
        Assert.True(result.Status.IsShimCurrent);
        Assert.True(result.Status.IsShimDirectoryOnUserPath);
        Assert.False(result.Status.RequiresTerminalRestart);
        Assert.True(result.Status.IsFullyInstalled);
        Assert.True(string.IsNullOrWhiteSpace(result.Status.Warning));
        Assert.Equal(1, environment.UserPathSetCount);
        Assert.Equal(1, environment.BroadcastCount);
        Assert.Equal("v1", File.ReadAllText(result.Status.Paths.InstalledCliPath));
        Assert.Contains($"\"{result.Status.Paths.InstalledCliPath}\" %*", File.ReadAllText(result.Status.Paths.ShimPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureInstalledAsync_IsIdempotentWhenCliShimAndPathAreCurrent()
    {
        var testRoot = CreateTempDirectory();
        CreateBundledCli(testRoot, CliInstallPlatform.Windows, "v1");
        var environment = new FakeCliEnvironmentVariableStore
        {
            ProcessPath = @"C:\Windows\System32",
            UserPath = @"C:\Users\test\bin",
        };
        var service = CreateService(testRoot, CliInstallPlatform.Windows, environment);
        var first = await service.EnsureInstalledAsync();

        var second = await service.EnsureInstalledAsync();

        Assert.True(first.InstalledOrUpdatedCli);
        Assert.False(second.InstalledOrUpdatedCli);
        Assert.False(second.CreatedOrUpdatedShim);
        Assert.False(second.UpdatedUserPath);
        Assert.Equal(1, environment.UserPathSetCount);
    }

    [Fact]
    public async Task StartupNotificationPolicy_DoesNotWarnForPathOnlyUpdates()
    {
        var testRoot = CreateTempDirectory();
        CreateBundledCli(testRoot, CliInstallPlatform.Windows, "v1");
        var environment = new FakeCliEnvironmentVariableStore
        {
            ProcessPath = @"C:\Windows\System32",
            UserPath = @"C:\Users\test\bin",
        };
        var service = CreateService(testRoot, CliInstallPlatform.Windows, environment);
        var first = await service.EnsureInstalledAsync();
        var second = await service.EnsureInstalledAsync();

        Assert.False(CliStartupNotificationPolicy.TryCreateWarning(first, out _));
        Assert.False(second.UpdatedUserPath);
        Assert.False(second.Status.RequiresTerminalRestart);
        Assert.False(CliStartupNotificationPolicy.TryCreateWarning(second, out _));
    }

    [Fact]
    public async Task StartupNotificationPolicy_WarnsForRealInstallProblems()
    {
        var testRoot = CreateTempDirectory();
        var environment = new FakeCliEnvironmentVariableStore();
        var service = CreateService(testRoot, CliInstallPlatform.Windows, environment);

        var result = await service.EnsureInstalledAsync();

        Assert.True(CliStartupNotificationPolicy.TryCreateWarning(result, out var warning));
        Assert.Contains("Bundled Sunder CLI", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetStatusAsync_WhenUserPathContainsShimDoesNotReportPathWarning()
    {
        var testRoot = CreateTempDirectory();
        CreateBundledCli(testRoot, CliInstallPlatform.Windows, "v1");
        var environment = new FakeCliEnvironmentVariableStore
        {
            ProcessPath = @"C:\Windows\System32",
            UserPath = @"C:\Users\test\bin",
        };
        var service = CreateService(testRoot, CliInstallPlatform.Windows, environment);
        await service.EnsureInstalledAsync();

        var status = await service.GetStatusAsync();

        Assert.False(status.RequiresTerminalRestart);
        Assert.True(status.IsFullyInstalled);
        Assert.True(string.IsNullOrWhiteSpace(status.Warning));
        Assert.Equal("Sunder CLI is installed.", status.Summary);
    }

    [Fact]
    public async Task EnsureInstalledAsync_CopiesFrameworkDependentCliCompanionFiles()
    {
        var testRoot = CreateTempDirectory();
        CreateBundledCli(testRoot, CliInstallPlatform.Windows, "exe");
        var bundledCliDirectory = Path.Combine(testRoot, "app", "Cli");
        File.WriteAllText(Path.Combine(bundledCliDirectory, "sunder.dll"), "dll");
        File.WriteAllText(Path.Combine(bundledCliDirectory, "Sunder.Protocol.dll"), "protocol");
        var environment = new FakeCliEnvironmentVariableStore();
        var service = CreateService(testRoot, CliInstallPlatform.Windows, environment);

        var result = await service.EnsureInstalledAsync();

        Assert.Equal("exe", File.ReadAllText(result.Status.Paths.InstalledCliPath));
        Assert.Equal("dll", File.ReadAllText(Path.Combine(result.Status.Paths.InstalledCliDirectory, "sunder.dll")));
        Assert.Equal("protocol", File.ReadAllText(Path.Combine(result.Status.Paths.InstalledCliDirectory, "Sunder.Protocol.dll")));
    }

    [Fact]
    public async Task EnsureInstalledAsync_ReplacesInstalledCliWhenBundledCliChanges()
    {
        var testRoot = CreateTempDirectory();
        var bundledCliPath = CreateBundledCli(testRoot, CliInstallPlatform.Windows, "v1");
        var environment = new FakeCliEnvironmentVariableStore();
        var service = CreateService(testRoot, CliInstallPlatform.Windows, environment);
        await service.EnsureInstalledAsync();

        await File.WriteAllTextAsync(bundledCliPath, "v2");
        var result = await service.EnsureInstalledAsync();

        Assert.True(result.InstalledOrUpdatedCli);
        Assert.Equal("v2", File.ReadAllText(result.Status.Paths.InstalledCliPath));
    }

    [Fact]
    public async Task EnsureInstalledAsync_UnixCreatesShimAndProvidesPathInstructionsWithoutWarning()
    {
        var testRoot = CreateTempDirectory();
        CreateBundledCli(testRoot, CliInstallPlatform.Linux, "v1");
        var environment = new FakeCliEnvironmentVariableStore { ProcessPath = "/usr/local/bin" };
        var service = CreateService(testRoot, CliInstallPlatform.Linux, environment);

        var result = await service.EnsureInstalledAsync();

        Assert.True(result.InstalledOrUpdatedCli);
        Assert.True(result.CreatedOrUpdatedShim);
        Assert.False(result.UpdatedUserPath);
        Assert.False(result.Status.RequiresManualPathConfiguration);
        Assert.True(result.Status.IsFullyInstalled);
        Assert.True(string.IsNullOrWhiteSpace(result.Status.Warning));
        Assert.Contains("export PATH=", result.Status.PathInstructions, StringComparison.Ordinal);

        var shimContent = File.ReadAllText(result.Status.Paths.ShimPath);
        Assert.StartsWith("#!/usr/bin/env sh\n", shimContent, StringComparison.Ordinal);
        Assert.Contains($"exec \"{result.Status.Paths.InstalledCliPath.Replace("\\", "\\\\", StringComparison.Ordinal)}\" \"$@\"", shimContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetStatusAsync_DetectsShimDirectoryOnCurrentPath()
    {
        var testRoot = CreateTempDirectory();
        CreateBundledCli(testRoot, CliInstallPlatform.Linux, "v1");
        var environment = new FakeCliEnvironmentVariableStore();
        var service = CreateService(testRoot, CliInstallPlatform.Linux, environment);
        var installResult = await service.EnsureInstalledAsync();
        environment.ProcessPath = $"/usr/local/bin:{installResult.Status.Paths.ShimDirectory}";

        var status = await service.GetStatusAsync();

        Assert.True(status.IsShimDirectoryOnCurrentProcessPath);
        Assert.False(status.RequiresManualPathConfiguration);
        Assert.True(status.IsFullyInstalled);
    }

    [Fact]
    public async Task UninstallAsync_RemovesInstalledCliAndShimWithoutChangingPath()
    {
        var testRoot = CreateTempDirectory();
        CreateBundledCli(testRoot, CliInstallPlatform.Windows, "v1");
        var bundledCliDirectory = Path.Combine(testRoot, "app", "Cli");
        File.WriteAllText(Path.Combine(bundledCliDirectory, "Sunder.Protocol.dll"), "protocol");
        var environment = new FakeCliEnvironmentVariableStore { UserPath = @"C:\Users\test\bin" };
        var service = CreateService(testRoot, CliInstallPlatform.Windows, environment);
        var installResult = await service.EnsureInstalledAsync();
        var installedCompanionPath = Path.Combine(installResult.Status.Paths.InstalledCliDirectory, "Sunder.Protocol.dll");
        Assert.True(File.Exists(installedCompanionPath));

        var status = await service.UninstallAsync();

        Assert.False(File.Exists(installResult.Status.Paths.InstalledCliPath));
        Assert.False(File.Exists(installedCompanionPath));
        Assert.False(Directory.Exists(installResult.Status.Paths.InstalledCliDirectory));
        Assert.False(File.Exists(installResult.Status.Paths.ShimPath));
        Assert.False(status.IsInstalled);
        Assert.False(status.IsShimInstalled);
        Assert.Contains("Sunder", environment.UserPath, StringComparison.Ordinal);
        Assert.Equal(1, environment.UserPathSetCount);
    }

    private static CliInstallationService CreateService(
        string testRoot,
        CliInstallPlatform platform,
        FakeCliEnvironmentVariableStore environment)
    {
        var options = new CliInstallationOptions
        {
            AppBaseDirectory = Path.Combine(testRoot, "app"),
            LocalApplicationDataPath = Path.Combine(testRoot, "local-app-data"),
            UserProfilePath = Path.Combine(testRoot, "home"),
            Platform = platform,
        };

        return new CliInstallationService(options, environment);
    }

    private static string CreateBundledCli(string testRoot, CliInstallPlatform platform, string content)
    {
        var fileName = platform == CliInstallPlatform.Windows ? "sunder.exe" : "sunder";
        var bundledCliDirectory = Path.Combine(testRoot, "app", "Cli");
        Directory.CreateDirectory(bundledCliDirectory);

        var bundledCliPath = Path.Combine(bundledCliDirectory, fileName);
        File.WriteAllText(bundledCliPath, content);
        return bundledCliPath;
    }

    private sealed class FakeCliEnvironmentVariableStore : ICliEnvironmentVariableStore
    {
        public string? ProcessPath { get; set; }

        public string? UserPath { get; set; }

        public int UserPathSetCount { get; private set; }

        public int BroadcastCount { get; private set; }

        public string? GetProcessEnvironmentVariable(string name)
            => string.Equals(name, "PATH", StringComparison.OrdinalIgnoreCase) ? ProcessPath : null;

        public string? GetUserEnvironmentVariable(string name)
            => string.Equals(name, "PATH", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Path", StringComparison.OrdinalIgnoreCase)
                    ? UserPath
                    : null;

        public void SetUserEnvironmentVariable(string name, string? value)
        {
            if (string.Equals(name, "PATH", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Path", StringComparison.OrdinalIgnoreCase))
            {
                UserPath = value;
                UserPathSetCount++;
            }
        }

        public void BroadcastEnvironmentChanged()
            => BroadcastCount++;
    }
}
