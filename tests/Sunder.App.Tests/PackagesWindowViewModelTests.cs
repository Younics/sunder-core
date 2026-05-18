using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.Protocol;
using Sunder.Registry.Shared;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using static Sunder.App.Tests.TestSupport.AsyncAssert;
using static Sunder.App.Tests.TestSupport.TestPaths;
using Xunit;

namespace Sunder.App.Tests;

public sealed class PackagesWindowViewModelTests
{
    [Fact]
    public async Task EnableSelectedPackageCommand_PublishesSuccessToastAfterRefresh()
    {
        var runtimeClient = new FakeRuntimeApiClient(
            [CreateInstalledPackage("agent", isEnabled: false)]
        );
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
        var runtimeClient = new FakeRuntimeApiClient(
            [CreateInstalledPackage("agent", isEnabled: false)]
        )
        {
            EnableResult = new PackageOperationResult(
                true,
                "Package 'Agent' is already enabled.",
                true,
                false,
                [],
                []
            ),
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
        var runtimeClient = new FakeRuntimeApiClient(
            [CreateInstalledPackage("agent", isEnabled: false)]
        )
        {
            EnableResult = new PackageOperationResult(
                false,
                "Package failed.",
                false,
                false,
                [],
                ["Package failed."]
            ),
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
    public async Task EnableSelectedPackageCommand_WhenOperationServicePresent_QueuesBackgroundOperation()
    {
        var runtimeClient = new FakeRuntimeApiClient(
            [CreateInstalledPackage("agent", isEnabled: false)]
        );
        var queue = new BackgroundProcessQueueService(maxParallelism: 1);
        var notificationCenter = CreateNotificationCenter();
        var lifecycleApplications = new List<IReadOnlyList<string>>();
        using var operationService = new PackageOperationService(
            queue,
            new FakeRuntimeApiClientFactory(runtimeClient),
            (packageIds, _) =>
            {
                lifecycleApplications.Add(packageIds.ToArray());
                return Task.CompletedTask;
            },
            notificationCenter);
        using var viewModel = new PackagesWindowViewModel(
            runtimeClient,
            new FakePackageArchivePicker(),
            packageOperationService: operationService,
            backgroundProcessQueue: queue,
            notificationCenter: notificationCenter)
        {
            Mode = PackageWindowMode.Installed,
            RegistryUrlText = string.Empty,
        };

        await viewModel.InitializeAsync();
        await viewModel.EnableSelectedPackageCommand.ExecuteAsync(null);
        await WaitForConditionAsync(() => queue.ListProcesses().Any(IsCompletedEnableOperation));

        Assert.Equal(["agent"], runtimeClient.EnabledPackageIds);
        Assert.Collection(lifecycleApplications, packageIds => Assert.Equal(["agent"], packageIds));
    }

    [Fact]
    public async Task InstalledPackageCommands_CanExecuteReflectsSelectionAndBusyState()
    {
        var runtimeClient = new FakeRuntimeApiClient(
            [CreateInstalledPackage("agent", isEnabled: false)]
        );
        using var viewModel = CreateViewModel(runtimeClient, CreateNotificationCenter());

        await viewModel.InitializeAsync();

        Assert.True(viewModel.RefreshCommand.CanExecute(null));
        Assert.True(viewModel.InstallPackageCommand.CanExecute(null));
        Assert.True(viewModel.EnableSelectedPackageCommand.CanExecute(null));
        Assert.False(viewModel.DisableSelectedPackageCommand.CanExecute(null));
        Assert.True(viewModel.UninstallSelectedPackageCommand.CanExecute(null));
        Assert.False(viewModel.UpdateSelectedInstalledPackageCommand.CanExecute(null));

        viewModel.IsBusy = true;

        Assert.False(viewModel.RefreshCommand.CanExecute(null));
        Assert.False(viewModel.InstallPackageCommand.CanExecute(null));
        Assert.False(viewModel.EnableSelectedPackageCommand.CanExecute(null));
        Assert.False(viewModel.DisableSelectedPackageCommand.CanExecute(null));
        Assert.False(viewModel.UninstallSelectedPackageCommand.CanExecute(null));
        Assert.False(viewModel.UpdateSelectedInstalledPackageCommand.CanExecute(null));
    }

    [Fact]
    public async Task InstalledPackages_UsePackageIconAssetUriWhenAvailable()
    {
        var runtimeClient = new FakeRuntimeApiClient(
            [
                CreateInstalledPackage(
                    "agent",
                    isEnabled: true,
                    new PackageIconDescriptor(null, "assets/icons/agent.svg")
                ),
            ]
        );
        var viewModel = CreateViewModel(runtimeClient, CreateNotificationCenter());

        await viewModel.InitializeAsync();

        var package = Assert.Single(viewModel.InstalledPackages);
        Assert.Equal(
            new Uri("file:///packages/agent/assets/assets/icons/agent.svg"),
            package.IconUri
        );
        Assert.Equal("A", package.Glyph);
        Assert.True(package.ShowGlyphFallback);
        Assert.True(viewModel.ShowSelectedPackageIcon);
        Assert.Equal("A", viewModel.SelectedPackageGlyph);
        Assert.True(viewModel.SelectedPackageShowGlyphFallback);
    }

    [Fact]
    public void MarketplacePackages_UseRegistryIconUrlWhenAvailable()
    {
        var iconUri = new Uri(
            "http://127.0.0.1:1/api/packages/sunder.package.agent/versions/1.0.0/icon"
        );
        using var package = new RegistryPackageSearchItemViewModel(
            new RegistryPackageSummary(
                "sunder.package.agent",
                "Sunder Agent",
                "Adds local agents.",
                "1.0.0",
                iconUri.ToString(),
                IsYanked: false,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow
            ),
            installedVersion: null,
            update: null,
            _ => Task.CompletedTask,
            loadIcon: false
        );

        Assert.Equal(iconUri, package.IconUri);
        Assert.Equal("S", package.Glyph);
        Assert.True(package.ShowGlyphFallback);
    }

    [Fact]
    public async Task SearchText_WhenMarketplaceMode_SearchesRegistryAfterThrottle()
    {
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ => [CreateRegistryPackage("sunder.package.agent")],
        };
        using var viewModel = CreateViewModel(
            new FakeRuntimeApiClient([]),
            CreateNotificationCenter(),
            _ => registryClient,
            TimeSpan.FromMilliseconds(40)
        );
        viewModel.Mode = PackageWindowMode.Marketplace;
        viewModel.RegistryUrlText = "https://registry.example/";

        viewModel.SearchText = " agent ";

        await Task.Delay(10);
        Assert.Empty(registryClient.SearchQueries);

        await WaitForConditionAsync(() => registryClient.SearchQueries.Count == 1);

        Assert.Collection(registryClient.SearchQueries, query => Assert.Equal("agent", query));
        Assert.Collection(
            viewModel.MarketplacePackages,
            package => Assert.Equal("sunder.package.agent", package.PackageId)
        );
        Assert.True(viewModel.HasSearchText);
    }

    [Fact]
    public async Task SearchText_WhenChangedBeforeThrottleElapsed_SearchesOnlyLatestText()
    {
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ => [CreateRegistryPackage("sunder.package.agent")],
        };
        using var viewModel = CreateViewModel(
            new FakeRuntimeApiClient([]),
            CreateNotificationCenter(),
            _ => registryClient,
            TimeSpan.FromMilliseconds(50)
        );
        viewModel.Mode = PackageWindowMode.Marketplace;
        viewModel.RegistryUrlText = "https://registry.example/";

        viewModel.SearchText = "a";
        await Task.Delay(10);
        viewModel.SearchText = "agent";

        await WaitForConditionAsync(() => registryClient.SearchQueries.Count == 1);

        Assert.Collection(registryClient.SearchQueries, query => Assert.Equal("agent", query));
    }

    [Fact]
    public async Task ClearSearchCommand_WhenMarketplaceMode_ClearsTextAndRefreshesResults()
    {
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = query =>
                query is null
                    ? [CreateRegistryPackage("sunder.package.tools")]
                    : [CreateRegistryPackage("sunder.package.agent")],
        };
        using var viewModel = CreateViewModel(
            new FakeRuntimeApiClient([]),
            CreateNotificationCenter(),
            _ => registryClient,
            TimeSpan.FromMilliseconds(40)
        );
        viewModel.Mode = PackageWindowMode.Marketplace;
        viewModel.RegistryUrlText = "https://registry.example/";
        viewModel.SearchText = "agent";
        await WaitForConditionAsync(() => registryClient.SearchQueries.Count == 1);

        viewModel.ClearSearchCommand.Execute(null);

        await WaitForConditionAsync(() => registryClient.SearchQueries.Count == 2);

        Assert.Equal(string.Empty, viewModel.SearchText);
        Assert.False(viewModel.HasSearchText);
        Assert.Collection(
            registryClient.SearchQueries,
            query => Assert.Equal("agent", query),
            Assert.Null
        );
        Assert.Collection(
            viewModel.MarketplacePackages,
            package => Assert.Equal("sunder.package.tools", package.PackageId)
        );
    }

