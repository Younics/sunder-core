using System.Reflection;
using Avalonia.Platform;

namespace Sunder.App.Services;

internal sealed class AppPackageAvaloniaAssetLoader : IAssetLoader
{
    private readonly AppPackageAssetLoaderDispatch _platformDispatch;
    private readonly AppPackageResourceAssemblyRegistry _packageResources;

    internal AppPackageAvaloniaAssetLoader(IAssetLoader fallback, AppPackageResourceAssemblyRegistry packageResources)
    {
        _platformDispatch = new AppPackageAssetLoaderDispatch(fallback);
        _packageResources = packageResources;
    }

    public static bool Install(AppPackageResourceAssemblyRegistry packageResources)
    {
        // AppBuilder initializes the platform services before App.Initialize.
        // Preserve that loader (including platform/designer configuration), rather
        // than constructing the obsolete StandardAssetLoader or silently masking
        // an invalid startup order. The service factory distinguishes an ordinary
        // package lookup from the next thread-local static facade forwarding call.
        var current = GetCurrentAssetLoader()
            ?? throw new InvalidOperationException("Avalonia must initialize its platform asset loader before package asset loading is installed.");
        if (current is AppPackageAvaloniaAssetLoader existing && ReferenceEquals(existing._packageResources, packageResources))
        {
            return true;
        }

        var fallback = current is AppPackageAvaloniaAssetLoader existingLoader
            ? existingLoader._platformDispatch.PlatformLoader
            : current;
        var loader = new AppPackageAvaloniaAssetLoader(fallback, packageResources);
        BindAssetLoader(() => loader._platformDispatch.Resolve(loader));
        return true;
    }

    public void SetDefaultAssembly(Assembly assembly)
        => _platformDispatch.Invoke(() => AssetLoader.SetDefaultAssembly(assembly));

    public bool Exists(Uri uri, Uri? baseUri = null)
    {
        var result = TryOpenPackageAsset(uri, baseUri, out var stream, out _);
        stream?.Dispose();
        return result switch
        {
            PackageAssetResolution.Found => true,
            PackageAssetResolution.RegisteredAssemblyMissingAsset => false,
            _ => _platformDispatch.Invoke(() => AssetLoader.Exists(uri, baseUri)),
        };
    }

    public Stream Open(Uri uri, Uri? baseUri = null)
        => OpenAndGetAssembly(uri, baseUri).stream;

    public (Stream stream, Assembly assembly) OpenAndGetAssembly(Uri uri, Uri? baseUri = null)
    {
        var result = TryOpenPackageAsset(uri, baseUri, out var stream, out var assembly);
        return result switch
        {
            PackageAssetResolution.Found => (stream, assembly),
            PackageAssetResolution.RegisteredAssemblyMissingAsset => throw new FileNotFoundException($"The resource {uri} could not be found."),
            _ => _platformDispatch.Invoke(() => AssetLoader.OpenAndGetAssembly(uri, baseUri)),
        };
    }

    public Assembly? GetAssembly(Uri uri, Uri? baseUri = null)
    {
        if (TryResolveAvaresUri(uri, baseUri, out var resolvedUri, out var assemblyName, out _)
            && _packageResources.TryGetAssembly(assemblyName, out var assembly))
        {
            return assembly;
        }

        return _platformDispatch.Invoke(() => AssetLoader.GetAssembly(resolvedUri ?? uri, baseUri));
    }

    public IEnumerable<Uri> GetAssets(Uri uri, Uri? baseUri)
    {
        if (!TryResolveAvaresUri(uri, baseUri, out _, out var assemblyName, out var path))
        {
            return _platformDispatch.Invoke(() => AssetLoader.GetAssets(uri, baseUri));
        }

        if (_packageResources.TryGetAvaloniaResourceUris(assemblyName, path, out var uris, out var assemblyRegistered))
        {
            return uris;
        }

        if (assemblyRegistered)
        {
            return [];
        }

        return _platformDispatch.Invoke(() => AssetLoader.GetAssets(uri, baseUri));
    }

    public void InvalidateAssemblyCache(string name)
        => _platformDispatch.Invoke(() => AssetLoader.InvalidateAssemblyCache(name));

