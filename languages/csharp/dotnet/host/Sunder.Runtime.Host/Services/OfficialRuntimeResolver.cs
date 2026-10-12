using System.Globalization;
using System.Text;
using System.Text.Json;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Packaging;
using static Sunder.Runtime.Host.Services.OfficialRuntimeSourcePolicy;

namespace Sunder.Runtime.Host.Services;

internal sealed record OfficialRuntimeCatalogResult(int TotalCount, IReadOnlyList<RuntimeExecutionRuntimeVendorArtifact> Items);

internal sealed class OfficialRuntimeResolver(OfficialRuntimeHttpClient http, TimeProvider clock)
{
    internal async Task<RuntimeExecutionRuntimeVendorArtifact> ResolveExactAsync(RuntimeExecutionRuntimeIdentity identity, CancellationToken token)
    {
        ValidateIdentity(identity);
        var candidates = await ReadCandidatesAsync(identity.SourceId, identity.Rid, token).ConfigureAwait(false);
        var candidate = candidates.SingleOrDefault(value => value.Identity.VendorVersion == identity.VendorVersion)
            ?? throw new RuntimeExecutionRuntimeException(ExecutionRuntimeErrorCodes.Missing, "The exact official runtime version is absent from vendor metadata.");
        var artifact = await CompleteAsync(candidate, token).ConfigureAwait(false);
        if (artifact.Identity != identity) throw new RuntimeExecutionRuntimeException(ExecutionRuntimeErrorCodes.ArtifactChanged);
        return artifact;
    }

    internal async Task<RuntimeExecutionRuntimeVendorArtifact> ResolveAsync(RuntimeExecutionRuntimeRequirement requirement,
        string rid, CancellationToken token, string? exactVersion = null)
    {
        ValidateRequirement(requirement);
        var source = Source(requirement.RuntimeId, requirement.ModuleAbi);
        var artifacts = await ReadCandidatesAsync(source, rid, token).ConfigureAwait(false);
        var selected = artifacts.Where(value => PackageVersionRange.IsSatisfiedBy(value.Identity.VendorVersion, requirement.VersionRange)
                && (exactVersion is null || value.Identity.VendorVersion == exactVersion)
                && value.Support.State == RuntimeExecutionRuntimeSupportState.Supported)
            .OrderByDescending(value => SemanticVersion.Parse(value.Identity.VendorVersion)).FirstOrDefault()
            ?? throw new RuntimeExecutionRuntimeException(ExecutionRuntimeErrorCodes.Unresolved, "No supported official runtime satisfies this requirement and RID.");
        return await CompleteAsync(selected, token).ConfigureAwait(false);
    }

    internal async Task<OfficialRuntimeCatalogResult> CatalogAsync(ConfiguredOfficialSourceId? source,
        string rid, int skip, int take, CancellationToken token)
    {
        var candidates = new List<RuntimeExecutionRuntimeVendorArtifact>();
        foreach (var id in source is { } selected ? new[] { selected } : [ConfiguredOfficialSourceId.DotNet, ConfiguredOfficialSourceId.Node])
            candidates.AddRange(await ReadCandidatesAsync(id, rid, token).ConfigureAwait(false));
        var result = new List<RuntimeExecutionRuntimeVendorArtifact>();
        foreach (var candidate in candidates.OrderBy(value => value.Identity.SourceId)
            .ThenByDescending(value => SemanticVersion.Parse(value.Identity.VendorVersion)).Skip(skip).Take(take))
            result.Add(await CompleteAsync(candidate, token).ConfigureAwait(false));
        return new(candidates.Count, result);
    }

