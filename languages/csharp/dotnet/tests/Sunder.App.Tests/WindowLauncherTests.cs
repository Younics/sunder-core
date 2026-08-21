using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.Runtime.Contracts;
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

    [Fact]
    public async Task ApplyPackageMetadataSnapshotAsync_FromWorker_MarshalsAuxiliaryWindowAccess()
    {
        var dispatcher = new TrackingUiDispatcher();
        using var harness = CreateHarness(uiDispatcher: dispatcher);

        await Task.Run(() => harness.Launcher.ApplyPackageMetadataSnapshotAsync(
            CreateSnapshot(),
            CancellationToken.None));

        Assert.Equal(1, dispatcher.InvocationCount);
    }

    [Fact]
    public async Task ApplyPackageLifecycleSnapshotAsync_FromWorker_MarshalsAuxiliaryWindowAccess()
    {
        var dispatcher = new TrackingUiDispatcher();
        using var harness = CreateHarness(uiDispatcher: dispatcher);

        await Task.Run(() => harness.Launcher.ApplyPackageLifecycleSnapshotAsync(
            CreateSnapshot(),
            [],
            retryDisabledPackageIds: null,
            CancellationToken.None));

        Assert.Equal(1, dispatcher.InvocationCount);
    }

    [Fact]
    public async Task ApplyPackageMetadataSnapshotAsync_PreCanceled_DoesNotQueuePresentation()
    {
        var dispatcher = new TrackingUiDispatcher();
        using var harness = CreateHarness(uiDispatcher: dispatcher);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Launcher.ApplyPackageMetadataSnapshotAsync(
                CreateSnapshot(),
                cancellation.Token));

        Assert.Equal(0, dispatcher.InvocationCount);
    }

    private static WindowLauncherHarness CreateHarness(
        BackgroundProcessQueueService? queue = null,
        IUiDispatcher? uiDispatcher = null)
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
            settingsWindowFactory: null!,
            packagesWindowFactory: null!,
            stacksWindowFactory: null!,
            uiDispatcher: uiDispatcher,
            backgroundProcessQueue: queue);

        return new WindowLauncherHarness(launcher, rootPath);
    }

    private static RuntimePackageSnapshot CreateSnapshot()
        => new(
            Guid.NewGuid(),
            SessionGeneration: 1,
            EventSequence: 1,
            RuntimeBootstrapState.Ready,
            [],
            [],
            [],
            []);

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
        public TClient CreateClient<TClient>() where TClient : class, IRuntimeClient
            => throw new InvalidOperationException("Runtime API is not used by these tests.");
    }

    private sealed class TrackingUiDispatcher : IUiDispatcher
    {
        private readonly AsyncLocal<int> _accessDepth = new();
        private int _invocationCount;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public bool CheckAccess() => _accessDepth.Value > 0;

        public Task InvokeAsync(Action action)
        {
            InvokeWithAccess(action);
            return Task.CompletedTask;
        }

        public async Task InvokeAsync(Func<Task> action)
        {
            BeginInvocation();
            try
            {
                await action();
            }
            finally
            {
                _accessDepth.Value--;
            }
        }

        public Task<T> InvokeAsync<T>(Func<T> action)
            => Task.FromResult(InvokeWithAccess(action));

        public async Task<T> InvokeAsync<T>(Func<Task<T>> action)
        {
            BeginInvocation();
            try
            {
                return await action();
            }
            finally
            {
                _accessDepth.Value--;
            }
        }

        private void InvokeWithAccess(Action action)
        {
            BeginInvocation();
            try
            {
                action();
            }
            finally
            {
                _accessDepth.Value--;
            }
        }

        private T InvokeWithAccess<T>(Func<T> action)
        {
            BeginInvocation();
            try
            {
                return action();
            }
            finally
            {
                _accessDepth.Value--;
            }
        }

        private void BeginInvocation()
        {
            Interlocked.Increment(ref _invocationCount);
            _accessDepth.Value++;
        }
    }
}
