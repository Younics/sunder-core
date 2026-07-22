using System.Reflection;
using System.Runtime.Loader;

namespace Sunder.Package.Hosting;

internal sealed class SharedContractLoadContext(
    string ownerName,
    Func<AssemblyName, Assembly?> resolveHostAssembly,
    Action<string, Assembly> sharedAssemblyLoaded)
    : AssemblyLoadContext($"Sunder.{ownerName}.SharedContracts.{Guid.NewGuid():N}", isCollectible: true)
{
    private readonly Dictionary<string, string> _assemblyPaths = new(StringComparer.OrdinalIgnoreCase);

    public void ReplaceMappings(IReadOnlyDictionary<string, string> assemblyPaths)
    {
        _assemblyPaths.Clear();
        foreach (var assemblyPath in assemblyPaths)
        {
            _assemblyPaths[assemblyPath.Key] = assemblyPath.Value;
        }
    }

    public Assembly LoadSharedAssembly(string path)
    {
        var assembly = LoadFromAssemblyPath(path);
        sharedAssemblyLoaded(path, assembly);
        return assembly;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var hostAssembly = resolveHostAssembly(assemblyName);
        if (hostAssembly is not null)
        {
            return hostAssembly;
        }

        if (assemblyName.Name is null || !_assemblyPaths.TryGetValue(assemblyName.Name, out var path))
        {
            return null;
        }

        var assembly = LoadFromAssemblyPath(path);
        sharedAssemblyLoaded(path, assembly);
        return assembly;
    }
}
