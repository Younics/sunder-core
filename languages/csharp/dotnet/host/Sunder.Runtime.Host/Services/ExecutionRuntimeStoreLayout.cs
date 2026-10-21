using System.Security.Cryptography;
using System.Text;

namespace Sunder.Runtime.Host.Services;

internal sealed class ExecutionRuntimeStoreLayout
{
    private static readonly char[] SegmentSeparators =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    public ExecutionRuntimeStoreLayout(RuntimePackagePaths packagePaths)
    {
        ArgumentNullException.ThrowIfNull(packagePaths);
        RuntimeStateRootPath = packagePaths.RootPath;
        RootPath = packagePaths.ExecutionRuntimeStoreRootPath;
        ArchiveRootPath = packagePaths.ExecutionRuntimeArtifactRootPath;
        ContentRootPath = packagePaths.ExecutionRuntimeRootsPath;
        ReceiptRootPath = packagePaths.ExecutionRuntimeReceiptRootPath;
        HealthRootPath = packagePaths.ExecutionRuntimeHealthRootPath;
        StagingRootPath = packagePaths.ExecutionRuntimeStagingRootPath;
        TransactionRootPath = packagePaths.ExecutionRuntimeTransactionRootPath;
        QuarantineRootPath = packagePaths.ExecutionRuntimeQuarantineRootPath;
        LeaseRootPath = packagePaths.ExecutionRuntimeLeaseRootPath;
        LockRootPath = Path.Combine(LeaseRootPath, "locks");
        IdentityLockRootPath = Path.Combine(LockRootPath, "identities");
        StoreFencePath = Path.Combine(LockRootPath, "store.lock");
        RecoveryFencePath = Path.Combine(LockRootPath, "recovery.lock");
        GarbageCollectionRetentionFencePath = Path.Combine(LockRootPath, "gc-retentions.lock");
        LeaseRecoveryFailurePath = Path.Combine(LockRootPath, "lease-recovery-blocked");
        TombstoneRootPath = packagePaths.ExecutionRuntimeTombstoneRootPath;
        GarbageCollectionMarkRootPath = Path.Combine(TombstoneRootPath, "marks");
    }

    public ExecutionRuntimeStoreLayout(string runtimeStateRootPath)
        : this(new RuntimePackagePaths(runtimeStateRootPath))
    {
    }

    public string RuntimeStateRootPath { get; }
    public string RootPath { get; }
    public string ArchiveRootPath { get; }
    public string ContentRootPath { get; }
    public string ReceiptRootPath { get; }
    public string HealthRootPath { get; }
    public string StagingRootPath { get; }
    public string TransactionRootPath { get; }
    public string QuarantineRootPath { get; }
    public string LeaseRootPath { get; }
    public string LockRootPath { get; }
    public string IdentityLockRootPath { get; }
    public string StoreFencePath { get; }
    public string RecoveryFencePath { get; }
    public string GarbageCollectionRetentionFencePath { get; }
    public string LeaseRecoveryFailurePath { get; }
    public string TombstoneRootPath { get; }
    public string GarbageCollectionMarkRootPath { get; }

    // Observation evidence is independent of vendor-install receipts and GC tombstones.
    // Keep it beneath the existing strict lease lock subtree, not among live lease documents.
    public string ExecutionObservationRootPath => Path.Combine(LockRootPath, "execution-observations");
    public string ExecutionObservationCaptureRootPath => Path.Combine(ExecutionObservationRootPath, "captures");
    public string ExecutionObservationCleanupRootPath => Path.Combine(ExecutionObservationRootPath, "cleanup");

    public string GetExecutionObservationCapturePath(string leaseId)
        => Path.Combine(ExecutionObservationCaptureRootPath, ValidateTransactionId(leaseId) + ".json");

    public string GetExecutionObservationCleanupPath(string leaseId)
        => Path.Combine(ExecutionObservationCleanupRootPath, ValidateTransactionId(leaseId) + ".json");

    public IReadOnlyList<string> DurableRootPaths =>
    [
        RootPath,
        ArchiveRootPath,
        ContentRootPath,
        ReceiptRootPath,
        HealthRootPath,
        StagingRootPath,
        TransactionRootPath,
        QuarantineRootPath,
        LeaseRootPath,
        LockRootPath,
        IdentityLockRootPath,
        TombstoneRootPath,
        GarbageCollectionMarkRootPath,
    ];

