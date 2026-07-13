using Sunder.App.Models;
using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class PresentationLifecycleArchitectureTests
{
    [Theory]
    [InlineData("PackagesWindowViewModel")]
    [InlineData("StacksWindowViewModel")]
    [InlineData("CreateStackWizardViewModel")]
    [InlineData("UseStackWizardViewModel")]
    [InlineData("SettingsWindowViewModel")]
    [InlineData("MainWindowViewModel")]
    public void HotspotFamilies_StayBelowFileSizeRatchet(string familyName)
    {
        var directory = Path.Combine(GetRepositoryRoot(), "src", "Host", "Sunder.App", "ViewModels");
        var files = Directory.EnumerateFiles(directory, familyName + "*.cs").OrderBy(path => path).ToArray();

        Assert.NotEmpty(files);
        Assert.All(files, path =>
            Assert.True(
                File.ReadLines(path).Count() < 675,
                $"{Path.GetFileName(path)} exceeded the 675-line presentation ratchet."));
    }

    [Fact]
    public void StacksWindow_OwnsPresentationOnly_NotCoordinatorLifecycleState()
    {
        var directory = Path.Combine(GetRepositoryRoot(), "src", "Host", "Sunder.App", "ViewModels");
        var source = string.Join('\n', Directory.EnumerateFiles(directory, "StacksWindowViewModel*.cs").Select(File.ReadAllText));

        Assert.DoesNotContain("_selectedStackCancellation", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_registryStackDetailsCancellation", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_selectedImportPlanId", source, StringComparison.Ordinal);
        Assert.Contains("StackLibraryCoordinator", source, StringComparison.Ordinal);
        Assert.Contains("StackSelectionCoordinator", source, StringComparison.Ordinal);
        Assert.Contains("StackDetailLoadCoordinator", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StackImportPresentationCoordinator", source, StringComparison.Ordinal);
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
        var registryClient = File.ReadAllText(Path.Combine(services, "RegistryApiClient.cs"));

        Assert.Contains("MaxLaunchPayloadCharacters", singleInstance, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadToEndAsync", singleInstance, StringComparison.Ordinal);
        Assert.Contains("BoundedImageContentLoader.LoadAsync", asyncImages, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAsByteArrayAsync", asyncImages, StringComparison.Ordinal);
        Assert.Contains("BoundedHttpContentReader", runtimeClient, StringComparison.Ordinal);
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
        var runtimeFiles = Directory.GetFiles(services, "RuntimeApiClient*.cs")
            .Where(path => !path.EndsWith("RuntimeApiClientFactory.cs", StringComparison.Ordinal))
            .ToArray();

        Assert.All(runtimeFiles, path => Assert.True(
            File.ReadLines(path).Count() < 500,
            $"{Path.GetFileName(path)} exceeded the 500-line Runtime client ratchet."));

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

        public void ShowSettings() => SettingsShown = true;

        public Task<bool> ShowPackageSettingsAsync(string packageId, IReadOnlyDictionary<string, string?>? parameters = null, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public void ShowPackages() { }

        public void ShowStacks() { }

        public void ShowDeveloperLogs() { }

        public void CloseForShutdown() { }
    }
}
