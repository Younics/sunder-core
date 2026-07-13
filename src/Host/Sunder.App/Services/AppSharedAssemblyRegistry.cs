using System.Reflection;
using System.Runtime.Loader;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Stacks;
using Sunder.Package.Hosting;

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
        [typeof(ISunderRuntimePackageModule).Assembly.GetName().Name!] = typeof(ISunderRuntimePackageModule).Assembly,
        [typeof(IAvaloniaPackageContributionRegistry).Assembly.GetName().Name!] = typeof(IAvaloniaPackageContributionRegistry).Assembly,
        [typeof(IPackageStackContributor).Assembly.GetName().Name!] = typeof(IPackageStackContributor).Assembly,
    };

    private readonly Dictionary<string, Assembly> _packageSharedAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sharedAssemblyPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AssemblyName> _sharedAssemblyNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _optionalHostAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingOptionalHostAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private SharedContractLoadContext _sharedAssemblyLoadContext;

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
            foreach (var candidate in SelectPreferredSharedContractCandidates(candidateAssemblies))
            {
                TryRegisterSharedAssemblyPath(candidate);
            }

            RegisterSharedDependencyClosure(candidateAssemblies);
        }
    }

    public bool RequiresResetForProbeDirectories(IEnumerable<string> probeDirectories)
    {
        lock (_syncRoot)
        {
            var incomingContracts = SelectPreferredSharedContractCandidates(IndexCandidateAssemblies(probeDirectories))
                .ToDictionary(candidate => candidate.Name.Name!, StringComparer.OrdinalIgnoreCase);
            var currentContractNames = _sharedAssemblyNames.Keys
                .Concat(_packageSharedAssemblies.Keys)
                .Where(IsSharedContractAssemblyName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var currentContractName in currentContractNames)
            {
                if (!incomingContracts.ContainsKey(currentContractName))
                {
                    return true;
                }
            }

            foreach (var (assemblyName, candidate) in incomingContracts)
            {
                if (TryGetCurrentSharedAssemblyName(assemblyName, out var currentAssemblyName)
                    && !SharedAssemblyIdentitiesMatch(currentAssemblyName, candidate.Name))
                {
                    return true;
                }
            }

            return false;
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

            if (!_sharedAssemblyPaths.TryGetValue(assemblyName.Name, out var sharedAssemblyPath))
            {
                return null;
            }

            var loadedAssembly = _sharedAssemblyLoadContext.LoadSharedAssembly(sharedAssemblyPath);
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

    private bool TryGetCurrentSharedAssemblyName(string assemblyName, out AssemblyName currentAssemblyName)
    {
        if (_packageSharedAssemblies.TryGetValue(assemblyName, out var loadedAssembly))
        {
            currentAssemblyName = loadedAssembly.GetName();
            return true;
        }

        return _sharedAssemblyNames.TryGetValue(assemblyName, out currentAssemblyName!);
    }

    private static IEnumerable<AssemblyCandidate> SelectPreferredSharedContractCandidates(
        IReadOnlyDictionary<string, List<AssemblyCandidate>> candidateAssemblies)
    {
        foreach (var candidates in candidateAssemblies.Values)
        {
            var contractCandidates = candidates
                .Where(candidate => IsSharedContractAssemblyName(candidate.Name.Name))
                .ToArray();
            if (contractCandidates.Length == 0)
            {
                continue;
            }

            yield return SelectPreferredSharedAssemblyCandidate(contractCandidates);
        }
    }

    private static AssemblyCandidate SelectPreferredSharedAssemblyCandidate(IReadOnlyList<AssemblyCandidate> candidates)
    {
        var selected = candidates[0];
        foreach (var candidate in candidates.Skip(1))
        {
            if (!SharedContractAssemblyPolicy.FamiliesMatch(selected.Name, candidate.Name)
                || NormalizeVersion(selected.Name.Version).Major != NormalizeVersion(candidate.Name.Version).Major)
            {
                throw new InvalidOperationException(
                    $"Conflicting shared assembly '{candidate.Name.Name}' was found in '{selected.Path}' and '{candidate.Path}'. Shared contract dependencies must use one identity family and major version per session.");
            }
            if (SharedAssemblyIdentitiesMatch(selected.Name, candidate.Name)
                && !SharedContractAssemblyPolicy.FilesRepresentSameDefinition(selected.Path, candidate.Path))
            {
                throw new InvalidOperationException(
                    $"Conflicting shared assembly '{candidate.Name.Name}' uses the same unsigned identity for different binary definitions in '{selected.Path}' and '{candidate.Path}'.");
            }

            if (CompareAssemblyVersions(candidate.Name.Version, selected.Name.Version) > 0)
            {
                selected = candidate;
            }
        }

        return selected;
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
        var loadedAssembly = _sharedAssemblyLoadContext.LoadSharedAssembly(_sharedAssemblyPaths[assemblyName]);
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

        if (requestedAssemblyName is not null && !SharedAssemblyIdentitiesMatch(requestedAssemblyName, candidate.Name))
        {
            if (!IsSharedAssemblyReferenceSatisfiedBy(requestedAssemblyName, candidate.Name))
            {
                throw new InvalidOperationException(
                    $"Shared assembly '{candidate.Name.Name}' requested identity '{requestedAssemblyName.FullName}', but candidate '{candidate.Path}' has identity '{candidate.Name.FullName}'.");
            }
        }

        if (_sharedAssemblyPaths.TryGetValue(candidate.Name.Name, out var existingPath))
        {
            var existingName = _sharedAssemblyNames[candidate.Name.Name];
            if (!SharedAssemblyFamiliesMatch(existingName, candidate.Name))
            {
                throw new InvalidOperationException(
                    $"Conflicting shared assembly '{candidate.Name.Name}' was found in '{existingPath}' and '{candidate.Path}'. Shared contract dependencies must use a single public key and culture per session.");
            }

            if (SharedAssemblyIdentitiesMatch(existingName, candidate.Name)
                && !SharedContractAssemblyPolicy.FilesRepresentSameDefinition(existingPath, candidate.Path))
            {
                throw new InvalidOperationException(
                    $"Conflicting shared assembly '{candidate.Name.Name}' uses the same unsigned identity for different binary definitions in '{existingPath}' and '{candidate.Path}'.");
            }

            if (CompareAssemblyVersions(candidate.Name.Version, existingName.Version) <= 0)
            {
                return;
            }

            if (_packageSharedAssemblies.TryGetValue(candidate.Name.Name, out var loadedAssembly)
                && !SharedAssemblyIdentitiesMatch(loadedAssembly.GetName(), candidate.Name))
            {
                throw new InvalidOperationException(
                    $"Shared assembly '{candidate.Name.Name}' cannot be upgraded from '{loadedAssembly.GetName().FullName}' to '{candidate.Name.FullName}' after it has been loaded for this session.");
            }
        }

        _sharedAssemblyPaths[candidate.Name.Name] = candidate.Path;
        _sharedAssemblyNames[candidate.Name.Name] = candidate.Name;
        _sharedAssemblyLoadContext.Register(candidate.Name.Name, candidate.Path);
    }

    private static AssemblyCandidate? FindCandidate(
        AssemblyName requestedAssemblyName,
        IReadOnlyDictionary<string, List<AssemblyCandidate>> candidateAssemblies)
    {
        if (requestedAssemblyName.Name is null || !candidateAssemblies.TryGetValue(requestedAssemblyName.Name, out var candidates))
        {
            return null;
        }

        AssemblyCandidate? selected = null;
        foreach (var candidate in candidates.Where(candidate => IsSharedAssemblyReferenceSatisfiedBy(requestedAssemblyName, candidate.Name)))
        {
            if (selected is null || CompareAssemblyVersions(candidate.Name.Version, selected.Value.Name.Version) > 0)
            {
                selected = candidate;
            }
        }

        return selected;
    }

    private static void ValidateSharedContractCompatibility(AssemblyName requestedAssemblyName, Assembly loadedAssembly)
    {
        var loadedAssemblyName = loadedAssembly.GetName();
        if (!IsSharedAssemblyReferenceSatisfiedBy(requestedAssemblyName, loadedAssemblyName))
        {
            throw new InvalidOperationException(
                $"Shared contract assembly '{requestedAssemblyName.Name}' requested identity '{requestedAssemblyName.FullName}', but '{loadedAssemblyName.FullName}' is already loaded for this session.");
        }
    }

    internal static bool IsSharedAssemblyReferenceSatisfiedBy(AssemblyName requestedAssemblyName, AssemblyName loadedAssemblyName)
        => SharedContractAssemblyPolicy.IsReferenceSatisfiedBy(requestedAssemblyName, loadedAssemblyName);

    private static bool SharedAssemblyFamiliesMatch(AssemblyName left, AssemblyName right)
        => SharedContractAssemblyPolicy.FamiliesMatch(left, right)
           && NormalizeVersion(left.Version).Major == NormalizeVersion(right.Version).Major;

    private static bool SharedAssemblyIdentitiesMatch(AssemblyName left, AssemblyName right)
        => SharedContractAssemblyPolicy.IdentitiesMatch(left, right);

    private static int CompareAssemblyVersions(Version? left, Version? right)
        => SharedContractAssemblyPolicy.CompareVersions(left, right);

    private static Version NormalizeVersion(Version? version)
        => version is null
            ? new Version(0, 0, 0, 0)
            : new Version(
                Math.Max(version.Major, 0),
                Math.Max(version.Minor, 0),
                Math.Max(version.Build, 0),
                Math.Max(version.Revision, 0));

    private static bool IsSharedContractAssemblyName(string? assemblyName)
        => SharedContractAssemblyPolicy.IsSharedContract(assemblyName);

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

    private SharedContractLoadContext CreateSharedAssemblyLoadContext()
        => new("App", ResolveHostSharedAssembly);

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

}