    public string GetArchivePath(string sha256)
        => GetContentAddressedPath(ArchiveRootPath, sha256, ".archive");

    public string GetContentRootPath(string contentIdentity)
        => GetContentAddressedPath(ContentRootPath, contentIdentity, extension: null);

    public string GetHealthPath(string contentIdentity)
        => GetContentAddressedPath(HealthRootPath, contentIdentity, ".json");

    public string GetReceiptPath(ExecutionRuntimeIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return Path.Combine(
            ReceiptRootPath,
            ValidateSegment(identity.SourceId.ToString(), nameof(identity.SourceId)),
            ValidateSegment(identity.RuntimeId, nameof(identity.RuntimeId)),
            ValidateSegment(identity.VendorVersion, nameof(identity.VendorVersion)),
            ValidateSegment(identity.Rid, nameof(identity.Rid)),
            ValidateSegment(identity.ModuleAbi, nameof(identity.ModuleAbi)),
            ValidateSegment(identity.VendorChecksumAlgorithm.ToString(), nameof(identity.VendorChecksumAlgorithm)),
            ValidateSegment(identity.VendorChecksumDigest, nameof(identity.VendorChecksumDigest)) + ".json");
    }

    public string GetStagingPath(string transactionId)
        => Path.Combine(StagingRootPath, ValidateTransactionId(transactionId));

    public string GetTransactionPath(string transactionId)
        => Path.Combine(TransactionRootPath, ValidateTransactionId(transactionId));

    public string GetTransactionJournalPath(string transactionId)
        => Path.Combine(GetTransactionPath(transactionId), "journal.json");

    public string GetLeasePath(string leaseId)
        => Path.Combine(LeaseRootPath, ValidateTransactionId(leaseId) + ".json");

    public string GetLeasePayloadReportPath(string leaseId)
        => Path.Combine(LeaseRootPath, ValidateTransactionId(leaseId) + ".payload");

    public string GetSingleFlightLockPath(ExecutionRuntimeIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity.SingleFlightKey)));
        return Path.Combine(IdentityLockRootPath, digest + ".lock");
    }

    public string GetVersionLockPath(ExecutionRuntimeIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity.VersionKey)));
        return Path.Combine(IdentityLockRootPath, digest + ".version.lock");
    }

    public string GetGarbageCollectionMarkPath(string contentIdentity)
        => Path.Combine(
            GarbageCollectionMarkRootPath,
            ValidateSha256AndReturn(contentIdentity, nameof(contentIdentity)) + ".json");

    public string GetTombstonePath(string tombstoneId)
        => Path.Combine(TombstoneRootPath, ValidateTransactionId(tombstoneId));

    public string CreateQuarantinePath(string label, DateTimeOffset now)
    {
        var safeLabel = ValidateSegment(label, nameof(label));
        return Path.Combine(
            QuarantineRootPath,
            $"{now:yyyyMMddTHHmmssfffffffZ}-{safeLabel}-{Guid.NewGuid():N}");
    }

    public void EnsureContained(string path, string trustedRoot)
    {
        var root = Path.GetFullPath(trustedRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(path);
        if (!string.Equals(candidate, root, PathComparison)
            && !candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new InvalidDataException(
                $"Execution runtime store path '{path}' is outside its trusted root.");
        }
    }

    private static string GetContentAddressedPath(string root, string sha256, string? extension)
    {
        ValidateSha256(sha256, nameof(sha256));
        return Path.Combine(root, sha256[..2], sha256 + extension);
    }

    private static string ValidateSha256AndReturn(string value, string parameterName)
    {
        ValidateSha256(value, parameterName);
        return value;
    }

    internal static void ValidateSha256(string value, string parameterName)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length != 64
            || value.Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("A lower-case SHA-256 digest is required.", parameterName);
        }
    }

    internal static string ValidateSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value is "." or ".."
            || value.IndexOfAny(SegmentSeparators) >= 0
            || value.Any(static character => char.IsControl(character)))
        {
            throw new ArgumentException("A safe filesystem segment is required.", parameterName);
        }
        return value;
    }

    private static string ValidateTransactionId(string transactionId)
        => Guid.TryParseExact(transactionId, "N", out _)
            ? transactionId
            : throw new ArgumentException("A lowercase N-format transaction id is required.", nameof(transactionId));

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
