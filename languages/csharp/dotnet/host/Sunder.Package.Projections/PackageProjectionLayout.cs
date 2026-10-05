using System.Security.Cryptography;
using System.Text;
using Sunder.Runtime.LocalState;

namespace Sunder.Package.Projections;

internal sealed class PackageProjectionLayout
{
    private const string AppRole = "app";
    private const string RuntimeRole = "runtime";

    public PackageProjectionLayout(
        string runtimeStateRootPath,
        string? executionRuntimeLeaseRootPath = null)
    {
        RuntimeStateRootPath = Path.GetFullPath(runtimeStateRootPath);
        ExecutionRuntimeLeaseRootPath = Path.GetFullPath(
            executionRuntimeLeaseRootPath
            ?? Path.Combine(
                RuntimeStateRootPath,
                RuntimeV1StateDescriptor.ExecutionRuntimeLeasesDirectory.Replace('/', Path.DirectorySeparatorChar)));
        FamilyRootPath = Path.Combine(
            RuntimeStateRootPath,
            RuntimeV1StateDescriptor.PackageProjectionCacheFamilyDirectory.Replace('/', Path.DirectorySeparatorChar));
        CoordinationLockPath = Path.Combine(
            RuntimeStateRootPath,
            RuntimeV1StateDescriptor.PackageProjectionCacheCoordinationFile.Replace('/', Path.DirectorySeparatorChar));
        RootPath = Path.Combine(
            RuntimeStateRootPath,
            RuntimeV1StateDescriptor.PackageProjectionCacheDirectory.Replace('/', Path.DirectorySeparatorChar));
        ObjectRootPath = Path.Combine(RootPath, "objects");
        StagingRootPath = Path.Combine(RootPath, "staging");
        QuarantineRootPath = Path.Combine(RootPath, "quarantine");
        LeaseRootPath = Path.Combine(RootPath, "leases");
        TombstoneRootPath = Path.Combine(RootPath, "tombstones");
        RetiredRootPath = Path.Combine(TombstoneRootPath, "retired");
        LockRootPath = Path.Combine(RootPath, "locks");
        MaintenanceLockPath = Path.Combine(LockRootPath, "maintenance.lock");
    }

    public string RuntimeStateRootPath { get; }
    public string ExecutionRuntimeLeaseRootPath { get; }
    public string FamilyRootPath { get; }
    public string CoordinationLockPath { get; }
    public string RootPath { get; }
    public string ObjectRootPath { get; }
    public string StagingRootPath { get; }
    public string QuarantineRootPath { get; }
    public string LeaseRootPath { get; }
    public string TombstoneRootPath { get; }
    public string RetiredRootPath { get; }
    public string LockRootPath { get; }
    public string MaintenanceLockPath { get; }

    public IReadOnlyList<string> DurableRootPaths =>
    [
        RootPath,
        ObjectRootPath,
        StagingRootPath,
        QuarantineRootPath,
        LeaseRootPath,
        TombstoneRootPath,
        RetiredRootPath,
        LockRootPath,
    ];

    public string GetEntryPath(PackageProjectionRole role, string cacheKey)
        => Path.Combine(GetRoleRoot(ObjectRootPath, role), cacheKey[..2], cacheKey);

    public string GetProjectionRootPath(PackageProjectionRole role, string cacheKey)
        => Path.Combine(GetEntryPath(role, cacheKey), "root");

    public string GetReceiptPath(PackageProjectionRole role, string cacheKey)
        => Path.Combine(GetEntryPath(role, cacheKey), "receipt.json");

    public string GetHealthPath(PackageProjectionRole role, string cacheKey)
        => Path.Combine(GetEntryPath(role, cacheKey), "health.json");

    public string GetStagingPath(PackageProjectionRole role)
        => Path.Combine(GetRoleRoot(StagingRootPath, role), Guid.NewGuid().ToString("N"));

    public string GetLeaseDirectory(PackageProjectionRole role, string cacheKey)
        => Path.Combine(GetRoleRoot(LeaseRootPath, role), cacheKey[..2], cacheKey);

    public string GetLeasePath(PackageProjectionRole role, string cacheKey, string leaseId)
        => Path.Combine(GetLeaseDirectory(role, cacheKey), ValidateGuid(leaseId) + ".json");

    public string GetProcessTreeLeasePath(string leaseId)
        => Path.Combine(ExecutionRuntimeLeaseRootPath, ValidateGuid(leaseId) + ".json");

    public string GetTombstonePath(PackageProjectionRole role, string cacheKey)
        => Path.Combine(GetRoleRoot(TombstoneRootPath, role), cacheKey[..2], cacheKey + ".json");

    public string GetKeyLockPath(PackageProjectionRole role, string cacheKey)
        => Path.Combine(GetRoleRoot(LockRootPath, role), cacheKey[..2], cacheKey + ".lock");

