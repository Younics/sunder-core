using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Services;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPackageViewRegistryTests
{
    [Fact]
    public void GetOrCreateView_CachesViewsUntilPackageCacheIsRemoved()
    {
        var registry = new AppPackageViewRegistry();
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        registry.RegisterPackageView<TestPackageView>("test.package", "test.view", serviceProvider);

        var first = registry.GetOrCreateView("test.view", _ => false, ReportFailure);
        var second = registry.GetOrCreateView("test.view", _ => false, ReportFailure);
        registry.RemoveCachedViews("test.package");
        var third = registry.GetOrCreateView("test.view", _ => false, ReportFailure);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.NotSame(first, third);
    }

    [Fact]
    public void RegisteredViews_ResolveConstructorDependenciesFromPackageServices()
    {
        var dependency = new ConstructorDependency();
        var serviceProvider = new ServiceCollection()
            .AddSingleton(dependency)
            .BuildServiceProvider();
        var registry = new AppPackageViewRegistry();
        registry.RegisterPackageView<ConstructorInjectedPackageView>("test.package", "test.package.view", serviceProvider);
        registry.RegisterSettingsView<ConstructorInjectedPackageView>("test.package", serviceProvider);

        var packageView = Assert.IsType<ConstructorInjectedPackageView>(
            registry.GetOrCreateView("test.package.view", _ => false, ReportFailure));
        var settingsView = Assert.IsType<ConstructorInjectedPackageView>(
            registry.GetOrCreateSettingsView("test.package", _ => false, ReportFailure));

        Assert.Same(dependency, packageView.Dependency);
        Assert.Same(dependency, settingsView.Dependency);
    }

    [Fact]
    public void PackageViewContracts_AreImmutableSnapshots()
    {
        var sourceParameters = new Dictionary<string, string?> { ["item"] = "original" };
        var navigation = new PackageViewNavigationContext("test.package.view", sourceParameters);
        sourceParameters["item"] = "changed";

        Assert.Equal("original", navigation.Parameters["item"]);
        var parameters = Assert.IsAssignableFrom<IDictionary<string, string?>>(navigation.Parameters);
        Assert.Throws<NotSupportedException>(() => parameters["item"] = "changed");
        Assert.All(typeof(PackageViewRegistration).GetProperties(), property => Assert.False(property.CanWrite));
    }

    [Fact]
    public async Task ViewNavigation_SupersedingOrClosingViewCancelsCurrentPresentation()
    {
        var probe = new NavigationProbe();
        var serviceProvider = new ServiceCollection()
            .AddSingleton(probe)
            .BuildServiceProvider();
        var registry = new AppPackageViewRegistry();
        registry.RegisterPackageView<NavigationPackageView>("test.package", "test.package.view", serviceProvider);
        var facade = new AppPackageHostedViewFacade(registry, _ => false, ReportFailure);
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

        var firstNavigation = facade.NotifyViewNavigatedAsync("test.package.view", null, CancellationToken.None).AsTask();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await facade.NotifyViewNavigatedAsync("test.package.view", null, CancellationToken.None);

        await firstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstNavigation);

        var closingNavigationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.NavigateAsync = async (_, cancellationToken) =>
        {
            closingNavigationStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };
        var closingNavigation = facade.NotifyViewNavigatedAsync("test.package.view", null, CancellationToken.None).AsTask();
        await closingNavigationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        facade.CancelViewNavigation("test.package.view");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => closingNavigation);
    }

    [Fact]
    public void GetOrCreateView_WhenCachedViewExists_DoesNotCreateReplacement()
    {
        TrackedDataContext.Created.Clear();
        var registry = new AppPackageViewRegistry();
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        registry.RegisterPackageView<TrackedDataContextPackageView>("test.package", "test.view", serviceProvider);

        var first = registry.GetOrCreateView("test.view", _ => false, ReportFailure);
        var second = registry.GetOrCreateView("test.view", _ => false, ReportFailure);

        Assert.Same(first, second);
        Assert.Collection(
            TrackedDataContext.Created,
            dataContext => Assert.False(dataContext.IsDisposed));
    }

    [Fact]
    public void RemoveCachedViews_DisposesCachedViewDataContext()
    {
        var registry = new AppPackageViewRegistry();
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        registry.RegisterPackageView<DisposableDataContextPackageView>("test.package", "test.view", serviceProvider);

        var view = Assert.IsType<DisposableDataContextPackageView>(registry.GetOrCreateView("test.view", _ => false, ReportFailure));
        var dataContext = Assert.IsType<DisposableDataContext>(view.DataContext);

        registry.RemoveCachedViews("test.package");

        Assert.True(dataContext.IsDisposed);
    }

    [Fact]
    public void RemoveCachedView_DisposesOnlyRequestedCachedView()
    {
        var registry = new AppPackageViewRegistry();
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        registry.RegisterPackageView<DisposableDataContextPackageView>("test.package", "test.one", serviceProvider);
        registry.RegisterPackageView<DisposableDataContextPackageView>("test.package", "test.two", serviceProvider);

        var first = Assert.IsType<DisposableDataContextPackageView>(registry.GetOrCreateView("test.one", _ => false, ReportFailure));
        var second = Assert.IsType<DisposableDataContextPackageView>(registry.GetOrCreateView("test.two", _ => false, ReportFailure));
        var firstDataContext = Assert.IsType<DisposableDataContext>(first.DataContext);
        var secondDataContext = Assert.IsType<DisposableDataContext>(second.DataContext);

        var removed = registry.RemoveCachedView("test.one");
        var reloadedFirst = registry.GetOrCreateView("test.one", _ => false, ReportFailure);
        var cachedSecond = registry.GetOrCreateView("test.two", _ => false, ReportFailure);

        Assert.True(removed);
        Assert.True(firstDataContext.IsDisposed);
        Assert.False(secondDataContext.IsDisposed);
        Assert.NotSame(first, reloadedFirst);
        Assert.Same(second, cachedSecond);
    }

    [Fact]
    public void UnregisterPackage_DisposesCachedViewDataContext()
    {
        var registry = new AppPackageViewRegistry();
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        registry.RegisterPackageView<DisposableDataContextPackageView>("test.package", "test.view", serviceProvider);

        var view = Assert.IsType<DisposableDataContextPackageView>(registry.GetOrCreateView("test.view", _ => false, ReportFailure));
        var dataContext = Assert.IsType<DisposableDataContext>(view.DataContext);

        registry.UnregisterPackage("test.package");

        Assert.True(dataContext.IsDisposed);
    }

    [Fact]
    public void GetOrCreateView_DoesNotReturnCachedViewForDisabledPackage()
    {
        var registry = new AppPackageViewRegistry();
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        registry.RegisterPackageView<TestPackageView>("test.package", "test.view", serviceProvider);

        var cached = registry.GetOrCreateView("test.view", _ => false, ReportFailure);
        var disabled = registry.GetOrCreateView("test.view", packageId => packageId == "test.package", ReportFailure);

        Assert.NotNull(cached);
        Assert.Null(disabled);
    }

    [Fact]
    public void RegisterPackageView_WhenViewIdBelongsToAnotherPackage_Throws()
    {
        var registry = new AppPackageViewRegistry();
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        registry.RegisterPackageView<TestPackageView>("test.package", "test.view", serviceProvider);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            registry.RegisterPackageView<TestPackageView>("other.package", "test.view", serviceProvider));

        Assert.Contains("test.package", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetOrCreateView_WhenPackageIsUnregisteredDuringCreation_DoesNotCacheCreatedView()
    {
        SelfUnregisteringPackageView.Created.Clear();
        var registry = new AppPackageViewRegistry();
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        SelfUnregisteringPackageView.OnCreate = () => registry.UnregisterPackage("test.package");
        registry.RegisterPackageView<SelfUnregisteringPackageView>("test.package", "test.view", serviceProvider);

        try
        {
            var view = registry.GetOrCreateView("test.view", _ => false, ReportFailure);

            Assert.Null(view);
            var createdView = Assert.Single(SelfUnregisteringPackageView.Created);
            Assert.True(createdView.IsDisposed);
            Assert.Null(registry.GetOrCreateView("test.view", _ => false, ReportFailure));
        }
        finally
        {
            SelfUnregisteringPackageView.OnCreate = null;
        }
    }

    [Fact]
    public void UnregisterPackage_WhenCachedViewDisposeThrows_DoesNotThrowAndContinuesCleanup()
    {
        ThrowingDisposePackageView.ControlDisposeCalled = false;
        var registry = new AppPackageViewRegistry();
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        registry.RegisterPackageView<ThrowingDisposePackageView>("test.package", "test.view", serviceProvider);
        var view = Assert.IsType<ThrowingDisposePackageView>(registry.GetOrCreateView("test.view", _ => false, ReportFailure));
        var dataContext = Assert.IsType<ThrowingDisposableDataContext>(view.DataContext);

        registry.UnregisterPackage("test.package");

        Assert.True(dataContext.DisposeCalled);
        Assert.True(ThrowingDisposePackageView.ControlDisposeCalled);
        Assert.Null(registry.GetOrCreateView("test.view", _ => false, ReportFailure));
    }

    [Fact]
    public void ListSettingsViewPackages_FiltersDisabledPackagesAndUsesDescriptors()
    {
        var registry = new AppPackageViewRegistry(new Dictionary<string, PackageSettingsViewDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            ["z.package"] = new("z.package", "Zulu", "Zulu summary"),
            ["a.package"] = new("a.package", "Alpha", "Alpha summary"),
        });
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        registry.RegisterSettingsView<TestPackageView>("z.package", serviceProvider);
        registry.RegisterSettingsView<TestPackageView>("a.package", serviceProvider);

        var packages = registry.ListSettingsViewPackages(packageId => packageId == "z.package");

        var package = Assert.Single(packages);
        Assert.Equal("a.package", package.PackageId);
        Assert.Equal("Alpha", package.DisplayName);
        Assert.Equal("Alpha summary", package.Summary);
    }

    private static void ReportFailure(string packageId, string message, Exception exception)
        => throw new InvalidOperationException($"Unexpected failure for {packageId}: {message}", exception);

    private sealed class TestPackageView : Control
    {
    }

    private sealed class ConstructorInjectedPackageView(ConstructorDependency dependency) : Control
    {
        public ConstructorDependency Dependency { get; } = dependency;
    }

    private sealed class ConstructorDependency;

    private sealed class NavigationPackageView(NavigationProbe probe) : Control, IPackageViewNavigationTarget
    {
        public ValueTask OnNavigatedToAsync(
            PackageViewNavigationContext context,
            CancellationToken cancellationToken = default)
            => probe.NavigateAsync(context, cancellationToken);
    }

    private sealed class NavigationProbe
    {
        public Func<PackageViewNavigationContext, CancellationToken, ValueTask> NavigateAsync { get; set; }
            = static (_, _) => ValueTask.CompletedTask;
    }

    private sealed class SelfUnregisteringPackageView : Control, IDisposable
    {
        public static List<SelfUnregisteringPackageView> Created { get; } = [];

        public static Action? OnCreate { get; set; }

        public SelfUnregisteringPackageView()
        {
            Created.Add(this);
            OnCreate?.Invoke();
        }

        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    private sealed class ThrowingDisposePackageView : Control, IDisposable
    {
        public static bool ControlDisposeCalled { get; set; }

        public ThrowingDisposePackageView()
        {
            DataContext = new ThrowingDisposableDataContext();
        }

        public void Dispose()
        {
            ControlDisposeCalled = true;
            throw new InvalidOperationException("Control dispose failed.");
        }
    }

    private sealed class ThrowingDisposableDataContext : IDisposable
    {
        public bool DisposeCalled { get; private set; }

        public void Dispose()
        {
            DisposeCalled = true;
            throw new InvalidOperationException("Data context dispose failed.");
        }
    }

    private sealed class DisposableDataContextPackageView : Control
    {
        public DisposableDataContextPackageView()
        {
            DataContext = new DisposableDataContext();
        }
    }

    private sealed class DisposableDataContext : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    private sealed class TrackedDataContextPackageView : Control
    {
        public TrackedDataContextPackageView()
        {
            DataContext = new TrackedDataContext();
        }
    }

    private sealed class TrackedDataContext : IDisposable
    {
        public static List<TrackedDataContext> Created { get; } = [];

        public TrackedDataContext()
        {
            Created.Add(this);
        }

        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
