using System.Reflection;
using Avalonia.Platform;
using Sunder.App;
using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPackageAvaloniaAssetLoaderTests
{
    private static readonly Uri PackageResourceUri = new("avares://Sunder.App/Assets/Images/icon.png");
    private static readonly object LocatorSync = new();

    [Fact]
    public void OpenAndGetAssembly_WhenPackageAssemblyRegistered_UsesRegisteredAssemblyBeforeFallback()
    {
        var fallback = new FallbackAssetLoader();
        var packageResources = new AppPackageResourceAssemblyRegistry();
        var loader = new AppPackageAvaloniaAssetLoader(fallback, packageResources);
        var appAssembly = typeof(App).Assembly;
        packageResources.RegisterPackageAssembly("agent", appAssembly);

        using var stream = loader.Open(PackageResourceUri);
        var (assemblyStream, assembly) = loader.OpenAndGetAssembly(PackageResourceUri);
        using var _ = assemblyStream;

        Assert.Same(appAssembly, assembly);
        Assert.True(stream.ReadByte() >= 0);
        Assert.False(fallback.WasOpened);
    }

    [Fact]
    public void Open_WhenRegisteredPackageAssemblyMissesResource_DoesNotFallBackToStaleAssembly()
    {
        var fallback = new FallbackAssetLoader();
        var packageResources = new AppPackageResourceAssemblyRegistry();
        var loader = new AppPackageAvaloniaAssetLoader(fallback, packageResources);
        packageResources.RegisterPackageAssembly("agent", typeof(App).Assembly);
        var missingResourceUri = new Uri("avares://Sunder.App/Assets/Images/missing-package-resource.png");

        Assert.False(loader.Exists(missingResourceUri));
        Assert.Throws<FileNotFoundException>(() => loader.Open(missingResourceUri));
        Assert.False(fallback.WasOpened);
    }

    [Fact]
    public void Open_WhenDependencyAssemblyRegistersAfterEntryAssembly_KeepsEntryAssemblyResources()
    {
        var fallback = new FallbackAssetLoader();
        var packageResources = new AppPackageResourceAssemblyRegistry();
        var loader = new AppPackageAvaloniaAssetLoader(fallback, packageResources);
        var appAssembly = typeof(App).Assembly;
        var dependencyAssembly = typeof(AppPackageAvaloniaAssetLoaderTests).Assembly;
        packageResources.RegisterPackageAssembly("agent", appAssembly);
        packageResources.RegisterPackageAssembly("agent", dependencyAssembly);

        var (stream, assembly) = loader.OpenAndGetAssembly(PackageResourceUri);
        using var _ = stream;

        Assert.Same(appAssembly, assembly);
        Assert.False(fallback.WasOpened);
    }

    [Fact]
    public void Install_ReplacesAvaloniaAssetLoaderService()
    {
        lock (LocatorSync)
        {
            using var scope = EnterAvaloniaLocatorScope();
            var fallback = new FallbackAssetLoader();
            BindAvaloniaAssetLoader(fallback);
            var packageResources = new AppPackageResourceAssemblyRegistry();

            Assert.True(AppPackageAvaloniaAssetLoader.Install(packageResources));
            packageResources.RegisterPackageAssembly("agent", typeof(App).Assembly);

            using var stream = AssetLoader.Open(PackageResourceUri);

            Assert.True(stream.ReadByte() >= 0);
            Assert.False(fallback.WasOpened);
        }
    }

    private static IDisposable EnterAvaloniaLocatorScope()
    {
        var enterScope = GetAvaloniaLocatorType().GetMethod(
            "EnterScope",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Avalonia service locator does not expose EnterScope().");

        return (IDisposable)(enterScope.Invoke(null, null)
            ?? throw new InvalidOperationException("Avalonia service locator did not return a scope."));
    }

    private static void BindAvaloniaAssetLoader(IAssetLoader assetLoader)
    {
        var locatorType = GetAvaloniaLocatorType();
        var currentMutable = locatorType.GetProperty(
            "CurrentMutable",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException("Mutable Avalonia service locator is not initialized.");
        var bind = locatorType.GetMethod(
            "Bind",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Mutable Avalonia service locator does not expose Bind<T>().");
        var registration = bind.MakeGenericMethod(typeof(IAssetLoader)).Invoke(currentMutable, null)
            ?? throw new InvalidOperationException("Avalonia service locator did not return a registration helper.");
        var toConstant = registration.GetType().GetMethod(
            "ToConstant",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Avalonia service registration helper does not expose ToConstant<T>().");

        toConstant.MakeGenericMethod(typeof(IAssetLoader)).Invoke(registration, [assetLoader]);
    }

    private static Type GetAvaloniaLocatorType()
        => typeof(AssetLoader).Assembly.GetType("Avalonia.AvaloniaLocator")
            ?? throw new InvalidOperationException("Avalonia service locator type could not be found.");

    private sealed class FallbackAssetLoader : IAssetLoader
    {
        public bool WasOpened { get; private set; }

        public void SetDefaultAssembly(Assembly assembly)
        {
        }

        public bool Exists(Uri uri, Uri? baseUri = null)
            => true;

        public Stream Open(Uri uri, Uri? baseUri = null)
        {
            WasOpened = true;
            return new MemoryStream(new byte[] { 0x42 });
        }

        public (Stream stream, Assembly assembly) OpenAndGetAssembly(Uri uri, Uri? baseUri = null)
            => (Open(uri, baseUri), typeof(FallbackAssetLoader).Assembly);

        public Assembly? GetAssembly(Uri uri, Uri? baseUri = null)
            => typeof(FallbackAssetLoader).Assembly;

        public IEnumerable<Uri> GetAssets(Uri uri, Uri? baseUri)
            => [uri];

        public void InvalidateAssemblyCache(string name)
        {
        }

        public void InvalidateAssemblyCache()
        {
        }
    }
}
