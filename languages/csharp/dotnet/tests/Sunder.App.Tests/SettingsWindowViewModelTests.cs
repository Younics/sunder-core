using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.App.Views.Controls;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using static Sunder.App.Tests.TestSupport.AsyncAssert;
using Xunit;

namespace Sunder.App.Tests;

public sealed class SettingsWindowViewModelTests
{
    [Fact]
    public async Task HostedSettings_DirectSelectionAndRestorationNavigateExactlyOnceEach()
    {
        using var runtimeClient = new FakeRuntimeApiClient();
        var probe = new SettingsNavigationProbe();
        await using var packageViewHostService = CreateHostedSettingsViewHost(probe);
        using var viewModel = new SettingsWindowViewModel(
            runtimeClient,
            packageViewHostService,
            new CliInstallationService());
        await viewModel.RefreshPackageSectionsAsync();

        await viewModel.SelectSectionAsync(Assert.Single(viewModel.PackageSettings.PackageSections));

        var directContext = Assert.Single(probe.Contexts);
        Assert.Equal("settings:agent", directContext.ViewId);
        Assert.Empty(directContext.Parameters);
        Assert.Equal(0, probe.DataContextNavigationCount);

        viewModel.DetachHostedPackageSettingsView();
        await viewModel.RefreshPackageSectionsAsync();

        Assert.Equal(2, probe.Contexts.Count);
        Assert.Equal(2, probe.PresentedCount);
        Assert.Equal(0, probe.DataContextNavigationCount);
    }

    [Fact]
    public async Task HostedSettings_PreparesInHiddenNonInteractiveSlotBeforePresentation()
    {
        using var runtimeClient = new FakeRuntimeApiClient();
        var preparationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new SettingsNavigationProbe
        {
            NavigateAsync = async (_, cancellationToken) =>
            {
                preparationStarted.TrySetResult();
                await releasePreparation.Task.WaitAsync(cancellationToken);
            },
        };
        await using var packageViewHostService = CreateHostedSettingsViewHost(probe);
        using var viewModel = new SettingsWindowViewModel(
            runtimeClient,
            packageViewHostService,
            new CliInstallationService());
        await viewModel.RefreshPackageSectionsAsync();

        var selection = viewModel.SelectPackageSettingsAsync("agent");
        await preparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(viewModel.HostedSettingsView);
        Assert.NotNull(viewModel.StagedHostedSettingsView);
        Assert.True(viewModel.IsCoreSelection);
        Assert.Equal("Appearance", viewModel.SelectedTitle);
        Assert.Equal(0, probe.PresentedCount);

        releasePreparation.TrySetResult();
        Assert.True(await selection);

        Assert.Null(viewModel.StagedHostedSettingsView);
        Assert.NotNull(viewModel.HostedSettingsView);
        Assert.Equal(1, probe.PresentedCount);
    }