    public string CreateQuarantinePath(PackageProjectionRole role, string cacheKey, string reason, DateTimeOffset now)
        => Path.Combine(
            GetRoleRoot(QuarantineRootPath, role),
            $"{now:yyyyMMddTHHmmssfffffffZ}-{ValidateLabel(reason)}-{cacheKey}-{Guid.NewGuid():N}");

    public string CreateRetiredPath(PackageProjectionRole role, string cacheKey, DateTimeOffset now)
        => Path.Combine(
            GetRoleRoot(RetiredRootPath, role),
            $"{now:yyyyMMddTHHmmssfffffffZ}-{cacheKey}-{Guid.NewGuid():N}");

    public IEnumerable<(PackageProjectionRole Role, string CacheKey, string Path)> EnumerateEntries()
    {
        foreach (var role in Enum.GetValues<PackageProjectionRole>())
        {
            var roleRoot = GetRoleRoot(ObjectRootPath, role);
            if (!Directory.Exists(roleRoot))
            {
                continue;
            }
            foreach (var prefixRoot in Directory.EnumerateDirectories(roleRoot))
            {
                foreach (var path in Directory.EnumerateDirectories(prefixRoot))
                {
                    var cacheKey = Path.GetFileName(path);
                    if (IsCanonicalSha256(cacheKey)
                        && string.Equals(Path.GetFileName(prefixRoot), cacheKey[..2], StringComparison.Ordinal))
                    {
                        yield return (role, cacheKey, path);
                    }
                }
            }
        }
    }

    public IEnumerable<string> EnumerateLeaseFiles()
        => Directory.Exists(LeaseRootPath)
            ? Directory.EnumerateFiles(LeaseRootPath, "*.json", SearchOption.AllDirectories)
            : [];

    public static string ComputeKey(
        PackageProjectionRole role,
        string sourceContentIdentity,
        string rulesIdentity,
        IReadOnlyList<string> targetKeys,
        IReadOnlyList<PackageProjectionInventoryEntry> inventory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "sunder.package.projection\0r1\0");
        Append(hash, GetRoleName(role));
        Append(hash, "\0");
        Append(hash, sourceContentIdentity);
        Append(hash, "\0");
        Append(hash, rulesIdentity);
        Append(hash, "\0");
        foreach (var targetKey in targetKeys.Order(StringComparer.Ordinal))
        {
            Append(hash, targetKey);
            Append(hash, "\0");
        }
        Append(hash, "\u001e");
        foreach (var file in inventory.OrderBy(static item => item.Path, StringComparer.Ordinal))
        {
            Append(hash, file.Path);
            Append(hash, "\0");
            Append(hash, file.Sha256);
            Append(hash, "\0");
            Append(hash, file.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, file.IsExecutable ? "\01\0" : "\00\0");
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static string NormalizeRelativePath(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (Path.IsPathFullyQualified(value)
            || value.Contains('\\')
            || value.StartsWith("/", StringComparison.Ordinal)
            || value.EndsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("A canonical forward-slash relative path is required.", parameterName);
        }
        var segments = value.Split('/');
        if (segments.Any(static segment => string.IsNullOrEmpty(segment)
                                           || segment is "." or ".."
                                           || segment.Any(char.IsControl)))
        {
            throw new ArgumentException("A safe relative path is required.", parameterName);
        }
        return string.Join('/', segments);
    }

    public static void ValidateCacheKey(string value, string parameterName)
    {
        if (!IsCanonicalSha256(value))
        {
            throw new ArgumentException("A lower-case SHA-256 digest is required.", parameterName);
        }
    }

    public static bool IsCanonicalSha256(string value)
        => value.Length == 64
           && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string GetRoleName(PackageProjectionRole role)
        => role switch
        {
            PackageProjectionRole.App => AppRole,
            PackageProjectionRole.Runtime => RuntimeRole,
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

    public static void EnsureContained(string path, string trustedRoot)
    {
        var root = Path.GetFullPath(trustedRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(path);
        if (!string.Equals(candidate, root, PathComparison)
            && !candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new InvalidDataException($"Projection path '{path}' is outside its trusted root.");
        }
    }

    private static string GetRoleRoot(string root, PackageProjectionRole role)
        => Path.Combine(root, GetRoleName(role));

    private static string ValidateGuid(string value)
        => Guid.TryParseExact(value, "N", out _) && string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal)
            ? value
            : throw new ArgumentException("A lowercase N-format id is required.", nameof(value));

    private static string ValidateLabel(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Any(static character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("A safe projection label is required.", nameof(value));
        }
        return value;
    }

    private static void Append(IncrementalHash hash, string value)
        => hash.AppendData(Encoding.UTF8.GetBytes(value));

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
