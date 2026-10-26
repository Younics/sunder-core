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
    private IReadOnlyList<string>? _requiredPackageIds;

    /// <summary>Validated required dependencies of this exact session source; null on older Hosts.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RequiredPackageIds
    {
        get => _requiredPackageIds;
        init => _requiredPackageIds = value is null ? null : RuntimeContractCollections.Freeze(value);
    }

    /// <summary>Full content identity of the actual validated source in this Runtime snapshot; absent on older Hosts.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceContentIdentity { get; init; }

    private IReadOnlyList<PackageRpcContractUseDescriptor> _rpcContractUses =
        RuntimeContractCollections.Freeze(RpcContractUses ?? []);

    public IReadOnlyList<PackageRpcContractUseDescriptor> RpcContractUses
    {
        get => _rpcContractUses;
        init => _rpcContractUses = RuntimeContractCollections.Freeze(value);
    }
}
