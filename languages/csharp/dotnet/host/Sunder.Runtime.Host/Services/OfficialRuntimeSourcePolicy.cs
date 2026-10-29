using System.Security.Cryptography;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Packaging;

namespace Sunder.Runtime.Host.Services;

// This is Host configuration, not an extensible URL/trust input supplied by a caller.
internal static class OfficialRuntimeSourcePolicy
{
    internal static string DotNetMetadata => MetadataUri(ConfiguredOfficialSourceId.DotNet).AbsoluteUri;
    internal const string NodeMetadata = "https://nodejs.org/dist/index.json";
    internal const int MaxMetadataBytes = 8 * 1024 * 1024;
    internal const int MaxChecksumBytes = 256 * 1024;
    internal const long MaxArchiveBytes = 512L * 1024 * 1024;
    internal const long MaxExpandedBytes = 1536L * 1024 * 1024;
    internal const long MaxFileBytes = 256L * 1024 * 1024;
    internal const int MaxEntries = 50_000;
    internal const int MaxPathLength = 512;
    internal const string EntryPointToken = "{packageEntryPoint}";

    internal static ConfiguredOfficialSourceId Source(string runtimeId, string abi) => (runtimeId, abi) switch
    {
        (SunderPackageFormat.DotNetExecutionRuntimeId, SunderPackageFormat.DotNetWorkerModuleAbi) => ConfiguredOfficialSourceId.DotNet,
        (SunderPackageFormat.NodeExecutionRuntimeId, SunderPackageFormat.NodeWorkerModuleAbi) => ConfiguredOfficialSourceId.Node,
        _ => throw new RuntimeExecutionRuntimeException(ExecutionRuntimeErrorCodes.Unsupported),
    };

    internal static string Profile(ConfiguredOfficialSourceId source, string? version = null) => source switch
    {
        ConfiguredOfficialSourceId.DotNet => $"dotnet-runtime-{(version is null ? SunderToolchainPolicy.DotNetRuntimeMajor : Major(version))}.v1",
        ConfiguredOfficialSourceId.Node => $"node-runtime-{(version is null ? SunderToolchainPolicy.NodeRuntimeMajor : Major(version))}.v1",
        _ => throw new RuntimeExecutionRuntimeException(ExecutionRuntimeErrorCodes.SourceNotAllowed),
    };

    internal static void ValidateRequirement(RuntimeExecutionRuntimeRequirement requirement)
    {
        if (requirement is null || !PackageTargetValidator.IsBoundedStableMajorRange(requirement.VersionRange))
            throw new RuntimeValidationException("A bounded stable runtime version range is required.");
        _ = Source(requirement.RuntimeId, requirement.ModuleAbi);
    }

    internal static int RequirementMajor(RuntimeExecutionRuntimeRequirement requirement)
    {
        ValidateRequirement(requirement);
        return PackageTargetValidator.RuntimeMajor(requirement.VersionRange);
    }

    internal static bool EligibleVersion(ConfiguredOfficialSourceId source, string? text, IReadOnlyList<int>? supportedMajors = null)
        => ValidStoredVersion(source, text) && (supportedMajors ?? SupportedMajors(source)).Contains(Major(text!));

    internal static IReadOnlyList<int> SupportedMajors(ConfiguredOfficialSourceId source) => source switch
    {
        ConfiguredOfficialSourceId.DotNet => SunderToolchainPolicy.DotNetRuntimeMajors,
        ConfiguredOfficialSourceId.Node => SunderToolchainPolicy.NodeRuntimeMajors,
        _ => throw new RuntimeExecutionRuntimeException(ExecutionRuntimeErrorCodes.SourceNotAllowed),
    };

    // Receipt validity is independent of the current supported range. These original
    // floors remain part of the V1 materialization profiles, including after retirement.
    private static bool ValidStoredVersion(ConfiguredOfficialSourceId source, string? text)
        => text is { Length: <= 32 } && SemanticVersion.TryParse(text, out var version)
            && version.Prerelease is null && version.BuildMetadata is null
            && int.TryParse(version.Major, out var major) && major <= 99
            && (source == ConfiguredOfficialSourceId.DotNet && major >= 10 && version.Minor == "0"
                || source == ConfiguredOfficialSourceId.Node && major >= 24
                    && (major != 24 || version.ComparePrecedenceTo(SemanticVersion.Parse("24.18.1")) >= 0));

    internal static int Major(string version) => int.Parse(SemanticVersion.Parse(version).Major, System.Globalization.CultureInfo.InvariantCulture);

    internal static bool ValidSupportLine(RuntimeExecutionRuntimeIdentity identity, string line)
        => identity.SourceId == ConfiguredOfficialSourceId.Node ? line == $"{Major(identity.VendorVersion)} LTS"
            : line == $"{Major(identity.VendorVersion)}.0 LTS" || line == $"{Major(identity.VendorVersion)}.0 STS";

