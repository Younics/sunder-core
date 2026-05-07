namespace Sunder.Runtime.Host.Services;

internal sealed class DevPackageLoadPlanner
{
    public IReadOnlyList<PreparedDevPackage> ResolveLoadOrder(
        IReadOnlyList<PreparedDevPackage> preparedPackages,
        ICollection<string> errors)
    {
        var packagesById = new Dictionary<string, PreparedDevPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var preparedPackage in preparedPackages)
        {
            if (!packagesById.TryAdd(preparedPackage.PackageId, preparedPackage))
            {
                errors.Add($"Duplicate dev package id '{preparedPackage.PackageId}' was supplied more than once.");
            }
        }

        var orderedPackages = new List<PreparedDevPackage>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var preparedPackage in packagesById.Values)
        {
            Visit(preparedPackage);
        }

        return orderedPackages;

        void Visit(PreparedDevPackage preparedPackage)
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

            foreach (var dependencyId in preparedPackage.Dependencies)
            {
                if (!packagesById.TryGetValue(dependencyId, out var dependencyPackage))
                {
                    errors.Add($"Dev package '{preparedPackage.PackageId}' depends on '{dependencyId}', but that package was not supplied.");
                    invalid.Add(preparedPackage.PackageId);
                    continue;
                }

                Visit(dependencyPackage);
                if (invalid.Contains(dependencyId))
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
