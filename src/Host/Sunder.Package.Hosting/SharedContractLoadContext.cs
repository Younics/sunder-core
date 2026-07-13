using System.Reflection;
using System.Runtime.Loader;

namespace Sunder.Package.Hosting;

internal sealed class SharedContractLoadContext(
    string ownerName,
    Func<AssemblyName, Assembly?> resolveHostAssembly)
    : AssemblyLoadContext($"Sunder.{ownerName}.SharedContracts.{Guid.NewGuid():N}", isCollectible: true)
{
    private readonly Dictionary<string, string> _assemblyPaths = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string assemblyName, string path)
        => _assemblyPaths[assemblyName] = path;

    public Assembly LoadSharedAssembly(string path)
        => LoadFromAssemblyPath(path);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var hostAssembly = resolveHostAssembly(assemblyName);
        if (hostAssembly is not null)
        {
            return hostAssembly;
        }

        return assemblyName.Name is not null && _assemblyPaths.TryGetValue(assemblyName.Name, out var path)
            ? LoadFromAssemblyPath(path)
            : null;
    }
}
