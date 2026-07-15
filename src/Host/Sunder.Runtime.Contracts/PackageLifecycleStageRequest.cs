namespace Sunder.Runtime.Contracts;

public sealed record PackageLifecycleStageRequest(IReadOnlyList<string> PackageIds)
{
    public IReadOnlyList<string> PackageIds { get; }
        = RuntimeContractCollections.Freeze(PackageIds);
}
