using System.Reflection;

namespace Sunder.Package.Hosting;

internal readonly record struct SharedAssemblyCandidate(string Path, AssemblyName Name);

internal sealed class SharedContractAssemblyRegistryCore : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, Assembly> _hostSharedAssemblies;
    private readonly Dictionary<string, Assembly> _packageSharedAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _optionalHostAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingOptionalHostAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly SharedContractLoadContext _sharedAssemblyLoadContext;

    public SharedContractAssemblyRegistryCore(string ownerName, Dictionary<string, Assembly> hostSharedAssemblies)
    {
        _hostSharedAssemblies = hostSharedAssemblies;
        _sharedAssemblyLoadContext = new SharedContractLoadContext(ownerName, ResolveHostSharedAssembly);
    }

    public Dictionary<string, string> SharedAssemblyPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, AssemblyName> SharedAssemblyNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterOptionalHostAssemblyName(string assemblyName)
        => _optionalHostAssemblies.Add(assemblyName);

    public void AddProbeDirectories(IEnumerable<string> probeDirectories)
    {
        lock (_syncRoot)
        {
            var candidateAssemblies = IndexCandidateAssemblies(probeDirectories);
            foreach (var candidate in SelectPreferredSharedContractCandidates(candidateAssemblies))
            {
                RegisterCandidate(candidate);
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
            foreach (var assemblyName in SharedAssemblyPaths
                         .Where(entry => IsPathInDirectories(entry.Value, normalizedProbeDirectories))
                         .Select(static entry => entry.Key)
                         .ToArray())
            {
                SharedAssemblyPaths.Remove(assemblyName);
                SharedAssemblyNames.Remove(assemblyName);
            }
        }
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
                ValidateSharedContractCompatibility(assemblyName, hostAssembly);
                return hostAssembly;
            }

            if (_optionalHostAssemblies.Contains(assemblyName.Name)
                && TryLoadOptionalHostAssembly(assemblyName.Name, out var optionalHostAssembly))
            {
                ValidateSharedContractCompatibility(assemblyName, optionalHostAssembly);
                return optionalHostAssembly;
            }

            if (_packageSharedAssemblies.TryGetValue(assemblyName.Name, out var packageAssembly))
            {
                ValidateSharedContractCompatibility(assemblyName, packageAssembly);
                return packageAssembly;
            }

            if (!SharedAssemblyPaths.TryGetValue(assemblyName.Name, out var sharedAssemblyPath))
            {
                return null;
            }

            var loadedAssembly = _sharedAssemblyLoadContext.LoadSharedAssembly(sharedAssemblyPath);
            ValidateSharedContractCompatibility(assemblyName, loadedAssembly);
            _packageSharedAssemblies[assemblyName.Name] = loadedAssembly;
            return loadedAssembly;
        }
    }

    public void RegisterCandidate(SharedAssemblyCandidate candidate, AssemblyName? requestedAssemblyName = null)
    {
        lock (_syncRoot)
        {
            if (candidate.Name.Name is null || _hostSharedAssemblies.ContainsKey(candidate.Name.Name))
            {
                return;
            }

            if (requestedAssemblyName is not null
                && !SharedContractAssemblyPolicy.IdentitiesMatch(requestedAssemblyName, candidate.Name)
                && !SharedContractAssemblyPolicy.IsReferenceSatisfiedBy(requestedAssemblyName, candidate.Name))
            {
                throw new InvalidOperationException(
                    $"Shared assembly '{candidate.Name.Name}' requested identity '{requestedAssemblyName.FullName}', but candidate '{candidate.Path}' has identity '{candidate.Name.FullName}'.");
            }

            if (SharedAssemblyPaths.TryGetValue(candidate.Name.Name, out var existingPath))
            {
                var existingName = SharedAssemblyNames[candidate.Name.Name];
                if (!FamiliesAndMajorMatch(existingName, candidate.Name))
                {
                    throw new InvalidOperationException(
                        $"Conflicting shared assembly '{candidate.Name.Name}' was found in '{existingPath}' and '{candidate.Path}'. Shared contract dependencies must use a single public key and culture per session.");
                }

                if (SharedContractAssemblyPolicy.IdentitiesMatch(existingName, candidate.Name)
                    && !SharedContractAssemblyPolicy.FilesRepresentSameDefinition(existingPath, candidate.Path))
                {
                    throw new InvalidOperationException(
                        $"Conflicting shared assembly '{candidate.Name.Name}' uses the same unsigned identity for different binary definitions in '{existingPath}' and '{candidate.Path}'.");
                }

                if (SharedContractAssemblyPolicy.CompareVersions(candidate.Name.Version, existingName.Version) <= 0)
                {
                    return;
                }

                if (_packageSharedAssemblies.TryGetValue(candidate.Name.Name, out var loadedAssembly)
                    && !SharedContractAssemblyPolicy.IdentitiesMatch(loadedAssembly.GetName(), candidate.Name))
                {
                    throw new InvalidOperationException(
                        $"Shared assembly '{candidate.Name.Name}' cannot be upgraded from '{loadedAssembly.GetName().FullName}' to '{candidate.Name.FullName}' after it has been loaded for this session.");
                }
            }

            SharedAssemblyPaths[candidate.Name.Name] = candidate.Path;
            SharedAssemblyNames[candidate.Name.Name] = candidate.Name;
            _sharedAssemblyLoadContext.Register(candidate.Name.Name, candidate.Path);
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            SharedAssemblyPaths.Clear();
            SharedAssemblyNames.Clear();
            _packageSharedAssemblies.Clear();
            _sharedAssemblyLoadContext.Unload();
        }
    }

    private static Dictionary<string, List<SharedAssemblyCandidate>> IndexCandidateAssemblies(
        IEnumerable<string> probeDirectories)
    {
        var candidates = new Dictionary<string, List<SharedAssemblyCandidate>>(StringComparer.OrdinalIgnoreCase);
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

                namedCandidates.Add(new SharedAssemblyCandidate(assemblyPath, assemblyName));
            }
        }

        return candidates;
    }

    private static IEnumerable<SharedAssemblyCandidate> SelectPreferredSharedContractCandidates(
        IReadOnlyDictionary<string, List<SharedAssemblyCandidate>> candidateAssemblies)
    {
        foreach (var candidates in candidateAssemblies.Values)
        {
            var contractCandidates = candidates
                .Where(candidate => SharedContractAssemblyPolicy.IsSharedContract(candidate.Name.Name))
                .ToArray();
            if (contractCandidates.Length > 0)
            {
                yield return SelectPreferredSharedAssemblyCandidate(contractCandidates);
            }
        }
    }

    private static SharedAssemblyCandidate SelectPreferredSharedAssemblyCandidate(
        IReadOnlyList<SharedAssemblyCandidate> candidates)
    {
        var selected = candidates[0];
        foreach (var candidate in candidates.Skip(1))
        {
            if (!FamiliesAndMajorMatch(selected.Name, candidate.Name))
            {
                throw new InvalidOperationException(
                    $"Conflicting shared assembly '{candidate.Name.Name}' was found in '{selected.Path}' and '{candidate.Path}'. Shared contract dependencies must use one identity family and major version per session.");
            }
            if (SharedContractAssemblyPolicy.IdentitiesMatch(selected.Name, candidate.Name)
                && !SharedContractAssemblyPolicy.FilesRepresentSameDefinition(selected.Path, candidate.Path))
            {
                throw new InvalidOperationException(
                    $"Conflicting shared assembly '{candidate.Name.Name}' uses the same unsigned identity for different binary definitions in '{selected.Path}' and '{candidate.Path}'.");
            }

            if (SharedContractAssemblyPolicy.CompareVersions(candidate.Name.Version, selected.Name.Version) > 0)
            {
                selected = candidate;
            }
        }

        return selected;
    }

    private void RegisterSharedDependencyClosure(
        IReadOnlyDictionary<string, List<SharedAssemblyCandidate>> candidateAssemblies)
    {
        var pending = new Queue<string>(SharedAssemblyPaths.Keys);
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

                if (SharedAssemblyPaths.ContainsKey(reference.Name))
                {
                    pending.Enqueue(reference.Name);
                    continue;
                }

                var candidate = FindCandidate(reference, candidateAssemblies);
                if (candidate is null)
                {
                    continue;
                }

                RegisterCandidate(candidate.Value, reference);
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

        var requestedAssemblyName = SharedAssemblyNames[assemblyName];
        var loadedAssembly = _sharedAssemblyLoadContext.LoadSharedAssembly(SharedAssemblyPaths[assemblyName]);
        ValidateSharedContractCompatibility(requestedAssemblyName, loadedAssembly);
        _packageSharedAssemblies[assemblyName] = loadedAssembly;
        return loadedAssembly;
    }

    private static SharedAssemblyCandidate? FindCandidate(
        AssemblyName requestedAssemblyName,
        IReadOnlyDictionary<string, List<SharedAssemblyCandidate>> candidateAssemblies)
    {
        if (requestedAssemblyName.Name is null
            || !candidateAssemblies.TryGetValue(requestedAssemblyName.Name, out var candidates))
        {
            return null;
        }

        SharedAssemblyCandidate? selected = null;
        foreach (var candidate in candidates.Where(candidate =>
                     SharedContractAssemblyPolicy.IsReferenceSatisfiedBy(requestedAssemblyName, candidate.Name)))
        {
            if (selected is null
                || SharedContractAssemblyPolicy.CompareVersions(candidate.Name.Version, selected.Value.Name.Version) > 0)
            {
                selected = candidate;
            }
        }

        return selected;
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

    private Assembly? ResolveHostSharedAssembly(AssemblyName assemblyName)
        => assemblyName.Name is not null
           && _hostSharedAssemblies.TryGetValue(assemblyName.Name, out var assembly)
            ? assembly
            : null;

    private static void ValidateSharedContractCompatibility(AssemblyName requestedAssemblyName, Assembly loadedAssembly)
    {
        var loadedAssemblyName = loadedAssembly.GetName();
        if (!SharedContractAssemblyPolicy.IsReferenceSatisfiedBy(requestedAssemblyName, loadedAssemblyName))
        {
            throw new InvalidOperationException(
                $"Shared contract assembly '{requestedAssemblyName.Name}' requested identity '{requestedAssemblyName.FullName}', but '{loadedAssemblyName.FullName}' is already loaded for this session.");
        }
    }

    private static bool FamiliesAndMajorMatch(AssemblyName left, AssemblyName right)
        => SharedContractAssemblyPolicy.FamiliesMatch(left, right)
           && NormalizeVersion(left.Version).Major == NormalizeVersion(right.Version).Major;

    private static Version NormalizeVersion(Version? version)
        => version is null
            ? new Version(0, 0, 0, 0)
            : new Version(
                Math.Max(version.Major, 0),
                Math.Max(version.Minor, 0),
                Math.Max(version.Build, 0),
                Math.Max(version.Revision, 0));

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
}