    private async Task<IReadOnlyList<RuntimeExecutionRuntimeVendorArtifact>> ReadCandidatesAsync(ConfiguredOfficialSourceId source,
        string rid, CancellationToken token)
    {
        _ = NodePlatform(rid); // Exactly the six package RIDs, never a musl alias.
        var (bytes, hash) = await http.ReadAsync(MetadataUri(source), source, MaxMetadataBytes, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        ValidateUniqueProperties(document.RootElement);
        var result = new List<RuntimeExecutionRuntimeVendorArtifact>();
        if (source == ConfiguredOfficialSourceId.DotNet)
        {
            var root = document.RootElement;
            if (Text(root, "channel-version") != "10.0" || Text(root, "release-type") != "lts")
                throw new InvalidDataException("Unexpected .NET Runtime channel.");
            var eol = DateTimeOffset.ParseExact(Text(root, "eol-date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            var supported = Text(root, "support-phase") is "active" or "maintenance" && eol > clock.GetUtcNow();
            foreach (var release in root.GetProperty("releases").EnumerateArray())
            {
                if (!release.TryGetProperty("runtime", out var runtime)) continue;
                var version = Text(runtime, "version");
                if (!EligibleVersion(source, version)) continue;
                foreach (var file in runtime.GetProperty("files").EnumerateArray())
                {
                    if (Text(file, "rid") != rid) continue;
                    var name = "dotnet-runtime-" + rid + (rid.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz");
                    if (Text(file, "name") != name) continue; // No SDK, ASP.NET, apphost pack or system installer.
                    var digest = Text(file, "hash").ToLowerInvariant();
                    var url = Text(file, "url");
                    var identity = new RuntimeExecutionRuntimeIdentity(source, "sunder.runtime.dotnet", version, rid,
                        "sunder.dotnet-worker-module.v1", RuntimeExecutionRuntimeChecksumAlgorithm.Sha512, digest);
                    result.Add(new(identity, Distribution(url, hash, rid, source), new(
                        supported ? RuntimeExecutionRuntimeSupportState.Supported : RuntimeExecutionRuntimeSupportState.EndOfLife,
                        "10.0 LTS", clock.GetUtcNow(), eol, null)));
                }
            }
        }
        else
        {
            var eol = new DateTimeOffset(2028, 4, 30, 0, 0, 0, TimeSpan.Zero); // Node's published 24.x LTS schedule.
            foreach (var release in document.RootElement.EnumerateArray())
            {
                var vendor = Text(release, "version");
                if (!vendor.StartsWith('v')) continue;
                var version = vendor[1..];
                if (!EligibleVersion(source, version) || release.GetProperty("lts").ValueKind != JsonValueKind.String) continue;
                var fileTag = rid.StartsWith("win-", StringComparison.Ordinal) ? rid + "-zip"
                    : rid.StartsWith("osx-", StringComparison.Ordinal) ? rid + "-tar" : rid;
                if (!release.GetProperty("files").EnumerateArray().Any(value => value.GetString() == fileTag)) continue;
                var url = $"https://nodejs.org/dist/v{version}/{ArchiveName(source, version, rid)}";
                // Internal incomplete candidate; it is never returned before checksum completion.
                result.Add(new(new(source, "sunder.runtime.node", version, rid, "sunder.node-worker-module.v1",
                    RuntimeExecutionRuntimeChecksumAlgorithm.Sha256, ""), Distribution(url, hash, rid, source),
                    new(eol > clock.GetUtcNow() ? RuntimeExecutionRuntimeSupportState.Supported : RuntimeExecutionRuntimeSupportState.EndOfLife,
                        "24 LTS", clock.GetUtcNow(), eol, null)));
            }
        }
        if (result.Count > 1024 || result.GroupBy(value => value.Identity.VendorVersion, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidDataException("Ambiguous or oversized official release inventory.");
        return result;
    }

    private async Task<RuntimeExecutionRuntimeVendorArtifact> CompleteAsync(RuntimeExecutionRuntimeVendorArtifact value, CancellationToken token)
    {
        if (value.Identity.SourceId == ConfiguredOfficialSourceId.Node)
        {
            var uri = ChecksumUri(value.Identity.VendorVersion);
            var (bytes, hash) = await http.ReadAsync(uri, ConfiguredOfficialSourceId.Node, MaxChecksumBytes, token,
                value.Identity.VendorVersion, checksum: true).ConfigureAwait(false);
            var name = ArchiveName(ConfiguredOfficialSourceId.Node, value.Identity.VendorVersion, value.Identity.Rid);
            var lines = new UTF8Encoding(false, true).GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var matches = new List<string>();
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r');
                if (line.Length < 67 || !IsDigest(line[..64], 64) || line[64] != ' ' || line[65] is not (' ' or '*'))
                    throw new InvalidDataException("Malformed Node checksum document.");
                if (line[66..] == name) matches.Add(line[..64]);
            }
            if (matches.Count != 1) throw new InvalidDataException("Node checksum must name the exact archive exactly once.");
            value = value with
            {
                Identity = value.Identity with { VendorChecksumDigest = matches[0] },
                Distribution = value.Distribution with { ChecksumUrl = uri.AbsoluteUri, ChecksumDocumentSha256 = hash },
            };
        }
        ValidateArtifact(value);
        return value;
    }

    private static RuntimeExecutionRuntimeDistribution Distribution(string url, string metadataHash, string rid, ConfiguredOfficialSourceId source)
        => new(url, MetadataUri(source).AbsoluteUri, metadataHash, null, null,
            rid.StartsWith("win-", StringComparison.Ordinal) ? RuntimeExecutionRuntimeArchiveFormat.Zip : RuntimeExecutionRuntimeArchiveFormat.TarGzip,
            Profile(source), null);

    private static string Text(JsonElement element, string name)
        => element.GetProperty(name).GetString() is { Length: > 0 and <= 2048 } value ? value
            : throw new InvalidDataException($"Invalid official metadata field '{name}'.");

    private static void ValidateUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new InvalidDataException("Duplicate official metadata property.");
                ValidateUniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateUniqueProperties(item);
    }
}