    [Fact]
    public async Task HostedSettings_RejectedPreparationKeepsCurrentDestinationPresented()
    {
        using var runtimeClient = new FakeRuntimeApiClient();
        var currentProbe = new SettingsNavigationProbe();
        var rejectedProbe = new SettingsNavigationProbe { PrepareResult = false };
        await using var packageViewHostService = CreateHostedSettingsViewHost(
            ("agent", currentProbe),
            ("tools", rejectedProbe));
        using var viewModel = new SettingsWindowViewModel(
            runtimeClient,
            packageViewHostService,
            new CliInstallationService());
        await viewModel.RefreshPackageSectionsAsync();
        Assert.True(await viewModel.SelectPackageSettingsAsync("agent"));
        var currentView = viewModel.HostedSettingsView;
        var currentTitle = viewModel.SelectedTitle;

        Assert.False(await viewModel.SelectPackageSettingsAsync("tools"));

        Assert.Same(currentView, viewModel.HostedSettingsView);
        Assert.Null(viewModel.StagedHostedSettingsView);
        Assert.Equal(currentTitle, viewModel.SelectedTitle);
        Assert.True(viewModel.PackageSettings.FindSection("agent")!.IsSelected);
        Assert.False(viewModel.PackageSettings.FindSection("tools")!.IsSelected);
        Assert.Equal(1, currentProbe.PresentedCount);
        Assert.Equal(0, rejectedProbe.PresentedCount);
        Assert.Contains("rejected navigation", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HostedSettings_SamePackageRejectionDoesNotPrepareOrMutatePresentedInstance()
    {
        using var runtimeClient = new FakeRuntimeApiClient();
        var probe = new SettingsNavigationProbe();
        await using var packageViewHostService = CreateHostedSettingsViewHost(probe);
        using var viewModel = new SettingsWindowViewModel(
            runtimeClient,
            packageViewHostService,
            new CliInstallationService());
        await viewModel.RefreshPackageSectionsAsync();
        Assert.True(await viewModel.SelectPackageSettingsAsync(
            "agent",
            new Dictionary<string, string?> { ["target"] = "accepted" }));
        var presentedBoundary = Assert.IsType<HostedPackageViewBoundary>(viewModel.HostedSettingsView);
        var presentedView = Assert.IsType<SettingsNavigationView>(presentedBoundary.HostedView);
        probe.PrepareResult = false;

        Assert.False(await viewModel.SelectPackageSettingsAsync(
            "agent",
            new Dictionary<string, string?> { ["target"] = "rejected" }));

        var retainedBoundary = Assert.IsType<HostedPackageViewBoundary>(viewModel.HostedSettingsView);
        var rejectedView = Assert.Single(probe.Views, view => !ReferenceEquals(view, presentedView));
        Assert.Same(presentedBoundary, retainedBoundary);
        Assert.Same(presentedView, retainedBoundary.HostedView);
        Assert.Equal(1, presentedView.PrepareCount);
        Assert.Equal("accepted", presentedView.PreparedTarget);
        Assert.Equal("rejected", rejectedView.PreparedTarget);
        Assert.True(rejectedView.IsDisposed);
        Assert.False(presentedView.IsDisposed);
    }

    [Fact]
    public async Task HostedSettings_SamePackageSupersessionPresentsOnlyLatestPreparedCandidate()
    {
        using var runtimeClient = new FakeRuntimeApiClient();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new SettingsNavigationProbe();
        probe.PrepareAsync = async (_, context, cancellationToken) =>
        {
            if (!string.Equals(context.Parameters.GetValueOrDefault("target"), "first", StringComparison.Ordinal))
            {
                return;
            }

            firstStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };
        await using var packageViewHostService = CreateHostedSettingsViewHost(probe);
        using var viewModel = new SettingsWindowViewModel(
            runtimeClient,
            packageViewHostService,
            new CliInstallationService());
        await viewModel.RefreshPackageSectionsAsync();
        Assert.True(await viewModel.SelectPackageSettingsAsync(
            "agent",
            new Dictionary<string, string?> { ["target"] = "initial" }));
        var initialView = Assert.IsType<SettingsNavigationView>(
            Assert.IsType<HostedPackageViewBoundary>(viewModel.HostedSettingsView).HostedView);

        var superseded = viewModel.SelectPackageSettingsAsync(
            "agent",
            new Dictionary<string, string?> { ["target"] = "first" });
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var latest = viewModel.SelectPackageSettingsAsync(
            "agent",
            new Dictionary<string, string?> { ["target"] = "latest" });

        Assert.False(await superseded.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await latest.WaitAsync(TimeSpan.FromSeconds(2)));

        var latestView = Assert.IsType<SettingsNavigationView>(
            Assert.IsType<HostedPackageViewBoundary>(viewModel.HostedSettingsView).HostedView);
        Assert.Equal("latest", latestView.PreparedTarget);
        Assert.NotSame(initialView, latestView);
        Assert.True(initialView.IsDisposed);
        Assert.Equal(3, probe.Views.Count);
        Assert.Single(probe.Views, view => !view.IsDisposed);
        Assert.Equal(2, probe.PresentedCount);
    }

    [Fact]
    public async Task SelectPackageSettingsAsync_SnapshotsParametersAndCancelsSupersededNavigation()
    {
        using var runtimeClient = new FakeRuntimeApiClient();
        var probe = new SettingsNavigationProbe();
        await using var packageViewHostService = CreateHostedSettingsViewHost(probe);
        using var viewModel = new SettingsWindowViewModel(
            runtimeClient,
            packageViewHostService,
            new CliInstallationService());
        await viewModel.RefreshPackageSectionsAsync();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        probe.NavigateAsync = async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref callCount) != 1)
            {
                return;
            }

            firstStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                firstCancelled.SetResult();
                throw;
            }
        };
        var parameters = new Dictionary<string, string?> { ["workspace"] = "original" };

        var firstSelection = viewModel.SelectPackageSettingsAsync("agent", parameters);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        parameters["workspace"] = "changed";
        var secondSelection = viewModel.SelectPackageSettingsAsync("agent");

        await firstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(await firstSelection);
        Assert.True(await secondSelection);
        Assert.Equal(2, probe.Contexts.Count);
        Assert.Equal("original", probe.Contexts[0].Parameters["workspace"]);
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IDictionary<string, string?>>(probe.Contexts[0].Parameters)["workspace"] = "mutated");
        Assert.Equal(0, probe.DataContextNavigationCount);
    }

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
            viewModel.PackageSettings.PackageSections,
            section => Assert.Equal("agent", section.PackageId));

        runtimeClient.ConfigurationSchemas =
        [
            CreateSchema("agent", "Agent"),
            CreateSchema("tools", "Tools"),
        ];

        await viewModel.RefreshPackageSectionsAsync();

        Assert.Collection(
            viewModel.PackageSettings.PackageSections,
            section => Assert.Equal("agent", section.PackageId),
            section => Assert.Equal("tools", section.PackageId));
        Assert.True(viewModel.PackageSettings.HasPackageSections);
    }

    [Fact]
    public async Task RefreshPackageSectionsAsync_WhenEarlierRefreshCompletesLater_AppliesLatestOnly()
    {
        var staleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        using var runtimeClient = new FakeRuntimeApiClient
        {
            GetConfigurationSchemasAsyncCallback = async cancellationToken =>
            {
                var call = Interlocked.Increment(ref callCount);
                if (call == 1)
                {
                    return [];
                }

                if (call == 2)
                {
                    staleStarted.SetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        staleCancelled.SetResult();
                        throw;
                    }
                }

                return [CreateSchema("tools", "Tools")];
            },
        };
        using var viewModel = CreateViewModel(runtimeClient);
        await WaitForConditionAsync(() => Volatile.Read(ref callCount) == 1);

        var staleRefresh = viewModel.RefreshPackageSectionsAsync();
        await staleStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var latestRefresh = viewModel.RefreshPackageSectionsAsync();

        await staleCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.WhenAll(staleRefresh, latestRefresh);

        var section = Assert.Single(viewModel.PackageSettings.PackageSections);
        Assert.Equal("tools", section.PackageId);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task RefreshPackageSectionsAsync_PreservesSelectedPackageFields()
    {
        using var runtimeClient = new FakeRuntimeApiClient
        {
            ConfigurationSchemas = [CreateSchema("agent", "Agent", "Configure Agent.")],
        };
        runtimeClient.ConfigurationValues["agent"] = new PackageSettingsValuesResponse(
            "agent",
            new Dictionary<string, string?> { ["apiKey"] = "stored" },
            []);
        using var viewModel = CreateViewModel(runtimeClient);

        await viewModel.RefreshPackageSectionsAsync();
        Assert.True(await viewModel.SelectPackageSettingsAsync("agent"));
        var field = Assert.IsType<TextSettingsFieldViewModel>(Assert.Single(Assert.Single(viewModel.PackageSettings.SelectedPackageSections).Fields));
        field.Value = "unsaved";

        runtimeClient.ConfigurationSchemas = [CreateSchema("agent", "Agent Updated", "Updated summary.")];

        await viewModel.RefreshPackageSectionsAsync();

        Assert.Equal("Agent Updated", viewModel.SelectedTitle);
        Assert.Equal("Updated summary.", viewModel.SelectedDescription);
        Assert.Equal(1, runtimeClient.GetPackageConfigurationValuesCallCount);
        Assert.True(Assert.Single(viewModel.PackageSettings.PackageSections).IsSelected);
        var refreshedField = Assert.IsType<TextSettingsFieldViewModel>(Assert.Single(Assert.Single(viewModel.PackageSettings.SelectedPackageSections).Fields));
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
        Assert.Empty(viewModel.PackageSettings.PackageSections);
        Assert.Empty(viewModel.PackageSettings.SelectedPackageSections);
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

                return new PackageSettingsValuesResponse(
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
        var section = Assert.Single(viewModel.PackageSettings.SelectedPackageSections);
        var field = Assert.IsType<TextSettingsFieldViewModel>(Assert.Single(section.Fields));
        Assert.Equal("tools", field.Value);
    }

    [Fact]
    public async Task SelectPackageSettingsAsync_WhenHostedNavigationFails_PreservesUsefulErrorText()
    {
        using var runtimeClient = new FakeRuntimeApiClient();
        var probe = new SettingsNavigationProbe
        {
            NavigateAsync = (_, _) => ValueTask.FromException(new InvalidOperationException("Navigation target failed.")),
        };
        await using var packageViewHostService = CreateHostedSettingsViewHost(probe);
        using var viewModel = new SettingsWindowViewModel(
            runtimeClient,
            packageViewHostService,
            new CliInstallationService());
        await viewModel.RefreshPackageSectionsAsync();

        var selected = await viewModel.SelectPackageSettingsAsync("agent");

        Assert.False(selected);
        Assert.Equal("Appearance", viewModel.SelectedTitle);
        Assert.Contains("Navigation target failed.", viewModel.StatusText, StringComparison.Ordinal);
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
        Assert.Empty(viewModel.PackageSettings.PackageSections);
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

    private static PackageViewHostService CreateHostedSettingsViewHost(SettingsNavigationProbe probe)
        => CreateHostedSettingsViewHost(("agent", probe));

    private static PackageViewHostService CreateHostedSettingsViewHost(
        params (string PackageId, SettingsNavigationProbe Probe)[] registrations)
    {
        var registry = new AppPackageViewRegistry();
        var providers = registrations.Select(registration =>
        {
            var provider = new ServiceCollection()
                .AddSingleton(registration.Probe)
                .BuildServiceProvider();
            registry.RegisterSettingsView<SettingsNavigationView>(registration.PackageId, provider);
            return provider;
        }).ToArray();
        return new PackageViewHostService(
            registry,
            [],
            providers,
            [],
            sessionFolder: null,
            uiDispatcher: new ImmediateUiDispatcher());
    }

    private static PackageSettingsSchemaDescriptor CreateSchema(
        string packageId,
        string displayName,
        string? summary = null) =>
        new(
            packageId,
            displayName,
            summary,
            [
                new PackageSettingsSectionDescriptor(
                    "general",
                    "General",
                    null,
                    [
                        new PackageSettingsFieldDescriptor(
                            "apiKey",
                            "API key",
                            PackageSettingsFieldKind.Text,
                            null,
                            false,
                            null,
                            null,
                            []),
                    ]),
            ]);

    private sealed class FakeRuntimeApiClient : IRuntimePackageSettingsClient
    {
        public IReadOnlyList<PackageSettingsSchemaDescriptor> ConfigurationSchemas { get; set; } = [];

        public Dictionary<string, PackageSettingsValuesResponse?> ConfigurationValues { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Func<CancellationToken, Task<IReadOnlyList<PackageSettingsSchemaDescriptor>>>? GetConfigurationSchemasAsyncCallback { get; init; }

        public Func<string, CancellationToken, Task<PackageSettingsValuesResponse?>>? GetConfigurationValuesAsyncCallback { get; init; }

        public Func<string, IReadOnlyDictionary<string, string?>, CancellationToken, Task>? SaveConfigurationValuesAsyncCallback { get; init; }

        public int GetPackageConfigurationValuesCallCount { get; private set; }

        public Task<IReadOnlyList<PackageSettingsSchemaDescriptor>> GetPackageSettingsSchemasAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetConfigurationSchemasAsyncCallback is not null)
            {
                return GetConfigurationSchemasAsyncCallback(cancellationToken);
            }

            return Task.FromResult(ConfigurationSchemas);
        }

        public Task<PackageSettingsValuesResponse?> GetPackageSettingsValuesAsync(
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

        public Task SavePackageSettingsValuesAsync(
            string packageId,
            IReadOnlyDictionary<string, string?> values,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return SaveConfigurationValuesAsyncCallback?.Invoke(packageId, values, cancellationToken) ?? Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class SettingsNavigationView : Control,
        IPackageViewNavigationTarget,
        IPackageViewNavigationPreparationTarget,
        IDisposable
    {
        private readonly SettingsNavigationProbe _probe;

        public SettingsNavigationView(SettingsNavigationProbe probe)
        {
            _probe = probe;
            _probe.Views.Add(this);
            DataContext = new SettingsNavigationDataContext(probe);
        }

        public int PrepareCount { get; private set; }

        public string? PreparedTarget { get; private set; }

        public bool IsDisposed { get; private set; }

        public async ValueTask OnNavigatedToAsync(
            PackageViewNavigationContext context,
            CancellationToken cancellationToken = default)
        {
            _probe.Contexts.Add(context);
            await _probe.NavigateAsync(context, cancellationToken);
        }

        public async ValueTask<bool> PrepareNavigationAsync(
            PackageViewNavigationContext context,
            CancellationToken cancellationToken = default)
        {
            PrepareCount++;
            PreparedTarget = context.Parameters.GetValueOrDefault("target");
            _probe.Contexts.Add(context);
            if (_probe.PrepareAsync is not null)
            {
                await _probe.PrepareAsync(this, context, cancellationToken);
            }
            else
            {
                await _probe.NavigateAsync(context, cancellationToken);
            }
            return _probe.PrepareResult;
        }

        public ValueTask OnNavigationPresentedAsync(
            PackageViewNavigationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _probe.PresentedCount++;
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            IsDisposed = true;
            DataContext = null;
        }
    }

    private sealed class SettingsNavigationDataContext(SettingsNavigationProbe probe) : IPackageViewNavigationTarget
    {
        public ValueTask OnNavigatedToAsync(
            PackageViewNavigationContext context,
            CancellationToken cancellationToken = default)
        {
            probe.DataContextNavigationCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SettingsNavigationProbe
    {
        public List<PackageViewNavigationContext> Contexts { get; } = [];

        public int DataContextNavigationCount { get; set; }

        public int PresentedCount { get; set; }

        public bool PrepareResult { get; set; } = true;

        public List<SettingsNavigationView> Views { get; } = [];

        public Func<SettingsNavigationView, PackageViewNavigationContext, CancellationToken, ValueTask>? PrepareAsync { get; set; }

        public Func<PackageViewNavigationContext, CancellationToken, ValueTask> NavigateAsync { get; set; }
            = static (_, _) => ValueTask.CompletedTask;
    }

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task InvokeAsync(Func<Task> action) => action();

        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());

        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    }
}
