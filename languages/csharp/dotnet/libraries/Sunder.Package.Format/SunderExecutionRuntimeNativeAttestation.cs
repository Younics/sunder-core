using System.Text.Json.Serialization;

namespace Sunder.Package.Format;

public sealed record SunderExecutionRuntimeNativeAttestation
{
    [JsonPropertyName("schemaVersion"), JsonRequired]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("runtimeId"), JsonRequired]
    public string? RuntimeId { get; init; }

    [JsonPropertyName("runtimeVersion"), JsonRequired]
    public string? RuntimeVersion { get; init; }

    [JsonPropertyName("packagingRevision"), JsonRequired]
    public int PackagingRevision { get; init; }

    [JsonPropertyName("rid"), JsonRequired]
    public string? Rid { get; init; }

    [JsonPropertyName("upstreamSha256"), JsonRequired]
    public string? UpstreamSha256 { get; init; }

    [JsonPropertyName("workflow"), JsonRequired]
    public SunderExecutionRuntimeAttestationWorkflow? Workflow { get; init; }

    [JsonPropertyName("toolchain"), JsonRequired]
    public SunderExecutionRuntimeAttestationToolchain? Toolchain { get; init; }

    [JsonPropertyName("launcherSmoke"), JsonRequired]
    public SunderExecutionRuntimeAttestationLauncherSmoke? LauncherSmoke { get; init; }

    [JsonPropertyName("platform"), JsonRequired]
    public SunderExecutionRuntimeAttestationPlatform? Platform { get; init; }

    [JsonPropertyName("signing"), JsonRequired]
    public SunderExecutionRuntimeAttestationSigning? Signing { get; init; }
}

public sealed record SunderExecutionRuntimeAttestationWorkflow
{
    [JsonPropertyName("name"), JsonRequired]
    public string? Name { get; init; }

    [JsonPropertyName("runId"), JsonRequired]
    public string? RunId { get; init; }

    [JsonPropertyName("sourceRevision"), JsonRequired]
    public string? SourceRevision { get; init; }
}

public sealed record SunderExecutionRuntimeAttestationToolchain
{
    [JsonPropertyName("name"), JsonRequired]
    public string? Name { get; init; }

    [JsonPropertyName("version"), JsonRequired]
    public string? Version { get; init; }
}

public sealed record SunderExecutionRuntimeAttestationLauncherSmoke
{
    [JsonPropertyName("result"), JsonRequired]
    public string? Result { get; init; }
}

public sealed record SunderExecutionRuntimeAttestationPlatform
{
    [JsonPropertyName("signature"), JsonRequired]
    public string? Signature { get; init; }

    [JsonPropertyName("notarization"), JsonRequired]
    public string? Notarization { get; init; }
}

public sealed record SunderExecutionRuntimeAttestationSigning
{
    [JsonPropertyName("algorithm"), JsonRequired]
    public string? Algorithm { get; init; }

    [JsonPropertyName("keyId"), JsonRequired]
    public string? KeyId { get; init; }

    [JsonPropertyName("signature"), JsonRequired]
    public string? Signature { get; init; }
}
