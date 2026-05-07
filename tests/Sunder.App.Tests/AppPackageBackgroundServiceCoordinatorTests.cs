using Sunder.App.Services;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPackageBackgroundServiceCoordinatorTests
{
    [Fact]
    public async Task StartAndStopAsync_RunOnlyServicesForRequestedPackage()
    {
        var coordinator = new AppPackageBackgroundServiceCoordinator();
        var first = new CountingBackgroundService();
        var second = new CountingBackgroundService();
        var otherPackage = new CountingBackgroundService();
        coordinator.Register("test.package", first);
        coordinator.Register("test.package", second);
        coordinator.Register("other.package", otherPackage);

        await coordinator.StartAsync("test.package");
        await coordinator.StopAsync("test.package");
        await coordinator.StopAsync("test.package");

        Assert.Equal(1, first.StartCount);
        Assert.Equal(1, second.StartCount);
        Assert.Equal(0, otherPackage.StartCount);
        Assert.Equal(1, first.StopCount);
        Assert.Equal(1, second.StopCount);
        Assert.Equal(0, otherPackage.StopCount);
    }

    [Fact]
    public async Task StopAllAsync_StopsRemainingPackagesAndSwallowsStopFailures()
    {
        var coordinator = new AppPackageBackgroundServiceCoordinator();
        var throwing = new CountingBackgroundService(throwOnStop: true);
        var remaining = new CountingBackgroundService();
        coordinator.Register("throwing.package", throwing);
        coordinator.Register("remaining.package", remaining);

        await coordinator.StopAllAsync();
        await coordinator.StopAllAsync();

        Assert.Equal(1, throwing.StopCount);
        Assert.Equal(1, remaining.StopCount);
    }

    private sealed class CountingBackgroundService(bool throwOnStop = false) : IPackageBackgroundService
    {
        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            if (throwOnStop)
            {
                throw new InvalidOperationException("Stop failed.");
            }

            return Task.CompletedTask;
        }
    }
}
