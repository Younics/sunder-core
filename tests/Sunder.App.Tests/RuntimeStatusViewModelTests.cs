using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.Protocol;
using Sunder.Sdk.Abstractions;
using static Sunder.App.Tests.TestSupport.AsyncAssert;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RuntimeStatusViewModelTests
{
    [Fact]
    public async Task ApplyRuntimeAddressAsync_WhenAddressIsInvalid_DoesNotUseRuntimeApiClient()
    {
        var runtimeApiClientFactory = new FakeRuntimeApiClientFactory();
        var persistedRuntimeUrls = new List<Uri>();
        var viewModel = CreateViewModel(
            runtimeApiClientFactory,
            initialSystemStatus: new SystemStatusResponse("Runtime", "1.0.0", true, DateTimeOffset.UtcNow),
            persistPreferredRuntimeUrl: persistedRuntimeUrls.Add);

        viewModel.RuntimeAddressText = "not a url";

        await viewModel.ApplyRuntimeAddressAsync();

        Assert.Equal(0, runtimeApiClientFactory.CreateCount);
        Assert.Empty(persistedRuntimeUrls);
        Assert.False(viewModel.IsRuntimeBusy);
        Assert.True(viewModel.IsRuntimeRunning);
        Assert.Equal("Runtime address invalid", viewModel.SystemStatusText);
        Assert.Contains("not a url", viewModel.RuntimeLastError);
    }

    [Fact]
    public async Task RefreshRuntimeAsync_WhenStatusSucceeds_SetsReadyState()
    {
        var runtimeApiClient = new FakeRuntimeApiClient
        {
            GetSystemStatus = _ => Task.FromResult<SystemStatusResponse?>(
                new SystemStatusResponse("Runtime", "1.2.3", true, DateTimeOffset.UtcNow)),
        };
        var viewModel = CreateViewModel(new FakeRuntimeApiClientFactory(runtimeApiClient));

        await viewModel.RefreshRuntimeAsync();

        Assert.True(viewModel.IsRuntimeRunning);
        Assert.True(viewModel.IsRuntimeReady);
        Assert.False(viewModel.IsRuntimeBusy);
        Assert.Equal("Runtime", viewModel.RuntimeName);
        Assert.Equal("1.2.3", viewModel.RuntimeVersion);
        Assert.Equal("Runtime ready", viewModel.RuntimeStatusText);
        Assert.Equal("Runtime ready", viewModel.SystemStatusText);
        Assert.Equal(string.Empty, viewModel.RuntimeLastError);
        Assert.True(runtimeApiClient.IsDisposed);
    }

    [Fact]
    public async Task RefreshRuntimeAsync_WhenStatusAndHealthFail_ClearsBusyStateAndReportsStatusError()
    {
        var runtimeApiClient = new FakeRuntimeApiClient
        {
            GetSystemStatus = _ => Task.FromException<SystemStatusResponse?>(new InvalidOperationException("status failed")),
            IsRuntimeHealthy = _ => Task.FromException<bool>(new InvalidOperationException("health failed")),
        };
        var viewModel = CreateViewModel(new FakeRuntimeApiClientFactory(runtimeApiClient));

        await viewModel.RefreshRuntimeAsync();

        Assert.False(viewModel.IsRuntimeBusy);
        Assert.False(viewModel.IsRuntimeRunning);
        Assert.False(viewModel.IsRuntimeReady);
        Assert.Equal("Runtime unavailable", viewModel.RuntimeStatusText);
        Assert.Equal("Runtime unavailable", viewModel.SystemStatusText);
        Assert.Equal("status failed", viewModel.RuntimeLastError);
        Assert.True(runtimeApiClient.IsDisposed);
    }

    [Fact]
    public async Task RefreshRuntimeAsync_WhenCalledConcurrently_SerializesRuntimeRequests()
    {
        var gatedStatus = new GatedRuntimeStatus();
        var runtimeApiClient = new FakeRuntimeApiClient
        {
            GetSystemStatus = gatedStatus.GetSystemStatusAsync,
        };
        var viewModel = CreateViewModel(new FakeRuntimeApiClientFactory(runtimeApiClient));

        var firstRefresh = viewModel.RefreshRuntimeAsync();
        await WaitForConditionAsync(() => gatedStatus.CallCount == 1);

        var secondRefresh = viewModel.RefreshRuntimeAsync();
        Assert.Equal(1, gatedStatus.CallCount);

        gatedStatus.ReleaseNext();
        await WaitForConditionAsync(() => gatedStatus.CallCount == 2);

        gatedStatus.ReleaseNext();
        await Task.WhenAll(firstRefresh, secondRefresh);
    }

    [Fact]
    public async Task StartRuntimeAsync_WhenStartFails_ClearsBusyStateAndReportsError()
    {
        var runtimeApiClientFactory = new FakeRuntimeApiClientFactory();
        var persistedRuntimeUrls = new List<Uri>();
        var viewModel = CreateViewModel(
            runtimeApiClientFactory,
            persistPreferredRuntimeUrl: persistedRuntimeUrls.Add,
            startRuntimeAsync: (_, _) => throw new InvalidOperationException("start failed"));
        viewModel.RuntimeAddressText = "http://localhost:6000";

        await viewModel.StartRuntimeAsync();

        Assert.Equal(0, runtimeApiClientFactory.CreateCount);
        Assert.Single(persistedRuntimeUrls);
        Assert.Equal("http://localhost:6000/", persistedRuntimeUrls[0].AbsoluteUri);
        Assert.False(viewModel.IsRuntimeBusy);
        Assert.False(viewModel.IsRuntimeRunning);
        Assert.False(viewModel.IsRuntimeReady);
        Assert.Equal("Runtime unavailable", viewModel.RuntimeStatusText);
        Assert.Equal("Runtime unavailable", viewModel.SystemStatusText);
        Assert.Equal("start failed", viewModel.RuntimeLastError);
    }

    private static RuntimeStatusViewModel CreateViewModel(
        IRuntimeApiClientFactory runtimeApiClientFactory,
        SystemStatusResponse? initialSystemStatus = null,
        Action<Uri>? persistPreferredRuntimeUrl = null,
        Func<Uri, CancellationToken, Task>? startRuntimeAsync = null)
        => new(
            new RuntimeConnectionState(AppStartupOptions.DefaultRuntimeUrl),
            runtimeApiClientFactory,
            new RuntimeHostProcessManager(new AppStartupOptions()),
            "System Ready",
            initialSystemStatus,
            startupErrors: [],
            persistPreferredRuntimeUrl ?? (_ => { }),
            startRuntimeAsync);

    private sealed class FakeRuntimeApiClientFactory : IRuntimeApiClientFactory
    {
        private readonly Func<FakeRuntimeApiClient> _createClient;

        public FakeRuntimeApiClientFactory()
            : this(() => new FakeRuntimeApiClient())
        {
        }

        public FakeRuntimeApiClientFactory(FakeRuntimeApiClient runtimeApiClient)
            : this(() => runtimeApiClient)
        {
        }

        private FakeRuntimeApiClientFactory(Func<FakeRuntimeApiClient> createClient)
        {
            _createClient = createClient;
        }

        public int CreateCount { get; private set; }

        public IRuntimeApiClient CreateClient()
        {
            CreateCount++;
            return _createClient();
        }
    }

    private sealed class FakeRuntimeApiClient : IRuntimeApiClient
    {
        public Func<CancellationToken, Task<SystemStatusResponse?>> GetSystemStatus { get; init; }
            = _ => Task.FromResult<SystemStatusResponse?>(null);

        public Func<CancellationToken, Task<bool>> IsRuntimeHealthy { get; init; }
            = _ => Task.FromResult(false);

        public bool IsDisposed { get; private set; }

        public int ShutdownCount { get; private set; }

        public Task<SystemStatusResponse?> GetSystemStatusAsync(CancellationToken cancellationToken = default)
            => GetSystemStatus(cancellationToken);

        public Task<bool> IsRuntimeHealthyAsync(CancellationToken cancellationToken = default)
            => IsRuntimeHealthy(cancellationToken);

        public Task<IReadOnlyList<ActivePackageDescriptor>> GetActivePackagesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SessionPackageDescriptor>> GetSessionPackagesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PackageSourceDescriptor>> GetActivePackageSourcesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Uri CreatePackageAssetUri(string packageId, string assetPath)
            => throw new NotSupportedException();

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

        public Task<DevPackageLoadResult> LoadDevPackagesAsync(IReadOnlyList<string> folders, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PackageConfigurationSchemaDescriptor>> GetConfigurationSchemasAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageConfigurationValuesResponse?> GetPackageConfigurationValuesAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SavePackageConfigurationValuesAsync(
            string packageId,
            IReadOnlyDictionary<string, string?> values,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageAuthStatusResponse?> GetPackageAuthStatusAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageAuthSessionStartResponse?> StartPackageAuthAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageAuthSessionStatusResponse?> GetPackageAuthSessionStatusAsync(
            string packageId,
            string authSessionId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageAuthStatusResponse?> DisconnectPackageAuthAsync(string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ReportPackageFaultAsync(
            string packageId,
            PackageFailureOrigin origin,
            string message,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            ShutdownCount++;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    private sealed class GatedRuntimeStatus
    {
        private readonly object _gate = new();
        private readonly Queue<TaskCompletionSource> _releases = [];
        private int _callCount;

        public int CallCount
        {
            get
            {
                lock (_gate)
                {
                    return _callCount;
                }
            }
        }

        public async Task<SystemStatusResponse?> GetSystemStatusAsync(CancellationToken cancellationToken)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _callCount++;
                _releases.Enqueue(release);
            }

            await release.Task.WaitAsync(cancellationToken);
            return new SystemStatusResponse("Runtime", "1.0.0", true, DateTimeOffset.UtcNow);
        }

        public void ReleaseNext()
        {
            TaskCompletionSource release;
            lock (_gate)
            {
                release = _releases.Dequeue();
            }

            release.SetResult();
        }
    }
}
