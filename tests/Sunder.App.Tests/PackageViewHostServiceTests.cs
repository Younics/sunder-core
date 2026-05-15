using Sunder.App.Services;
using Sunder.Protocol;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed class PackageViewHostServiceTests
{
    [Fact]
    public async Task DisablePackageAsync_WaitsForBackgroundServicesToStop()
    {
        var backgroundServices = new AppPackageBackgroundServiceCoordinator();
        var backgroundService = new BlockingBackgroundService();
        backgroundServices.Register("test.package", backgroundService);
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(),
            backgroundServices,
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder: null);

        var disableTask = hostService.DisablePackageAsync(
            "test.package",
            "Activation failed.",
            PackageFailureOrigin.AppActivation);

        await backgroundService.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(disableTask.IsCompleted);

        backgroundService.AllowStop.SetResult();
        await disableTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, backgroundService.StopCount);
    }

    [Fact]
    public async Task DisablePackageAsync_StopsPackageOnlyOnce()
    {
        var backgroundServices = new AppPackageBackgroundServiceCoordinator();
        var backgroundService = new BlockingBackgroundService();
        backgroundServices.Register("test.package", backgroundService);
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(),
            backgroundServices,
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder: null);

        backgroundService.AllowStop.SetResult();
        await hostService.DisablePackageAsync("test.package", "First failure.", PackageFailureOrigin.AppActivation);
        await hostService.DisablePackageAsync("test.package", "Second failure.", PackageFailureOrigin.AppActivation);

        Assert.Equal(1, backgroundService.StopCount);
    }

    [Fact]
    public async Task DisposeAsync_LeavesSessionFolderForProcessLifetime()
    {
        var sessionFolder = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionFolder);
        try
        {
            var hostService = new PackageViewHostService(
                new AppPackageViewRegistry(),
                new AppPackageBackgroundServiceCoordinator(),
                [],
                [],
                [],
                faultReporter: null,
                sessionFolder);

            await hostService.DisposeAsync();

            Assert.True(Directory.Exists(sessionFolder));
        }
        finally
        {
            TryDeleteDirectory(sessionFolder);
        }
    }

    [Fact]
    public void CleanupStaleSessions_RemovesFoldersWithoutRunningOwner()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var activeFolder = Path.Combine(rootPath, $"20260511190000-{Environment.ProcessId}-{Guid.NewGuid():N}");
        var staleFolder = Path.Combine(rootPath, $"20260511190001-{int.MaxValue}-{Guid.NewGuid():N}");
        var legacyFolder = Path.Combine(rootPath, $"20260511190002-{Guid.NewGuid():N}");
        Directory.CreateDirectory(activeFolder);
        Directory.CreateDirectory(staleFolder);
        Directory.CreateDirectory(legacyFolder);
        try
        {
            AppPackageSessionDirectories.CleanupStaleSessions(rootPath);

            Assert.True(Directory.Exists(activeFolder));
            Assert.False(Directory.Exists(staleFolder));
            Assert.False(Directory.Exists(legacyFolder));
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task ApplyPackageDeltaAsync_WhenPackageReloads_UsesNewShadowFolder()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "session");
        Directory.CreateDirectory(sessionFolder);
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var package = CreateActiveAgentPackage();
        var source = new PackageSourceDescriptor("agent", PackageSourceKind.Dev, packageSourceFolder);
        var hostService = new PackageViewHostService(
            new AppPackageViewRegistry(),
            new AppPackageBackgroundServiceCoordinator(),
            [],
            [],
            [],
            faultReporter: null,
            sessionFolder);

        try
        {
            await hostService.ApplyPackageDeltaAsync([package], [source]);
            await hostService.ApplyPackageDeltaAsync([package], [source], ["agent"]);

            var shadowFolders = Directory.EnumerateDirectories(sessionFolder)
                .Select(Path.GetFileName)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Assert.Equal(2, shadowFolders.Length);
            Assert.StartsWith("0001-agent", shadowFolders[0]);
            Assert.StartsWith("0002-agent", shadowFolders[1]);
        }
        finally
        {
            await hostService.DisposeAsync();
            TryDeleteDirectoryBestEffort(rootPath);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void TryDeleteDirectoryBestEffort(string path)
    {
        try
        {
            TryDeleteDirectory(path);
        }
        catch
        {
            // Loaded package shadows can remain locked until process exit.
        }
    }

    private static ActivePackageDescriptor CreateActiveAgentPackage()
        => new(
            "agent",
            "Agent",
            "1.0.0",
            null,
            true,
            PackageReadinessState.Ready,
            [new PackageViewDescriptor("agent.chat", "agent", "Chat", null, "middle")]);

    private static string CreateAppPackageSource(string rootPath, string packageId)
    {
        var packageSourceFolder = Path.Combine(rootPath, "package-source");
        var libraryFolder = Path.Combine(packageSourceFolder, "lib");
        Directory.CreateDirectory(libraryFolder);

        var assemblyPath = typeof(ShellLifecycleTestPackageModule).Assembly.Location;
        var entryAssemblyFileName = Path.GetFileName(assemblyPath);
        File.WriteAllText(Path.Combine(packageSourceFolder, "sunder-package.json"), $$"""
            {
              "id": "{{packageId}}",
              "entryAssembly": "{{entryAssemblyFileName}}"
            }
            """);

        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
        {
            File.Copy(file, Path.Combine(libraryFolder, Path.GetFileName(file)), overwrite: true);
        }

        var depsPath = Path.ChangeExtension(assemblyPath, ".deps.json");
        if (File.Exists(depsPath))
        {
            File.Copy(depsPath, Path.Combine(libraryFolder, Path.GetFileName(depsPath)), overwrite: true);
        }

        return packageSourceFolder;
    }

    private sealed class BlockingBackgroundService : IPackageBackgroundService
    {
        public TaskCompletionSource StopStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowStop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StopCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            StopStarted.TrySetResult();
            await AllowStop.Task.WaitAsync(cancellationToken);
        }
    }
}
