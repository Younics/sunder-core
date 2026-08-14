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
    InstalledPackageProvenance? Provenance = null,
    IReadOnlyList<string>? GrantedSensitiveCapabilities = null)
{
    private IReadOnlyList<PackageRpcContractUseDescriptor> _rpcContractUses =
        RuntimeContractCollections.Freeze(RpcContractUses ?? []);
    private InstalledPackageProvenance _provenance = Provenance ?? InstalledPackageProvenance.Unknown;
    private IReadOnlyList<string> _grantedSensitiveCapabilities =
        RuntimeContractCollections.Freeze(GrantedSensitiveCapabilities ?? []);

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

    public IReadOnlyList<string> GrantedSensitiveCapabilities
    {
        get => _grantedSensitiveCapabilities;
        init => _grantedSensitiveCapabilities = RuntimeContractCollections.Freeze(value);
    }
}
