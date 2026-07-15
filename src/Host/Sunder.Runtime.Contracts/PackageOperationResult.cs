namespace Sunder.Runtime.Contracts;

public sealed record PackageOperationResult(
    bool Success,
    string? Message,
    bool RuntimeSessionApplied,
    bool RequiresAppRestart,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    private IReadOnlyList<string> _warnings = RuntimeContractCollections.Freeze(Warnings);
    private IReadOnlyList<string> _errors = RuntimeContractCollections.Freeze(Errors);
    private IReadOnlyList<string> _impactedPackageIds = RuntimeContractCollections.Freeze(Array.Empty<string>());
    private PackageLifecycleChangeSet _changeSet = PackageLifecycleChangeSet.Empty;

    public IReadOnlyList<string> Warnings
    {
        get => _warnings;
        init => _warnings = RuntimeContractCollections.Freeze(value);
    }

    public IReadOnlyList<string> Errors
    {
        get => _errors;
        init => _errors = RuntimeContractCollections.Freeze(value);
    }

    public bool AppShellApplied { get; init; }

    public IReadOnlyList<string> ImpactedPackageIds
    {
        get => _impactedPackageIds;
        init => _impactedPackageIds = RuntimeContractCollections.Freeze(value);
    }

    public PackageLifecycleChangeSet ChangeSet
    {
        get => _changeSet;
        init => _changeSet = value with { };
    }

    public RuntimePackageStamp? CommittedStamp { get; init; }

    public bool StoreCommitted { get; init; }

    public bool RuntimeSessionReconciliationPending { get; init; }
}
