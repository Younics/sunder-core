using Sunder.App.Services;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPackageSessionServiceTests
{
    [Fact]
    public void DevelopmentSessions_WhenRuntimePathsCannotBeShared_ExposeUnavailableReason()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var events = new List<string>();
        var runtimeClient = new FakeRuntimeApiClient(folder, events);
        using var developerLog = new DeveloperLogService();
        using var service = new AppPackageSessionService(new FakeRuntimeApiClientFactory(runtimeClient), developerLog);
        service.Attach(
            (packageIds, _) =>
            {
                events.Add("apply:" + string.Join(",", packageIds));
                Assert.True(runtimeClient.CommitCalled);
                return Task.CompletedTask;
            },
            (activePackages, packageSources, impactedPackageIds, _) =>
            {
                events.Add("preflight:" + string.Join(",", impactedPackageIds));
                Assert.False(runtimeClient.CommitCalled);
                Assert.Contains(activePackages, package => package.PackageId == "agent");
                Assert.Contains(packageSources, source => source.PackageId == "agent" && source.SourceKind == PackageSourceKind.Dev);
                return Task.CompletedTask;
            });

        Assert.False(service.Availability.IsAvailable);
        Assert.Contains("cannot pass local paths", service.Availability.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(events);
    }

    [Fact]
    public async Task LoadDevelopmentPackageAsync_WhenUnavailable_ReturnsStructuredUnsupportedOutcome()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var events = new List<string>();
        var runtimeClient = new FakeRuntimeApiClient(folder, events);
        using var developerLog = new DeveloperLogService();
        using var service = new AppPackageSessionService(new FakeRuntimeApiClientFactory(runtimeClient), developerLog);
        service.Attach(
            (_, _) => Task.CompletedTask,
            (_, _, _, _) => throw new InvalidOperationException("preflight failed"));

        var result = await service.LoadDevelopmentPackageAsync(new PackageDevelopmentSessionLoadRequest(folder));

        Assert.Equal(PackageDevelopmentSessionOperationOutcome.Unsupported, result.Outcome);
        Assert.Contains("cannot pass local paths", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(runtimeClient.CommitCalled);
        Assert.False(runtimeClient.DiscardCalled);
        Assert.Empty(events);
    }

    private sealed class FakeRuntimeApiClientFactory(FakeRuntimeApiClient runtimeApiClient) : IRuntimeApiClientFactory
    {
        public TClient CreateClient<TClient>() where TClient : class, IRuntimeClient
            => (TClient)(object)runtimeApiClient;
    }

    private sealed class FakeRuntimeApiClient(string folder, List<string> events) : IRuntimePackageSessionClient
    {
        private readonly ActivePackageDescriptor[] _activePackages =
        [
            new("agent", "Agent", "1.0.0", null, true, PackageReadinessState.Ready, []),
        ];

        private readonly PackageUiSnapshotDescriptor[] _packageSources =
        [
            RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, folder),
        ];

        public bool CommitCalled { get; private set; }

        public bool DiscardCalled { get; private set; }

        public PackageLifecycleStageRequest? StageRequest { get; private set; }

        public Task<IReadOnlyList<ActivePackageDescriptor>> GetActivePackagesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ActivePackageDescriptor>>(_activePackages);

        public Task<IReadOnlyList<SessionPackageDescriptor>> GetSessionPackagesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SessionPackageDescriptor>>([]);

        public Task<Sunder.Runtime.Contracts.PackageSessionStatus?> GetPackageSessionStatusAsync(string packageId, CancellationToken cancellationToken = default)
        {
            events.Add("status");
            return Task.FromResult<Sunder.Runtime.Contracts.PackageSessionStatus?>(new Sunder.Runtime.Contracts.PackageSessionStatus(
                packageId,
                "Agent",
                "1.0.0",
                PackageSourceKind.Dev,
                true,
                false,
                false,
                PackageReadinessState.Ready,
                ErrorMessage: null));
        }

        public Task<PackageLifecycleOperationResult> LoadPackageLifecycleAsync(PackageLifecycleLoadRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(PackageLifecycleOperationResult.Failed("Not configured for this test."));

        public Task<PackageSessionOperationResult> LoadPackageSessionAsync(PackageSessionLoadRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(PackageSessionOperationResult.Failed("Not configured for this test."));

        public Task<PackageSessionOperationResult> UnloadPackageSessionAsync(string packageId, PackageSourceKind sourceKind, CancellationToken cancellationToken = default)
            => Task.FromResult(PackageSessionOperationResult.Failed("Not configured for this test."));

        public Task<PackageOperationResult> ReloadInstalledPackageSessionAsync(IReadOnlyList<string> impactedPackageIds, CancellationToken cancellationToken = default)
            => Task.FromResult(new PackageOperationResult(true, null, true, false, [], []));

        public Task<PackageLifecycleStageResult> StagePackageLifecycleAsync(PackageLifecycleStageRequest request, CancellationToken cancellationToken = default)
        {
            StageRequest = request;
            events.Add("stage");
            return Task.FromResult(new PackageLifecycleStageResult(
                "stage-1",
                _activePackages,
                _packageSources,
                [],
                [],
                ["agent"]));
        }

        public Task<PackageLifecycleOperationResult> CommitPackageLifecycleStageAsync(string stageId, CancellationToken cancellationToken = default)
        {
            CommitCalled = true;
            events.Add("commit");
            return Task.FromResult(new PackageLifecycleOperationResult(
                true,
                "Committed.",
                _activePackages,
                _packageSources,
                [],
                [],
                ["agent"]));
        }

        public Task DiscardPackageLifecycleStageAsync(string stageId, CancellationToken cancellationToken = default)
        {
            DiscardCalled = true;
            events.Add("discard");
            return Task.CompletedTask;
        }

        public Task ReportPackageFaultAsync(string packageId, PackageFailureOrigin origin, string message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
