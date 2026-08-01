namespace Sunder.Runtime.Contracts;

public sealed record InstalledPackageDescriptor(
    string PackageId,
    string Name,
    string Version,
    PackageHostRoles HostRoles,
    string? Summary,
    PackageIconDescriptor? Icon,
    bool IsEnabled,
    IReadOnlyList<PackageDependencyDescriptor> DependsOn,
    DateTimeOffset InstalledAtUtc,
    string? StatusMessage,
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
