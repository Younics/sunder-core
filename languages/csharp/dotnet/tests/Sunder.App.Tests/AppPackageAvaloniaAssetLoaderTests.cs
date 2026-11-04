using System.Reflection;
using Avalonia.Platform;
using Sunder.App;
using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

[Collection(HeadlessAppCollection.Name)]
public sealed class AppPackageAvaloniaAssetLoaderTests
{
    private static readonly Uri PackageResourceUri = new("avares://Sunder.App/Assets/Images/icon.png");
    private static readonly object LocatorSync = new();

    [Fact]
    public void SameNamedResourcesResolveInTheirOwningContextAndSharedThemesStayShared()
    {
        WithInstalledLoader((fallback, resources) =>
        {
            using var shared = new AppSharedAssemblyRegistry();
            var contexts = new[] { "one", "two" }.Select(id => new AppPackageLoadContext(id, typeof(App).Assembly.Location,
                AppPackageTargetEnvironment.CurrentRid, shared, (owner, assembly) => resources.RegisterPackageAssembly(owner, assembly))).ToArray();
            try
            {
                var assemblies = contexts.Select(context => context.LoadPackageEntryAssembly()).ToArray();
                Assert.NotSame(assemblies[0], assemblies[1]);
                Assert.Throws<InvalidOperationException>(() => AssetLoader.Open(PackageResourceUri));
                foreach (var index in new[] { 1, 0, 1 })
                {
                    using var resolution = contexts[index].EnterContextualReflection();
                    var (stream, assembly) = AssetLoader.OpenAndGetAssembly(PackageResourceUri);
                    using (stream) Assert.True(stream.ReadByte() >= 0);
                    Assert.Same(assemblies[index], assembly);
                    Assert.Same(assemblies[index], AssetLoader.GetAssembly(PackageResourceUri));
                    var themeUri = new Uri("avares://Sunder.App.Bridge/Themes/SunderThemeResources.axaml");
                    Assert.Same(typeof(Sunder.App.Bridge.IAppPackageEntryPointV1).Assembly, AssetLoader.GetAssembly(themeUri));
                    Assert.IsType<Avalonia.Controls.ResourceDictionary>(Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(themeUri));
                }
                Assert.False(fallback.WasOpened);
                resources.RemovePackage("two");
                using (contexts[1].EnterContextualReflection())
                    Assert.Same(assemblies[1], AssetLoader.GetAssembly(PackageResourceUri));
                Assert.Same(assemblies[0], AssetLoader.GetAssembly(PackageResourceUri));
            }
            finally
            {
                resources.ReplacePackageAssemblies([]);
                foreach (var context in contexts) context.Unload();
            }
        });
    }

    [Fact]
    public void OpenAndGetAssembly_WhenPackageAssemblyRegistered_UsesRegisteredAssemblyBeforeFallback()
    {
        WithInstalledLoader((fallback, packageResources) =>
        {
            var appAssembly = typeof(App).Assembly;
            packageResources.RegisterPackageAssembly("agent", appAssembly);

            using var stream = AssetLoader.Open(PackageResourceUri);
            var (assemblyStream, assembly) = AssetLoader.OpenAndGetAssembly(PackageResourceUri);
            using var _ = assemblyStream;

            Assert.Same(appAssembly, assembly);
            Assert.True(stream.ReadByte() >= 0);
            Assert.False(fallback.WasOpened);
        });
    }

    [Fact]
    public void Open_WhenRegisteredPackageAssemblyMissesResource_DoesNotFallBackToStaleAssembly()
    {
        WithInstalledLoader((fallback, packageResources) =>
        {
            packageResources.RegisterPackageAssembly("agent", typeof(App).Assembly);
            var missingResourceUri = new Uri("avares://Sunder.App/Assets/Images/missing-package-resource.png");

            Assert.False(AssetLoader.Exists(missingResourceUri));
            Assert.Throws<FileNotFoundException>(() => AssetLoader.Open(missingResourceUri));
            Assert.False(fallback.WasOpened);
        });
    }

