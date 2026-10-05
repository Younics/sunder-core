using System.Text.Json.Serialization;

namespace Sunder.ExecutionRuntime.Build;

internal sealed record ExecutionRuntimeLockDocument
{
    [JsonPropertyName("$schema"), JsonRequired]
    public string? Schema { get; init; }

    [JsonPropertyName("schemaVersion"), JsonRequired]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("lockKind"), JsonRequired]
    public string? LockKind { get; init; }

    [JsonPropertyName("coverage"), JsonRequired]
    public ExecutionRuntimeLockCoverage? Coverage { get; init; }

    [JsonPropertyName("entries"), JsonRequired]
    public IReadOnlyList<ExecutionRuntimeLockEntry?>? Entries { get; init; }
}

internal sealed record ExecutionRuntimeLockCoverage
{
    [JsonPropertyName("runtimes"), JsonRequired]
    public IReadOnlyList<ExecutionRuntimeLockCoverageRuntime?>? Runtimes { get; init; }

    [JsonPropertyName("rids"), JsonRequired]
    public IReadOnlyList<string?>? Rids { get; init; }
}

internal sealed record ExecutionRuntimeLockCoverageRuntime
{
    [JsonPropertyName("id"), JsonRequired]
    public string? Id { get; init; }

    [JsonPropertyName("major"), JsonRequired]
    public int Major { get; init; }
}

internal sealed record ExecutionRuntimeLockEntry
{
    [JsonPropertyName("id"), JsonRequired]
    public string? Id { get; init; }

    [JsonPropertyName("version"), JsonRequired]
    public string? Version { get; init; }

    [JsonPropertyName("packagingRevision"), JsonRequired]
    public int PackagingRevision { get; init; }

    [JsonPropertyName("minimumHostTrustGeneration"), JsonRequired]
    public long MinimumHostTrustGeneration { get; init; }

    [JsonPropertyName("supportLine"), JsonRequired]
    public string? SupportLine { get; init; }

    [JsonPropertyName("defaultStatus"), JsonRequired]
    public string? DefaultStatus { get; init; }

    [JsonPropertyName("rid"), JsonRequired]
    public string? Rid { get; init; }

    [JsonPropertyName("source"), JsonRequired]
    public ExecutionRuntimeLockSource? Source { get; init; }

    [JsonPropertyName("license"), JsonRequired]
    public ExecutionRuntimeLockLicense? License { get; init; }

    [JsonPropertyName("profile"), JsonRequired]
    public ExecutionRuntimeLockProfile? Profile { get; init; }

    [JsonPropertyName("recipe"), JsonRequired]
    public ExecutionRuntimeLockRecipe? Recipe { get; init; }

    [JsonPropertyName("trust"), JsonRequired]
    public ExecutionRuntimeLockTrust? Trust { get; init; }
}

internal sealed record ExecutionRuntimeLockSource
{
    [JsonPropertyName("name"), JsonRequired]
    public string? Name { get; init; }

    [JsonPropertyName("publisher"), JsonRequired]
    public string? Publisher { get; init; }

    [JsonPropertyName("uri"), JsonRequired]
    public string? Uri { get; init; }

    [JsonPropertyName("sha256"), JsonRequired]
    public string? Sha256 { get; init; }

    [JsonPropertyName("archiveFormat"), JsonRequired]
    public string? ArchiveFormat { get; init; }

    [JsonPropertyName("sourceRoot"), JsonRequired]
    public string? SourceRoot { get; init; }
}

internal sealed record ExecutionRuntimeLockLicense
{
    [JsonPropertyName("spdx"), JsonRequired]
    public IReadOnlyList<string?>? Spdx { get; init; }

    [JsonPropertyName("files"), JsonRequired]
    public IReadOnlyList<ExecutionRuntimeLockLicenseFile?>? Files { get; init; }
}

internal sealed record ExecutionRuntimeLockLicenseFile
{
    [JsonPropertyName("path"), JsonRequired]
    public string? Path { get; init; }

    [JsonPropertyName("sha256"), JsonRequired]
    public string? Sha256 { get; init; }
}

internal sealed record ExecutionRuntimeLockProfile
{
    [JsonPropertyName("name"), JsonRequired]
    public string? Name { get; init; }

    [JsonPropertyName("moduleAbi"), JsonRequired]
    public string? ModuleAbi { get; init; }

    [JsonPropertyName("workerProtocol"), JsonRequired]
    public string? WorkerProtocol { get; init; }

    [JsonPropertyName("launchExecutable"), JsonRequired]
    public string? LaunchExecutable { get; init; }

    [JsonPropertyName("launchArguments"), JsonRequired]
    public IReadOnlyList<string?>? LaunchArguments { get; init; }
}

internal sealed record ExecutionRuntimeLockRecipe
{
    [JsonPropertyName("name"), JsonRequired]
    public string? Name { get; init; }

    [JsonPropertyName("payloadRoot"), JsonRequired]
    public string? PayloadRoot { get; init; }

    [JsonPropertyName("launcherSource"), JsonRequired]
    public string? LauncherSource { get; init; }

    [JsonPropertyName("excludedSourcePaths"), JsonRequired]
    public IReadOnlyList<string?>? ExcludedSourcePaths { get; init; }

    [JsonPropertyName("toolchain"), JsonRequired]
    public ExecutionRuntimeLockToolchain? Toolchain { get; init; }

    [JsonPropertyName("smokeArguments"), JsonRequired]
    public IReadOnlyList<string?>? SmokeArguments { get; init; }
}

internal sealed record ExecutionRuntimeLockToolchain
{
    [JsonPropertyName("name"), JsonRequired]
    public string? Name { get; init; }

    [JsonPropertyName("version"), JsonRequired]
    public string? Version { get; init; }
}

internal sealed record ExecutionRuntimeLockTrust
{
    [JsonPropertyName("scope"), JsonRequired]
    public string? Scope { get; init; }

    [JsonPropertyName("sourceApproval"), JsonRequired]
    public ExecutionRuntimeLockSourceApproval? SourceApproval { get; init; }

    [JsonPropertyName("releaseSigningKeyId"), JsonRequired]
    public string? ReleaseSigningKeyId { get; init; }

    [JsonPropertyName("attestationSigningKeyId"), JsonRequired]
    public string? AttestationSigningKeyId { get; init; }

    [JsonPropertyName("attestationWorkflow"), JsonRequired]
    public string? AttestationWorkflow { get; init; }

    [JsonPropertyName("nativeRunner"), JsonRequired]
    public string? NativeRunner { get; init; }

    [JsonPropertyName("platformSignature"), JsonRequired]
    public string? PlatformSignature { get; init; }

    [JsonPropertyName("notarization"), JsonRequired]
    public string? Notarization { get; init; }
}

internal sealed record ExecutionRuntimeLockSourceApproval
{
    [JsonPropertyName("status"), JsonRequired]
    public string? Status { get; init; }

    [JsonPropertyName("reference"), JsonRequired]
    public string? Reference { get; init; }

    [JsonPropertyName("approvedBy"), JsonRequired]
    public string? ApprovedBy { get; init; }
}
