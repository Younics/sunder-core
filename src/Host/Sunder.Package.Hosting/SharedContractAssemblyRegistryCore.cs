using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Sunder.Package.Hosting;

internal readonly record struct SharedAssemblyCandidate(string Path, AssemblyName Name);

internal sealed class SharedContractAssemblyRegistryCore : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, Assembly> _hostSharedAssemblies;
    private readonly Dictionary<string, Assembly> _packageSharedAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LoadedSharedAssembly> _loadedCandidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<SharedAssemblyCandidate>> _probeDirectoryCandidates = new(PathComparer);
    private readonly List<SharedAssemblyCandidate> _manualCandidates = [];
    private readonly HashSet<string> _optionalHostAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingOptionalHostAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly SharedContractLoadContext _sharedAssemblyLoadContext;

    public SharedContractAssemblyRegistryCore(string ownerName, Dictionary<string, Assembly> hostSharedAssemblies)
    {
        _hostSharedAssemblies = hostSharedAssemblies;
        _sharedAssemblyLoadContext = new SharedContractLoadContext(
            ownerName,
            ResolveHostSharedAssembly,
            TrackLoadedSharedAssembly);
    }

    public Dictionary<string, string> SharedAssemblyPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, AssemblyName> SharedAssemblyNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterOptionalHostAssemblyName(string assemblyName)
        => _optionalHostAssemblies.Add(assemblyName);

    public void AddProbeDirectories(IEnumerable<string> probeDirectories)
    {
        lock (_syncRoot)
        {
            var proposedDirectories = new Dictionary<string, IReadOnlyList<SharedAssemblyCandidate>>(
                _probeDirectoryCandidates,
                PathComparer);
            foreach (var probeDirectory in NormalizeProbeDirectories(probeDirectories))
            {
                proposedDirectories[probeDirectory] = IndexProbeDirectory(probeDirectory);
            }

            var selection = BuildSelection(proposedDirectories, _manualCandidates);
            EnsureSelectionCanReplaceCurrent(selection);
            ReplaceDictionary(_probeDirectoryCandidates, proposedDirectories);
            ApplySelection(selection);
        }
    }

    public bool TryRemoveProbeDirectories(IEnumerable<string> probeDirectories)
    {
        lock (_syncRoot)
        {
            var normalizedDirectories = NormalizeProbeDirectories(probeDirectories);
            if (normalizedDirectories.Count == 0)
            {
                return true;
            }

            var proposedDirectories = new Dictionary<string, IReadOnlyList<SharedAssemblyCandidate>>(
                _probeDirectoryCandidates,
                PathComparer);
            foreach (var probeDirectory in normalizedDirectories)
            {
                proposedDirectories.Remove(probeDirectory);
            }

            var selection = BuildSelection(proposedDirectories, _manualCandidates);
            if (!CanReplaceLoadedSelection(selection))
            {
                return false;
            }

            ReplaceDictionary(_probeDirectoryCandidates, proposedDirectories);
            ApplySelection(selection);
            return true;
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
            if (!SharedContractAssemblyPolicy.IsReferenceSatisfiedBy(
                    assemblyName,
                    SharedAssemblyNames[assemblyName.Name]))
            {
                throw new InvalidOperationException(
                    $"Shared contract assembly '{assemblyName.Name}' requested identity '{assemblyName.FullName}', but selected candidate '{SharedAssemblyNames[assemblyName.Name].FullName}' is incompatible.");
            }

            var loadedAssembly = _sharedAssemblyLoadContext.LoadSharedAssembly(sharedAssemblyPath);
            ValidateSharedContractCompatibility(assemblyName, loadedAssembly);
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

            var proposedCandidates = _manualCandidates.Append(candidate).ToArray();
            var selection = BuildSelection(_probeDirectoryCandidates, proposedCandidates);
            EnsureSelectionCanReplaceCurrent(selection);
            _manualCandidates.Add(candidate);
            ApplySelection(selection);
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            SharedAssemblyPaths.Clear();
            SharedAssemblyNames.Clear();
            _packageSharedAssemblies.Clear();
            _loadedCandidates.Clear();
            _probeDirectoryCandidates.Clear();
            _manualCandidates.Clear();
            _sharedAssemblyLoadContext.Unload();
        }
    }

    private Dictionary<string, SharedAssemblyCandidate> BuildSelection(
        IReadOnlyDictionary<string, IReadOnlyList<SharedAssemblyCandidate>> probeDirectories,
        IReadOnlyCollection<SharedAssemblyCandidate> manualCandidates)
    {
        var allCandidates = probeDirectories.Values
            .SelectMany(static candidates => candidates)
            .Concat(manualCandidates)
            .GroupBy(static candidate => candidate.Name.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var rootNames = allCandidates
            .Where(static entry => entry.Value.Any(candidate =>
                SharedContractAssemblyPolicy.IsSharedContract(candidate.Name.Name)))
            .Select(static entry => entry.Key)
            .Concat(manualCandidates.Select(static candidate => candidate.Name.Name!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selected = new Dictionary<string, SharedAssemblyCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var rootName in rootNames)
        {
            selected[rootName] = SelectPreferredSharedAssemblyCandidate(allCandidates[rootName]);
        }

        var pending = new Queue<string>(rootNames);
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryDequeue(out var assemblyName))
        {
            if (!processed.Add(assemblyName)
                || !selected.TryGetValue(assemblyName, out var selectedAssembly)
                || !File.Exists(selectedAssembly.Path))
            {
                continue;
            }

            foreach (var reference in ReadAssemblyReferences(selectedAssembly.Path))
            {
                if (reference.Name is null
                    || _hostSharedAssemblies.ContainsKey(reference.Name)
                    || _optionalHostAssemblies.Contains(reference.Name))
                {
                    continue;
                }

                if (selected.TryGetValue(reference.Name, out var existing))
                {
                    if (!SharedContractAssemblyPolicy.IsReferenceSatisfiedBy(reference, existing.Name))
                    {
                        throw new InvalidOperationException(
                            $"Shared assembly '{selectedAssembly.Name.Name}' requires '{reference.FullName}', but selected candidate '{existing.Name.FullName}' is incompatible.");
                    }
                    pending.Enqueue(reference.Name);
                    continue;
                }

                var candidate = FindCandidate(reference, allCandidates);
                if (candidate is null)
                {
                    continue;
                }

                selected[reference.Name] = candidate.Value;
                pending.Enqueue(reference.Name);
            }
        }

        return selected;
    }

    private void EnsureSelectionCanReplaceCurrent(
        IReadOnlyDictionary<string, SharedAssemblyCandidate> selection)
    {
        if (!CanReplaceLoadedSelection(selection))
        {
            throw new InvalidOperationException(
                "Shared contract candidates cannot be changed after a different assembly definition has been loaded for this session.");
        }
    }

    private bool CanReplaceLoadedSelection(
        IReadOnlyDictionary<string, SharedAssemblyCandidate> selection)
    {
        foreach (var pinnedAssemblyName in GetPinnedAssemblyNames())
        {
            if (!selection.TryGetValue(pinnedAssemblyName, out var replacement))
            {
                return false;
            }
            if (!_loadedCandidates.TryGetValue(pinnedAssemblyName, out var loaded))
            {
                continue;
            }
            if (!SharedContractAssemblyPolicy.IdentitiesMatch(loaded.Name, replacement.Name))
            {
                return false;
            }

            try
            {
                if (!SharedContractAssemblyPolicy.FileMatchesDefinition(
                        replacement.Path,
                        loaded.DefinitionHash))
                {
                    return false;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        return true;
    }

    private IReadOnlySet<string> GetPinnedAssemblyNames()
    {
        var pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(_loadedCandidates.Keys);
        while (pending.TryDequeue(out var assemblyName))
        {
            if (!pinned.Add(assemblyName))
            {
                continue;
            }
            if (!SharedAssemblyPaths.TryGetValue(assemblyName, out var path)
                || !File.Exists(path))
            {
                pinned.UnionWith(SharedAssemblyPaths.Keys);
                return pinned;
            }

            IReadOnlyList<AssemblyName> references;
            try
            {
                references = ReadAssemblyReferences(path);
            }
            catch (Exception exception) when (exception is BadImageFormatException or IOException or UnauthorizedAccessException)
            {
                pinned.UnionWith(SharedAssemblyPaths.Keys);
                return pinned;
            }
            foreach (var reference in references)
            {
                if (reference.Name is not null && SharedAssemblyPaths.ContainsKey(reference.Name))
                {
                    pending.Enqueue(reference.Name);
                }
            }
        }
        return pinned;
    }

    private void ApplySelection(IReadOnlyDictionary<string, SharedAssemblyCandidate> selection)
    {
        SharedAssemblyPaths.Clear();
        SharedAssemblyNames.Clear();
        foreach (var candidate in selection.Values.OrderBy(
                     static candidate => candidate.Name.Name,
                     StringComparer.OrdinalIgnoreCase))
        {
            SharedAssemblyPaths[candidate.Name.Name!] = candidate.Path;
            SharedAssemblyNames[candidate.Name.Name!] = candidate.Name;
        }
        _sharedAssemblyLoadContext.ReplaceMappings(SharedAssemblyPaths);
    }

    private static IReadOnlyList<SharedAssemblyCandidate> IndexProbeDirectory(string probeDirectory)
    {
        if (!Directory.Exists(probeDirectory))
        {
            return [];
        }

        var candidates = new List<SharedAssemblyCandidate>();
        foreach (var assemblyPath in Directory.EnumerateFiles(probeDirectory, "*.dll", SearchOption.TopDirectoryOnly)
                     .OrderBy(static path => path, PathComparer))
        {
            try
            {
                var assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
                if (!string.IsNullOrWhiteSpace(assemblyName.Name))
                {
                    candidates.Add(new SharedAssemblyCandidate(assemblyPath, assemblyName));
                }
            }
            catch (Exception exception) when (exception is BadImageFormatException or FileLoadException or IOException or UnauthorizedAccessException)
            {
            }
        }
        return candidates;
    }

    private static SharedAssemblyCandidate SelectPreferredSharedAssemblyCandidate(
        IReadOnlyList<SharedAssemblyCandidate> candidates)
    {
        var ordered = candidates
            .OrderByDescending(static candidate => candidate.Name.Version, VersionComparer.Instance)
            .ThenBy(static candidate => candidate.Path, PathComparer)
            .ToArray();
        var selected = ordered[0];
        foreach (var candidate in ordered.Skip(1))
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
        }
        return selected;
    }

    private static SharedAssemblyCandidate? FindCandidate(
        AssemblyName requestedAssemblyName,
        IReadOnlyDictionary<string, SharedAssemblyCandidate[]> candidateAssemblies)
    {
        if (requestedAssemblyName.Name is null
            || !candidateAssemblies.TryGetValue(requestedAssemblyName.Name, out var candidates))
        {
            return null;
        }

        var compatible = candidates
            .Where(candidate => SharedContractAssemblyPolicy.IsReferenceSatisfiedBy(
                requestedAssemblyName,
                candidate.Name))
            .ToArray();
        return compatible.Length == 0
            ? null
            : SelectPreferredSharedAssemblyCandidate(compatible);
    }

    private static IReadOnlyList<AssemblyName> ReadAssemblyReferences(string assemblyPath)
    {
        using var stream = new FileStream(
            assemblyPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
        {
            return [];
        }

        var metadata = peReader.GetMetadataReader();
        var references = new List<AssemblyName>();
        foreach (var handle in metadata.AssemblyReferences)
        {
            var reference = metadata.GetAssemblyReference(handle);
            var assemblyName = new AssemblyName
            {
                Name = metadata.GetString(reference.Name),
                Version = reference.Version,
                CultureName = reference.Culture.IsNil ? null : metadata.GetString(reference.Culture),
            };
            var keyOrToken = reference.PublicKeyOrToken.IsNil
                ? []
                : metadata.GetBlobBytes(reference.PublicKeyOrToken);
            if ((reference.Flags & AssemblyFlags.PublicKey) != 0)
            {
                assemblyName.SetPublicKey(keyOrToken);
            }
            else
            {
                assemblyName.SetPublicKeyToken(keyOrToken);
            }
            references.Add(assemblyName);
        }
        return references;
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

    private void TrackLoadedSharedAssembly(string path, Assembly assembly)
    {
        lock (_syncRoot)
        {
            var assemblyName = assembly.GetName();
            if (assemblyName.Name is null)
            {
                return;
            }
            _packageSharedAssemblies[assemblyName.Name] = assembly;
            _loadedCandidates[assemblyName.Name] = new LoadedSharedAssembly(
                assemblyName,
                SharedContractAssemblyPolicy.ComputeDefinitionHash(path));
        }
    }

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

    private static IReadOnlyList<string> NormalizeProbeDirectories(IEnumerable<string> probeDirectories)
        => probeDirectories
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(PathComparer)
            .ToArray();

    private static void ReplaceDictionary<TValue>(
        IDictionary<string, TValue> destination,
        IReadOnlyDictionary<string, TValue> source)
    {
        destination.Clear();
        foreach (var entry in source)
        {
            destination.Add(entry.Key, entry.Value);
        }
    }

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class VersionComparer : IComparer<Version?>
    {
        public static VersionComparer Instance { get; } = new();

        public int Compare(Version? left, Version? right)
            => SharedContractAssemblyPolicy.CompareVersions(left, right);
    }

    private sealed record LoadedSharedAssembly(
        AssemblyName Name,
        byte[] DefinitionHash);
}
