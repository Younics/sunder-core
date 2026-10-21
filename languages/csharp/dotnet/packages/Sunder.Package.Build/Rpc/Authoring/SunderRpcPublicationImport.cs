using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;

namespace Sunder.Package.Build.Rpc.Authoring;

internal sealed record SunderRpcPublishedContract(string ContractId, string Version, string DescriptorSha256, string AbiSha256, string Exposure);
internal sealed record SunderRpcVerifiedPackagePublication(string RegistryOrigin, string PackageId, string PackageVersion,
    string ContentIdentity, string PublishedAtUtc, IReadOnlyList<SunderRpcPublishedContract> OwnedContracts);

internal sealed class SunderRpcPublicationImport(HttpClient http, bool allowDevelopmentRegistryOrigin = false)
{
    internal async Task<bool> ImportAsync(string projectDirectory, string ownedPath, string packagePath, string registryOrigin, CancellationToken token = default)
    {
        SunderRpcAuthoringDocuments.RequireCanonicalRegistryOrigin(registryOrigin, "publication import", allowDevelopmentRegistryOrigin);
        projectDirectory = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(projectDirectory);
        var path = GenerateSunderRpcBindingsTask.ResolveOwnedBaselinePath(projectDirectory, ownedPath);
        var baseline = File.Exists(path) ? SunderRpcAuthoringDocuments.ReadOwnedPackageBaseline(path) : null;
        if (baseline is null) GenerateSunderRpcBindingsTask.VerifyNoLostPublicBaseline(projectDirectory, Path.Combine(projectDirectory, "obj", GenerateSunderRpcBindingsTask.GeneratedDirectoryName));
        packagePath = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(Path.GetFullPath(packagePath, projectDirectory));
        var temporary = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(Directory.CreateTempSubdirectory("sunder-publication-").FullName);
        try
        {
            var inspectionPath = Path.Combine(temporary, "inspection");
            var validation = await SunderPackagePublicApiInspection.ExportArchiveAsync(packagePath, inspectionPath, token);
            if (!validation.Success) throw Invalid("The retained package failed canonical validation: " + string.Join(" | ", validation.Errors));
            using var receipt = SunderRpcAuthoringJson.Parse(File.ReadAllBytes(Path.Combine(inspectionPath, SunderPackagePublicApiInspection.ReceiptFileName)), 2_097_152);
            var local = receipt.RootElement;
            var inventory = SunderPackagePublicApiInventory.ParseVerifyHash(System.Text.Encoding.UTF8.GetBytes(local.GetProperty("inventory").GetRawText()),
                local.GetProperty("publicInventorySha256").GetString()!);
            var expectedHash = local.GetProperty("archiveSha256").GetString()!;
            var observed = HashArchive(packagePath, token);
            if (observed.Hash != expectedHash) throw Invalid("The retained archive changed during inspection.");
            if (baseline is not null && validation.Manifest!.Id != baseline.PackageId) throw Invalid("The archive does not belong to the owned baseline.");
            var verified = await new SunderRpcContractRestore(http, allowDevelopmentRegistryOrigin: allowDevelopmentRegistryOrigin)
                .VerifyPackagePublicationAsync(registryOrigin, inventory, observed.Hash, observed.Bytes, token);
            var owned = (validation.Manifest!.ContractBundles ?? []).Where(bundle => bundle?.Source == "owned")
                .Select(bundle => new SunderRpcPublishedContract(bundle!.ContractId!, bundle.Version!, bundle.DescriptorSha256!, bundle.AbiSha256!, bundle.Exposure!))
                .OrderBy(value => value.ContractId, StringComparer.Ordinal).ToArray();
            if (!owned.SequenceEqual(verified.OwnedContracts)) throw Invalid("Registry bundle ownership differs from the validated archive.");
            if (baseline is null)
            {
                if (owned.Any(contract => contract.Exposure == "public")) throw Invalid("Public publication requires its retained owned baseline.");
                if (HashArchive(packagePath, token) != observed) throw Invalid("The retained private archive changed during publication verification.");
                return false; // Exact private bytes were verified; private publication has no independent baseline transition.
            }
            var projections = SunderRpcModelProjectionDocument.Parse(SunderRpcDescriptorFileReader.ReadStableBytes(
                Path.Combine(inspectionPath, SunderPackageFormat.RpcModelProjectionFileName), 4_194_304, "publication model projection"));
            if (projections.ModelProjectionSha256 != local.GetProperty("modelProjectionSha256").GetString())
                throw Invalid("The inspected public model projection changed during publication verification.");
            SunderRpcAuthoringDocuments.ValidateReviewedArtifactProjections(baseline, owned, projections.Contracts);
            var proposal = SunderRpcAuthoringDocuments.PlanPackagePublication(baseline, verified);
            return GenerateSunderRpcBindingsTask.CommitOwnedPublication(projectDirectory, path, baseline.ExactBytes, proposal, () =>
            {
                token.ThrowIfCancellationRequested();
                if (HashArchive(packagePath, token) != observed) throw Invalid("The retained archive changed before evidence commit.");
            }, token);
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
    }

    private static (string Hash, long Bytes) HashArchive(string path, CancellationToken token)
    {
        GeneratedOutputPathSafety.EnsureSafeAncestors(path, includeLeaf: true, "publication archive");
        GeneratedOutputPathSafety.EnsureRegularFile(path, "publication archive");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = file.Length; if (length is <= 0 or > 536_870_912) throw Invalid("The archive exceeds its byte limit.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var bytes = new byte[65_536]; long read = 0; int count;
        while ((count = file.Read(bytes)) != 0)
        {
            token.ThrowIfCancellationRequested(); read += count;
            if (read > length) throw Invalid("The retained archive changed during reading.");
            hash.AppendData(bytes, 0, count);
        }
        if (read != length || file.Length != length) throw Invalid("The retained archive changed during reading.");
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), length);
    }
    internal static InvalidDataException Invalid(string message) => new("rpc-authoring.publication-evidence-mismatch: " + message);
}

