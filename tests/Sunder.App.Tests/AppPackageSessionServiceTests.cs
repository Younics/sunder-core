using Sunder.App.Services;
using Sunder.Protocol;
using Sunder.Sdk.Abstractions;
using Xunit;
using SdkPackageSessionLoadRequest = Sunder.Sdk.Abstractions.PackageSessionLoadRequest;
using SdkPackageSessionSourceKind = Sunder.Sdk.Abstractions.PackageSessionSourceKind;

namespace Sunder.App.Tests;

public sealed class AppPackageSessionServiceTests
{
    [Fact]
    public async Task LoadPackageAsync_ForDevSource_StagesPreflightsCommitsAndAppliesInOrder()
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
                Assert.Contains(packageSources, source => source.PackageId == "agent" && source.Kind == PackageSourceKind.Dev);
                return Task.CompletedTask;
            });

        var status = await service.LoadPackageAsync(new SdkPackageSessionLoadRequest(SdkPackageSessionSourceKind.Dev, folder));

        Assert.Equal("agent", status.PackageId);
        Assert.Equal(SdkPackageSessionSourceKind.Dev, status.ActiveSourceKind);
        Assert.Equal(PackageLifecycleOverlayOwner.Sdk, runtimeClient.StageRequest?.OverlayOwner);
        Assert.Equal(["stage", "preflight:agent", "commit", "apply:agent", "status"], events);
    }

    [Fact]
    public async Task LoadPackageAsync_WhenDevPreflightFails_DiscardsStageWithoutCommitting()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var events = new List<string>();
        var runtimeClient = new FakeRuntimeApiClient(folder, events);
        using var developerLog = new DeveloperLogService();
        using var service = new AppPackageSessionService(new FakeRuntimeApiClientFactory(runtimeClient), developerLog);
        service.Attach(
            (_, _) => Task.CompletedTask,
            (_, _, _, _) => throw new InvalidOperationException("preflight failed"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.LoadPackageAsync(new SdkPackageSessionLoadRequest(SdkPackageSessionSourceKind.Dev, folder)));

        Assert.Equal("preflight failed", exception.Message);
        Assert.False(runtimeClient.CommitCalled);
        Assert.True(runtimeClient.DiscardCalled);
        Assert.Equal(["stage", "discard"], events);
    }

    private sealed class FakeRuntimeApiClientFactory(FakeRuntimeApiClient runtimeApiClient) : IRuntimeApiClientFactory
    {
        public IRuntimeApiClient CreateClient() => runtimeApiClient;
    }

    private sealed class FakeRuntimeApiClient(string folder, List<string> events) : IRuntimeApiClient
    {
        private readonly ActivePackageDescriptor[] _activePackages =
        [
            new("agent", "Agent", "1.0.0", null, true, PackageReadinessState.Ready, []),
        ];

        private readonly PackageSourceDescriptor[] _packageSources =
        [
            new("agent", PackageSourceKind.Dev, folder),
        ];

        public bool CommitCalled { get; private set; }

        public bool DiscardCalled { get; private set; }

        public PackageLifecycleStageRequest? StageRequest { get; private set; }

        public Task<SystemStatusResponse?> GetSystemStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<SystemStatusResponse?>(null);

        public Task<bool> IsRuntimeHealthyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<IReadOnlyList<ActivePackageDescriptor>> GetActivePackagesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ActivePackageDescriptor>>(_activePackages);

        public Task<IReadOnlyList<SessionPackageDescriptor>> GetSessionPackagesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SessionPackageDescriptor>>([]);

        public Task<IReadOnlyList<PackageSourceDescriptor>> GetActivePackageSourcesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PackageSourceDescriptor>>(_packageSources);

        public Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<InstalledPackageDescriptor>>([]);

        public Task<Sunder.Protocol.PackageSessionStatus?> GetPackageSessionStatusAsync(string packageId, CancellationToken cancellationToken = default)
        {
            events.Add("status");
            return Task.FromResult<Sunder.Protocol.PackageSessionStatus?>(new Sunder.Protocol.PackageSessionStatus(
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

        public Uri CreatePackageAssetUri(string packageId, string assetPath)
            => new($"file:///packages/{packageId}/{assetPath}");

        public Task<PackageOperationResult> InstallPackageFromPathAsync(string packagePath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageOperationResult> UpgradePackageFromPathAsync(
            string packageId,
            string packagePath,
            bool allowDowngrade = false,
            bool reinstall = false,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageOperationResult> EnableInstalledPackageAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageOperationResult> DisableInstalledPackageAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageOperationResult> UninstallPackageAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageLifecycleOperationResult> LoadPackageLifecycleAsync(PackageLifecycleLoadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

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

        public Task<IReadOnlyList<PackageConfigurationSchemaDescriptor>> GetConfigurationSchemasAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageConfigurationValuesResponse?> GetPackageConfigurationValuesAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SavePackageConfigurationValuesAsync(string packageId, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageAuthStatusResponse?> GetPackageAuthStatusAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageAuthSessionStartResponse?> StartPackageAuthAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageAuthSessionStatusResponse?> GetPackageAuthSessionStatusAsync(string packageId, string authSessionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageAuthStatusResponse?> DisconnectPackageAuthAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ReportPackageFaultAsync(string packageId, PackageFailureOrigin origin, string message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ShutdownAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
