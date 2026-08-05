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
    IReadOnlyList<PackageRpcContractUseDescriptor>? RpcContractUses = null,
    InstalledPackageProvenance? Provenance = null)
{
    private IReadOnlyList<PackageRpcContractUseDescriptor> _rpcContractUses =
        RuntimeContractCollections.Freeze(RpcContractUses ?? []);
    private InstalledPackageProvenance _provenance = Provenance ?? InstalledPackageProvenance.Unknown;

    public IReadOnlyList<PackageRpcContractUseDescriptor> RpcContractUses
    {
        get => _rpcContractUses;
        init => _rpcContractUses = RuntimeContractCollections.Freeze(value);
    }

    public InstalledPackageProvenance Provenance
    {
        get => _provenance;
        init => _provenance = value ?? InstalledPackageProvenance.Unknown;
    }
}
