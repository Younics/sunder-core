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

    private static void TryDeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
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
