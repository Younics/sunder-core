using Sunder.App.Services;
using Sunder.App.Models;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed class ShellStartupCoordinatorTests
{
    [Fact]
    public async Task WaitForReadyRuntimeSnapshotAsync_WaitsThroughStartingAndReturnsAtomicReadySnapshot()
    {
        var runtimeInstanceId = Guid.NewGuid();
        var client = new SnapshotClient(
            CreateSnapshot(runtimeInstanceId, RuntimeBootstrapState.Starting, generation: 0),
            CreateSnapshot(runtimeInstanceId, RuntimeBootstrapState.Starting, generation: 0),
            CreateSnapshot(runtimeInstanceId, RuntimeBootstrapState.Ready, generation: 1));
        var delayCount = 0;

        var snapshot = await ShellStartupCoordinator.WaitForReadyRuntimeSnapshotAsync(
            client,
            (_, _) =>
            {
                delayCount++;
                return Task.CompletedTask;
            },
            maxAttempts: 3);

        Assert.Equal(RuntimeBootstrapState.Ready, snapshot.BootstrapState);
        Assert.Equal(runtimeInstanceId, snapshot.RuntimeInstanceId);
        Assert.Equal(1, snapshot.SessionGeneration);
        Assert.Equal(3, client.CallCount);
        Assert.Equal(2, delayCount);
    }

    [Fact]
    public async Task WaitForReadyRuntimeSnapshotAsync_SurfacesBootstrapFailure()
    {
        var client = new SnapshotClient(CreateSnapshot(
            Guid.NewGuid(),
            RuntimeBootstrapState.Failed,
            generation: 2,
            errors: ["dev package activation failed"]));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ShellStartupCoordinator.WaitForReadyRuntimeSnapshotAsync(client));

        Assert.Equal("dev package activation failed", exception.Message);
    }

    [Fact]
    public async Task WaitForReadyRuntimeSnapshotAsync_CancelsSnapshotWaitDuringBootstrapDelay()
    {
        var client = new SnapshotClient(CreateSnapshot(
            Guid.NewGuid(),
            RuntimeBootstrapState.Starting,
            generation: 0));
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var wait = ShellStartupCoordinator.WaitForReadyRuntimeSnapshotAsync(
            client,
            async (_, cancellationToken) =>
            {
                delayStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            cancellationToken: cancellation.Token);
        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public void ValidateStartupOptions_RejectsParseErrorsUnlessCoreShellWasExplicitlyChosen()
    {
        var startupOptions = new AppStartupOptions
        {
            ParseErrors = ["--watch requires at least one --dev-package folder."],
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => ShellStartupCoordinator.ValidateStartupOptions(startupOptions, openCoreShell: false));

        Assert.Contains("startup arguments", exception.Message, StringComparison.Ordinal);
        ShellStartupCoordinator.ValidateStartupOptions(startupOptions, openCoreShell: true);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ShouldCheckPackageUpdates_SkipsCoreShell(bool openCoreShell, bool expected)
        => Assert.Equal(expected, ShellStartupCoordinator.ShouldCheckPackageUpdates(openCoreShell));

    [Fact]
    public void StartupCoordinator_UsesOnlyAtomicPackageSnapshotForPackageStartupState()
    {
        var source = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src",
            "Host",
            "Sunder.App",
            "Services",
            "ShellStartupCoordinator.cs"));

        Assert.Contains("GetRuntimePackageSnapshotAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadPackageLifecycleAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetActivePackagesAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetActivePackageUiSnapshotsAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationToken.None", source, StringComparison.Ordinal);
    }

    private static RuntimePackageSnapshot CreateSnapshot(
        Guid runtimeInstanceId,
        RuntimeBootstrapState state,
        long generation,
        IReadOnlyList<string>? errors = null)
        => new(runtimeInstanceId, generation, generation, state, [], [], [], [], errors ?? []);

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the Sunder Core repository root.");
    }

    private sealed class SnapshotClient(params RuntimePackageSnapshot[] snapshots) : IRuntimeSnapshotClient
    {
        private readonly Queue<RuntimePackageSnapshot> _snapshots = new(snapshots);

        public int CallCount { get; private set; }

        public Task<RuntimePackageSnapshot> GetRuntimePackageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(_snapshots.Dequeue());
        }

        public void Dispose()
        {
        }
    }
}
