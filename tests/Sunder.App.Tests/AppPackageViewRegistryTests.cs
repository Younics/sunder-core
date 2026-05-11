using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Services;
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
}
