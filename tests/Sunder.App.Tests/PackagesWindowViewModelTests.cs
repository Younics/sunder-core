using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.Protocol;
using Sunder.Registry.Shared;
using Sunder.Sdk.Notifications;
using Xunit;

namespace Sunder.App.Tests;

public sealed class PackagesWindowViewModelTests
{
    [Fact]
    public async Task EnableSelectedPackageCommand_PublishesSuccessToastAfterRefresh()
    {
        var runtimeClient = new FakeRuntimeApiClient([CreateInstalledPackage("agent", isEnabled: false)]);
        var notificationCenter = CreateNotificationCenter();
        var toasts = new List<AppToastNotification>();
        var installedCallsAtToast = 0;
        notificationCenter.ToastQueued += toast =>
        {
            installedCallsAtToast = runtimeClient.GetInstalledPackagesCallCount;
            toasts.Add(toast);
        };
        var viewModel = CreateViewModel(runtimeClient, notificationCenter);

        await viewModel.InitializeAsync();
        await viewModel.EnableSelectedPackageCommand.ExecuteAsync(null);

        var toast = Assert.Single(toasts);
        Assert.Equal("Package enabled", toast.Title);
        Assert.Equal("Enabled package 'Agent'.", toast.Message);
        Assert.Equal(PackageNotificationSeverity.Success, toast.Severity);
        Assert.True(installedCallsAtToast >= 2);
        Assert.Empty(notificationCenter.ListNotifications());
    }

    [Fact]
    public async Task EnableSelectedPackageCommand_DoesNotToastWhenOperationIsNoop()
    {
        var runtimeClient = new FakeRuntimeApiClient([CreateInstalledPackage("agent", isEnabled: false)])
        {
            EnableResult = new PackageOperationResult(true, "Package 'Agent' is already enabled.", false, [], []),
        };
        var notificationCenter = CreateNotificationCenter();
        var toasts = new List<AppToastNotification>();
        notificationCenter.ToastQueued += toasts.Add;
        var viewModel = CreateViewModel(runtimeClient, notificationCenter);

        await viewModel.InitializeAsync();
        await viewModel.EnableSelectedPackageCommand.ExecuteAsync(null);

        Assert.Empty(toasts);
        Assert.Empty(notificationCenter.ListNotifications());
    }

    [Fact]
    public async Task EnableSelectedPackageCommand_DoesNotToastWhenOperationFails()
    {
        var runtimeClient = new FakeRuntimeApiClient([CreateInstalledPackage("agent", isEnabled: false)])
        {
            EnableResult = new PackageOperationResult(false, "Package failed.", false, [], ["Package failed."]),
        };
        var notificationCenter = CreateNotificationCenter();
        var toasts = new List<AppToastNotification>();
        notificationCenter.ToastQueued += toasts.Add;
        var viewModel = CreateViewModel(runtimeClient, notificationCenter);

        await viewModel.InitializeAsync();
        await viewModel.EnableSelectedPackageCommand.ExecuteAsync(null);

        Assert.Empty(toasts);
        Assert.Empty(notificationCenter.ListNotifications());
    }

    [Fact]
    public async Task InstalledPackages_UsePackageIconAssetUriWhenAvailable()
    {
        var runtimeClient = new FakeRuntimeApiClient([
            CreateInstalledPackage("agent", isEnabled: true, new PackageIconDescriptor(null, "assets/icons/agent.svg")),
        ]);
        var viewModel = CreateViewModel(runtimeClient, CreateNotificationCenter());

        await viewModel.InitializeAsync();

        var package = Assert.Single(viewModel.InstalledPackages);
        Assert.Equal(new Uri("file:///packages/agent/assets/assets/icons/agent.svg"), package.IconUri);
        Assert.Equal("A", package.Glyph);
        Assert.True(package.ShowGlyphFallback);
        Assert.True(viewModel.ShowSelectedPackageIcon);
        Assert.Equal("A", viewModel.SelectedPackageGlyph);
        Assert.True(viewModel.SelectedPackageShowGlyphFallback);
    }

