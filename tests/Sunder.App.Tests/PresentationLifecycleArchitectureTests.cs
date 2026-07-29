using Sunder.App.Models;
using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class PresentationLifecycleArchitectureTests
{
    [Fact]
    public void PackagePresentation_OwnsCatalogsAndUsesOneExecutorPort()
    {
        var viewModelType = typeof(Sunder.App.ViewModels.PackagesWindowViewModel);
        Assert.Equal(typeof(Sunder.App.ViewModels.InstalledPackagesPaneViewModel), viewModelType.GetProperty("Installed")?.PropertyType);
        Assert.Equal(typeof(Sunder.App.ViewModels.MarketplacePackagesPaneViewModel), viewModelType.GetProperty("Marketplace")?.PropertyType);
        Assert.Equal(typeof(Sunder.App.ViewModels.PackageOperationPresentationViewModel), viewModelType.GetProperty("Operations")?.PropertyType);

        var constructor = Assert.Single(viewModelType.GetConstructors(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
        var executorParameter = Assert.Single(constructor.GetParameters(), parameter => parameter.ParameterType == typeof(IPackageOperationExecutor));
        Assert.False(executorParameter.HasDefaultValue);
        Assert.True(typeof(IPackageOperationExecutor).IsAssignableFrom(typeof(PackageOperationService)));
    }

    [Fact]
    public void StacksWindow_OwnsLocalRegistryAndPublishingComponents()
    {
        var type = typeof(Sunder.App.ViewModels.StacksWindowViewModel);
        Assert.Equal(typeof(Sunder.App.ViewModels.LocalStacksViewModel), type.GetProperty("Local")?.PropertyType);
        Assert.Equal(typeof(Sunder.App.ViewModels.RegistryStacksViewModel), type.GetProperty("Registry")?.PropertyType);
        Assert.Contains(
            type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            field => field.FieldType == typeof(Sunder.App.ViewModels.StackPublishingController));
        Assert.DoesNotContain(
            type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            field => field.FieldType == typeof(Sunder.App.ViewModels.StackLibraryCoordinator));
    }

    [Fact]
    public void WindowLauncher_HasNoFallbackWindowConstruction()
    {
        var path = Path.Combine(GetRepositoryRoot(), "src", "Host", "Sunder.App", "Services", "WindowLauncher.cs");
        var source = File.ReadAllText(path);

        Assert.DoesNotContain("?? new SettingsWindow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("?? new PackagesWindow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("?? new StacksWindow", source, StringComparison.Ordinal);
        Assert.Contains("_settingsWindowFactory.Create", source, StringComparison.Ordinal);
        Assert.Contains("_packagesWindowFactory.Create", source, StringComparison.Ordinal);
        Assert.Contains("_stacksWindowFactory.Create", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageCatalogSources_UseSharedProjectionPipeline()
    {
        var directory = Path.Combine(GetRepositoryRoot(), "src", "Host", "Sunder.App", "ViewModels");
        var installed = File.ReadAllText(Path.Combine(directory, "InstalledPackageCatalogProjector.cs"));
        var marketplace = File.ReadAllText(Path.Combine(directory, "MarketplacePackageSearchProjector.cs"));

        Assert.Contains("PackageCatalogProjection.Project", installed, StringComparison.Ordinal);
        Assert.Contains("PackageCatalogProjection.Project", marketplace, StringComparison.Ordinal);
        Assert.Contains("PackageCatalogInstallationIndex", installed, StringComparison.Ordinal);
        Assert.Contains("PackageCatalogInstallationIndex", marketplace, StringComparison.Ordinal);
    }

    [Fact]
    public void PresentationHotspots_HaveNoUnownedMutationPatterns()
    {
        var directory = Path.Combine(GetRepositoryRoot(), "src", "Host", "Sunder.App", "ViewModels");
        var families = new[]
        {
            "PackagesWindowViewModel",
            "SettingsWindowViewModel",
            "StacksWindowViewModel",
            "UseStackWizardViewModel",
            "MainWindowViewModel",
        };
        var source = string.Join('\n', families.SelectMany(family =>
            Directory.EnumerateFiles(directory, family + "*.cs").Select(File.ReadAllText)));

        Assert.DoesNotContain("CancellationToken.None", source, StringComparison.Ordinal);
        Assert.DoesNotContain("async void", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = ", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowView_DelegatesLayoutPolicyToPresentationComponents()
    {
        var path = Path.Combine(GetRepositoryRoot(), "src", "Host", "Sunder.App", "Views", "MainWindow.axaml.cs");
        var source = File.ReadAllText(path);

        Assert.DoesNotContain("CalculateTopColumnWidths", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CalculateResizableExtent", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellPanels_KeepRetainedViewsHostedAndStageWithoutPresentingThem()
    {
        var controls = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "Host",
            "Sunder.App",
            "Views",
            "Controls");
        var panelHost = File.ReadAllText(Path.Combine(controls, "ShellPanelHost.axaml"));
        var workspace = File.ReadAllText(Path.Combine(controls, "ShellWorkspace.axaml"));

        Assert.Contains("ItemsSource=\"{Binding HostedViews}\"", panelHost, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsLayoutVisible}\"", panelHost, StringComparison.Ordinal);
        Assert.Contains("Opacity=\"{Binding PresentationOpacity}\"", panelHost, StringComparison.Ordinal);
        Assert.Contains(
            "IsHitTestVisible=\"{Binding IsPresentationHitTestVisible}\"",
            panelHost,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding MiddlePanel.HostedViews}\"",
            workspace,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsVisible=\"{Binding IsLayoutVisible}\"",
            workspace,
            StringComparison.Ordinal);
        Assert.Contains(
            "Opacity=\"{Binding PresentationOpacity}\"",
            workspace,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsHitTestVisible=\"{Binding IsPresentationHitTestVisible}\"",
            workspace,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ShellPanels_DoNotAnimateRetainedContentLifecycle()
    {
        var controls = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "Host",
            "Sunder.App",
            "Views",
            "Controls");
        var source = string.Join(
            '\n',
            File.ReadAllText(Path.Combine(controls, "ShellPanelHost.axaml")),
            File.ReadAllText(Path.Combine(controls, "ShellWorkspace.axaml")));

        Assert.DoesNotContain("DoubleTransition", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ContentOpacity", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ContentOffset", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Transition Property=\"Width\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Transition Property=\"Height\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Transition Property=\"Margin\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GridLengthTransition", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellPostPresentationWork_RunsBelowInputPriority()
    {
        var path = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "Host",
            "Sunder.App",
            "Services",
            "ShellUiWorkScheduler.cs");
        var source = File.ReadAllText(path);

        Assert.Contains("Func<CancellationToken, Task> work", source, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.UIThread.InvokeAsync", source, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.Background", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcherPriority.Render", source, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidateVisual", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MacNativeMenuRefresh_MarshalsBeforeStateAccessAndCoalesces()
    {
        var path = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "Host",
            "Sunder.App",
            "Views",
            "MacNativeMenuController.cs");
        var source = File.ReadAllText(path);

        var eventHandlerStart = source.IndexOf(
            "private void ViewModel_OnShellViewStateChanged()",
            StringComparison.Ordinal);
        var schedulerStart = source.IndexOf(
            "private void ScheduleMenuRefresh()",
            StringComparison.Ordinal);
        var schedulerEnd = source.IndexOf(
            "private void UpdateMenuIfDirty()",
            StringComparison.Ordinal);
        Assert.True(eventHandlerStart >= 0 && schedulerStart > eventHandlerStart);
        Assert.True(schedulerEnd > schedulerStart);
        var eventHandler = source[eventHandlerStart..schedulerStart];
        var scheduler = source[schedulerStart..schedulerEnd];

        Assert.Contains("if (!Dispatcher.UIThread.CheckAccess())", eventHandler, StringComparison.Ordinal);
        Assert.Contains(
            "Dispatcher.UIThread.Post(ScheduleMenuRefresh, DispatcherPriority.Normal)",
            eventHandler,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_menuDirty", eventHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("_menuRefreshScheduled", eventHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("_disposed", eventHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("_rootMenu", eventHandler, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.UIThread.VerifyAccess()", scheduler, StringComparison.Ordinal);
        Assert.Contains("if (_disposed)", scheduler, StringComparison.Ordinal);
        Assert.Contains("_menuDirty = true", scheduler, StringComparison.Ordinal);
        Assert.Contains(
            "if (_rootMenu is null || _menuRefreshScheduled)",
            scheduler,
            StringComparison.Ordinal);
        Assert.Contains("_menuRefreshScheduled = true", scheduler, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.Background", scheduler, StringComparison.Ordinal);
        Assert.Contains("_menuRefreshScheduled = false", scheduler, StringComparison.Ordinal);
        Assert.Contains("UpdateMenuIfDirty();", scheduler, StringComparison.Ordinal);
        Assert.Contains("_rootMenu.NeedsUpdate += Menu_OnNeedsUpdate", source, StringComparison.Ordinal);
        Assert.Contains("Bitmap.DecodeToWidth(", source, StringComparison.Ordinal);
        Assert.Contains("NativeMenuIconSize", source, StringComparison.Ordinal);
        Assert.Contains("CreateScaledBitmap(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MacMainWindowLifecycle_CoordinatesCloseAndReopensFromDock()
    {
        var appPath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "Host",
            "Sunder.App",
            "App.axaml.cs");
        var appSource = File.ReadAllText(appPath);
        var launcherPath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "Host",
            "Sunder.App",
            "Services",
            "WindowLauncher.cs");
        var launcherSource = File.ReadAllText(launcherPath);
        var mainWindowPath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "Host",
            "Sunder.App",
            "Views",
            "MainWindow.axaml.cs");
        var mainWindowSource = File.ReadAllText(mainWindowPath);
        var shellSessionPath = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "Host",
            "Sunder.App",
            "Services",
            "ShellSession.cs");
        var shellSessionSource = File.ReadAllText(shellSessionPath);

        Assert.Contains("ShutdownMode.OnExplicitShutdown", appSource, StringComparison.Ordinal);
        Assert.Contains("TryGetFeature<IActivatableLifetime>()", appSource, StringComparison.Ordinal);
        Assert.Contains("e.Kind == ActivationKind.Reopen", appSource, StringComparison.Ordinal);
        Assert.Contains(
            "session.WindowLauncher.ActivateMainWindow();",
            appSource,
            StringComparison.Ordinal);
        Assert.Contains("WindowCloseToHideCoordinator", mainWindowSource, StringComparison.Ordinal);
        Assert.Contains("MainWindow.HidingForClose +=", shellSessionSource, StringComparison.Ordinal);
        Assert.Contains("CloseAboutSunderWindow();", shellSessionSource, StringComparison.Ordinal);
        Assert.Contains("internal void ActivateMainWindow()", launcherSource, StringComparison.Ordinal);
        Assert.Contains("ShowWindow(mainWindow);", launcherSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SettingsWindow.axaml.cs")]
    [InlineData("PackagesWindow.axaml.cs")]
    [InlineData("StacksWindow.axaml.cs")]
    [InlineData("DeveloperLogWindow.axaml.cs")]
    public void CachedSecondaryWindows_UseCentralCloseToHideCoordinator(string fileName)
    {
        var path = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "Host",
            "Sunder.App",
            "Views",
            fileName);
        var source = File.ReadAllText(path);

        Assert.Contains("WindowCloseToHideCoordinator", source, StringComparison.Ordinal);
        Assert.Contains("hideOnClose: true", source, StringComparison.Ordinal);
        Assert.Contains("_closeCoordinator.CloseForShutdown()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void InitialShellReveal_WaitsForNativeComposition()
    {
        var path = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "Host",
            "Sunder.App",
            "Services",
            "InitialShellRenderWaiter.cs");
        var source = File.ReadAllText(path);

        Assert.Contains("RequestAnimationFrame", source, StringComparison.Ordinal);
        Assert.Contains("RequestCompositionBatchCommitAsync", source, StringComparison.Ordinal);
        Assert.Contains(".Rendered.WaitAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellStateUpdates_FromConcurrentSnapshots_PreserveBothWrites()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var service = new ShellStateService(Path.Combine(root, "shell-state.json"));
        var packagesWindow = service.Load();
        var stacksWindow = service.Load();

        service.Update(packagesWindow, state => state.PackagesSidebarWidth = 512);
        service.Update(stacksWindow, state => state.StacksSidebarWidth = 444);

        var persisted = service.Load();
        Assert.Equal(512, persisted.PackagesSidebarWidth);
        Assert.Equal(444, persisted.StacksSidebarWidth);
        Assert.True(persisted.Revision >= 2);
    }

    [Fact]
    public void ShellStateSave_RejectsStaleWholeStateSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-tests", Guid.NewGuid().ToString("N"));
        var service = new ShellStateService(Path.Combine(root, "shell-state.json"));
        var stale = service.Load();
        var current = service.Load();
        service.Update(current, state => state.SelectedMiddleViewId = "newer");

        stale.SelectedMiddleViewId = "stale";
        service.Save(stale);

        Assert.Equal("newer", service.Load().SelectedMiddleViewId);
    }

    [Fact]
    public void LatestRequest_ReplacementCancelsOldRequestAndGuardsCompletion()
    {
        using var owner = new LatestAsyncRequest();
        using var first = owner.Start();
        using var second = owner.Start();

        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(first.IsCurrent);
        Assert.True(second.IsCurrent);
        first.Complete();
        Assert.True(owner.IsBusy);
        second.Complete();
        Assert.False(owner.IsBusy);
    }

    [Fact]
    public void LatestRequest_DisposeCancelsCurrentRequest()
    {
        var owner = new LatestAsyncRequest();
        var request = owner.Start();

        owner.Dispose();

        Assert.True(request.Token.IsCancellationRequested);
        Assert.False(request.IsCurrent);
        Assert.False(owner.IsBusy);
        request.Dispose();
    }

    [Fact]
    public async Task SettingsNavigation_UsesInjectedDispatcher()
    {
        var dispatcher = new RecordingDispatcher();
        var launcher = new RecordingWindowLauncher();
        var service = new AppPackageSettingsNavigationService(dispatcher);
        service.Attach(launcher);

        Assert.True(await service.OpenSettingsAsync());

        Assert.Equal(1, dispatcher.InvocationCount);
        Assert.True(launcher.SettingsShown);
    }

    [Fact]
    public async Task SettingsNavigation_SnapshotsAndForwardsParametersBeforeDeferredDispatch()
    {
        var dispatcher = new DeferredDispatcher();
        var launcher = new RecordingWindowLauncher();
        var service = new AppPackageSettingsNavigationService(dispatcher);
        service.Attach(launcher);
        var parameters = new Dictionary<string, string?> { ["section"] = "original" };

        var open = service.OpenSettingsAsync(parameters).AsTask();
        await dispatcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        parameters["section"] = "mutated";
        dispatcher.Release.SetResult();

        Assert.True(await open);
        Assert.Equal("original", launcher.SettingsParameters?["section"]);
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IDictionary<string, string?>>(launcher.SettingsParameters)["section"] = "changed");
    }

    [Fact]
    public async Task PackageSettingsNavigation_SnapshotsParametersBeforeDeferredDispatch()
    {
        var dispatcher = new DeferredDispatcher();
        var launcher = new RecordingWindowLauncher();
        var service = new AppPackageSettingsNavigationService(dispatcher);
        service.Attach(launcher);
        var parameters = new Dictionary<string, string?> { ["workspace"] = "original" };

        var open = service.OpenPackageSettingsAsync("agent", parameters).AsTask();
        await dispatcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        parameters["workspace"] = "mutated";
        dispatcher.Release.SetResult();

        Assert.True(await open);
        Assert.Equal("agent", launcher.PackageSettingsId);
        Assert.Equal("original", launcher.PackageSettingsParameters?["workspace"]);
    }

    [Fact]
    public async Task PackageSettingsPublication_RechecksGenerationWhenDispatchedActionExecutes()
    {
        var dispatcher = new DeferredDispatcher();
        var launcher = new RecordingWindowLauncher();
        var target = new AppPackageSettingsNavigationService(dispatcher);
        target.Attach(launcher);
        var publication = new AppPackageGenerationPublication();
        var service = new AppPackagePublicationServices(
            shellViewService: null,
            settingsNavigationService: target,
            publication);
        publication.Publish();

        var open = service.OpenSettingsAsync().AsTask();
        await dispatcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        publication.Revoke();
        dispatcher.Release.SetResult();

        Assert.False(await open);
        Assert.False(launcher.SettingsShown);
    }

    [Fact]
    public async Task PackageIconLoad_PreCancelledRequestDoesNotStartImageWork()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PackageIconImageLoader.LoadAsync(new Uri("https://127.0.0.1:1/icon.png"), cancellation.Token));
    }

    [Fact]
    public void AppExternalPayloadReaders_AreBounded()
    {
        var services = Path.Combine(GetRepositoryRoot(), "src", "Host", "Sunder.App", "Services");
        var singleInstance = File.ReadAllText(Path.Combine(services, "AppSingleInstanceCoordinator.cs"));
        var asyncImages = File.ReadAllText(Path.Combine(services, "SunderAsyncImageLoader.cs"));
        var runtimeClient = File.ReadAllText(Path.Combine(services, "RuntimeApiClient.cs"));
        var runtimeResponseReader = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src",
            "Host",
            "Sunder.Runtime.Client",
            "RuntimeHttpResponseReader.cs"));
        var registryClient = File.ReadAllText(Path.Combine(services, "RegistryApiClient.cs"));

        Assert.Contains("MaxLaunchPayloadCharacters", singleInstance, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadToEndAsync", singleInstance, StringComparison.Ordinal);
        Assert.Contains("BoundedImageContentLoader.LoadAsync", asyncImages, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAsByteArrayAsync", asyncImages, StringComparison.Ordinal);
        Assert.Contains("RuntimeManagementClient", runtimeClient, StringComparison.Ordinal);
        Assert.Contains("ReadBoundedAsync", runtimeResponseReader, StringComparison.Ordinal);
        Assert.Contains("BoundedHttpContentReader", registryClient, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadFromJsonAsync", runtimeClient, StringComparison.Ordinal);
        Assert.DoesNotContain("GetFromJsonAsync", registryClient, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeClientInterface_DoesNotExposeRemovedSessionApplicationSwitchesOrPathHandles()
    {
        var source = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(), "src", "Host", "Sunder.App", "Services", "IRuntimeApiClient.cs"));

        Assert.DoesNotContain("applyRuntimeSession", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new ContentUploadDescriptor(packagePath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new ContentUploadDescriptor(stackPath", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientCapabilityInterfaces_HaveNoDefaultImplementations()
    {
        var interfaces = typeof(IRuntimeClient).Assembly.GetTypes()
            .Where(type => type.IsInterface
                           && (typeof(IRuntimeClient).IsAssignableFrom(type)
                               || typeof(IRegistryClient).IsAssignableFrom(type)))
            .ToArray();

        Assert.NotEmpty(interfaces);
        Assert.All(
            interfaces.SelectMany(type => type.GetMethods(
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly)),
            method => Assert.True(method.IsAbstract, $"{method.DeclaringType?.Name}.{method.Name} has a default implementation."));
    }

    [Fact]
    public void RuntimeClientEndpointFamilies_StayReviewedAndConsumersAvoidTheComposite()
    {
        var services = Path.Combine(GetRepositoryRoot(), "src", "Host", "Sunder.App", "Services");
        var appDirectory = Path.Combine(GetRepositoryRoot(), "src", "Host", "Sunder.App");
        var broadDependencies = Directory.GetFiles(appDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("IRuntimeApiClient.cs", StringComparison.Ordinal)
                           && !path.Contains("RuntimeApiClient.", StringComparison.Ordinal)
                           && !path.EndsWith("RuntimeApiClient.cs", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("IRuntimeApiClient ", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(broadDependencies);

        var broadRegistryDependencies = Directory.GetFiles(appDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("IRegistryApiClient.cs", StringComparison.Ordinal)
                           && !path.EndsWith("RegistryApiClient.cs", StringComparison.Ordinal)
                           && !path.EndsWith("RegistryStacksViewModel.cs", StringComparison.Ordinal)
                           && !path.EndsWith("StacksWindowViewModel.State.cs", StringComparison.Ordinal)
                           && !path.EndsWith("UseStackWizardViewModel.State.cs", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("IRegistryApiClient ", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(broadRegistryDependencies);

        var testDirectory = Path.Combine(GetRepositoryRoot(), "tests", "Sunder.App.Tests");
        var broadTestDoubles = Directory.GetFiles(testDirectory, "*.cs")
            .Where(path => System.Text.RegularExpressions.Regex.IsMatch(
                File.ReadAllText(path),
                @":\s*IRuntimeApiClient(?:\s|$)"))
            .ToArray();
        Assert.Empty(broadTestDoubles);
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the Sunder Core repository root.");
    }

    private sealed class RecordingDispatcher : IUiDispatcher
    {
        public int InvocationCount { get; private set; }

        public bool CheckAccess() => false;

        public Task InvokeAsync(Action action)
        {
            InvocationCount++;
            action();
            return Task.CompletedTask;
        }

        public async Task InvokeAsync(Func<Task> action)
        {
            InvocationCount++;
            await action();
        }

        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            InvocationCount++;
            return Task.FromResult(action());
        }

        public async Task<T> InvokeAsync<T>(Func<Task<T>> action)
        {
            InvocationCount++;
            return await action();
        }
    }

    private sealed class RecordingWindowLauncher : IWindowLauncher
    {
        public bool SettingsShown { get; private set; }

        public IReadOnlyDictionary<string, string?>? SettingsParameters { get; private set; }

        public string? PackageSettingsId { get; private set; }

        public IReadOnlyDictionary<string, string?>? PackageSettingsParameters { get; private set; }

        public void ShowSettings(IReadOnlyDictionary<string, string?>? parameters = null)
        {
            SettingsShown = true;
            SettingsParameters = parameters;
        }

        public Task<bool> ShowPackageSettingsAsync(string packageId, IReadOnlyDictionary<string, string?>? parameters = null, CancellationToken cancellationToken = default)
        {
            PackageSettingsId = packageId;
            PackageSettingsParameters = parameters;
            return Task.FromResult(true);
        }

        public void ShowPackages() { }

        public void ShowStacks() { }

        public void ShowDeveloperLogs() { }

        public void CloseForShutdown() { }
    }

    private sealed class DeferredDispatcher : IUiDispatcher
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CheckAccess() => false;

        public Task InvokeAsync(Action action) => throw new NotSupportedException();

        public Task InvokeAsync(Func<Task> action) => throw new NotSupportedException();

        public async Task<T> InvokeAsync<T>(Func<T> action)
        {
            Started.SetResult();
            await Release.Task;
            return action();
        }

        public async Task<T> InvokeAsync<T>(Func<Task<T>> action)
        {
            Started.SetResult();
            await Release.Task;
            return await action();
        }
    }
}