    [Fact]
    public void Open_WhenDependencyAssemblyRegistersAfterEntryAssembly_KeepsEntryAssemblyResources()
    {
        WithInstalledLoader((fallback, packageResources) =>
        {
            var appAssembly = typeof(App).Assembly;
            var dependencyAssembly = typeof(AppPackageAvaloniaAssetLoaderTests).Assembly;
            packageResources.RegisterPackageAssembly("agent", appAssembly);
            packageResources.RegisterPackageAssembly("agent", dependencyAssembly);

            var (stream, assembly) = AssetLoader.OpenAndGetAssembly(PackageResourceUri);
            using var _ = stream;

            Assert.Same(appAssembly, assembly);
            Assert.False(fallback.WasOpened);
        });
    }

    [Fact]
    public void Install_ReplacesAvaloniaAssetLoaderService()
    {
        WithInstalledLoader((fallback, packageResources) =>
        {
            packageResources.RegisterPackageAssembly("agent", typeof(App).Assembly);

            using var stream = AssetLoader.Open(PackageResourceUri);

            Assert.True(stream.ReadByte() >= 0);
            Assert.False(fallback.WasOpened);

            // Reinstallation retains the platform loader, not a chain of routers.
            Assert.True(AppPackageAvaloniaAssetLoader.Install(packageResources));
            Assert.True(AppPackageAvaloniaAssetLoader.Install(new AppPackageResourceAssemblyRegistry()));
            using var shellStream = AssetLoader.Open(new Uri("avares://Unregistered.Shell/asset.png"));
            Assert.Equal(0x42, shellStream.ReadByte());
            Assert.Equal(1, fallback.OpenCount);
        });
    }

    [Fact]
    public void StaticFacade_WhenForwarding_UsesCapturedPlatformForEveryOperation()
    {
        WithInstalledLoader((fallback, _) =>
        {
            var uri = new Uri("avares://Unregistered.Shell/asset.png");
            AssetLoader.SetDefaultAssembly(typeof(App).Assembly);
            Assert.Same(typeof(App).Assembly, fallback.DefaultAssembly);
            Assert.True(AssetLoader.Exists(uri));
            Assert.Same(typeof(FallbackAssetLoader).Assembly, AssetLoader.GetAssembly(uri));
            Assert.Equal([uri], AssetLoader.GetAssets(uri, null));
            using (var stream = AssetLoader.Open(uri)) Assert.Equal(0x42, stream.ReadByte());
            var (pairedStream, assembly) = AssetLoader.OpenAndGetAssembly(uri);
            using (pairedStream) Assert.Equal(0x42, pairedStream.ReadByte());
            Assert.Same(typeof(FallbackAssetLoader).Assembly, assembly);
            AssetLoader.InvalidateAssemblyCache("Unregistered.Shell");
            AssetLoader.InvalidateAssemblyCache();

            Assert.Equal(1, fallback.DefaultAssemblyCount);
            Assert.Equal(1, fallback.ExistsCount);
            Assert.Equal(1, fallback.GetAssemblyCount);
            Assert.Equal(1, fallback.GetAssetsCount);
            Assert.Equal(2, fallback.OpenCount);
            Assert.Equal(["Unregistered.Shell", null], fallback.InvalidatedAssemblyNames);
        });
    }

    [Fact]
    public void StaticFacade_WhenPlatformLoaderReenters_PackageResolutionIsNotBypassed()
    {
        WithInstalledLoader((fallback, packageResources) =>
        {
            packageResources.RegisterPackageAssembly("agent", typeof(App).Assembly);
            var missing = new Uri("avares://Sunder.App/Assets/Images/missing-package-resource.png");
            fallback.OnOpen = () => Assert.False(AssetLoader.Exists(missing));

            using var stream = AssetLoader.Open(new Uri("avares://Unregistered.Shell/asset.png"));

            Assert.Equal(0x42, stream.ReadByte());
            Assert.Equal(1, fallback.OpenCount);
            Assert.Equal(0, fallback.ExistsCount);
        });
    }