    [Fact]
    public void MarketplacePackages_UseRegistryIconUrlWhenAvailable()
    {
        var iconUri = new Uri("http://127.0.0.1:1/api/packages/sunder.package.agent/versions/1.0.0/icon");
        using var package = new RegistryPackageSearchItemViewModel(
            new RegistryPackageSummary(
                "sunder.package.agent",
                "Sunder Agent",
                "Adds local agent profiles.",
                "1.0.0",
                iconUri.ToString(),
                IsYanked: false,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow),
            installedVersion: null,
            update: null,
            _ => Task.CompletedTask,
            loadIcon: false);

        Assert.Equal(iconUri, package.IconUri);
        Assert.Equal("S", package.Glyph);
        Assert.True(package.ShowGlyphFallback);
    }

    private static PackagesWindowViewModel CreateViewModel(
        FakeRuntimeApiClient runtimeClient,
        NotificationCenterService notificationCenter)
    {
        var viewModel = new PackagesWindowViewModel(
            runtimeClient,
            new FakePackageArchivePicker(),
            notificationCenter: notificationCenter)
        {
            Mode = PackageWindowMode.Installed,
            RegistryUrlText = string.Empty,
        };

        return viewModel;
    }

    private static NotificationCenterService CreateNotificationCenter()
        => new(Path.Combine(CreateTempDirectory(), "notifications.json"));

    private static InstalledPackageDescriptor CreateInstalledPackage(
        string packageId,
        bool isEnabled,
        PackageIconDescriptor? icon = null)
        => new(
            packageId,
            ToDisplayName(packageId),
            "1.0.0",
            Summary: null,
            Icon: icon,
            isEnabled,
            DependsOn: [],
            DateTimeOffset.UtcNow,
            StatusMessage: isEnabled ? null : "Disabled");

    private static string ToDisplayName(string packageId)
        => string.Concat(packageId[..1].ToUpperInvariant(), packageId[1..]);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakePackageArchivePicker : IPackageArchivePicker
    {
        public Task<string?> PickPackagePathAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class FakeRuntimeApiClient(IReadOnlyList<InstalledPackageDescriptor> installedPackages) : IRuntimeApiClient
    {
        private readonly List<InstalledPackageDescriptor> _installedPackages = installedPackages.ToList();

        public int GetInstalledPackagesCallCount { get; private set; }

        public PackageOperationResult? EnableResult { get; init; }

        public Task<SystemStatusResponse?> GetSystemStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<SystemStatusResponse?>(null);

        public Task<bool> IsRuntimeHealthyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<IReadOnlyList<ActivePackageDescriptor>> GetActivePackagesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ActivePackageDescriptor>>([]);

        public Task<IReadOnlyList<SessionPackageDescriptor>> GetSessionPackagesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SessionPackageDescriptor>>([]);

        public Task<IReadOnlyList<PackageSourceDescriptor>> GetActivePackageSourcesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PackageSourceDescriptor>>([]);

        public Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default)
        {
            GetInstalledPackagesCallCount++;
            return Task.FromResult<IReadOnlyList<InstalledPackageDescriptor>>(_installedPackages.ToArray());
        }

        public Uri CreatePackageAssetUri(string packageId, string assetPath)
            => new($"file:///packages/{Uri.EscapeDataString(packageId)}/assets/{assetPath.Replace('\\', '/')}");

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
        {
            if (EnableResult is not null)
            {
                return Task.FromResult(EnableResult);
            }

            SetPackageEnabled(packageId, isEnabled: true);
            return Task.FromResult(new PackageOperationResult(true, $"Enabled package '{ToDisplayName(packageId)}'.", false, [], [])
            {
                ImpactedPackageIds = [packageId],
            });
        }

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
            => throw new NotSupportedException();

        public void Dispose()
        {
        }

        private void SetPackageEnabled(string packageId, bool isEnabled)
        {
            var index = _installedPackages.FindIndex(package => string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                _installedPackages[index] = _installedPackages[index] with
                {
                    IsEnabled = isEnabled,
                    StatusMessage = isEnabled ? null : "Disabled",
                };
            }
        }
    }
}