internal static partial class SunderRpcAuthoringDocuments
{
    internal static byte[] PlanPackagePublication(SunderRpcOwnedPackageBaseline baseline, SunderRpcVerifiedPackagePublication publication)
    {
        if (baseline.PackageId != publication.PackageId) throw SunderRpcPublicationImport.Invalid("Publication names a different owner.");
        var bundles = publication.OwnedContracts.ToDictionary(value => (value.ContractId, value.Version));
        var governed = baseline.Contracts.Where(pair => HasPublicEvidence(pair.Value)).Select(pair => pair.Value.ContractId).ToHashSet(StringComparer.Ordinal);
        var snapshots = baseline.Contracts.Values.SelectMany(value => value.History.Concat(value.Current is null ? [] : new[] { value.Current })).ToArray();
        foreach (var bundle in bundles.Values)
        {
            if (bundle.Exposure == "package" && !governed.Contains(bundle.ContractId)) continue;
            var snapshot = snapshots.SingleOrDefault(value => value.Descriptor.ContractId == bundle.ContractId && value.Descriptor.Version == bundle.Version);
            if (snapshot is null || snapshot.State == "draft" || snapshot.Exposure != bundle.Exposure
                || snapshot.Descriptor.Sha256 != bundle.DescriptorSha256 || snapshot.Descriptor.AbiSha256 != bundle.AbiSha256)
                throw SunderRpcPublicationImport.Invalid("Published owned contracts must match retained immutable baseline snapshots.");
            if (snapshot.State != "published" && snapshot.SnapshotJson.GetProperty("packageRelease").GetProperty("packageVersion").GetString() != publication.PackageVersion)
                throw SunderRpcPublicationImport.Invalid("The artifact differs from a contract's frozen package release.");
        }
        var timestamp = NormalizePublicationTimestamp(publication.PublishedAtUtc);
        var contracts = baseline.Contracts.ToDictionary(pair => pair.Key, pair => pair.Value with
        {
            Current = pair.Value.Current is { } current ? Confirm(current) : null,
            History = pair.Value.History.Select(Confirm).ToArray(),
        }, StringComparer.Ordinal);
        return WriteOwnedPackageBaseline(baseline with { Contracts = contracts });

        SunderRpcOwnedContractSnapshot Confirm(SunderRpcOwnedContractSnapshot snapshot)
        {
            if (!governed.Contains(snapshot.Descriptor.ContractId)) return snapshot;
            if (snapshot.State == "draft" || snapshot.SnapshotJson.GetProperty("packageRelease").GetProperty("packageVersion").GetString() != publication.PackageVersion) return snapshot;
            if (!bundles.TryGetValue((snapshot.Descriptor.ContractId, snapshot.Descriptor.Version), out var bundle) || snapshot.Exposure != bundle.Exposure
                || snapshot.Descriptor.Sha256 != bundle.DescriptorSha256 || snapshot.Descriptor.AbiSha256 != bundle.AbiSha256)
                throw SunderRpcPublicationImport.Invalid("A frozen snapshot is absent or differs from the published archive.");
            var evidence = new JsonObject
            {
                ["registryOrigin"] = publication.RegistryOrigin, ["packageId"] = publication.PackageId, ["packageVersion"] = publication.PackageVersion,
                ["packageContentIdentity"] = publication.ContentIdentity, ["publishedAtUtc"] = timestamp, ["etag"] = "\"" + snapshot.Descriptor.Sha256 + "\"",
            };
            if (snapshot.State == "published")
            {
                if (!JsonNode.DeepEquals(JsonNode.Parse(snapshot.SnapshotJson.GetProperty("publication").GetRawText()), evidence))
                    throw SunderRpcPublicationImport.Invalid("Previously retained publication evidence conflicts.");
                return snapshot;
            }
            var json = JsonNode.Parse(snapshot.SnapshotJson.GetRawText())!.AsObject(); json["state"] = "published"; json["publication"] = evidence;
            return snapshot with { State = "published", SnapshotJson = JsonSerializer.SerializeToElement(json) };
        }
    }

