using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal static class PackageSessionImpactAnalyzer
{
    public static IReadOnlyList<string> Compare(
        IReadOnlyList<ActivePackageDescriptor> currentPackages,
        IReadOnlyList<RuntimePackageSource> currentSources,
        IReadOnlyList<ActivePackageDescriptor> stagedPackages,
        IReadOnlyList<RuntimePackageSource> stagedSources,
        IReadOnlyCollection<string> reloadFolders)
    {
        var impacted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentById = currentPackages.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        var stagedById = stagedPackages.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        foreach (var packageId in currentById.Keys.Concat(stagedById.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            currentById.TryGetValue(packageId, out var current);
            stagedById.TryGetValue(packageId, out var staged);
            if (!DescriptorsEqual(current, staged)) impacted.Add(packageId);
        }

        var currentSourceById = currentSources.GroupBy(source => source.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var stagedSourceById = stagedSources.GroupBy(source => source.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var packageId in currentSourceById.Keys.Concat(stagedSourceById.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            currentSourceById.TryGetValue(packageId, out var current);
            stagedSourceById.TryGetValue(packageId, out var staged);
            if (current is null
                || staged is null
                || current.Kind != staged.Kind
                || !string.Equals(Path.GetFullPath(current.SourceFolder), Path.GetFullPath(staged.SourceFolder), StringComparison.OrdinalIgnoreCase))
            {
                impacted.Add(packageId);
            }
        }

        var forced = reloadFolders.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var source in stagedSources.Where(source => source.Kind == PackageSourceKind.Dev && forced.Contains(Path.GetFullPath(source.SourceFolder))))
        {
            impacted.Add(source.PackageId);
        }
        return impacted.OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool DescriptorsEqual(ActivePackageDescriptor? current, ActivePackageDescriptor? staged)
    {
        if (current is null || staged is null) return current is null && staged is null;
        return string.Equals(current.PackageId, staged.PackageId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(current.DisplayName, staged.DisplayName, StringComparison.Ordinal)
               && string.Equals(current.Version, staged.Version, StringComparison.OrdinalIgnoreCase)
               && current.Icon == staged.Icon
               && current.IsEnabled == staged.IsEnabled
               && current.Readiness == staged.Readiness
               && current.Views.SequenceEqual(staged.Views);
    }
}