    [Fact]
    public async Task MarketplaceSelection_WhenEarlierDetailsCompleteLater_DoesNotOverwriteCurrentDetails()
    {
        var agentDetailsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agentDetailsCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAgentDetails = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ =>
            [
                CreateRegistryPackage("sunder.package.agent", "9.0.0"),
                CreateRegistryPackage("sunder.package.tools", "2.0.0"),
            ],
            PackageDetails = async (packageId, cancellationToken) =>
            {
                if (string.Equals(packageId, "sunder.package.agent", StringComparison.OrdinalIgnoreCase))
                {
                    agentDetailsStarted.SetResult();
                    try
                    {
                        await releaseAgentDetails.Task.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        agentDetailsCancelled.SetResult();
                        throw;
                    }

                    return CreateRegistryPackageDetails(packageId, "9.0.0");
                }

                return CreateRegistryPackageDetails(packageId, "2.0.0");
            },
        };
        using var viewModel = CreateViewModel(
            new FakeRuntimeApiClient([]),
            CreateNotificationCenter(),
            _ => registryClient,
            TimeSpan.FromMilliseconds(40)
        );
        viewModel.RegistryUrlText = "https://registry.example/";

        var searchTask = viewModel.SearchMarketplaceCommand.ExecuteAsync(null);
        await agentDetailsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await viewModel.MarketplacePackages[1].SelectCommand.ExecuteAsync(null);
        await agentDetailsCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("Sunder.package.tools", viewModel.SelectedPackageTitle);
        Assert.Collection(viewModel.MarketplaceVersions, version => Assert.Equal("2.0.0", version.Version));

