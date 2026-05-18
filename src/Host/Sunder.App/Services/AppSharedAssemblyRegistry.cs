using System.Reflection;
using System.Runtime.Loader;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class AppSharedAssemblyRegistry : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, Assembly> _hostSharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        [typeof(Control).Assembly.GetName().Name!] = typeof(Control).Assembly,
        [typeof(Avalonia.AvaloniaObject).Assembly.GetName().Name!] = typeof(Avalonia.AvaloniaObject).Assembly,
        [typeof(Avalonia.Markup.Xaml.AvaloniaXamlLoader).Assembly.GetName().Name!] = typeof(Avalonia.Markup.Xaml.AvaloniaXamlLoader).Assembly,
        [typeof(IServiceCollection).Assembly.GetName().Name!] = typeof(IServiceCollection).Assembly,
        [typeof(ILoggerFactory).Assembly.GetName().Name!] = typeof(ILoggerFactory).Assembly,
        [typeof(ISunderPackageModule).Assembly.GetName().Name!] = typeof(ISunderPackageModule).Assembly,
    };

    private readonly Dictionary<string, Assembly> _packageSharedAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sharedAssemblyPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AssemblyName> _sharedAssemblyNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _optionalHostAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingOptionalHostAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private SharedPackageAssemblyLoadContext _sharedAssemblyLoadContext;

    public AppSharedAssemblyRegistry(IEnumerable<string> probeDirectories)
    {
        _sharedAssemblyLoadContext = CreateSharedAssemblyLoadContext();

        RegisterOptionalHostAssemblyName("Avalonia");
        RegisterOptionalHostAssemblyName("AvaloniaEdit");
        RegisterOptionalHostAssemblyName("Avalonia.Markup");
        RegisterOptionalHostAssemblyName("Avalonia.Dialogs");
        RegisterOptionalHostAssemblyName("Avalonia.Remote.Protocol");
        RegisterOptionalHostAssemblyName("Avalonia.Metal");
        RegisterOptionalHostAssemblyName("Avalonia.OpenGL");
        RegisterOptionalHostAssemblyName("Avalonia.Vulkan");
        RegisterOptionalHostAssemblyName("Avalonia.MicroCom");
        RegisterOptionalHostAssemblyName("MicroCom.Runtime");

        AddProbeDirectories(probeDirectories);
    }

    public void AddProbeDirectories(IEnumerable<string> probeDirectories)
    {
        lock (_syncRoot)
        {
            var candidateAssemblies = IndexCandidateAssemblies(probeDirectories);
            foreach (var candidate in candidateAssemblies.Values.SelectMany(static candidate => candidate)
                         .Where(static candidate => candidate.Name.Name?.EndsWith(".Contracts", StringComparison.OrdinalIgnoreCase) == true))
            {
                TryRegisterSharedAssemblyPath(candidate);
            }

            RegisterSharedDependencyClosure(candidateAssemblies);
        }
    }

    public void RemoveProbeDirectories(IEnumerable<string> probeDirectories)
    {
        var normalizedProbeDirectories = probeDirectories
            .Where(static probeDirectory => !string.IsNullOrWhiteSpace(probeDirectory))
            .Select(NormalizeDirectoryPath)
            .ToArray();
        if (normalizedProbeDirectories.Length == 0)
        {
            return;
        }

        lock (_syncRoot)
        {
            foreach (var assemblyName in _sharedAssemblyPaths
                         .Where(entry => IsPathInDirectories(entry.Value, normalizedProbeDirectories))
                         .Select(static entry => entry.Key)
                         .ToArray())
            {
                _sharedAssemblyPaths.Remove(assemblyName);
                _sharedAssemblyNames.Remove(assemblyName);
            }
        }
    }

    public void ResetPackageAssemblies()
    {
        lock (_syncRoot)
        {
            _sharedAssemblyPaths.Clear();
            _sharedAssemblyNames.Clear();
            _packageSharedAssemblies.Clear();
            var previousLoadContext = _sharedAssemblyLoadContext;
            _sharedAssemblyLoadContext = CreateSharedAssemblyLoadContext();
            previousLoadContext.Unload();
        }
    }

    private void RegisterOptionalHostAssemblyName(string assemblyName)
    {
        _optionalHostAssemblies.Add(assemblyName);
    }

    public Assembly? ResolveSharedAssembly(AssemblyName assemblyName)
    {
        lock (_syncRoot)
        {
            if (assemblyName.Name is null)
            {
                return null;
            }

            if (_hostSharedAssemblies.TryGetValue(assemblyName.Name, out var hostAssembly))
            {
                // Host-owned boundary assemblies are authoritative for the session.
                // Packages may reference older patch/minor versions of the same host-shared assembly.
                return hostAssembly;
            }

            if (_optionalHostAssemblies.Contains(assemblyName.Name)
                && TryLoadOptionalHostAssembly(assemblyName.Name, out var optionalHostAssembly))
            {
                return optionalHostAssembly;
            }

            if (_packageSharedAssemblies.TryGetValue(assemblyName.Name, out var packageAssembly))
            {
                ValidateSharedContractCompatibility(assemblyName, packageAssembly);
                return packageAssembly;
            }

            if (!_sharedAssemblyPaths.TryGetValue(assemblyName.Name, out var sharedAssemblyPath))
            {
                return null;
            }

            var loadedAssembly = _sharedAssemblyLoadContext.LoadPackageSharedAssembly(sharedAssemblyPath);
            ValidateSharedContractCompatibility(assemblyName, loadedAssembly);
            _packageSharedAssemblies[assemblyName.Name] = loadedAssembly;
            return loadedAssembly;
        }
    }

    private bool TryLoadOptionalHostAssembly(string assemblyName, out Assembly assembly)
    {
        assembly = null!;
        if (_missingOptionalHostAssemblies.Contains(assemblyName))
        {
            return false;
        }

        try
        {
            assembly = Assembly.Load(new AssemblyName(assemblyName));
            _hostSharedAssemblies[assemblyName] = assembly;
            return true;
        }
        catch
        {
            _missingOptionalHostAssemblies.Add(assemblyName);
            return false;
        }
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
                if (reference.Name is null || _hostSharedAssemblies.ContainsKey(reference.Name))
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
        if (_hostSharedAssemblies.TryGetValue(assemblyName, out var hostAssembly))
        {
            return hostAssembly;
        }

        if (_packageSharedAssemblies.TryGetValue(assemblyName, out var packageAssembly))
        {
            return packageAssembly;
        }

        var requestedAssemblyName = _sharedAssemblyNames[assemblyName];
        var loadedAssembly = _sharedAssemblyLoadContext.LoadPackageSharedAssembly(_sharedAssemblyPaths[assemblyName]);
        ValidateSharedContractCompatibility(requestedAssemblyName, loadedAssembly);
        _packageSharedAssemblies[assemblyName] = loadedAssembly;
        return loadedAssembly;
    }

    private void TryRegisterSharedAssemblyPath(AssemblyCandidate candidate, AssemblyName? requestedAssemblyName = null)
    {
        if (candidate.Name.Name is null || _hostSharedAssemblies.ContainsKey(candidate.Name.Name))
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
            var existingName = _sharedAssemblyNames[candidate.Name.Name];
            if (!AssemblyName.ReferenceMatchesDefinition(existingName, candidate.Name))
            {
                throw new InvalidOperationException(
                    $"Conflicting shared assembly '{candidate.Name.Name}' was found in '{existingPath}' and '{candidate.Path}'. Shared contract dependencies must use a single version per session.");
            }

            return;
        }

        _sharedAssemblyPaths[candidate.Name.Name] = candidate.Path;
        _sharedAssemblyNames[candidate.Name.Name] = candidate.Name;
        _sharedAssemblyLoadContext.RegisterPackageSharedAssembly(candidate.Name.Name, candidate.Path);
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

    private Assembly? ResolveHostSharedAssembly(AssemblyName assemblyName)
    {
        if (assemblyName.Name is null)
        {
            return null;
        }

        return _hostSharedAssemblies.TryGetValue(assemblyName.Name, out var assembly)
            ? assembly
            : null;
    }

    private static bool IsPathInDirectories(string path, IReadOnlyList<string> normalizedDirectories)
    {
        var normalizedPath = Path.GetFullPath(path);
        return normalizedDirectories.Any(directory => normalizedPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return fullPath.EndsWith(Path.DirectorySeparatorChar) || fullPath.EndsWith(Path.AltDirectorySeparatorChar)
            ? fullPath
            : fullPath + Path.DirectorySeparatorChar;
    }

    private SharedPackageAssemblyLoadContext CreateSharedAssemblyLoadContext()
        => new(ResolveHostSharedAssembly);

    public void Dispose()
    {
        lock (_syncRoot)
        {
            _sharedAssemblyPaths.Clear();
            _sharedAssemblyNames.Clear();
            _packageSharedAssemblies.Clear();
            _sharedAssemblyLoadContext.Unload();
        }
    }

    private readonly record struct AssemblyCandidate(string Path, AssemblyName Name);

    private sealed class SharedPackageAssemblyLoadContext(Func<AssemblyName, Assembly?> resolveHostAssembly)
        : AssemblyLoadContext($"Sunder.App.SharedContracts.{Guid.NewGuid():N}", isCollectible: true)
    {
        private readonly Dictionary<string, string> _assemblyPaths = new(StringComparer.OrdinalIgnoreCase);

        public void RegisterPackageSharedAssembly(string assemblyName, string path)
            => _assemblyPaths[assemblyName] = path;

        public Assembly LoadPackageSharedAssembly(string path)
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
}
