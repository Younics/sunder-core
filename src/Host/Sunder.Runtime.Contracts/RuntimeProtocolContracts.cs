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
    public const string PackageRuntimeOperationsV1 = "package-runtime-operations.v1";
    public const string PackageRuntimeStreamEnvelopesV1 = "package-runtime-stream-envelopes.v1";
    public const string PackageSessionDrainingV1 = "package-session-draining.v1";
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
    RuntimeProductVersionDiagnostics Product);

public static class PackageRuntimeStreamFrameTypes
{
    public const string Event = "event";
    public const string Completed = "completed";
    public const string Error = "error";
}

public sealed record PackageRuntimeStreamError(string Code, string Message);