    [Fact]
    public void StaticFacade_WhenPlatformOpenIsBlocked_ConcurrentPackageReadKeepsItsGeneration()
    {
        WithInstalledLoader((fallback, packageResources) =>
        {
            packageResources.RegisterPackageAssembly("agent", typeof(App).Assembly);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            fallback.OnOpen = () =>
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            };
            var platformRead = Task.Run(() =>
            {
                using var stream = AssetLoader.Open(new Uri("avares://Unregistered.Shell/asset.png"));
                return stream.ReadByte();
            });
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                var (stream, assembly) = AssetLoader.OpenAndGetAssembly(PackageResourceUri);
                using var _ = stream;
                Assert.Same(typeof(App).Assembly, assembly);
                Assert.True(stream.ReadByte() >= 0);
                Assert.Equal(1, fallback.OpenCount);
            }
            finally
            {
                release.Set();
                Assert.Equal(0x42, platformRead.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            }
        });
    }

    [Fact]
    public void PlatformDispatch_WhenOperationThrowsBeforeResolution_ClearsPendingCall()
    {
        var platform = new FallbackAssetLoader();
        var package = new FallbackAssetLoader();
        var dispatch = new AppPackageAssetLoaderDispatch(platform);

        Assert.Throws<IOException>(() => dispatch.Invoke<int>(() => throw new IOException("Before service resolution.")));

        Assert.Same(package, dispatch.Resolve(package));
    }

    [Fact]
    public void Install_WhenPlatformLoaderIsAbsent_RejectsInvalidStartupWithoutCreatingOne()
    {
        lock (LocatorSync)
        {
            using var scope = EnterAvaloniaLocatorScope();
            BindAvaloniaAssetLoader(null);

            var error = Assert.Throws<InvalidOperationException>(() =>
                AppPackageAvaloniaAssetLoader.Install(new AppPackageResourceAssemblyRegistry()));

            Assert.Contains("initialize its platform asset loader", error.Message, StringComparison.Ordinal);
        }
    }

    private static void WithInstalledLoader(Action<FallbackAssetLoader, AppPackageResourceAssemblyRegistry> action)
    {
        lock (LocatorSync)
        {
            using var scope = EnterAvaloniaLocatorScope();
            var fallback = new FallbackAssetLoader();
            BindAvaloniaAssetLoader(fallback);
            var packageResources = new AppPackageResourceAssemblyRegistry();
            Assert.True(AppPackageAvaloniaAssetLoader.Install(packageResources));
            action(fallback, packageResources);
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

    private static void BindAvaloniaAssetLoader(IAssetLoader? assetLoader)
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
        public int OpenCount { get; private set; }
        public int DefaultAssemblyCount { get; private set; }
        public int ExistsCount { get; private set; }
        public int GetAssemblyCount { get; private set; }
        public int GetAssetsCount { get; private set; }
        public Assembly? DefaultAssembly { get; private set; }
        public List<string?> InvalidatedAssemblyNames { get; } = [];
        public Action? OnOpen { get; set; }

        public void SetDefaultAssembly(Assembly assembly)
        {
            DefaultAssemblyCount++;
            DefaultAssembly = assembly;
        }

        public bool Exists(Uri uri, Uri? baseUri = null)
        {
            ExistsCount++;
            return true;
        }

        public Stream Open(Uri uri, Uri? baseUri = null)
        {
            WasOpened = true;
            OpenCount++;
            OnOpen?.Invoke();
            return new MemoryStream(new byte[] { 0x42 });
        }

        public (Stream stream, Assembly assembly) OpenAndGetAssembly(Uri uri, Uri? baseUri = null)
            => (Open(uri, baseUri), typeof(FallbackAssetLoader).Assembly);

        public Assembly? GetAssembly(Uri uri, Uri? baseUri = null)
        {
            GetAssemblyCount++;
            return typeof(FallbackAssetLoader).Assembly;
        }

        public IEnumerable<Uri> GetAssets(Uri uri, Uri? baseUri)
        {
            GetAssetsCount++;
            return [uri];
        }

        public void InvalidateAssemblyCache(string name)
        {
            InvalidatedAssemblyNames.Add(name);
        }

        public void InvalidateAssemblyCache()
        {
            InvalidatedAssemblyNames.Add(null);
        }
    }
}