    internal static byte[] PlanPlatformPublication(SunderRpcOwnedPlatformBaseline baseline, SunderVerifiedPlatformContractCatalog catalog, string origin, string publishedAt)
    {
        RequireCanonicalRegistryOrigin(origin, "platform publication"); var timestamp = NormalizePublicationTimestamp(publishedAt);
        var descriptors = catalog.Contracts.ToDictionary(value => (value.ContractId, value.Version));
        var snapshots = baseline.Contracts.Values.SelectMany(value => value.History.Concat(value.Current is null ? [] : new[] { value.Current })).ToArray();
        foreach (var descriptor in descriptors.Values)
        {
            var previous = snapshots.SingleOrDefault(value => value.Descriptor.ContractId == descriptor.ContractId && value.Descriptor.Version == descriptor.Version);
            if (previous is null || previous.State == "draft" || previous.Exposure != "public" || previous.Descriptor.Sha256 != descriptor.Sha256
                || previous.Descriptor.AbiSha256 != descriptor.AbiSha256 || previous.State != "published"
                && previous.SnapshotJson.GetProperty("catalogRelease").GetProperty("releaseVersion").GetString() != catalog.ReleaseVersion)
                throw SunderRpcPublicationImport.Invalid("Published platform descriptor differs from its immutable frozen baseline.");
        }
        return WriteOwnedPlatformBaseline(baseline with { Contracts = baseline.Contracts.ToDictionary(pair => pair.Key, pair => pair.Value with
        {
            Current = pair.Value.Current is { } current ? Confirm(current) : null, History = pair.Value.History.Select(Confirm).ToArray(),
        }, StringComparer.Ordinal) });
        SunderRpcOwnedContractSnapshot Confirm(SunderRpcOwnedContractSnapshot snapshot)
        {
            if (snapshot.State == "draft" || snapshot.SnapshotJson.GetProperty("catalogRelease").GetProperty("releaseVersion").GetString() != catalog.ReleaseVersion) return snapshot;
            if (!descriptors.TryGetValue((snapshot.Descriptor.ContractId, snapshot.Descriptor.Version), out var descriptor)
                || descriptor.Sha256 != snapshot.Descriptor.Sha256 || descriptor.AbiSha256 != snapshot.Descriptor.AbiSha256)
                throw SunderRpcPublicationImport.Invalid("A frozen platform snapshot is absent from the accepted catalog.");
            var evidence = new JsonObject
            {
                ["registryOrigin"] = origin, ["authorityId"] = "sunder.core", ["catalogRelease"] = catalog.ReleaseVersion,
                ["catalogContentIdentity"] = catalog.ContentIdentity,
                ["publishedAtUtc"] = timestamp, ["etag"] = "\"" + descriptor.Sha256 + "\"",
            };
            if (snapshot.State == "published")
            {
                if (!JsonNode.DeepEquals(JsonNode.Parse(snapshot.SnapshotJson.GetProperty("publication").GetRawText()), evidence))
                    throw SunderRpcPublicationImport.Invalid("Previously retained platform publication conflicts.");
                return snapshot;
            }
            var json = JsonNode.Parse(snapshot.SnapshotJson.GetRawText())!.AsObject(); json["state"] = "published"; json["publication"] = evidence;
            return snapshot with { State = "published", SnapshotJson = JsonSerializer.SerializeToElement(json) };
        }
    }

    private static string NormalizePublicationTimestamp(string value)
    {
        var timestamp = value.EndsWith("+00:00", StringComparison.Ordinal) ? value[..^6] + "Z" : value;
        if (timestamp.Contains('.', StringComparison.Ordinal) && timestamp.EndsWith('Z')) timestamp = timestamp[..^1].TrimEnd('0').TrimEnd('.') + "Z";
        RequireCanonicalTimestamp(timestamp, "publication import"); return timestamp;
    }
}
