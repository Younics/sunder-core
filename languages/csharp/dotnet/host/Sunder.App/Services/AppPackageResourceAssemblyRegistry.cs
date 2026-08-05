using System.Collections;
using System.Reflection;

namespace Sunder.App.Services;

public sealed class AppPackageResourceAssemblyRegistry
{
    private readonly Dictionary<string, List<AppPackageResourceAssemblyRegistration>> _registrationsByAssemblyName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Assembly, AppPackageAvaloniaResourceCatalog?> _catalogsByAssembly = [];
    private readonly object _syncRoot = new();
    private long _nextGeneration;

    public IReadOnlyList<string> RegisterPackageAssembly(string packageId, Assembly assembly)
    {
        var assemblyName = assembly.GetName().Name;
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(assemblyName))
        {
            return [];
        }

        var changedAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_syncRoot)
        {
            RemovePackageAssemblyRegistrations(packageId, assemblyName, changedAssemblyNames);
            if (!_registrationsByAssemblyName.TryGetValue(assemblyName, out var registrations))
            {
                registrations = [];
                _registrationsByAssemblyName[assemblyName] = registrations;
            }

            registrations.Add(new AppPackageResourceAssemblyRegistration(packageId, assembly, ++_nextGeneration));
            changedAssemblyNames.Add(assemblyName);
            PruneUnregisteredCatalogs();
        }

        return changedAssemblyNames.ToArray();
    }

    public IReadOnlyList<string> ReplacePackageAssemblies(
        IReadOnlyList<(string PackageId, Assembly Assembly)> packageAssemblies)
    {
        var changedAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_syncRoot)
        {
            changedAssemblyNames.UnionWith(_registrationsByAssemblyName.Keys);
            _registrationsByAssemblyName.Clear();
            foreach (var (packageId, assembly) in packageAssemblies)
            {
                var assemblyName = assembly.GetName().Name;
                if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(assemblyName))
                {
                    continue;
                }

                if (!_registrationsByAssemblyName.TryGetValue(assemblyName, out var registrations))
                {
                    registrations = [];
                    _registrationsByAssemblyName[assemblyName] = registrations;
                }

                registrations.Add(new AppPackageResourceAssemblyRegistration(packageId, assembly, ++_nextGeneration));
                changedAssemblyNames.Add(assemblyName);
            }

            PruneUnregisteredCatalogs();
        }

        return changedAssemblyNames.ToArray();
    }

    public IReadOnlyList<string> RemovePackage(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return [];
        }

        var changedAssemblyNames = new List<string>();
        lock (_syncRoot)
        {
            RemovePackageRegistrations(packageId, changedAssemblyNames);
            PruneUnregisteredCatalogs();
        }

        return changedAssemblyNames;
    }

    public bool TryOpenAvaloniaResource(
        string assemblyName,
        string path,
        out Stream stream,
        out Assembly assembly,
        out bool assemblyRegistered)
    {
        stream = null!;
        assembly = null!;
        assemblyRegistered = false;

        AppPackageAvaloniaResourceCatalog? catalog;
        lock (_syncRoot)
        {
            if (!TryResolveRegistration(assemblyName, out var registration))
            {
                return false;
            }

            assemblyRegistered = true;
            assembly = registration.Assembly;
            catalog = ResolveCatalog(registration.Assembly);
        }

        return catalog is not null && catalog.TryOpen(path, out stream);
    }

    public bool TryGetAssembly(string assemblyName, out Assembly assembly)
    {
        lock (_syncRoot)
        {
            if (TryResolveRegistration(assemblyName, out var registration))
            {
                assembly = registration.Assembly;
                return true;
            }
        }

        assembly = null!;
        return false;
    }

    public bool TryGetAvaloniaResourceUris(string assemblyName, string path, out IReadOnlyList<Uri> uris, out bool assemblyRegistered)
    {
        uris = [];
        assemblyRegistered = false;

        AppPackageAvaloniaResourceCatalog? catalog;
        Assembly assembly;
        lock (_syncRoot)
        {
            if (!TryResolveRegistration(assemblyName, out var registration))
            {
                return false;
            }

            assemblyRegistered = true;
            assembly = registration.Assembly;
            catalog = ResolveCatalog(registration.Assembly);
        }

        if (catalog is null)
        {
            return false;
        }

        uris = catalog.GetResourceUris(assembly.GetName().Name ?? assemblyName, path);
        return true;
    }

    private bool TryResolveRegistration(string assemblyName, out AppPackageResourceAssemblyRegistration registration)
    {
        if (_registrationsByAssemblyName.TryGetValue(assemblyName, out var registrations) && registrations.Count > 0)
        {
            registration = registrations.MaxBy(candidate => candidate.Generation)!;
            return true;
        }

        registration = default!;
        return false;
    }

    private void RemovePackageAssemblyRegistrations(string packageId, string assemblyName, ICollection<string> changedAssemblyNames)
    {
        if (!_registrationsByAssemblyName.TryGetValue(assemblyName, out var registrations))
        {
            return;
        }

        var removedCount = registrations.RemoveAll(registration =>
            string.Equals(registration.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
        if (removedCount == 0)
        {
            return;
        }

        changedAssemblyNames.Add(assemblyName);
        if (registrations.Count == 0)
        {
            _registrationsByAssemblyName.Remove(assemblyName);
        }
    }

    private void RemovePackageRegistrations(string packageId, ICollection<string> changedAssemblyNames)
    {
        foreach (var (assemblyName, registrations) in _registrationsByAssemblyName.ToArray())
        {
            var removedCount = registrations.RemoveAll(registration =>
                string.Equals(registration.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
            if (removedCount == 0)
            {
                continue;
            }

            changedAssemblyNames.Add(assemblyName);
            if (registrations.Count == 0)
            {
                _registrationsByAssemblyName.Remove(assemblyName);
            }
        }
    }

    private void PruneUnregisteredCatalogs()
    {
        var registeredAssemblies = _registrationsByAssemblyName.Values
            .SelectMany(static registrations => registrations)
            .Select(static registration => registration.Assembly)
            .ToHashSet();
        foreach (var assembly in _catalogsByAssembly.Keys.ToArray())
        {
            if (!registeredAssemblies.Contains(assembly))
            {
                _catalogsByAssembly.Remove(assembly);
            }
        }
    }

    private AppPackageAvaloniaResourceCatalog? ResolveCatalog(Assembly assembly)
    {
        if (_catalogsByAssembly.TryGetValue(assembly, out var catalog))
        {
            return catalog;
        }

        catalog = AppPackageAvaloniaResourceCatalog.TryCreate(assembly);
        _catalogsByAssembly[assembly] = catalog;
        return catalog;
    }

    private sealed record AppPackageResourceAssemblyRegistration(string PackageId, Assembly Assembly, long Generation);

    private sealed class AppPackageAvaloniaResourceCatalog
    {
        private static readonly Type? AssemblyDescriptorType = typeof(Avalonia.Platform.IAssetLoader)
            .Assembly
            .GetType("Avalonia.Platform.Internal.AssemblyDescriptor");

        private readonly Assembly _assembly;
        private readonly IDictionary _avaloniaResources;

        private AppPackageAvaloniaResourceCatalog(Assembly assembly, IDictionary avaloniaResources)
        {
            _assembly = assembly;
            _avaloniaResources = avaloniaResources;
        }

        public static AppPackageAvaloniaResourceCatalog? TryCreate(Assembly assembly)
        {
            if (AssemblyDescriptorType is null)
            {
                return null;
            }

            try
            {
                var descriptor = Activator.CreateInstance(AssemblyDescriptorType, assembly);
                var resources = AssemblyDescriptorType.GetProperty("AvaloniaResources")?.GetValue(descriptor) as IDictionary;
                return resources is null
                    ? null
                    : new AppPackageAvaloniaResourceCatalog(assembly, resources);
            }
            catch (Exception ex)
            {
                AppSessionLog.WriteError($"Failed to index Avalonia resources for package assembly '{assembly.GetName().Name}'.", ex);
                return null;
            }
        }

        public bool TryOpen(string path, out Stream stream)
        {
            if (_avaloniaResources[path] is { } descriptor)
            {
                stream = OpenDescriptorStream(descriptor);
                return true;
            }

            stream = null!;
            return false;
        }

        public IReadOnlyList<Uri> GetResourceUris(string assemblyName, string path)
        {
            if (path.Length > 0 && path[^1] != '/')
            {
                path += '/';
            }

            return _avaloniaResources.Keys
                .OfType<string>()
                .Where(key => key.StartsWith(path, StringComparison.Ordinal))
                .Select(key => new Uri($"avares://{assemblyName}{key}"))
                .ToArray();
        }

        private Stream OpenDescriptorStream(object descriptor)
        {
            try
            {
                var stream = descriptor.GetType().GetMethod("GetStream")?.Invoke(descriptor, null) as Stream;
                return stream ?? throw new InvalidOperationException($"Avalonia resource descriptor for '{_assembly.GetName().Name}' did not return a stream.");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        }
    }
}
