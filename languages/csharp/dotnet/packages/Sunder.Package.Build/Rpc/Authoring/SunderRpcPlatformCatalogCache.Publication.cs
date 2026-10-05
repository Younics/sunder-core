using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;

namespace Sunder.Package.Build.Rpc.Authoring;

internal static partial class SunderRpcPlatformCatalogCache
{
    private static readonly JsonSerializerOptions PublicationJson = new(JsonSerializerDefaults.Web);
    internal const string ReceiptSuffix = ".receipt.json";

    internal static byte[] ReceiptBytes(string hash, int bytes) => SunderRpcAuthoringDocuments.WriteDeterministicDocument(
        JsonSerializer.SerializeToElement(new { schemaVersion = 1, catalogSha256 = hash, catalogBytes = bytes }));

    internal static byte[] ReadOwnedPair(string directory, string hash)
    {
        if (!Digest(hash)) throw new InvalidDataException("contract-cache.entry-corrupt: Catalog content address is invalid.");
        var body = Path.Combine(directory, hash + ".json"); var receipt = Path.Combine(directory, hash + ReceiptSuffix);
        if (!GeneratedOutputPathSafety.PathExists(body) || !GeneratedOutputPathSafety.PathExists(receipt)) throw Missing();
        var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(body, MaximumBytes, "catalog cache body");
        try { SunderPlatformContractCatalog.ValidateCacheOwnership(bytes, hash); }
        catch (InvalidDataException error) { throw new InvalidDataException("contract-cache.entry-corrupt: Catalog bytes differ from their canonical content address. Run explicit contracts restore.", error); }
        if (!SunderRpcDescriptorFileReader.ReadStableBytes(receipt, 16_384, "catalog cache receipt").AsSpan().SequenceEqual(ReceiptBytes(hash, bytes.Length)))
            throw new InvalidDataException("contract-cache.entry-corrupt: Catalog ownership receipt differs from its exact bytes. Run explicit contracts restore.");
        return bytes;
    }

    internal static bool IsPublicationArtifact(string key)
    {
        foreach (var prefix in new[] { DirectoryName + "/.stage-", DirectoryName + "/.repair-", DirectoryName + "/.completed-" })
            if (key.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(key[prefix.Length..], "D", out var id) && id.ToString("D") == key[prefix.Length..]) return true;
        return false;
    }

