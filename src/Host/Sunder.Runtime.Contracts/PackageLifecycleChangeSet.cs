namespace Sunder.Runtime.Contracts;

public sealed record PackageLifecycleChangeSet(
    IReadOnlyList<string> StoreChangedPackageIds,
    IReadOnlyList<string> RuntimeReloadPackageIds,
    IReadOnlyList<string> AppReloadPackageIds,
    IReadOnlyList<string> RemovedPackageIds,
    bool SharedAssemblyResetRequired)
{
    public IReadOnlyList<string> StoreChangedPackageIds { get; }
        = RuntimeContractCollections.Freeze(StoreChangedPackageIds);

    public IReadOnlyList<string> RuntimeReloadPackageIds { get; }
        = RuntimeContractCollections.Freeze(RuntimeReloadPackageIds);

    public IReadOnlyList<string> AppReloadPackageIds { get; }
        = RuntimeContractCollections.Freeze(AppReloadPackageIds);

    public IReadOnlyList<string> RemovedPackageIds { get; }
        = RuntimeContractCollections.Freeze(RemovedPackageIds);

    public static PackageLifecycleChangeSet Empty { get; } = new([], [], [], [], SharedAssemblyResetRequired: false);
}
