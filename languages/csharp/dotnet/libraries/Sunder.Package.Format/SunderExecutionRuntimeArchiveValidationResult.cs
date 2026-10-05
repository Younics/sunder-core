namespace Sunder.Package.Format;

public sealed record SunderExecutionRuntimeArchiveValidationResult
{
    public SunderExecutionRuntimeManifest? Manifest { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public string? ManifestSha256 { get; init; }

    public string? ArchiveSha256 { get; init; }

    public long ArchiveSize { get; init; }

    public string? ValidatedArchivePath { get; init; }

    public string? ContentIdentity { get; init; }

    public IReadOnlyList<SunderExecutionRuntimeContentRecord> ContentInventory { get; init; }
        = Array.Empty<SunderExecutionRuntimeContentRecord>();

    public SunderExecutionRuntimeContentRecord? LauncherContentRecord { get; init; }

    public bool SignatureVerified { get; init; }

    internal bool NativeAttestationSignatureVerified { get; init; }

    internal SunderExecutionRuntimeNativeAttestation? NativeAttestation { get; init; }

    public bool Success => Manifest is not null
                           && ManifestSha256 is not null
                           && ArchiveSha256 is not null
                           && ValidatedArchivePath is not null
                           && ContentIdentity is not null
                           && LauncherContentRecord is not null
                           && SignatureVerified
                           && Errors.Count == 0;
}
