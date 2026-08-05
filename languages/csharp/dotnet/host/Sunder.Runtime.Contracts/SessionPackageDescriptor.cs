namespace Sunder.Runtime.Contracts;

public sealed record SessionPackageDescriptor(
    string PackageId,
    string DisplayName,
    string Version,
    PackageHostRoles HostRoles,
    PackageIconDescriptor? Icon,
    bool IsEnabled,
    PackageReadinessState Readiness,
    IReadOnlyList<PackageViewDescriptor> Views,
    PackageFailureOrigin? FailureOrigin,
    string? LastError,
    DateTimeOffset? LastFailureAtUtc,
    int FailureCount,
    IReadOnlyList<PackageRpcContractUseDescriptor>? RpcContractUses = null)
{
    private IReadOnlyList<PackageRpcContractUseDescriptor> _rpcContractUses =
        RuntimeContractCollections.Freeze(RpcContractUses ?? []);

    public IReadOnlyList<PackageRpcContractUseDescriptor> RpcContractUses
    {
        get => _rpcContractUses;
        init => _rpcContractUses = RuntimeContractCollections.Freeze(value);
    }
}
