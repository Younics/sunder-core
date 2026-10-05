using System.Text.Json.Serialization;

namespace Sunder.Package.Format;

public sealed record SunderExecutionRuntimeManifest
{
    [JsonPropertyName("schemaVersion"), JsonRequired]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("id"), JsonRequired]
    public string? Id { get; init; }

    [JsonPropertyName("version"), JsonRequired]
    public string? Version { get; init; }

    [JsonPropertyName("packagingRevision"), JsonRequired]
    public int PackagingRevision { get; init; }

    [JsonPropertyName("rid"), JsonRequired]
    public string? Rid { get; init; }

    [JsonPropertyName("moduleAbi"), JsonRequired]
    public string? ModuleAbi { get; init; }

    [JsonPropertyName("workerProtocol"), JsonRequired]
    public string? WorkerProtocol { get; init; }

    [JsonPropertyName("launch"), JsonRequired]
    public SunderExecutionRuntimeLaunch? Launch { get; init; }

    [JsonPropertyName("contentIndex"), JsonRequired]
    public string? ContentIndex { get; init; }

    [JsonPropertyName("upstream"), JsonRequired]
    public SunderExecutionRuntimeUpstream? Upstream { get; init; }

    [JsonPropertyName("provenance"), JsonRequired]
    public SunderExecutionRuntimeProvenance? Provenance { get; init; }

    [JsonPropertyName("licenses"), JsonRequired]
    public IReadOnlyList<string?>? Licenses { get; init; }

    [JsonPropertyName("signing"), JsonRequired]
    public SunderExecutionRuntimeSigning? Signing { get; init; }
}

public sealed record SunderExecutionRuntimeLaunch
{
    [JsonPropertyName("executable"), JsonRequired]
    public string? Executable { get; init; }

    [JsonPropertyName("arguments"), JsonRequired]
    public IReadOnlyList<string?>? Arguments { get; init; }
}

public sealed record SunderExecutionRuntimeUpstream
{
    [JsonPropertyName("name"), JsonRequired]
    public string? Name { get; init; }

    [JsonPropertyName("version"), JsonRequired]
    public string? Version { get; init; }

    [JsonPropertyName("sourceUri"), JsonRequired]
    public string? SourceUri { get; init; }

    [JsonPropertyName("sourceSha256"), JsonRequired]
    public string? SourceSha256 { get; init; }
}

public sealed record SunderExecutionRuntimeProvenance
{
    [JsonPropertyName("attestationPath"), JsonRequired]
    public string? AttestationPath { get; init; }

    [JsonPropertyName("attestationSha256"), JsonRequired]
    public string? AttestationSha256 { get; init; }

    [JsonPropertyName("attestationKeyId"), JsonRequired]
    public string? AttestationKeyId { get; init; }
}

public sealed record SunderExecutionRuntimeSigning
{
    [JsonPropertyName("algorithm"), JsonRequired]
    public string? Algorithm { get; init; }

    [JsonPropertyName("keyId"), JsonRequired]
    public string? KeyId { get; init; }

    [JsonPropertyName("signature"), JsonRequired]
    public string? Signature { get; init; }
}
