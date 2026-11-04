using System.Reflection;
using System.Runtime.Loader;

namespace Sunder.Package.Hosting;

internal abstract class CollectiblePackageLoadContext : AssemblyLoadContext
{
    private readonly string _entryAssemblyPath;
    private readonly AssemblyDependencyResolver _dependencyResolver;
    private readonly Func<AssemblyName, Assembly?> _resolveSharedAssembly;
    private readonly Action<Assembly>? _assemblyLoaded;
    private readonly string _runtimeIdentifier;

    protected CollectiblePackageLoadContext(
        string name,
        string entryAssemblyPath,
        string runtimeIdentifier,
        Func<AssemblyName, Assembly?> resolveSharedAssembly,
        Action<Assembly>? assemblyLoaded = null)
        : base(name, isCollectible: true)
    {
        _entryAssemblyPath = entryAssemblyPath;
        _runtimeIdentifier = runtimeIdentifier;
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
        if (candidatePath is null && (assemblyName.Name == "Sunder.Sdk" || assemblyName.Name.StartsWith("Sunder.Sdk.", StringComparison.Ordinal)))
            // FileNotFoundException permits the CLR to try Default; FileLoadException stops that fallback.
            throw new FileLoadException($"Package-local SDK assembly '{assemblyName.FullName}' is missing from the selected App payload.", assemblyName.Name + ".dll");
        return candidatePath is null ? null : LoadAndTrack(candidatePath);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var candidatePath = _dependencyResolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (candidatePath is not null)
        {
            return LoadUnmanagedDllFromPath(candidatePath);
        }

        var runtimeCandidatePath = NativeLibraryFallbackResolver.Resolve(
            _entryAssemblyPath,
            unmanagedDllName,
            _runtimeIdentifier);
        if (runtimeCandidatePath is not null)
        {
            return LoadUnmanagedDllFromPath(runtimeCandidatePath);
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