    public void InvalidateAssemblyCache()
        => _platformDispatch.Invoke(AssetLoader.InvalidateAssemblyCache);

    internal static void TryInvalidateAssemblyCache(IEnumerable<string> assemblyNames)
    {
        foreach (var assemblyName in assemblyNames.Where(static name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                AssetLoader.InvalidateAssemblyCache(assemblyName);
            }
            catch (Exception ex) when (AppPackageUiFaultPolicy.IsRecoverable(ex))
            {
                AppSessionLog.WriteError($"Failed to invalidate Avalonia asset cache for package assembly '{assemblyName}'.", ex);
            }
        }
    }

    private static IAssetLoader? GetCurrentAssetLoader()
    {
        var current = GetAvaloniaLocatorProperty("Current").GetValue(null)
            ?? throw new InvalidOperationException("Avalonia service locator is not initialized.");
        var getService = current.GetType().GetMethod(
            "GetService",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            [typeof(Type)])
            ?? throw new InvalidOperationException("Avalonia service locator does not expose GetService(Type).");

        return getService.Invoke(current, [typeof(IAssetLoader)]) as IAssetLoader;
    }

    private static void BindAssetLoader(Func<IAssetLoader> resolveAssetLoader)
    {
        var locatorType = GetAvaloniaLocatorType();
        var currentMutable = GetAvaloniaLocatorProperty("CurrentMutable").GetValue(null)
            ?? throw new InvalidOperationException("Mutable Avalonia service locator is not initialized.");
        var bind = locatorType.GetMethod(
            "Bind",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Mutable Avalonia service locator does not expose Bind<T>().");
        var registration = bind.MakeGenericMethod(typeof(IAssetLoader)).Invoke(currentMutable, null)
            ?? throw new InvalidOperationException("Avalonia service locator did not return a registration helper.");
        var toFunc = registration.GetType().GetMethod(
            "ToFunc",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Avalonia service registration helper does not expose ToFunc<T>().");

        toFunc.MakeGenericMethod(typeof(IAssetLoader)).Invoke(registration, [resolveAssetLoader]);
    }

    private static PropertyInfo GetAvaloniaLocatorProperty(string propertyName)
        => GetAvaloniaLocatorType().GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Avalonia service locator does not expose {propertyName}.");

    private static Type GetAvaloniaLocatorType()
        => typeof(AssetLoader).Assembly.GetType("Avalonia.AvaloniaLocator")
            ?? throw new InvalidOperationException("Avalonia service locator type could not be found.");

    private PackageAssetResolution TryOpenPackageAsset(Uri uri, Uri? baseUri, out Stream stream, out Assembly assembly)
    {
        stream = null!;
        assembly = null!;
        if (!TryResolveAvaresUri(uri, baseUri, out _, out var assemblyName, out var path))
        {
            return PackageAssetResolution.NotPackageAssembly;
        }

        if (_packageResources.TryOpenAvaloniaResource(assemblyName, path, out stream, out assembly, out var assemblyRegistered))
        {
            return PackageAssetResolution.Found;
        }

        return assemblyRegistered
            ? PackageAssetResolution.RegisteredAssemblyMissingAsset
            : PackageAssetResolution.NotPackageAssembly;
    }

    private static bool TryResolveAvaresUri(
        Uri uri,
        Uri? baseUri,
        out Uri? resolvedUri,
        out string assemblyName,
        out string path)
    {
        resolvedUri = uri.IsAbsoluteUri
            ? uri
            : baseUri is null ? null : new Uri(baseUri, uri);
        if (resolvedUri is null || !string.Equals(resolvedUri.Scheme, "avares", StringComparison.OrdinalIgnoreCase))
        {
            assemblyName = string.Empty;
            path = string.Empty;
            return false;
        }

        assemblyName = Uri.UnescapeDataString(resolvedUri.Authority);
        path = Uri.UnescapeDataString(resolvedUri.AbsolutePath);
        return !string.IsNullOrWhiteSpace(assemblyName) && !string.IsNullOrWhiteSpace(path);
    }

    private enum PackageAssetResolution
    {
        NotPackageAssembly,
        Found,
        RegisteredAssemblyMissingAsset,
    }
}
