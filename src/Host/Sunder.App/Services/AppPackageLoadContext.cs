using System.Reflection;
using System.Runtime.Loader;

namespace Sunder.App.Services;

internal sealed class AppPackageLoadContext(
    string packageId,
    string entryAssemblyPath,
    AppSharedAssemblyRegistry sharedAssemblyRegistry,
    Action<string, Assembly> registerPackageAssembly)
    : AssemblyLoadContext($"Sunder.App.Package.{packageId}.{Guid.NewGuid():N}", isCollectible: true)
{
    private readonly string _packageId = packageId;
    private readonly string _entryAssemblyPath = entryAssemblyPath;
    private readonly string[] _probeDirectories = [Path.GetDirectoryName(entryAssemblyPath)!];
    private readonly AssemblyDependencyResolver _dependencyResolver = new(entryAssemblyPath);
    private readonly AppSharedAssemblyRegistry _sharedAssemblyRegistry = sharedAssemblyRegistry;
    private readonly Action<string, Assembly> _registerPackageAssembly = registerPackageAssembly;

    public Assembly LoadPackageEntryAssembly()
    {
        var assembly = LoadFromAssemblyPath(_entryAssemblyPath);
        _registerPackageAssembly(_packageId, assembly);
        return assembly;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is null)
        {
            return null;
        }

        var sharedAssembly = _sharedAssemblyRegistry.ResolveSharedAssembly(assemblyName);
        if (sharedAssembly is not null)
        {
            return sharedAssembly;
        }

        var candidatePath = _dependencyResolver.ResolveAssemblyToPath(assemblyName);
        if (candidatePath is not null)
        {
            var assembly = LoadFromAssemblyPath(candidatePath);
            _registerPackageAssembly(_packageId, assembly);
            return assembly;
        }

        return null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var candidatePath = _dependencyResolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (candidatePath is not null)
        {
            return LoadUnmanagedDllFromPath(candidatePath);
        }

        var nativeLibraryFileName = OperatingSystem.IsWindows()
            ? unmanagedDllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? unmanagedDllName : $"{unmanagedDllName}.dll"
            : OperatingSystem.IsMacOS()
                ? unmanagedDllName.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase) ? unmanagedDllName : $"lib{unmanagedDllName}.dylib"
                : unmanagedDllName.EndsWith(".so", StringComparison.OrdinalIgnoreCase) ? unmanagedDllName : $"lib{unmanagedDllName}.so";

        foreach (var probeDirectory in _probeDirectories)
        {
            var runtimesDirectory = Path.Combine(probeDirectory, "runtimes");
            if (!Directory.Exists(runtimesDirectory))
            {
                continue;
            }

            foreach (var runtimeCandidatePath in Directory.EnumerateFiles(runtimesDirectory, nativeLibraryFileName, SearchOption.AllDirectories))
            {
                return LoadUnmanagedDllFromPath(runtimeCandidatePath);
            }
        }

        return base.LoadUnmanagedDll(unmanagedDllName);
    }
}
