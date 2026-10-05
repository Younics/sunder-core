using Sunder.Sdk.Packaging;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageLoadPlanner
{
    public IReadOnlyList<IReadOnlyList<PreparedRuntimePackage>> ResolveLoadWaves(
        IReadOnlyList<PreparedRuntimePackage> preparedPackages,
        ICollection<string> errors)
    {
        var packagesById = new Dictionary<string, PreparedRuntimePackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var preparedPackage in preparedPackages)
        {
            if (!packagesById.TryAdd(preparedPackage.PackageId, preparedPackage))
            {
                errors.Add($"Duplicate package id '{preparedPackage.PackageId}' was supplied more than once.");
            }
        }

        var invalid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preparedPackage in packagesById.Values)
        {
            foreach (var dependency in preparedPackage.Dependencies)
            {
                if (!packagesById.TryGetValue(dependency.PackageId, out var dependencyPackage))
                {
                    errors.Add($"Package '{preparedPackage.PackageId}' depends on '{dependency.PackageId}' {dependency.VersionRange}, but that package was not supplied.");
                    invalid.Add(preparedPackage.PackageId);
                    continue;
                }
                if (!PackageVersionRange.IsSatisfiedBy(dependencyPackage.Version, dependency.VersionRange))
                {
                    errors.Add(
                        $"Package '{preparedPackage.PackageId}' requires '{dependency.PackageId}' version '{dependency.VersionRange}', "
                        + $"but session package version '{dependencyPackage.Version}' does not satisfy that range.");
                    invalid.Add(preparedPackage.PackageId);
                }
            }
        }

        PropagateInvalidDependencies();
        var waves = ResolveValidWaves();
        var planned = waves
            .SelectMany(static wave => wave)
            .Select(static package => package.PackageId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining = packagesById.Values
            .Where(package => !invalid.Contains(package.PackageId) && !planned.Contains(package.PackageId))
            .ToArray();
        if (remaining.Length == 0)
        {
            return waves;
        }

        foreach (var package in remaining.Where(package => IsInDependencyCycle(package.PackageId)))
        {
            errors.Add($"Dependency cycle detected while loading '{package.PackageId}'.");
            invalid.Add(package.PackageId);
        }
        PropagateInvalidDependencies();
        return waves;

        void PropagateInvalidDependencies()
        {
            bool changed;
            do
            {
                changed = false;
                foreach (var package in packagesById.Values)
                {
                    if (!invalid.Contains(package.PackageId)
                        && package.Dependencies.Any(dependency => invalid.Contains(dependency.PackageId)))
                    {
                        invalid.Add(package.PackageId);
                        changed = true;
                    }
                }
            } while (changed);
        }

        List<IReadOnlyList<PreparedRuntimePackage>> ResolveValidWaves()
        {
            var pending = packagesById.Values
                .Where(package => !invalid.Contains(package.PackageId))
                .ToList();
            var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<IReadOnlyList<PreparedRuntimePackage>>();
            while (pending.Count != 0)
            {
                var wave = pending
                    .Where(package => package.Dependencies.All(dependency => resolved.Contains(dependency.PackageId)))
                    .ToArray();
                if (wave.Length == 0) break;
                result.Add(wave);
                foreach (var package in wave)
                {
                    pending.Remove(package);
                    resolved.Add(package.PackageId);
                }
            }
            return result;
        }

        bool IsInDependencyCycle(string packageId)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return ReachesStart(packageId);

            bool ReachesStart(string current)
            {
                if (!visited.Add(current)) return false;
                foreach (var dependency in packagesById[current].Dependencies)
                {
                    if (string.Equals(dependency.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                    if (packagesById.ContainsKey(dependency.PackageId)
                        && !invalid.Contains(dependency.PackageId)
                        && ReachesStart(dependency.PackageId))
                    {
                        return true;
                    }
                }
                return false;
            }
        }
    }

    public IReadOnlyList<PreparedRuntimePackage> ResolveLoadOrder(
        IReadOnlyList<PreparedRuntimePackage> preparedPackages,
        ICollection<string> errors)
        => ResolveLoadWaves(preparedPackages, errors).SelectMany(static wave => wave).ToArray();
}
