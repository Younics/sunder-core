using System.Reflection;
using System.Runtime.Loader;

namespace Sunder.Package.Hosting;

internal abstract class CollectiblePackageLoadContext : AssemblyLoadContext
{
    private readonly string _entryAssemblyPath;
    private readonly string[] _probeDirectories;
    private readonly AssemblyDependencyResolver _dependencyResolver;
    private readonly Func<AssemblyName, Assembly?> _resolveSharedAssembly;
    private readonly Action<Assembly>? _assemblyLoaded;

    protected CollectiblePackageLoadContext(
        string name,
        string entryAssemblyPath,
        Func<AssemblyName, Assembly?> resolveSharedAssembly,
        Action<Assembly>? assemblyLoaded = null)
        : base(name, isCollectible: true)
    {
        _entryAssemblyPath = entryAssemblyPath;
        _probeDirectories = [Path.GetDirectoryName(entryAssemblyPath)!];
        _dependencyResolver = new AssemblyDependencyResolver(entryAssemblyPath);
        _resolveSharedAssembly = resolveSharedAssembly;
        _assemblyLoaded = assemblyLoaded;
    }

    public Assembly LoadPackageEntryAssembly() => LoadAndTrack(_entryAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is null)
        {
            return null;
        }

        var sharedAssembly = _resolveSharedAssembly(assemblyName);
        if (sharedAssembly is not null)
        {
            return sharedAssembly;
        }

        var candidatePath = _dependencyResolver.ResolveAssemblyToPath(assemblyName);
        return candidatePath is null ? null : LoadAndTrack(candidatePath);
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

            foreach (var runtimeCandidatePath in Directory.EnumerateFiles(
                         runtimesDirectory,
                         nativeLibraryFileName,
                         SearchOption.AllDirectories))
            {
                return LoadUnmanagedDllFromPath(runtimeCandidatePath);
            }
        }

        return base.LoadUnmanagedDll(unmanagedDllName);
    }

    private Assembly LoadAndTrack(string path)
    {
        var assembly = LoadFromAssemblyPath(path);
        _assemblyLoaded?.Invoke(assembly);
        return assembly;
    }
}
