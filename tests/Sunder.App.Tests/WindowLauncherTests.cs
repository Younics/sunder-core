using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed class WindowLauncherTests
{
    [Fact]
    public void CloseForShutdown_WhenQueueIsOwned_DisposesQueue()
    {
        using var harness = CreateHarness();
        var queue = harness.Launcher.BackgroundProcesses;

        harness.Launcher.CloseForShutdown();

        Assert.Throws<ObjectDisposedException>(() => EnqueueProbe(queue));
    }

    [Fact]
    public void Dispose_WhenQueueIsInjected_DoesNotDisposeQueue()
    {
        using var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        using var harness = CreateHarness(queue);

        harness.Launcher.Dispose();

        var probe = EnqueueProbe(queue);
        Assert.NotEqual(Guid.Empty, probe.ProcessId);
    }

    private static WindowLauncherHarness CreateHarness(BackgroundProcessQueueService? queue = null)
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var shellState = new ShellState();
        var launcher = new WindowLauncher(
            PackageViewHostService.Empty,
            new ThrowingRuntimeApiClientFactory(),
            new CliInstallationService(),
            new NotificationCenterService(Path.Combine(rootPath, "notifications.json")),
            new ShellStateService(Path.Combine(rootPath, "shell-state.json")),
            shellState,
            backgroundProcessQueue: queue);

        return new WindowLauncherHarness(launcher, rootPath);
    }

    private static BackgroundProcessSnapshot EnqueueProbe(BackgroundProcessQueueService queue)
        => queue.Enqueue(new BackgroundProcessRequest(
            "Probe",
            "test",
            BackgroundProcessIndicator.Main,
            BackgroundProcessConcurrencyMode.ParallelWithinGroup,
            CanCancel: false,
            _ => Task.CompletedTask));

    private sealed class WindowLauncherHarness(WindowLauncher launcher, string rootPath) : IDisposable
    {
        public WindowLauncher Launcher { get; } = launcher;

        public void Dispose()
        {
            Launcher.Dispose();
            try
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
            catch
            {
                // Test cleanup should not hide assertion failures.
            }
        }
    }

    private sealed class ThrowingRuntimeApiClientFactory : IRuntimeApiClientFactory
    {
        public IRuntimeApiClient CreateClient()
            => throw new NotSupportedException("Runtime API is not used by these tests.");
    }
}
