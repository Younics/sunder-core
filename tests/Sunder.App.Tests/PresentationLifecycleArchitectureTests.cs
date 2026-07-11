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
    public void HotspotFamilies_StayBelowFileSizeRatchet(string familyName)
    {
        var directory = Path.Combine(GetRepositoryRoot(), "src", "Host", "Sunder.App", "ViewModels");
        var files = Directory.EnumerateFiles(directory, familyName + "*.cs").OrderBy(path => path).ToArray();

        Assert.NotEmpty(files);
        Assert.All(files, path =>
            Assert.True(
                File.ReadLines(path).Count() < 700,
                $"{Path.GetFileName(path)} exceeded the 700-line presentation ratchet."));
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
