using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.Protocol;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed class SettingsWindowViewModelTests
{
    [Fact]
    public async Task RefreshPackageSectionsAsync_LoadsNewPackageSchemas()
    {
        using var runtimeClient = new FakeRuntimeApiClient
        {
            ConfigurationSchemas = [CreateSchema("agent", "Agent")],
        };
        using var viewModel = CreateViewModel(runtimeClient);

        await viewModel.RefreshPackageSectionsAsync();

        Assert.Collection(
            viewModel.PackageSections,
            section => Assert.Equal("agent", section.PackageId));

        runtimeClient.ConfigurationSchemas =
        [
            CreateSchema("agent", "Agent"),
            CreateSchema("tools", "Tools"),
        ];

        await viewModel.RefreshPackageSectionsAsync();

        Assert.Collection(
            viewModel.PackageSections,
            section => Assert.Equal("agent", section.PackageId),
            section => Assert.Equal("tools", section.PackageId));
        Assert.True(viewModel.HasPackageSections);
    }

    [Fact]
    public async Task RefreshPackageSectionsAsync_PreservesSelectedPackageFields()
    {
        using var runtimeClient = new FakeRuntimeApiClient
        {
            ConfigurationSchemas = [CreateSchema("agent", "Agent", "Configure Agent.")],
        };
        runtimeClient.ConfigurationValues["agent"] = new PackageConfigurationValuesResponse(
            "agent",
            new Dictionary<string, string?> { ["apiKey"] = "stored" },
            []);
        using var viewModel = CreateViewModel(runtimeClient);

        await viewModel.RefreshPackageSectionsAsync();
        Assert.True(await viewModel.SelectPackageSettingsAsync("agent"));
        var field = Assert.IsType<TextSettingsFieldViewModel>(Assert.Single(Assert.Single(viewModel.SelectedPackageSections).Fields));
        field.Value = "unsaved";

        runtimeClient.ConfigurationSchemas = [CreateSchema("agent", "Agent Updated", "Updated summary.")];

        await viewModel.RefreshPackageSectionsAsync();

        Assert.Equal("Agent Updated", viewModel.SelectedTitle);
        Assert.Equal("Updated summary.", viewModel.SelectedDescription);
        Assert.Equal(1, runtimeClient.GetPackageConfigurationValuesCallCount);
        Assert.True(Assert.Single(viewModel.PackageSections).IsSelected);
        var refreshedField = Assert.IsType<TextSettingsFieldViewModel>(Assert.Single(Assert.Single(viewModel.SelectedPackageSections).Fields));
        Assert.Same(field, refreshedField);
        Assert.Equal("unsaved", refreshedField.Value);
    }

    [Fact]
    public async Task RefreshPackageSectionsAsync_FallsBackToCoreSectionWhenSelectedPackageDisappears()
    {
        using var runtimeClient = new FakeRuntimeApiClient
        {
            ConfigurationSchemas = [CreateSchema("agent", "Agent")],
        };
        using var viewModel = CreateViewModel(runtimeClient);

        await viewModel.RefreshPackageSectionsAsync();
        Assert.True(await viewModel.SelectPackageSettingsAsync("agent"));

        runtimeClient.ConfigurationSchemas = [];

        await viewModel.RefreshPackageSectionsAsync();

        Assert.False(viewModel.IsPackageSelection);
        Assert.Equal("Appearance", viewModel.SelectedTitle);
        Assert.True(viewModel.CoreSections[0].IsSelected);
        Assert.Empty(viewModel.PackageSections);
        Assert.Empty(viewModel.SelectedPackageSections);
    }

    [Fact]
    public async Task SelectPackageSettingsAsync_WhenEarlierFieldLoadCompletesLater_DoesNotOverwriteCurrentSelection()
    {
        var agentValuesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAgentValues = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var runtimeClient = new FakeRuntimeApiClient
        {
            ConfigurationSchemas =
            [
                CreateSchema("agent", "Agent"),
                CreateSchema("tools", "Tools"),
            ],
            GetConfigurationValuesAsyncCallback = async (packageId, cancellationToken) =>
            {
                if (string.Equals(packageId, "agent", StringComparison.OrdinalIgnoreCase))
                {
                    agentValuesStarted.SetResult();
                    await releaseAgentValues.Task.WaitAsync(cancellationToken);
                }

                return new PackageConfigurationValuesResponse(
                    packageId,
                    new Dictionary<string, string?> { ["apiKey"] = packageId },
                    []);
            },
        };
        using var viewModel = CreateViewModel(runtimeClient);
        await viewModel.RefreshPackageSectionsAsync();

        var agentSelectionTask = viewModel.SelectPackageSettingsAsync("agent");
        await agentValuesStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(await viewModel.SelectPackageSettingsAsync("tools"));

        releaseAgentValues.SetResult();
        Assert.False(await agentSelectionTask);

        Assert.Equal("Tools", viewModel.SelectedTitle);
        var section = Assert.Single(viewModel.SelectedPackageSections);
        var field = Assert.IsType<TextSettingsFieldViewModel>(Assert.Single(section.Fields));
        Assert.Equal("tools", field.Value);
    }

    [Fact]
    public async Task ApplyAsync_WhenSelectionChangesDuringSave_DoesNotOverwriteCurrentSelectionStatus()
    {
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var savedPackageIds = new List<string>();
        using var runtimeClient = new FakeRuntimeApiClient
        {
            ConfigurationSchemas =
            [
                CreateSchema("agent", "Agent"),
                CreateSchema("tools", "Tools"),
            ],
            SaveConfigurationValuesAsyncCallback = async (packageId, _, cancellationToken) =>
            {
                savedPackageIds.Add(packageId);
                saveStarted.SetResult();
                await releaseSave.Task.WaitAsync(cancellationToken);
            },
        };
        using var viewModel = CreateViewModel(runtimeClient);
        await viewModel.RefreshPackageSectionsAsync();
        Assert.True(await viewModel.SelectPackageSettingsAsync("agent"));

        var applyTask = viewModel.ApplyAsync();
        await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(await viewModel.SelectPackageSettingsAsync("tools"));
        releaseSave.SetResult();
        var applied = await applyTask;

        Assert.False(applied);
        Assert.Equal(["agent"], savedPackageIds);
        Assert.Equal("Tools", viewModel.SelectedTitle);
        Assert.Equal(string.Empty, viewModel.StatusText);
    }

    [Fact]
    public async Task SaveAsync_WhenPackageConfigurationSaveFails_ReturnsFalseAndKeepsWindowOpenableStatus()
    {
        using var runtimeClient = new FakeRuntimeApiClient
        {
            ConfigurationSchemas = [CreateSchema("agent", "Agent")],
            SaveConfigurationValuesAsyncCallback = (_, _, _) => throw new InvalidOperationException("Save failed."),
        };
        using var viewModel = CreateViewModel(runtimeClient);
        await viewModel.RefreshPackageSectionsAsync();
        Assert.True(await viewModel.SelectPackageSettingsAsync("agent"));

        var saved = await viewModel.SaveAsync();

        Assert.False(saved);
        Assert.Equal("Save failed.", viewModel.StatusText);
    }

    [Fact]
    public async Task Dispose_CancelsInFlightPackageSectionLoad()
    {
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var runtimeClient = new FakeRuntimeApiClient
        {
            GetConfigurationSchemasAsyncCallback = async cancellationToken =>
            {
                loadStarted.SetResult();
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    loadCancelled.SetResult();
                    throw;
                }

                return [];
            },
        };
        var viewModel = CreateViewModel(runtimeClient);
        await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.Dispose();

        await loadCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(viewModel.PackageSections);
    }

    [Fact]
    public async Task BackgroundProcesses_IncludesOnlySettingsIndicatorProcessesInSettingsFooter()
    {
        using var runtimeClient = new FakeRuntimeApiClient();
        var queue = new BackgroundProcessQueueService(maxParallelism: 2);
        using var viewModel = new SettingsWindowViewModel(
            runtimeClient,
            PackageViewHostService.Empty,
            new CliInstallationService(),
            backgroundProcessQueue: queue);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        queue.Enqueue(new BackgroundProcessRequest(
            "Hidden work",
            "hidden:work",
            BackgroundProcessIndicator.Hidden,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup,
            true,
            async _ => await release.Task));
        queue.Enqueue(new BackgroundProcessRequest(
            "Settings package work",
            "package:test:work",
            BackgroundProcessIndicator.Settings,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup,
            true,
            async _ => await release.Task));

        await WaitForConditionAsync(() => queue.ListProcesses().Count(process => process.State == BackgroundProcessState.Running) == 2);
        viewModel.BackgroundProcesses.Refresh();

        Assert.Equal("Settings package work", Assert.Single(viewModel.BackgroundProcesses.Processes).Title);
        release.SetResult();
        await WaitForConditionAsync(() => queue.ListProcesses().All(process => process.IsTerminal));
    }

    private static SettingsWindowViewModel CreateViewModel(FakeRuntimeApiClient runtimeClient) =>
        new(
            runtimeClient,
            PackageViewHostService.Empty,
            new CliInstallationService());

    private static async Task WaitForConditionAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.True(condition());
                return;
            }

            await Task.Delay(10);
        }
    }

    private static PackageConfigurationSchemaDescriptor CreateSchema(
        string packageId,
        string displayName,
        string? summary = null) =>
        new(
            packageId,
            displayName,
            summary,
            [
                new PackageConfigurationSectionDescriptor(
                    "general",
                    "General",
                    null,
                    [
                        new PackageConfigurationFieldDescriptor(
                            "apiKey",
                            "API key",
                            PackageConfigurationFieldKind.Text,
                            null,
                            false,
                            null,
                            null,
                            []),
                    ]),
            ]);

    private sealed class FakeRuntimeApiClient : IRuntimeApiClient
    {
        public IReadOnlyList<PackageConfigurationSchemaDescriptor> ConfigurationSchemas { get; set; } = [];

        public Dictionary<string, PackageConfigurationValuesResponse?> ConfigurationValues { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Func<CancellationToken, Task<IReadOnlyList<PackageConfigurationSchemaDescriptor>>>? GetConfigurationSchemasAsyncCallback { get; init; }

        public Func<string, CancellationToken, Task<PackageConfigurationValuesResponse?>>? GetConfigurationValuesAsyncCallback { get; init; }

        public Func<string, IReadOnlyDictionary<string, string?>, CancellationToken, Task>? SaveConfigurationValuesAsyncCallback { get; init; }

        public int GetPackageConfigurationValuesCallCount { get; private set; }

        public Task<SystemStatusResponse?> GetSystemStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SystemStatusResponse?>(null);

        public Task<bool> IsRuntimeHealthyAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<ActivePackageDescriptor>> GetActivePackagesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ActivePackageDescriptor>>([]);

        public Task<IReadOnlyList<SessionPackageDescriptor>> GetSessionPackagesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionPackageDescriptor>>([]);

        public Task<IReadOnlyList<PackageSourceDescriptor>> GetActivePackageSourcesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PackageSourceDescriptor>>([]);

        public Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InstalledPackageDescriptor>>([]);

        public Uri CreatePackageAssetUri(string packageId, string assetPath) =>
            throw new NotSupportedException();

        public Task<PackageOperationResult> InstallPackageFromPathAsync(
            string packagePath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PackageOperationResult> UpgradePackageFromPathAsync(
            string packageId,
            string packagePath,
            bool allowDowngrade = false,
            bool reinstall = false,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PackageOperationResult> EnableInstalledPackageAsync(
            string packageId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PackageOperationResult> DisableInstalledPackageAsync(
            string packageId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PackageOperationResult> UninstallPackageAsync(
            string packageId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DevPackageLoadResult> LoadDevPackagesAsync(
            IReadOnlyList<string> folders,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PackageConfigurationSchemaDescriptor>> GetConfigurationSchemasAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetConfigurationSchemasAsyncCallback is not null)
            {
                return GetConfigurationSchemasAsyncCallback(cancellationToken);
            }

            return Task.FromResult(ConfigurationSchemas);
        }

        public Task<PackageConfigurationValuesResponse?> GetPackageConfigurationValuesAsync(
            string packageId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetPackageConfigurationValuesCallCount++;
            if (GetConfigurationValuesAsyncCallback is not null)
            {
                return GetConfigurationValuesAsyncCallback(packageId, cancellationToken);
            }

            ConfigurationValues.TryGetValue(packageId, out var values);
            return Task.FromResult(values);
        }

        public Task SavePackageConfigurationValuesAsync(
            string packageId,
            IReadOnlyDictionary<string, string?> values,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return SaveConfigurationValuesAsyncCallback?.Invoke(packageId, values, cancellationToken) ?? Task.CompletedTask;
        }

        public Task<PackageAuthStatusResponse?> GetPackageAuthStatusAsync(
            string packageId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PackageAuthSessionStartResponse?> StartPackageAuthAsync(
            string packageId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PackageAuthSessionStatusResponse?> GetPackageAuthSessionStatusAsync(
            string packageId,
            string authSessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PackageAuthStatusResponse?> DisconnectPackageAuthAsync(
            string packageId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReportPackageFaultAsync(
            string packageId,
            PackageFailureOrigin origin,
            string message,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ShutdownAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
