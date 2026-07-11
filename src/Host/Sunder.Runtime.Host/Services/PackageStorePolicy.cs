using System.Text.Json;
using Sunder.Package.Format;

namespace Sunder.Runtime.Host.Services;

internal static class PackageStorePolicy
{
    public static IReadOnlyList<string> ValidateCatalog(InstalledPackageStore store, IReadOnlyList<InstalledPackageRecord> packages)
    {
        var errors = new List<string>();
        try
        {
            store.ValidateCatalog(packages);
        }
        catch (InvalidDataException exception)
        {
            return [exception.Message];
        }

        var byId = packages.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            foreach (var dependency in package.DependsOn)
            {
                if (!byId.TryGetValue(dependency.PackageId, out var installedDependency))
                {
                    errors.Add($"Package '{package.PackageId}' depends on missing package '{dependency.PackageId}'.");
                }
                else if (!PackageVersionRange.IsSatisfiedBy(installedDependency.Version, dependency.VersionRange))
                {
                    errors.Add($"Package '{package.PackageId}' requires '{dependency.PackageId}' version '{dependency.VersionRange}', but '{installedDependency.Version}' would be installed.");
                }
                else if (package.IsEnabled && !installedDependency.IsEnabled)
                {
                    errors.Add($"Package '{package.PackageId}' depends on disabled package '{dependency.PackageId}'.");
                }
            }
        }

        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages) Visit(package);
        return errors;

        void Visit(InstalledPackageRecord package)
        {
            if (visited.Contains(package.PackageId)) return;
            if (!visiting.Add(package.PackageId))
            {
                errors.Add($"Dependency cycle detected at package '{package.PackageId}'.");
                return;
            }
            foreach (var dependency in package.DependsOn)
            {
                if (byId.TryGetValue(dependency.PackageId, out var dependencyPackage)) Visit(dependencyPackage);
            }
            visiting.Remove(package.PackageId);
            visited.Add(package.PackageId);
        }
    }

    public static string? ValidateReplacementVersion(InstalledPackageRecord current, InstalledPackageRecord replacement, bool allowDowngrade, bool reinstall)
    {
        if (!SemanticVersion.TryParse(current.Version, out var currentVersion)
            || !SemanticVersion.TryParse(replacement.Version, out var replacementVersion))
        {
            return $"Package version '{replacement.Version}' or installed version '{current.Version}' is invalid.";
        }
        var comparison = replacementVersion.CompareTo(currentVersion);
        if (comparison < 0 && !allowDowngrade) return $"Package '{current.PackageId}' cannot be downgraded from {current.Version} to {replacement.Version} without allowing downgrades.";
        return comparison == 0 && !reinstall ? $"Package '{current.PackageId}' version {replacement.Version} is already installed." : null;
    }

    public static IReadOnlyList<string> BuildRemovalSet(string packageId, IEnumerable<InstalledPackageRecord> packages)
    {
        var all = packages.ToArray();
        var removals = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { packageId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var package in all)
            {
                if (!removals.Contains(package.PackageId) && package.DependsOn.Any(dependency => removals.Contains(dependency.PackageId)))
                {
                    removals.Add(package.PackageId);
                    changed = true;
                }
            }
        }
        return removals.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static void AddImpactedDependents(ISet<string> impactedPackageIds, IReadOnlyList<InstalledPackageRecord> packages)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var package in packages)
            {
                if (!impactedPackageIds.Contains(package.PackageId) && package.DependsOn.Any(dependency => impactedPackageIds.Contains(dependency.PackageId)))
                {
                    impactedPackageIds.Add(package.PackageId);
                    changed = true;
                }
            }
        }
    }

    public static string BuildMessage(IReadOnlyList<string> messages, int impactedCount)
        => messages.Count switch { 0 => "No package changes required.", 1 => messages[0], _ => $"Applied {impactedCount} package store change(s)." };

    public static bool CatalogsEqual(IReadOnlyList<InstalledPackageRecord> left, IReadOnlyList<InstalledPackageRecord> right)
        => JsonSerializer.Serialize(left.OrderBy(package => package.PackageId, StringComparer.OrdinalIgnoreCase), InstalledPackageStore.JsonOptions)
           == JsonSerializer.Serialize(right.OrderBy(package => package.PackageId, StringComparer.OrdinalIgnoreCase), InstalledPackageStore.JsonOptions);
}