    internal static void ValidateIdentity(RuntimeExecutionRuntimeIdentity identity)
    {
        if (identity is null || Source(identity.RuntimeId, identity.ModuleAbi) != identity.SourceId
            || !ValidStoredVersion(identity.SourceId, identity.VendorVersion)
            || !SunderPackageFormat.IsRuntimeIdentifier(identity.Rid)
            || identity.VendorChecksumAlgorithm != (identity.SourceId == ConfiguredOfficialSourceId.DotNet
                ? RuntimeExecutionRuntimeChecksumAlgorithm.Sha512 : RuntimeExecutionRuntimeChecksumAlgorithm.Sha256)
            || !IsDigest(identity.VendorChecksumDigest, identity.SourceId == ConfiguredOfficialSourceId.DotNet ? 128 : 64))
            throw new RuntimeExecutionRuntimeException(ExecutionRuntimeErrorCodes.IntegrityFailed, "Invalid official runtime identity.");
    }

    internal static bool IsDigest(string? value, int length) => value?.Length == length
        && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static string NodePlatform(string rid) => rid switch
    {
        "win-x64" or "win-arm64" or "linux-x64" or "linux-arm64" => rid,
        "osx-x64" => "darwin-x64",
        "osx-arm64" => "darwin-arm64",
        _ => throw new RuntimeExecutionRuntimeException(ExecutionRuntimeResolutionConflictCodes.RidUnavailable),
    };

    internal static string ArchiveName(ConfiguredOfficialSourceId source, string version, string rid)
    {
        if (!ValidStoredVersion(source, version) || !SunderPackageFormat.IsRuntimeIdentifier(rid))
            throw new RuntimeExecutionRuntimeException(ExecutionRuntimeErrorCodes.Unsupported);
        var extension = rid.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz";
        return source == ConfiguredOfficialSourceId.DotNet
            ? $"dotnet-runtime-{version}-{rid}{extension}" : $"node-v{version}-{NodePlatform(rid)}{extension}";
    }

    internal static Uri MetadataUri(ConfiguredOfficialSourceId source, int? major = null) => new(source switch
    {
        ConfiguredOfficialSourceId.DotNet when (major ?? SunderToolchainPolicy.DotNetRuntimeMajor) is >= 10 and <= 99
            => $"https://builds.dotnet.microsoft.com/dotnet/release-metadata/{major ?? SunderToolchainPolicy.DotNetRuntimeMajor}.0/releases.json",
        ConfiguredOfficialSourceId.Node => NodeMetadata,
        _ => throw new RuntimeExecutionRuntimeException(ExecutionRuntimeErrorCodes.SourceNotAllowed),
    });

    internal static Uri ChecksumUri(string version)
    {
        if (!ValidStoredVersion(ConfiguredOfficialSourceId.Node, version))
            throw new RuntimeExecutionRuntimeException(ExecutionRuntimeErrorCodes.Unsupported);
        return new($"https://nodejs.org/dist/v{version}/SHASUMS256.txt");
    }

