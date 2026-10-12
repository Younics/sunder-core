using System.Text.Json.Serialization;

namespace Sunder.Runtime.Contracts;

public static class RuntimeProtocol
{
    public const string Identity = "dev.sunder.runtime";
    public const int CurrentRevision = 1;
    public const int MinimumSupportedRevision = 1;
    public const int MaximumSupportedRevision = 1;
}

public static class RuntimeProtocolFeatures
{
    public const string VersionedApiV1 = "api.v1";
    public const string PackageSessionDrainingV1 = "package-session-draining.v1";
    public const string AtomicPackageSnapshotV1 = "atomic-package-snapshot.v1";
    public const string PackageStageStatusV1 = "package-stage-status.v1";
    public const string DevPackageOwnerLeasesV1 = "dev-package-owner-leases.v1";
    public const string DevPackageImportsV1 = "dev-package-imports.v1";
    public const string TargetAwarePackageSnapshotsV1 = "target-aware-package-snapshots.v1";
    public const string StartupDiagnosticsV1 = "startup-diagnostics.v1";
    public const string SchemaFirstRpcV1 = "schema-first-rpc.v1";
    public const string RpcPermissionsV1 = "rpc-permissions.v1";
    public const string AppWebRpcV1 = "app-web-rpc.v1";
    public const string RuntimeResourcePolicyV1 = "runtime-resource-policy.v1";
    public const string PackageRuntimeRecoveryV1 = "package-runtime-recovery.v1";
    public const string OfficialRuntimeDownloadsV1 = "official-runtime-downloads.v1";
    public const string PackageInstallResumptionV1 = "package-install-resumption.v1";
}

public sealed record RuntimeProductVersionDiagnostics(
    string ProductName,
    string ProductVersion,
    string InformationalVersion);

public sealed record RuntimeResourceProfileDescriptor(
    string EffectiveProfileHash,
    int MaximumRpcValueBytes,
    int MaximumValueDepth,
    int MaximumValueNodes,
    int MaximumObjectMembers,
    int MaximumInlineDecodedBytes,
    int MaximumContentReferenceBytes);

public sealed record RuntimeHandshakeResponse(
    string ProtocolIdentity,
    int ProtocolRevision,
    int MinimumSupportedRevision,
    int MaximumSupportedRevision,
    Guid RuntimeInstanceId,
    IReadOnlyList<string> SupportedFeatures,
    [property: JsonPropertyOrder(2)] RuntimeProductVersionDiagnostics Product,
    [property: JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    RuntimeResourceProfileDescriptor? ResourceProfile = null)
{
    private IReadOnlyList<string> _supportedFeatures = RuntimeContractCollections.Freeze(SupportedFeatures);

    [JsonPropertyOrder(1)]
    public IReadOnlyList<string> SupportedFeatures
    {
        get => _supportedFeatures;
        init => _supportedFeatures = RuntimeContractCollections.Freeze(value);
    }
}
