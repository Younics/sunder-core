using System.Text.Json.Serialization;

namespace Sunder.Runtime.Contracts;

public static class RuntimeProtocol
{
    public const string Identity = "dev.sunder.runtime";
    public const int CurrentRevision = 3;
    public const int MinimumSupportedRevision = 3;
    public const int MaximumSupportedRevision = 3;
}

public static class RuntimeProtocolFeatures
{
    public const string VersionedApiV1 = "api.v1";
    public const string PackageRuntimeOperationsV1 = "package-runtime-operations.v1";
    public const string PackageRuntimeStreamEnvelopesV1 = "package-runtime-stream-envelopes.v1";
    public const string PackageSessionDrainingV1 = "package-session-draining.v1";
    public const string AtomicPackageSnapshotV1 = "atomic-package-snapshot.v1";
    public const string PackageStageStatusV1 = "package-stage-status.v1";
    public const string DevPackageOwnerLeasesV1 = "dev-package-owner-leases.v1";
}

public sealed record RuntimeProductVersionDiagnostics(
    string ProductName,
    string ProductVersion,
    string InformationalVersion);

public sealed record RuntimeHandshakeResponse(
    string ProtocolIdentity,
    int ProtocolRevision,
    int MinimumSupportedRevision,
    int MaximumSupportedRevision,
    Guid RuntimeInstanceId,
    IReadOnlyList<string> SupportedFeatures,
    [property: JsonPropertyOrder(2)] RuntimeProductVersionDiagnostics Product)
{
    private IReadOnlyList<string> _supportedFeatures = RuntimeContractCollections.Freeze(SupportedFeatures);

    [JsonPropertyOrder(1)]
    public IReadOnlyList<string> SupportedFeatures
    {
        get => _supportedFeatures;
        init => _supportedFeatures = RuntimeContractCollections.Freeze(value);
    }
}

public static class PackageRuntimeStreamFrameTypes
{
    public const string Event = "event";
    public const string Completed = "completed";
    public const string Error = "error";
}

public sealed record PackageRuntimeStreamError(string Code, string Message);
