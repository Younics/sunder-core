using Sunder.Sdk.Packaging;

namespace Sunder.Package.Format;

internal static class PackageDependencyValidator
{
    public static void Validate(
        IReadOnlyList<SunderPackageDependencyManifest>? dependencies,
        ICollection<string> errors)
    {
        if (dependencies is { Count: > SunderPackageFormat.MaxDependencies })
        {
            errors.Add($"Package manifest declares {dependencies.Count} dependencies; the limit is {SunderPackageFormat.MaxDependencies}.");
        }

        var seenDependencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in (dependencies ?? []).Take(SunderPackageFormat.MaxDependencies))
        {
            if (dependency is null)
            {
                errors.Add("Package dependency entry is null.");
                continue;
            }
            if (!PackageId.TryParse(dependency.PackageId, out _))
            {
                errors.Add($"Dependency package id '{dependency.PackageId}' must use lowercase dot-separated ASCII identifiers.");
            }
            else if (!seenDependencies.Add(dependency.PackageId!))
            {
                errors.Add($"Dependency package id '{dependency.PackageId}' is declared more than once.");
            }

            if (!PackageVersionRange.TryParse(dependency.VersionRange, out _))
            {
                errors.Add($"Dependency '{dependency.PackageId ?? "unknown"}' has unsupported versionRange '{dependency.VersionRange}'.");
            }
        }
    }
}
