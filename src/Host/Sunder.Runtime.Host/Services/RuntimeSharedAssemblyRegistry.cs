using System.Reflection;
using System.Runtime.Loader;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sunder.Sdk.Abstractions;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeSharedAssemblyRegistry
{
    private readonly Dictionary<string, Assembly> _sharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        [typeof(Control).Assembly.GetName().Name!] = typeof(Control).Assembly,
        [typeof(Avalonia.AvaloniaObject).Assembly.GetName().Name!] = typeof(Avalonia.AvaloniaObject).Assembly,
        [typeof(Avalonia.Markup.Xaml.AvaloniaXamlLoader).Assembly.GetName().Name!] = typeof(Avalonia.Markup.Xaml.AvaloniaXamlLoader).Assembly,
        [typeof(IServiceCollection).Assembly.GetName().Name!] = typeof(IServiceCollection).Assembly,
        [typeof(ILoggerFactory).Assembly.GetName().Name!] = typeof(ILoggerFactory).Assembly,
        [typeof(ISunderPackageModule).Assembly.GetName().Name!] = typeof(ISunderPackageModule).Assembly,
    };

    private readonly Dictionary<string, string> _sharedAssemblyPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AssemblyName> _sharedAssemblyNames = new(StringComparer.OrdinalIgnoreCase);

    public RuntimeSharedAssemblyRegistry(IEnumerable<string> probeDirectories)
    {
        RegisterOptionalHostAssembly("Avalonia");
        RegisterOptionalHostAssembly("Avalonia.Markup");
        RegisterOptionalHostAssembly("Avalonia.Dialogs");
        RegisterOptionalHostAssembly("Avalonia.Remote.Protocol");
        RegisterOptionalHostAssembly("Avalonia.Metal");
        RegisterOptionalHostAssembly("Avalonia.OpenGL");
        RegisterOptionalHostAssembly("Avalonia.Vulkan");
        RegisterOptionalHostAssembly("Avalonia.MicroCom");
        RegisterOptionalHostAssembly("MicroCom.Runtime");

        var candidateAssemblies = IndexCandidateAssemblies(probeDirectories);
        foreach (var candidate in candidateAssemblies.Values.SelectMany(static candidate => candidate)
                     .Where(static candidate => candidate.Name.Name?.EndsWith(".Contracts", StringComparison.OrdinalIgnoreCase) == true))
        {
            TryRegisterSharedAssemblyPath(candidate);
        }

        RegisterSharedDependencyClosure(candidateAssemblies);
    }

    private void RegisterOptionalHostAssembly(string assemblyName)
    {
        if (_sharedAssemblies.ContainsKey(assemblyName))
        {
            return;
        }

        try
        {
            var assembly = Assembly.Load(new AssemblyName(assemblyName));
            _sharedAssemblies[assemblyName] = assembly;
        }
        catch
        {
            // Optional host-owned assemblies may not be loaded in every runtime host configuration.
        }
    }

    public Assembly? ResolveSharedAssembly(AssemblyName assemblyName)
    {
        if (assemblyName.Name is null)
        {
            return null;
        }

        if (_sharedAssemblies.TryGetValue(assemblyName.Name, out var sharedAssembly))
        {
            // Host-owned boundary assemblies are authoritative for the session.
            // Packages may reference older patch/minor versions of the same host-shared assembly.
            return sharedAssembly;
        }

        if (!_sharedAssemblyPaths.TryGetValue(assemblyName.Name, out var sharedAssemblyPath))
        {
            return null;
        }

        var loadedAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(sharedAssemblyPath);
        ValidateSharedContractCompatibility(assemblyName, loadedAssembly);
        _sharedAssemblies[assemblyName.Name] = loadedAssembly;
        return loadedAssembly;
    }

    private static Dictionary<string, List<AssemblyCandidate>> IndexCandidateAssemblies(IEnumerable<string> probeDirectories)
    {
        var candidates = new Dictionary<string, List<AssemblyCandidate>>(StringComparer.OrdinalIgnoreCase);
        foreach (var probeDirectory in probeDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(probeDirectory))
            {
                continue;
            }

            foreach (var assemblyPath in Directory.EnumerateFiles(probeDirectory, "*.dll", SearchOption.TopDirectoryOnly))
            {
                AssemblyName assemblyName;
                try
                {
                    assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(assemblyName.Name))
                {
                    continue;
                }

                if (!candidates.TryGetValue(assemblyName.Name, out var namedCandidates))
                {
                    namedCandidates = [];
                    candidates[assemblyName.Name] = namedCandidates;
                }

                namedCandidates.Add(new AssemblyCandidate(assemblyPath, assemblyName));
            }
        }

        return candidates;
    }

    private void RegisterSharedDependencyClosure(IReadOnlyDictionary<string, List<AssemblyCandidate>> candidateAssemblies)
    {
        var pending = new Queue<string>(_sharedAssemblyPaths.Keys);
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryDequeue(out var assemblyName))
        {
            if (!processed.Add(assemblyName))
            {
                continue;
            }

            var assembly = LoadSharedAssembly(assemblyName);
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name is null || _sharedAssemblies.ContainsKey(reference.Name))
                {
                    continue;
                }

                if (_sharedAssemblyPaths.ContainsKey(reference.Name))
                {
                    pending.Enqueue(reference.Name);
                    continue;
                }

                var candidate = FindCandidate(reference, candidateAssemblies);
                if (candidate is null)
                {
                    continue;
                }

                TryRegisterSharedAssemblyPath(candidate.Value, reference);
                pending.Enqueue(candidate.Value.Name.Name!);
            }
        }
    }

    private Assembly LoadSharedAssembly(string assemblyName)
    {
        if (_sharedAssemblies.TryGetValue(assemblyName, out var sharedAssembly))
        {
            return sharedAssembly;
        }

        var loadedAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(_sharedAssemblyPaths[assemblyName]);
        ValidateSharedContractCompatibility(_sharedAssemblyNames[assemblyName], loadedAssembly);
        _sharedAssemblies[assemblyName] = loadedAssembly;
        return loadedAssembly;
    }

    private void TryRegisterSharedAssemblyPath(AssemblyCandidate candidate, AssemblyName? requestedAssemblyName = null)
    {
        if (candidate.Name.Name is null || _sharedAssemblies.ContainsKey(candidate.Name.Name))
        {
            return;
        }

        if (requestedAssemblyName is not null && !AssemblyName.ReferenceMatchesDefinition(requestedAssemblyName, candidate.Name))
        {
            throw new InvalidOperationException(
                $"Shared assembly '{candidate.Name.Name}' requested identity '{requestedAssemblyName.FullName}', but candidate '{candidate.Path}' has identity '{candidate.Name.FullName}'.");
        }

        if (_sharedAssemblyPaths.TryGetValue(candidate.Name.Name, out var existingPath))
        {
            if (!AssemblyName.ReferenceMatchesDefinition(_sharedAssemblyNames[candidate.Name.Name], candidate.Name))
            {
                throw new InvalidOperationException(
                    $"Conflicting shared assembly '{candidate.Name.Name}' was found in '{existingPath}' and '{candidate.Path}'. Shared contract dependencies must use a single version per session.");
            }

            return;
        }

        _sharedAssemblyPaths[candidate.Name.Name] = candidate.Path;
        _sharedAssemblyNames[candidate.Name.Name] = candidate.Name;
    }

    private static AssemblyCandidate? FindCandidate(
        AssemblyName requestedAssemblyName,
        IReadOnlyDictionary<string, List<AssemblyCandidate>> candidateAssemblies)
    {
        if (requestedAssemblyName.Name is null || !candidateAssemblies.TryGetValue(requestedAssemblyName.Name, out var candidates))
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            if (AssemblyName.ReferenceMatchesDefinition(requestedAssemblyName, candidate.Name))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void ValidateSharedContractCompatibility(AssemblyName requestedAssemblyName, Assembly loadedAssembly)
    {
        var loadedAssemblyName = loadedAssembly.GetName();
        if (!AssemblyName.ReferenceMatchesDefinition(requestedAssemblyName, loadedAssemblyName))
        {
            throw new InvalidOperationException(
                $"Shared contract assembly '{requestedAssemblyName.Name}' requested identity '{requestedAssemblyName.FullName}', but '{loadedAssemblyName.FullName}' is already loaded for this session.");
        }
    }

    private readonly record struct AssemblyCandidate(string Path, AssemblyName Name);
}
