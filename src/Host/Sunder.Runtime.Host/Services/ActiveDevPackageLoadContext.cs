using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.InteropServices;
namespace Sunder.Runtime.Host.Services;

internal sealed class ActiveDevPackageLoadContext(
    string packageId,
    string entryAssemblyPath,
    RuntimeSharedAssemblyRegistry sharedAssemblyRegistry)
    : AssemblyLoadContext($"Sunder.DevPackage.{packageId}.{Guid.NewGuid():N}", isCollectible: true)
{
    private readonly string[] _probeDirectories = [Path.GetDirectoryName(entryAssemblyPath)!];
    private readonly AssemblyDependencyResolver _dependencyResolver = new(entryAssemblyPath);
    private readonly RuntimeSharedAssemblyRegistry _sharedAssemblyRegistry = sharedAssemblyRegistry;

    public Assembly LoadPackageEntryAssembly() => LoadFromAssemblyPath(entryAssemblyPath);

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
            return LoadFromAssemblyPath(candidatePath);
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

        var nativeLibraryFileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? unmanagedDllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? unmanagedDllName : $"{unmanagedDllName}.dll"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
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
