using System.Reflection;
using System.Runtime.Loader;

namespace Sunder.Package.Hosting;

internal sealed class HostSharedAssemblyRegistryCore : IDisposable
{
    private readonly Dictionary<string, Assembly> _hostAssemblies;
    private readonly HashSet<string> _optionalHostAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingOptionalHostAssemblies = new(StringComparer.OrdinalIgnoreCase);

    public HostSharedAssemblyRegistryCore(Dictionary<string, Assembly> hostAssemblies)
    {
        _hostAssemblies = hostAssemblies;
    }

    public void RegisterOptionalHostAssemblyName(string assemblyName)
        => _optionalHostAssemblies.Add(assemblyName);

    // Asset URIs carry a simple name, not a CLR reference identity. This lookup is
    // confined to the deliberate shared set; CLR references still use validation below.
    public Assembly? ResolveResourceAssembly(string name)
        => _hostAssemblies.TryGetValue(name, out var assembly) || TryLoadOptionalHostAssembly(name, out assembly) ? assembly : null;

    public Assembly? ResolveSharedAssembly(AssemblyName requested)
    {
        if (requested.Name is null) return null;
        if (!_hostAssemblies.TryGetValue(requested.Name, out var assembly)
            && (!TryLoadOptionalHostAssembly(requested.Name, out assembly) || assembly is null))
        {
            return null;
        }

        var loaded = assembly.GetName();
        if (!IsReferenceSatisfiedBy(requested, loaded))
        {
            throw new InvalidOperationException(
                $"Host-shared assembly '{requested.Name}' requested identity '{requested.FullName}', but the Host provides incompatible identity '{loaded.FullName}'.");
        }
        return assembly;
    }

    public static bool IsReferenceSatisfiedBy(AssemblyName requested, AssemblyName loaded)
        => FamiliesMatch(requested, loaded)
           && NormalizeVersion(requested.Version).Major == NormalizeVersion(loaded.Version).Major
           && NormalizeVersion(loaded.Version).CompareTo(NormalizeVersion(requested.Version)) >= 0;

    public void Dispose()
    {
        _optionalHostAssemblies.Clear();
        _missingOptionalHostAssemblies.Clear();
    }

    private bool TryLoadOptionalHostAssembly(string assemblyName, out Assembly? assembly)
    {
        assembly = null;
        if (!_optionalHostAssemblies.Contains(assemblyName)
            || _missingOptionalHostAssemblies.Contains(assemblyName))
        {
            return false;
        }
        try
        {
            // View factories enter their package's contextual reflection scope.
            // Host-owned assemblies must bypass it to avoid reentering that
            // package's shared-assembly resolver recursively.
            assembly = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(assemblyName));
            _hostAssemblies[assemblyName] = assembly;
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            _missingOptionalHostAssemblies.Add(assemblyName);
            return false;
        }
    }

    private static bool FamiliesMatch(AssemblyName left, AssemblyName right)
        => string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)
           && string.Equals(left.CultureName ?? string.Empty, right.CultureName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
           && (left.GetPublicKeyToken() ?? []).SequenceEqual(right.GetPublicKeyToken() ?? []);

    private static Version NormalizeVersion(Version? version)
        => version is null
            ? new Version(0, 0, 0, 0)
            : new Version(
                Math.Max(version.Major, 0),
                Math.Max(version.Minor, 0),
                Math.Max(version.Build, 0),
                Math.Max(version.Revision, 0));
}