    internal static void ValidateUri(Uri uri, ConfiguredOfficialSourceId source, string? version = null,
        string? rid = null, bool checksum = false)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.OriginalString.Length > 2048 || uri.OriginalString.Contains('%')
            || uri.OriginalString.Contains('\\') || uri.OriginalString.Any(char.IsControl))
            throw new RuntimeExecutionRuntimeException(ExecutionRuntimeErrorCodes.SourceNotAllowed);
        bool allowed;
        if (version is null)
            allowed = uri == MetadataUri(source) || source == ConfiguredOfficialSourceId.DotNet
                && Enumerable.Range(10, 90).Any(major => uri == MetadataUri(source, major));
        else if (checksum)
            allowed = source == ConfiguredOfficialSourceId.Node && uri == ChecksumUri(version);
        else
        {
            var name = ArchiveName(source, version, rid!);
            allowed = source switch
            {
                ConfiguredOfficialSourceId.Node => uri.Host == "nodejs.org"
                    && (uri.AbsolutePath == $"/dist/v{version}/{name}" || uri.AbsolutePath == $"/download/release/v{version}/{name}"),
                ConfiguredOfficialSourceId.DotNet =>
                    (uri.Host is "builds.dotnet.microsoft.com" or "dotnetcli.azureedge.net" or "dotnetcli.blob.core.windows.net"
                        && uri.AbsolutePath == $"/dotnet/Runtime/{version}/{name}")
                    || uri.Host == "download.visualstudio.microsoft.com" && IsVisualStudioPath(uri.AbsolutePath, name),
                _ => false,
            };
        }
        if (!allowed) throw new RuntimeExecutionRuntimeException(ExecutionRuntimeErrorCodes.SourceNotAllowed);
    }

    private static bool IsVisualStudioPath(string path, string name)
    {
        var parts = path.Split('/');
        return parts.Length == 6 && parts[0] == "" && parts[1] == "download" && parts[2] == "pr"
            && Guid.TryParseExact(parts[3], "D", out _) && parts[4].Length is >= 32 and <= 128
            && parts[4].All(char.IsAsciiHexDigit) && parts[5] == name;
    }

    internal static void ValidateArtifact(RuntimeExecutionRuntimeVendorArtifact artifact)
    {
        ValidateIdentity(artifact.Identity);
        ValidateDistribution(artifact.Identity, artifact.Distribution);
        if (artifact.Support is null || !Enum.IsDefined(artifact.Support.State) || artifact.Support.ObservedAtUtc == default
            || !ValidSupportLine(artifact.Identity, artifact.Support.SupportLine))
            throw new InvalidDataException("Invalid vendor support observation.");
    }

    private static void ValidateDistribution(RuntimeExecutionRuntimeIdentity identity, RuntimeExecutionRuntimeDistribution distribution)
    {
        if (distribution is null) throw new InvalidDataException("Missing official distribution evidence.");
        if (distribution.MetadataUrl != MetadataUri(identity.SourceId, Major(identity.VendorVersion)).AbsoluteUri)
            throw new InvalidDataException("Runtime metadata does not match its major line.");
        ValidateUri(new(distribution.DownloadUrl), identity.SourceId, identity.VendorVersion, identity.Rid);
        if (!IsDigest(distribution.MetadataSha256, 64) || distribution.MaterializationProfile != Profile(identity.SourceId, identity.VendorVersion)
            || distribution.DownloadSize is <= 0 or > MaxArchiveBytes
            || distribution.ArchiveFormat != (identity.Rid.StartsWith("win-", StringComparison.Ordinal)
                ? RuntimeExecutionRuntimeArchiveFormat.Zip : RuntimeExecutionRuntimeArchiveFormat.TarGzip))
            throw new InvalidDataException("Invalid official distribution evidence.");
        if (identity.SourceId == ConfiguredOfficialSourceId.Node)
        {
            if (distribution.ChecksumUrl is null || !IsDigest(distribution.ChecksumDocumentSha256, 64))
                throw new InvalidDataException("Node requires separate checksum document evidence.");
            ValidateUri(new(distribution.ChecksumUrl), identity.SourceId, identity.VendorVersion, checksum: true);
        }
        else if (distribution.ChecksumUrl is not null || distribution.ChecksumDocumentSha256 is not null)
            throw new InvalidDataException(".NET checksum authority is its release metadata.");
    }

    internal static void ValidateInstalledArtifact(RuntimeExecutionRuntimeIdentity identity, RuntimeExecutionRuntimeInstalledArtifact installed)
    {
        ValidateIdentity(identity);
        if (installed is null || !IsDigest(installed.ArchiveSha256, 64) || !IsDigest(installed.ContentIdentity, 64)
            || installed.ArchiveSize is <= 0 or > MaxArchiveBytes || installed.Provenance is null
            || installed.Provenance.RetrievedAtUtc == default || installed.Provenance.RetrievedAtUtc.Offset != TimeSpan.Zero
            || identity.SourceId == ConfiguredOfficialSourceId.Node && installed.ArchiveSha256 != identity.VendorChecksumDigest)
            throw new InvalidDataException("Invalid installed official runtime evidence.");
        ValidateDistribution(identity, installed.Provenance.Distribution);
        ValidateUri(new(installed.Provenance.FinalDownloadUrl), identity.SourceId, identity.VendorVersion, identity.Rid);
        if (installed.Provenance.Distribution.DownloadSize is { } expected && expected != installed.ArchiveSize)
            throw new InvalidDataException("Installed official archive size does not match its distribution evidence.");
    }

    internal static HashAlgorithmName VendorHash(RuntimeExecutionRuntimeIdentity identity)
        => identity.VendorChecksumAlgorithm == RuntimeExecutionRuntimeChecksumAlgorithm.Sha512 ? HashAlgorithmName.SHA512 : HashAlgorithmName.SHA256;

    // Consent binds the selected subject and extraction policy, not whole-feed hashes or observation timestamps.
    internal static object Selection(RuntimeExecutionRuntimeVendorArtifact artifact) => new
    {
        artifact.Identity, artifact.Distribution.DownloadUrl, artifact.Distribution.MetadataUrl,
        artifact.Distribution.ChecksumUrl, artifact.Distribution.ArchiveFormat, artifact.Distribution.MaterializationProfile,
        artifact.Support.State, artifact.Support.SupportLine, artifact.Support.EndOfLifeAtUtc,
    };
}