    internal static bool ProvePublicationArtifact(string directory, string key, string[] names)
    {
        if (!IsPublicationArtifact(key) || !names.Contains("intent.json", StringComparer.Ordinal)) return false;
        try
        {
            var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(Path.Combine(directory, "intent.json"), 65_536, "catalog publication intent");
            using var document = SunderRpcAuthoringJson.Parse(bytes, 65_536);
            SunderRpcAuthoringJson.RequireObject(document.RootElement, "schemaVersion", "token", "catalogSha256", "previousCatalogSha256", "previousReceiptSha256");
            var intent = JsonSerializer.Deserialize<CatalogIntent>(bytes, PublicationJson)!;
            if (intent.SchemaVersion != 1 || !Guid.TryParseExact(intent.Token, "D", out var id) || id.ToString("D") != intent.Token
                || !key.EndsWith(intent.Token, StringComparison.Ordinal) || !Digest(intent.CatalogSha256)
                || intent.PreviousCatalogSha256 != "" && !Digest(intent.PreviousCatalogSha256)
                || intent.PreviousReceiptSha256 != "" && !Digest(intent.PreviousReceiptSha256)
                || !bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(intent, PublicationJson))) return false;
            var body = intent.CatalogSha256 + ".json"; var receipt = intent.CatalogSha256 + ReceiptSuffix;
            if (names.Any(name => name != "intent.json" && name != body && name != receipt)) return false;
            var repair = key.StartsWith(DirectoryName + "/.repair-", StringComparison.Ordinal);
            foreach (var name in names.Where(name => name != "intent.json"))
            {
                var value = SunderRpcDescriptorFileReader.ReadStableBytes(Path.Combine(directory, name), MaximumBytes, "retained catalog evidence");
                if (repair)
                {
                    if (Hash(value) != (name == body ? intent.PreviousCatalogSha256 : intent.PreviousReceiptSha256)) return false;
                }
                else if (name == body) SunderPlatformContractCatalog.ValidateCacheOwnership(value, intent.CatalogSha256);
                else
                {
                    using var record = SunderRpcAuthoringJson.Parse(value, 16_384);
                    var length = record.RootElement.GetProperty("catalogBytes").GetInt32();
                    if (length is < 2 or > MaximumBytes || !value.AsSpan().SequenceEqual(ReceiptBytes(intent.CatalogSha256, length))) return false;
                }
            }
            if (key.StartsWith(DirectoryName + "/.stage-", StringComparison.Ordinal) && names.Length != 3) return false;
            if (!repair && names.Contains(body, StringComparer.Ordinal) && names.Contains(receipt, StringComparer.Ordinal))
                _ = ReadOwnedPair(directory, intent.CatalogSha256);
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return false; }
    }

    private static void PublishPair(string directory, string hash, byte[] body, CancellationToken token, Action<string>? checkpoint)
    {
        token.ThrowIfCancellationRequested();
        var entries = new[] { (Name: hash + ".json", Bytes: body), (Name: hash + ReceiptSuffix, Bytes: ReceiptBytes(hash, body.Length)) };
        var previous = entries.ToDictionary(entry => entry.Name, entry => ReadExisting(Path.Combine(directory, entry.Name)), StringComparer.Ordinal);
        if (entries.All(entry => previous[entry.Name] is { } old && old.AsSpan().SequenceEqual(entry.Bytes))) return;
        var id = Guid.NewGuid().ToString("D"); var stage = Path.Combine(directory, ".stage-" + id);
        var repair = Path.Combine(directory, ".repair-" + id);
        var intent = new CatalogIntent(1, id, hash, previous[entries[0].Name] is { } oldBody ? Hash(oldBody) : "",
            previous[entries[1].Name] is { } oldReceipt ? Hash(oldReceipt) : "");
        var intentBytes = JsonSerializer.SerializeToUtf8Bytes(intent, PublicationJson);
        GeneratedOutputPathSafety.EnsureSafeAncestors(stage, includeLeaf: true, "catalog stage");
        if (GeneratedOutputPathSafety.PathExists(stage)) throw new IOException("Catalog publication stage already exists.");
        Directory.CreateDirectory(stage);
        foreach (var entry in entries) WriteNew(Path.Combine(stage, entry.Name), entry.Bytes);
        WriteNew(Path.Combine(stage, "intent.json"), intentBytes);
        checkpoint?.Invoke("catalog-staged");
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            var source = Path.Combine(stage, entry.Name); var target = Path.Combine(directory, entry.Name);
            if (!SunderRpcDescriptorFileReader.ReadStableBytes(source, MaximumBytes, "catalog stage").AsSpan().SequenceEqual(entry.Bytes)) throw new InvalidDataException("contract-cache.entry-corrupt: Catalog stage changed.");
            var current = ReadExisting(target);
            if (current is not null && current.AsSpan().SequenceEqual(entry.Bytes)) continue;
            var old = previous[entry.Name];
            if (old is null ? current is not null : current is null || !current.AsSpan().SequenceEqual(old)) throw new InvalidDataException("contract-cache.entry-corrupt: Catalog changed during publication.");
            if (current is not null)
            {
                GeneratedOutputPathSafety.EnsureSafeAncestors(repair, includeLeaf: true, "catalog repair evidence");
                Directory.CreateDirectory(repair);
                var evidence = Path.Combine(repair, "intent.json");
                if (!GeneratedOutputPathSafety.PathExists(evidence)) WriteNew(evidence, intentBytes);
                else if (!SunderRpcDescriptorFileReader.ReadStableBytes(evidence, 65_536, "catalog repair intent").AsSpan().SequenceEqual(intentBytes)) throw new InvalidDataException("Catalog repair evidence changed.");
                var preserved = Path.Combine(repair, entry.Name);
                GeneratedOutputPathSafety.EnsureSafeAncestors(preserved, includeLeaf: true, "catalog repair preimage");
                File.Move(target, preserved);
                checkpoint?.Invoke("catalog-quarantined");
            }
            var temporary = Path.Combine(directory, ".write-" + Guid.NewGuid().ToString("D"));
            WriteNew(temporary, entry.Bytes);
            GeneratedOutputPathSafety.EnsureSafeAncestors(target, includeLeaf: true, "catalog installation");
            File.Move(temporary, target);
            checkpoint?.Invoke("catalog-published");
        }
        _ = ReadOwnedPair(directory, hash);
        var completed = Path.Combine(directory, ".completed-" + id);
        GeneratedOutputPathSafety.EnsureSafeAncestors(completed, includeLeaf: true, "catalog completion");
        Directory.Move(stage, completed);
        checkpoint?.Invoke("catalog-committed");
        foreach (var entry in entries.Append((Name: "intent.json", Bytes: intentBytes)))
        {
            var path = Path.Combine(completed, entry.Name);
            if (!SunderRpcDescriptorFileReader.ReadStableBytes(path, MaximumBytes, "catalog cleanup").AsSpan().SequenceEqual(entry.Bytes))
                throw new InvalidDataException("contract-cache.entry-corrupt: Catalog cleanup preimage changed; it was preserved.");
            File.Delete(path);
        }
        Directory.Delete(completed);
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        GeneratedOutputPathSafety.EnsureSafeAncestors(path, includeLeaf: true, "catalog write");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes); stream.Flush(flushToDisk: true);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private sealed record CatalogIntent(int SchemaVersion, string Token, string CatalogSha256, string PreviousCatalogSha256, string PreviousReceiptSha256);
}
