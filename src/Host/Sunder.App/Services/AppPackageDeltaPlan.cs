using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

internal sealed class AppPackageDeltaPlan
{
    private readonly Dictionary<string, ActivePackageDescriptor> _activePackagesById;
    private readonly HashSet<string> _forceReloadPackages;
    private readonly Dictionary<string, PackageUiSnapshotDescriptor> _sourcesByPackageId;

    public AppPackageDeltaPlan(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IReadOnlyCollection<string>? forceReloadPackageIds)
    {
        _forceReloadPackages = forceReloadPackageIds is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(forceReloadPackageIds, StringComparer.OrdinalIgnoreCase);
        _activePackagesById = activePackages.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        _sourcesByPackageId = packageSources
            .GroupBy(source => source.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    public bool IsPackageInactive(string packageId)
        => !_activePackagesById.ContainsKey(packageId);

    public bool TryGetSource(ActivePackageDescriptor activePackage, out PackageUiSnapshotDescriptor source)
        => _sourcesByPackageId.TryGetValue(activePackage.PackageId, out source!);

    public AppPackageDeltaAction GetAction(
        ActivePackageDescriptor activePackage,
        PackageUiSnapshotDescriptor source,
        AppLoadedPackageHandle? loadedPackage,
        bool isPackageDisabled)
    {
        var forceReload = _forceReloadPackages.Contains(activePackage.PackageId);
        if (!forceReload && isPackageDisabled)
        {
            return loadedPackage is null
                ? AppPackageDeltaAction.SkipDisabled
                : AppPackageDeltaAction.UnloadDisabled;
        }

        if (!forceReload && loadedPackage is not null && IsSameLoadedPackage(loadedPackage, activePackage, source))
        {
            return AppPackageDeltaAction.SkipLoaded;
        }

        return loadedPackage is null
            ? AppPackageDeltaAction.Load
            : AppPackageDeltaAction.Reload;
    }

    private static bool IsSameLoadedPackage(
        AppLoadedPackageHandle loadedPackage,
        ActivePackageDescriptor activePackage,
        PackageUiSnapshotDescriptor source)
        => string.Equals(loadedPackage.Package.Version, activePackage.Version, StringComparison.OrdinalIgnoreCase)
           && loadedPackage.Source.SourceKind == source.SourceKind
           && string.Equals(loadedPackage.Source.ContentHash, source.ContentHash, StringComparison.OrdinalIgnoreCase)
           && loadedPackage.Source.SessionGeneration == source.SessionGeneration;
}

internal enum AppPackageDeltaAction
{
    Load = 0,
    Reload = 1,
    SkipLoaded = 2,
    SkipDisabled = 3,
    UnloadDisabled = 4,
    MissingSource = 5,
}