        releaseAgentDetails.SetResult();
        await searchTask;

        Assert.Equal("Sunder.package.tools", viewModel.SelectedPackageTitle);
        Assert.Collection(viewModel.MarketplaceVersions, version => Assert.Equal("2.0.0", version.Version));
    }

    [Fact]
    public async Task MarketplaceSelection_WhenDetailsContainProfile_PopulatesProfileSections()
    {
        var profile = new RegistryPackageProfile(
            "sunder.package.agent",
            "Detailed local agent package.",
            "# Sunder Agent",
            "https://example.test/agent",
            "https://example.test/source",
            "https://example.test/issues",
            " MIT ",
            ["agent", " local ", "agent", ""],
            [],
            DateTimeOffset.UtcNow);
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ => [CreateRegistryPackage("sunder.package.agent")],
            PackageDetails = (packageId, _) => Task.FromResult<RegistryPackageDetails?>(
                CreateRegistryPackageDetails(packageId, "1.0.0", profile)),
        };
        using var viewModel = CreateViewModel(
            new FakeRuntimeApiClient([]),
            CreateNotificationCenter(),
            _ => registryClient,
            TimeSpan.FromMilliseconds(40));
        viewModel.RegistryUrlText = "https://registry.example/";

        await viewModel.SearchMarketplaceCommand.ExecuteAsync(null);

        Assert.Equal("Detailed local agent package.", viewModel.SelectedPackageSummary);
        Assert.True(viewModel.HasMarketplaceReadme);
        Assert.True(viewModel.HasMarketplaceProfile);
        Assert.False(viewModel.HasMarketplaceProfileMedia);
        Assert.Collection(
            viewModel.MarketplaceProfileLinks,
            link =>
            {
                Assert.Equal("Website", link.Label);
                Assert.Equal(new Uri("https://example.test/agent"), link.NavigateUri);
            },
            link =>
            {
                Assert.Equal("Source", link.Label);
                Assert.Equal(new Uri("https://example.test/source"), link.NavigateUri);
            },
            link =>
            {
                Assert.Equal("Issues", link.Label);
                Assert.Equal(new Uri("https://example.test/issues"), link.NavigateUri);
            });
        Assert.Collection(
            viewModel.MarketplaceProfileMetadata,
            item =>
            {
                Assert.Equal("License", item.Label);
                Assert.Equal("MIT", item.Value);
            });
        Assert.Collection(
            viewModel.MarketplaceProfileTags,
            tag => Assert.Equal("agent", tag),
            tag => Assert.Equal("local", tag));
    }

    [Fact]
    public async Task InstallSelectedMarketplacePackageCommand_InstallsSelectedVersion()
    {
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ => [CreateRegistryPackage("sunder.package.agent", "2.0.0")],
            PackageDetails = (packageId, _) => Task.FromResult<RegistryPackageDetails?>(new RegistryPackageDetails(
                packageId,
                ToDisplayName(packageId),
                null,
                "2.0.0",
                null,
                [
                    new RegistryPackageVersionSummary("2.0.0", false, null, DateTimeOffset.UtcNow),
                    new RegistryPackageVersionSummary("1.5.0", false, null, DateTimeOffset.UtcNow.AddDays(-1)),
                ],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                Profile: null)),
            InstallPlan = new RegistryResolveInstallPlanResponse(
                true,
                [CreatePlanItem("sunder.package.agent", "1.5.0")],
                [],
                [],
                []),
        };
        using var viewModel = CreateViewModel(
            new FakeRuntimeApiClient([]),
            CreateNotificationCenter(),
            _ => registryClient,
            TimeSpan.FromMilliseconds(40));
        viewModel.RegistryUrlText = "https://registry.example/";

        await viewModel.SearchMarketplaceCommand.ExecuteAsync(null);
        viewModel.MarketplaceVersions.Single(version => version.Version == "1.5.0").SelectCommand.Execute(null);
        await viewModel.InstallSelectedMarketplacePackageCommand.ExecuteAsync(null);

        Assert.NotNull(registryClient.LastInstallPlanRequest);
        Assert.Equal("sunder.package.agent", registryClient.LastInstallPlanRequest.PackageId);
        Assert.Equal("1.5.0", registryClient.LastInstallPlanRequest.Version);
        Assert.Null(registryClient.LastInstallPlanRequest.Tag);
    }

    [Fact]
    public async Task SearchText_WhenSwitchingModes_RestoresModeSpecificSearch()
    {
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ => [CreateRegistryPackage("sunder.package.tools")],
        };
        using var viewModel = CreateViewModel(
            new FakeRuntimeApiClient(
            [
                CreateInstalledPackage("sunder.package.agent", isEnabled: true),
                CreateInstalledPackage("sunder.package.tools", isEnabled: true),
            ]),
            CreateNotificationCenter(),
            _ => registryClient,
            TimeSpan.FromMilliseconds(40)
        );
        viewModel.RegistryUrlText = "https://registry.example/";
        await viewModel.InitializeAsync();
        viewModel.SearchText = "agent";
        Assert.Collection(viewModel.InstalledPackages, package => Assert.Equal("sunder.package.agent", package.PackageId));

        await viewModel.ShowMarketplaceCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, viewModel.SearchText);
        viewModel.SearchText = "tools";
        await WaitForConditionAsync(() => registryClient.SearchQueries.Count > 0);

        await viewModel.ShowInstalledCommand.ExecuteAsync(null);

        Assert.Equal("agent", viewModel.SearchText);
        Assert.Collection(viewModel.InstalledPackages, package => Assert.Equal("sunder.package.agent", package.PackageId));
    }

    [Fact]
    public async Task PackageOperationCompletion_RebuildsInstalledPackagesWhileMarketplaceTabIsActive()
    {
        var registryClient = new FakeRegistryApiClient
        {
            SearchResults = _ => [CreateRegistryPackage("sunder.package.agent")],
        };
        var runtimeClient = new FakeRuntimeApiClient([CreateInstalledPackage("sunder.package.tools", isEnabled: true)]);
        using var viewModel = new PackagesWindowViewModel(
            runtimeClient,
            new FakePackageArchivePicker(),
            notificationCenter: CreateNotificationCenter(),
            registryClientFactory: _ => registryClient,
            marketplaceSearchThrottleDelay: TimeSpan.FromMilliseconds(40))
        {
            RegistryUrlText = "https://registry.example/",
        };

        await viewModel.InitializeAsync();
        Assert.Equal(PackageWindowMode.Marketplace, viewModel.Mode);
        Assert.DoesNotContain(viewModel.InstalledPackages, package => package.PackageId == "sunder.package.agent");
        runtimeClient.AddInstalledPackage(CreateInstalledPackage("sunder.package.agent", isEnabled: true));

        await InvokeRefreshAfterPackageOperationAsync(
            viewModel,
            new PackageOperationMetadata("sunder.package.agent", PackageOperationKind.InstallMarketplace, "Sunder Agent"));

        Assert.Equal(PackageWindowMode.Marketplace, viewModel.Mode);
        Assert.Contains(viewModel.InstalledPackages, package => package.PackageId == "sunder.package.agent");
    }

    private static async Task InvokeRefreshAfterPackageOperationAsync(
        PackagesWindowViewModel viewModel,
        PackageOperationMetadata metadata)
    {
        var method = typeof(PackagesWindowViewModel).GetMethod(
            "RefreshAfterPackageOperationAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        var snapshot = new BackgroundProcessSnapshot(
            Guid.NewGuid(),
            "Install Sunder Agent",
            PackageOperationService.PackageStoreGroupKey,
            BackgroundProcessIndicator.Packages,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup,
            BackgroundProcessState.Completed,
            "Completed",
            100,
            true,
            metadata.ToMetadata(),
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        await (Task)method.Invoke(viewModel, [snapshot])!;
    }

    private static PackagesWindowViewModel CreateViewModel(
        FakeRuntimeApiClient runtimeClient,
        NotificationCenterService notificationCenter,
        Func<Uri, IRegistryApiClient>? registryClientFactory = null,
        TimeSpan? marketplaceSearchThrottleDelay = null
    )
    {
        var viewModel = new PackagesWindowViewModel(
            runtimeClient,
            new FakePackageArchivePicker(),
            notificationCenter: notificationCenter,
            registryClientFactory: registryClientFactory,
            marketplaceSearchThrottleDelay: marketplaceSearchThrottleDelay
        )
        {
            Mode = PackageWindowMode.Installed,
            RegistryUrlText = string.Empty,
        };

        return viewModel;
    }

    private static NotificationCenterService CreateNotificationCenter() =>
        new(Path.Combine(CreateTempDirectory(), "notifications.json"));

    private static InstalledPackageDescriptor CreateInstalledPackage(
        string packageId,
        bool isEnabled,
        PackageIconDescriptor? icon = null
    ) =>
        new(
            packageId,
            ToDisplayName(packageId),
            "1.0.0",
            Summary: null,
            Icon: icon,
            isEnabled,
            DependsOn: [],
            DateTimeOffset.UtcNow,
            StatusMessage: isEnabled ? null : "Disabled"
        );

    private static RegistryPackageSummary CreateRegistryPackage(string packageId, string latestVersion = "1.0.0") =>
        new(
            packageId,
            ToDisplayName(packageId),
            null,
            latestVersion,
            null,
            false,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow
        );

    private static RegistryPackageDetails CreateRegistryPackageDetails(
        string packageId,
        string version,
        RegistryPackageProfile? profile = null) =>
        new(
            packageId,
            ToDisplayName(packageId),
            null,
            version,
            null,
            [new RegistryPackageVersionSummary(version, false, null, DateTimeOffset.UtcNow)],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            profile
        );

    private static RegistryPackageInstallPlanItem CreatePlanItem(string packageId, string version)
        => new(
            packageId,
            CurrentVersion: null,
            version,
            IsUpdate: false,
            DeprecatedMessage: null,
            DependsOn: [],
            new RegistryPackageArtifact("", 0, $"download/{packageId}/{version}"));

    private static string ToDisplayName(string packageId) =>
        string.Concat(packageId[..1].ToUpperInvariant(), packageId[1..]);

    private static bool IsCompletedEnableOperation(BackgroundProcessSnapshot snapshot)
        => snapshot.State == BackgroundProcessState.Completed
           && PackageOperationMetadata.TryCreate(snapshot.Metadata, out var metadata)
           && metadata.Kind == PackageOperationKind.Enable
           && string.Equals(metadata.PackageId, "agent", StringComparison.OrdinalIgnoreCase);

    private sealed class FakePackageArchivePicker : IPackageArchivePicker
    {
        public Task<string?> PickPackagePathAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class FakeRuntimeApiClientFactory(FakeRuntimeApiClient runtimeApiClient) : IRuntimeApiClientFactory
    {
        public IRuntimeApiClient CreateClient() => runtimeApiClient;
    }

    private sealed class FakeRegistryApiClient : IRegistryApiClient
    {
        private readonly object _gate = new();
        private readonly List<string?> _searchQueries = [];

        public Uri RegistryUrl { get; } = new("https://registry.example/");

        public IReadOnlyList<string?> SearchQueries
        {
            get
            {
                lock (_gate)
                {
                    return _searchQueries.ToArray();
                }
            }
        }

        public Func<string?, IReadOnlyList<RegistryPackageSummary>> SearchResults { get; init; } = _ => [];

        public Func<string, CancellationToken, Task<RegistryPackageDetails?>> PackageDetails { get; init; } =
            (packageId, _) => Task.FromResult<RegistryPackageDetails?>(CreateRegistryPackageDetails(packageId, "1.0.0"));

        public RegistryResolveInstallPlanResponse InstallPlan { get; init; } = new(true, [], [], [], []);

        public RegistryResolveInstallPlanRequest? LastInstallPlanRequest { get; private set; }

        public Task<IReadOnlyList<RegistryPackageSummary>> SearchAsync(
            string? query,
            int skip,
            int take,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _searchQueries.Add(query);
            }

            var results = SearchResults(query)
                .Skip(skip)
                .Take(take)
                .ToArray();
            return Task.FromResult<IReadOnlyList<RegistryPackageSummary>>(results);
        }

        public Task<RegistryPackageDetails?> GetPackageAsync(
            string packageId,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return PackageDetails(packageId, cancellationToken);
        }

        public Task<RegistryPackageVersionDetails?> GetVersionAsync(
            string packageId,
            string version,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<RegistryResolveUpdatesResponse> ResolveUpdatesAsync(
            RegistryResolveUpdatesRequest request,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new RegistryResolveUpdatesResponse([]));

        public Task<RegistryResolveInstallPlanResponse> ResolveInstallPlanAsync(
            RegistryResolveInstallPlanRequest request,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastInstallPlanRequest = request;
            return Task.FromResult(InstallPlan);
        }

        public Task DownloadArtifactAsync(
            RegistryPackageArtifact artifact,
            string packageId,
            string version,
            string destinationPath,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    private sealed class FakeRuntimeApiClient(
        IReadOnlyList<InstalledPackageDescriptor> installedPackages
    ) : IRuntimeApiClient
    {
        private readonly List<InstalledPackageDescriptor> _installedPackages =
            installedPackages.ToList();

        public int GetInstalledPackagesCallCount { get; private set; }

        public List<string> EnabledPackageIds { get; } = [];

        public PackageOperationResult? EnableResult { get; init; }

        public string? RegistryInstallPackageId { get; init; }

        public void AddInstalledPackage(InstalledPackageDescriptor package)
        {
            _installedPackages.RemoveAll(existing =>
                string.Equals(existing.PackageId, package.PackageId, StringComparison.OrdinalIgnoreCase));
            _installedPackages.Add(package);
        }

        public Task<SystemStatusResponse?> GetSystemStatusAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult<SystemStatusResponse?>(null);

        public Task<bool> IsRuntimeHealthyAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<ActivePackageDescriptor>> GetActivePackagesAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<ActivePackageDescriptor>>([]);

        public Task<IReadOnlyList<SessionPackageDescriptor>> GetSessionPackagesAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<SessionPackageDescriptor>>([]);

        public Task<IReadOnlyList<PackageSourceDescriptor>> GetActivePackageSourcesAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<PackageSourceDescriptor>>([]);

        public Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(
            CancellationToken cancellationToken = default
        )
        {
            GetInstalledPackagesCallCount++;
            return Task.FromResult<IReadOnlyList<InstalledPackageDescriptor>>(
                _installedPackages.ToArray()
            );
        }

        public Uri CreatePackageAssetUri(string packageId, string assetPath) =>
            new(
                $"file:///packages/{Uri.EscapeDataString(packageId)}/assets/{assetPath.Replace('\\', '/')}"
            );

        public Task<PackageOperationResult> InstallPackageFromPathAsync(
            string packagePath,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packageId = RegistryInstallPackageId ?? Path.GetFileNameWithoutExtension(packagePath);
            if (!_installedPackages.Any(package => string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase)))
            {
                _installedPackages.Add(CreateInstalledPackage(packageId, isEnabled: true));
            }

            return Task.FromResult(
                new PackageOperationResult(
                    true,
                    $"Installed package '{ToDisplayName(packageId)}'.",
                    true,
                    false,
                    [],
                    [])
                {
                    ImpactedPackageIds = [packageId],
                });
        }

        public Task<PackageOperationResult> UpgradePackageFromPathAsync(
            string packageId,
            string packagePath,
            bool allowDowngrade = false,
            bool reinstall = false,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<PackageOperationResult> EnableInstalledPackageAsync(
            string packageId,
            CancellationToken cancellationToken = default
        )
        {
            if (EnableResult is not null)
            {
                return Task.FromResult(EnableResult);
            }

            EnabledPackageIds.Add(packageId);
            SetPackageEnabled(packageId, isEnabled: true);
            return Task.FromResult(
                new PackageOperationResult(
                    true,
                    $"Enabled package '{ToDisplayName(packageId)}'.",
                    true,
                    false,
                    [],
                    []
                )
                {
                    ImpactedPackageIds = [packageId],
                }
            );
        }

        public Task<PackageOperationResult> DisableInstalledPackageAsync(
            string packageId,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<PackageOperationResult> UninstallPackageAsync(
            string packageId,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<PackageLifecycleOperationResult> LoadPackageLifecycleAsync(
            PackageLifecycleLoadRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<
            IReadOnlyList<PackageConfigurationSchemaDescriptor>
        > GetConfigurationSchemasAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PackageConfigurationValuesResponse?> GetPackageConfigurationValuesAsync(
            string packageId,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task SavePackageConfigurationValuesAsync(
            string packageId,
            IReadOnlyDictionary<string, string?> values,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<PackageAuthStatusResponse?> GetPackageAuthStatusAsync(
            string packageId,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<PackageAuthSessionStartResponse?> StartPackageAuthAsync(
            string packageId,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<PackageAuthSessionStatusResponse?> GetPackageAuthSessionStatusAsync(
            string packageId,
            string authSessionId,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<PackageAuthStatusResponse?> DisconnectPackageAuthAsync(
            string packageId,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task ReportPackageFaultAsync(
            string packageId,
            PackageFailureOrigin origin,
            string message,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task ShutdownAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Dispose() { }

        private void SetPackageEnabled(string packageId, bool isEnabled)
        {
            var index = _installedPackages.FindIndex(package =>
                string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
            );
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
