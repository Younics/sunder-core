using Sunder.Sdk.Packaging;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageLoadPlanner
{
    public IReadOnlyList<PreparedRuntimePackage> ResolveLoadOrder(
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

        var orderedPackages = new List<PreparedRuntimePackage>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var preparedPackage in packagesById.Values)
        {
            Visit(preparedPackage);
        }

        return orderedPackages;

        void Visit(PreparedRuntimePackage preparedPackage)
        {
            if (visited.Contains(preparedPackage.PackageId) || invalid.Contains(preparedPackage.PackageId))
            {
                return;
            }

            if (!visiting.Add(preparedPackage.PackageId))
            {
                errors.Add($"Dependency cycle detected while loading '{preparedPackage.PackageId}'.");
                invalid.Add(preparedPackage.PackageId);
                return;
            }

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
                    continue;
                }

                Visit(dependencyPackage);
                if (invalid.Contains(dependency.PackageId))
                {
                    invalid.Add(preparedPackage.PackageId);
                }
            }

            visiting.Remove(preparedPackage.PackageId);
            if (invalid.Contains(preparedPackage.PackageId))
            {
                return;
            }

            visited.Add(preparedPackage.PackageId);
            orderedPackages.Add(preparedPackage);
        }
    }
}
