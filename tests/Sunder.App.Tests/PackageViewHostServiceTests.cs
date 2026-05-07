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
