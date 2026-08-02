using System.Net;
using Sunder.App.Services;
using Sunder.App.Models;
using Sunder.Runtime.Client;
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
    public async Task LoadRuntimePackagesForStartupAsync_WhenDevAcquisitionFails_ReleasesAndRefreshesSnapshot()
    {
        var runtimeInstanceId = Guid.NewGuid();
        var operations = new List<string>();
        var client = new SnapshotClient(
            operations,
            CreateSnapshot(runtimeInstanceId, RuntimeBootstrapState.Ready, generation: 1),
            CreateSnapshot(runtimeInstanceId, RuntimeBootstrapState.Ready, generation: 2));

        var result = await ShellStartupCoordinator.LoadRuntimePackagesForStartupAsync(
            client,
            loadDevPackages: true,
            _ =>
            {
                operations.Add("acquire");
                throw CreatePackageValidationException("manifest is invalid");
            },
            _ =>
            {
                operations.Add("release");
                return Task.CompletedTask;
            });

        Assert.False(result.DevPackageOwnerAcquired);
        Assert.Equal(2, result.Snapshot.SessionGeneration);
        Assert.Contains("manifest is invalid", result.Warning, StringComparison.Ordinal);
        Assert.Equal(["snapshot", "acquire", "release", "snapshot"], operations);
    }

    [Fact]
    public async Task LoadRuntimePackagesForStartupAsync_WhenDevAcquisitionSucceeds_UsesLeaseGeneration()
    {
        var runtimeInstanceId = Guid.NewGuid();
        var client = new SnapshotClient(
            CreateSnapshot(runtimeInstanceId, RuntimeBootstrapState.Ready, generation: 1),
            CreateSnapshot(runtimeInstanceId, RuntimeBootstrapState.Ready, generation: 3));

        var result = await ShellStartupCoordinator.LoadRuntimePackagesForStartupAsync(
            client,
            loadDevPackages: true,
            _ => Task.FromResult(new DevPackageOwnerLeaseResponse(
                runtimeInstanceId,
                "owner",
                "mutation",
                1,
                DateTimeOffset.UtcNow.AddMinutes(1),
                3,
                [])),
            _ => Task.CompletedTask);

        Assert.True(result.DevPackageOwnerAcquired);
        Assert.Equal(3, result.Snapshot.SessionGeneration);
        Assert.Null(result.Warning);
        Assert.Equal(2, client.CallCount);
    }

    [Fact]
    public async Task LoadRuntimePackagesForStartupAsync_WhenFallbackCleanupFails_RemainsFatal()
    {
        var runtimeInstanceId = Guid.NewGuid();
        var client = new SnapshotClient(
            CreateSnapshot(runtimeInstanceId, RuntimeBootstrapState.Ready, generation: 1));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ShellStartupCoordinator.LoadRuntimePackagesForStartupAsync(
                client,
                loadDevPackages: true,
                _ => throw CreatePackageValidationException("manifest is invalid"),
                _ => throw new HttpRequestException("release failed")));

        Assert.Contains("could not restore", exception.Message, StringComparison.Ordinal);
        Assert.Contains("release failed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task LoadRuntimePackagesForStartupAsync_WhenAcquisitionOutcomeIsAmbiguous_FailsClosed()
    {
        var runtimeInstanceId = Guid.NewGuid();
        var client = new SnapshotClient(
            CreateSnapshot(runtimeInstanceId, RuntimeBootstrapState.Ready, generation: 1));
        var releaseCalled = false;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ShellStartupCoordinator.LoadRuntimePackagesForStartupAsync(
                client,
                loadDevPackages: true,
                _ => throw new HttpRequestException("response lost"),
                _ =>
                {
                    releaseCalled = true;
                    return Task.CompletedTask;
                }));

        Assert.Contains("could not confirm", exception.Message, StringComparison.Ordinal);
        Assert.False(releaseCalled);
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task LoadConsistentPackageUiStateAsync_WhenWorkerChangesAtSameGeneration_RetriesOnNewInstance()
    {
        var firstRuntimeInstanceId = Guid.NewGuid();
        var secondRuntimeInstanceId = Guid.NewGuid();
        var initial = CreateSnapshot(
            firstRuntimeInstanceId,
            RuntimeBootstrapState.Ready,
            generation: 1);
        var client = new SnapshotClient(
            CreateSnapshot(secondRuntimeInstanceId, RuntimeBootstrapState.Ready, generation: 1),
            CreateSnapshot(secondRuntimeInstanceId, RuntimeBootstrapState.Ready, generation: 1));
        var sourceLoads = 0;

        var result = await ShellStartupCoordinator.LoadConsistentPackageUiStateAsync(
            client,
            initial,
            _ =>
            {
                sourceLoads++;
                return Task.FromResult<IReadOnlyList<PackageUiSnapshotDescriptor>>(
                    [CreatePackageSource(generation: 1)]);
            });

        Assert.Equal(secondRuntimeInstanceId, result.Snapshot.RuntimeInstanceId);
        Assert.Equal(2, sourceLoads);
        Assert.Equal(2, client.CallCount);
    }

    [Fact]
    public async Task LoadConsistentPackageUiStateAsync_WhenDevOwnerWorkerChanges_FailsClosed()
    {
        var firstRuntimeInstanceId = Guid.NewGuid();
        var secondRuntimeInstanceId = Guid.NewGuid();
        var initial = CreateSnapshot(
            firstRuntimeInstanceId,
            RuntimeBootstrapState.Ready,
            generation: 1);
        var client = new SnapshotClient(
            CreateSnapshot(secondRuntimeInstanceId, RuntimeBootstrapState.Ready, generation: 1));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ShellStartupCoordinator.LoadConsistentPackageUiStateAsync(
                client,
                initial,
                _ => Task.FromResult<IReadOnlyList<PackageUiSnapshotDescriptor>>(
                    [CreatePackageSource(generation: 1)]),
                requiredRuntimeInstanceId: firstRuntimeInstanceId));

        Assert.Contains("reacquire its dev package owner lease", exception.Message, StringComparison.Ordinal);
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

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void ShouldStreamPackageLogs_SkipsCoreShell(
        bool openCoreShell,
        bool developerLogEnabled,
        bool expected)
        => Assert.Equal(expected, ShellSession.ShouldStreamPackageLogs(openCoreShell, developerLogEnabled));

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
        Assert.Contains("GetActivePackageUiSnapshotsAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationToken.None", source, StringComparison.Ordinal);
    }

    private static RuntimePackageSnapshot CreateSnapshot(
        Guid runtimeInstanceId,
        RuntimeBootstrapState state,
        long generation,
        IReadOnlyList<string>? errors = null)
        => new(runtimeInstanceId, generation, generation, state, [], [], [], errors ?? []);

    private static RuntimeClientException CreatePackageValidationException(string detail)
        => new(
            HttpStatusCode.UnprocessableEntity,
            "Package validation failed",
            detail,
            "runtime.v1.package-validation");

    private static PackageUiSnapshotDescriptor CreatePackageSource(long generation)
        => new(
            "example.package",
            PackageSourceKind.Installed,
            generation,
            RuntimeContractTestData.AppTarget(),
            new string('a', 64),
            "snapshot",
            "packages/ui-snapshots/snapshot");

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the Sunder Core repository root.");
    }

    private sealed class SnapshotClient : IRuntimeSnapshotClient
    {
        private readonly Queue<RuntimePackageSnapshot> _snapshots;
        private readonly ICollection<string>? _operations;

        public SnapshotClient(params RuntimePackageSnapshot[] snapshots)
            : this(null, snapshots)
        {
        }

        public SnapshotClient(
            ICollection<string>? operations,
            params RuntimePackageSnapshot[] snapshots)
        {
            _operations = operations;
            _snapshots = new Queue<RuntimePackageSnapshot>(snapshots);
        }

        public int CallCount { get; private set; }

        public Task<RuntimePackageSnapshot> GetRuntimePackageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _operations?.Add("snapshot");
            CallCount++;
            return Task.FromResult(_snapshots.Dequeue());
        }

        public void Dispose()
        {
        }
    }
}
