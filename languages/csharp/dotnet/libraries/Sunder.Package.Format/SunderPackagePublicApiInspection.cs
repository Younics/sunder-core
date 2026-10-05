using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Format;

// Local inspection evidence, not Registry ownership or publication authority.
internal sealed record SunderPackagePublicApiSnapshot(
    SunderPackagePublicApiInventory Inventory,
    string ManifestSha256,
    string ContentIndexSha256,
    IReadOnlyDictionary<string, byte[]> Descriptors);

internal static class SunderPackagePublicApiInspection
{
    internal const string ReceiptFileName = "inspection.json";
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // The caller owns the private extraction and has just passed full archive validation.
    internal static SunderPackagePublicApiSnapshot ReadValidated(string root, SunderPackageManifest manifest, bool includeDescriptors = false)
    {
        var manifestBytes = ReadBounded(SunderArchive.ResolveFile(root, ArchiveRelativePath.Parse(SunderPackageFormat.ManifestPath)), 1_048_576);
        var indexBytes = ReadBounded(SunderArchive.ResolveFile(root, ArchiveRelativePath.Parse(SunderPackageFormat.ContentIndexPath)), 1_048_576);
        var manifestHash = SHA256.HashData(manifestBytes); var indexHash = SHA256.HashData(indexBytes);
        var contentIdentity = Convert.ToHexStringLower(SHA256.HashData([.. manifestHash, .. indexHash]));
        var contracts = new JsonArray(); var descriptors = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var bundle in (manifest.ContractBundles ?? []).Where(bundle => bundle?.Exposure == "public").OrderBy(bundle => bundle!.ContractId, StringComparer.Ordinal))
        {
            var path = SunderArchive.ResolveFile(root, ArchiveRelativePath.Parse(SunderPackageFormat.SharedPayloadRoot + bundle!.DescriptorPath));
            var descriptor = SunderRpcContractDescriptor.Parse(ReadBounded(path, (int)SunderPackageFormat.MaxContractDescriptorBytes));
            if (descriptor.ContractId != bundle.ContractId || descriptor.Version != bundle.Version || descriptor.Sha256 != bundle.DescriptorSha256 || descriptor.AbiSha256 != bundle.AbiSha256)
                throw new InvalidDataException("Validated public descriptor identity changed during inspection.");
            var canonical = descriptor.GetCanonicalUtf8();
            if (includeDescriptors) descriptors.Add(descriptor.Sha256, canonical);
            var record = new JsonObject
            {
                ["contractId"] = bundle.ContractId, ["contractVersion"] = bundle.Version, ["descriptorBytes"] = canonical.Length,
                ["descriptorSha256"] = bundle.DescriptorSha256, ["abiSha256"] = bundle.AbiSha256, ["artifactSource"] = bundle.Source,
                ["authorityKind"] = bundle.AuthorityKind, ["authorityId"] = bundle.AuthorityId,
                ["selectedAuthorityRelease"] = bundle.Source == "owned" ? manifest.Version : bundle.SelectedAuthorityRelease,
                ["selectedAuthorityContentIdentity"] = bundle.Source == "owned" ? contentIdentity : bundle.SelectedAuthorityContentIdentity,
            };
            contracts.Add(record);
        }
        var inventory = SunderPackagePublicApiInventory.Parse(JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["schemaVersion"] = 1, ["packageId"] = manifest.Id, ["packageVersion"] = manifest.Version,
            ["packageContentIdentity"] = contentIdentity, ["publicContractCount"] = contracts.Count, ["contracts"] = contracts,
        }, Json));
        return new(inventory, Convert.ToHexStringLower(manifestHash), Convert.ToHexStringLower(indexHash), descriptors);
    }

    internal static async Task<SunderPackageArchiveValidationResult> ExportArchiveAsync(string archivePath, string outputPath, CancellationToken token = default)
    {
        archivePath = Normalize(archivePath); outputPath = Normalize(outputPath);
        EnsureSafePath(archivePath); EnsureSafePath(outputPath);
        if (!string.Equals(Path.GetExtension(archivePath), SunderPackageFormat.Extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Public API inspection requires a canonical .sunderpkg archive.");
        if (File.Exists(outputPath) || Directory.Exists(outputPath)) throw new IOException("API inspection output must not already exist.");
        var parent = Path.GetDirectoryName(outputPath)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("API inspection output parent must already exist.");
        var temporary = Normalize(Directory.CreateTempSubdirectory("sunder-api-inspection-").FullName);
        var stage = Path.Combine(parent, ".sunder-api-inspection-" + Guid.NewGuid().ToString("N"));
        var stageCreated = false;
        try
        {
            var copy = Path.Combine(temporary, "candidate.sunderpkg");
            var archiveHash = await CopyArchiveAsync(archivePath, copy, token);
            var extracted = Path.Combine(temporary, "extracted");
            var validation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(copy, extracted, token);
            if (!validation.Success || validation.Manifest is not { } manifest) return validation;
            var snapshot = ReadValidated(extracted, manifest, includeDescriptors: true);
            EnsureSafePath(stage); Directory.CreateDirectory(stage); stageCreated = true;
            foreach (var (hash, bytes) in snapshot.Descriptors.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                await WriteNewAsync(Path.Combine(stage, hash + ".json"), bytes, token);
            }
            var receipt = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, archiveSha256 = archiveHash, manifestSha256 = snapshot.ManifestSha256,
                contentIndexSha256 = snapshot.ContentIndexSha256, inventory = snapshot.Inventory.Payload,
                publicInventorySha256 = snapshot.Inventory.PublicInventorySha256,
            }, Json);
            if (receipt.Length > 2_097_152) throw new InvalidDataException("API inspection metadata exceeds its byte bound.");
            await WriteNewAsync(Path.Combine(stage, ReceiptFileName), receipt, token);
            token.ThrowIfCancellationRequested(); EnsureSafePath(outputPath); EnsureSafePath(stage);
            Directory.Move(stage, outputPath); stageCreated = false;
            return validation;
        }
        finally
        {
            SunderArchiveFileSystem.TryDeleteDirectory(temporary);
            if (stageCreated && Directory.Exists(stage))
            {
                EnsureSafePath(stage);
                var files = Directory.EnumerateFileSystemEntries(stage).Take(130).ToArray();
                if (files.Length <= 129 && files.All(file => (File.GetAttributes(file) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0))
                { foreach (var file in files) File.Delete(file); Directory.Delete(stage); }
            }
        }
    }

    private static async Task<string> CopyArchiveAsync(string source, string target, CancellationToken token)
    {
        EnsureSafePath(source);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length is <= 0 or > SunderPackageFormat.MaxArchiveBytes) throw new InvalidDataException("Candidate archive exceeds its compressed byte bound.");
        var length = input.Length;
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65_536]; long copied = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, token); if (count == 0) break;
            if (count > SunderPackageFormat.MaxArchiveBytes - copied) throw new InvalidDataException("Candidate grew beyond its compressed byte bound.");
            copied += count; hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        if (copied != length || input.Length != length) throw new IOException("Candidate archive changed during snapshot capture.");
        await output.FlushAsync(token);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        EnsureSafePath(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > maximum) throw new InvalidDataException("Inspection input exceeds its byte bound.");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new IOException("Inspection input changed during reading.");
        return bytes;
    }

    private static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken token)
    {
        EnsureSafePath(path);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, token); await stream.FlushAsync(token); stream.Flush(flushToDisk: true);
    }

    private static string Normalize(string path)
    {
        path = Path.GetFullPath(path);
        if (OperatingSystem.IsMacOS()) foreach (var alias in new[] { "/var", "/tmp", "/etc" })
            if ((path == alias || path.StartsWith(alias + "/", StringComparison.Ordinal)) && new DirectoryInfo(alias).ResolveLinkTarget(returnFinalTarget: true)?.FullName == "/private" + alias)
                return "/private" + path;
        return path;
    }

    private static void EnsureSafePath(string path)
    {
        path = Normalize(path); var root = Path.GetPathRoot(path)!; var current = root;
        var segments = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            var file = new FileInfo(current);
            if (file.LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null) throw new IOException("Inspection paths cannot contain links or reparse points.");
            if (!File.Exists(current) && !Directory.Exists(current)) return;
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || index + 1 < segments.Length && (attributes & FileAttributes.Directory) == 0)
                throw new IOException("Inspection paths must have regular directory ancestors.");
        }
    }
}
