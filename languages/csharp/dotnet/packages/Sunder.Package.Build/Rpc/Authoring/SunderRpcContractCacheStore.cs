using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring;

// Shared with Node rpc-contract-cache.ts. Cache integrity is not Registry or platform authority.
internal static partial class SunderRpcContractCacheStore
{
    internal const string Marker = ".sunder-contract-cache";
    internal const string MarkerText = "sunder.contract-cache.v1\n";
    internal const string Pending = ".pending";
    internal const string IntentFile = "intent.json";
    internal const int MaximumDescriptorBytes = 4_194_304;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static void InitializeRoot(string root)
    {
        GeneratedOutputPathSafety.EnsureSafeAncestors(root, includeLeaf: true, "contract cache");
        Directory.CreateDirectory(root);
        GeneratedOutputPathSafety.EnsureRegularDirectory(root, "contract cache");
        var marker = Path.Combine(root, Marker);
        if (!GeneratedOutputPathSafety.PathExists(marker))
        {
            if (Directory.EnumerateFileSystemEntries(root).Any()) throw Corrupt("Cannot adopt an unmarked nonempty cache.");
            WriteNew(marker, Encoding.UTF8.GetBytes(MarkerText));
        }
        ValidateRoot(root);
    }

    internal static void ValidateRoot(string root, bool requireComplete = false, bool allowCollectionRecovery = false)
    {
        GeneratedOutputPathSafety.EnsureSafeAncestors(root, includeLeaf: true, "contract cache");
        GeneratedOutputPathSafety.EnsureRegularDirectory(root, "contract cache");
        var marker = Path.Combine(root, Marker);
        if (!GeneratedOutputPathSafety.PathExists(marker)
            || !SunderRpcDescriptorFileReader.ReadStableBytes(marker, 128, "contract cache marker").AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(MarkerText)))
            throw Corrupt("The cache ownership marker is missing or invalid.");
        if (requireComplete && GeneratedOutputPathSafety.PathExists(Path.Combine(root, Pending)))
            throw Corrupt("A cache publication is incomplete. Run explicit contracts restore to recover it.");
        if (!allowCollectionRecovery && GeneratedOutputPathSafety.PathExists(Path.Combine(root, CollectionPending)))
            throw Corrupt("Cache collection is incomplete. Run explicit contracts gc with the original reviewed locks to recover it.");
    }

    internal static byte[] ReceiptBytes(SunderRpcContractCacheRequirement value)
        => SunderRpcAuthoringDocuments.WriteDeterministicDocument(JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1, contractId = value.ContractId, contractVersion = value.ContractVersion,
            descriptorBytes = value.DescriptorBytes, descriptorSha256 = value.DescriptorSha256, abiSha256 = value.AbiSha256,
        }, Json));

    internal static void Publish(string root, IReadOnlyDictionary<string, byte[]> entries,
        IReadOnlyDictionary<string, SunderRpcContractCacheRequirement> requirements, CancellationToken token = default,
        Action<string>? checkpoint = null)
    {
        if (entries.Count is < 1 or > 128) throw Corrupt("Publication requires 1..128 descriptor entries.");
        foreach (var (hash, bytes) in entries)
        {
            if (!requirements.TryGetValue(hash, out var requirement)) throw Corrupt("Publication has an unrequested entry.");
            var descriptor = SunderRpcContractDescriptor.Parse(bytes);
            if (bytes.Length != requirement.DescriptorBytes || descriptor.Sha256 != hash || hash != requirement.DescriptorSha256
                || descriptor.AbiSha256 != requirement.AbiSha256 || descriptor.ContractId != requirement.ContractId
                || descriptor.Version != requirement.ContractVersion || !bytes.AsSpan().SequenceEqual(descriptor.GetCanonicalUtf8()))
                throw Corrupt("Publication bytes do not reproduce their exact canonical requirement.");
        }
        root = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(root);
        using var lease = GeneratedOutputLock.Acquire([root], TimeSpan.FromSeconds(30), token);
        InitializeRoot(root);
        Recover(root, token, checkpoint);
        var selected = entries.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
        {
            var requirement = requirements[pair.Key];
            if (requirement.DescriptorSha256 != pair.Key) throw Corrupt("Cache publication identity differs from its requirement.");
            var receipt = ReceiptBytes(requirement);
            var oldDescriptor = ExistingHash(SunderRpcContractCache.GetDescriptorPath(root, pair.Key));
            var oldReceipt = ExistingHash(SunderRpcContractCache.GetReceiptPath(root, pair.Key));
            return (Hash: pair.Key, Descriptor: pair.Value, Receipt: receipt, OldDescriptor: oldDescriptor, OldReceipt: oldReceipt);
        }).Where(entry => entry.OldDescriptor != Hash(entry.Descriptor) || entry.OldReceipt != Hash(entry.Receipt)).ToArray();
        if (selected.Length == 0) return;
        var id = Guid.NewGuid().ToString("D");
        var stage = Path.Combine(root, ".stage-" + id);
        var pending = Path.Combine(root, Pending);
        try
        {
            Directory.CreateDirectory(stage);
            foreach (var entry in selected)
            {
                token.ThrowIfCancellationRequested();
                WriteNew(SunderRpcContractCache.GetDescriptorPath(stage, entry.Hash), entry.Descriptor);
                WriteNew(SunderRpcContractCache.GetReceiptPath(stage, entry.Hash), entry.Receipt);
                ValidatePair(stage, entry.Hash);
            }
            var intent = new CacheIntent(1, id, selected.Select(entry => new CacheEntry(entry.Hash, entry.OldDescriptor, entry.OldReceipt)).ToArray());
            WriteNew(Path.Combine(stage, IntentFile), JsonSerializer.SerializeToUtf8Bytes(intent, Json));
            token.ThrowIfCancellationRequested();
            GeneratedOutputPathSafety.EnsureSafeAncestors(pending, includeLeaf: true, "cache publication");
            Directory.Move(stage, pending);
            checkpoint?.Invoke("prepared");
            Recover(root, token, checkpoint);
        }
        finally
        {
            if (Directory.Exists(stage)) DeleteFlatStage(stage);
        }
    }

    // Must be called under the cache lease. Build never calls this recovery writer.
    private static void Recover(string root, CancellationToken token, Action<string>? checkpoint)
    {
        var pending = Path.Combine(root, Pending);
        if (!GeneratedOutputPathSafety.PathExists(pending)) return;
        GeneratedOutputPathSafety.EnsureRegularDirectory(pending, "pending cache publication");
        var intentBytes = SunderRpcDescriptorFileReader.ReadStableBytes(Path.Combine(pending, IntentFile), 65_536, "cache publication intent");
        using var document = SunderRpcAuthoringJson.Parse(intentBytes, 65_536);
        SunderRpcAuthoringJson.RequireObject(document.RootElement, "schemaVersion", "token", "entries");
        var intent = JsonSerializer.Deserialize<CacheIntent>(intentBytes, Json) ?? throw Corrupt("Cache intent is empty.");
        if (intent.SchemaVersion != 1 || !Guid.TryParseExact(intent.Token, "D", out var id) || id.ToString("D") != intent.Token
            || intent.Entries is not { Length: >= 1 and <= 128 }
            || !intentBytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(intent, Json)))
            throw Corrupt("Cache intent is not canonical, closed or bounded.");
        var expectedFiles = new HashSet<string>(StringComparer.Ordinal) { IntentFile };
        string? previous = null;
        foreach (var entry in intent.Entries)
        {
            if (entry is null || !Digest(entry.DescriptorSha256) || previous is not null && string.CompareOrdinal(previous, entry.DescriptorSha256) >= 0
                || entry.PreviousDescriptorSha256 != "" && !Digest(entry.PreviousDescriptorSha256)
                || entry.PreviousReceiptSha256 != "" && !Digest(entry.PreviousReceiptSha256))
                throw Corrupt("Cache intent entries have invalid hashes or order.");
            previous = entry.DescriptorSha256;
            expectedFiles.Add(entry.DescriptorSha256 + ".json");
            expectedFiles.Add(entry.DescriptorSha256 + SunderRpcContractCache.ReceiptSuffix);
            ValidatePair(pending, entry.DescriptorSha256);
        }
        RequireFlatFiles(pending, expectedFiles);
        var quarantine = Path.Combine(root, ".quarantine", intent.Token);
        foreach (var entry in intent.Entries)
        {
            Install(entry.DescriptorSha256 + ".json", entry.PreviousDescriptorSha256);
            Install(entry.DescriptorSha256 + SunderRpcContractCache.ReceiptSuffix, entry.PreviousReceiptSha256);
        }
        checkpoint?.Invoke("installed");
        // A directory tombstone is the visibility commit. A crash while disposing it cannot
        // turn a complete published cache into a malformed pending transaction.
        var completed = Path.Combine(root, ".completed-" + intent.Token);
        GeneratedOutputPathSafety.EnsureSafeAncestors(completed, includeLeaf: true, "completed cache publication");
        Directory.Move(pending, completed);
        checkpoint?.Invoke("committed");
        DeleteFlatStage(completed);

        void Install(string name, string previousHash)
        {
            token.ThrowIfCancellationRequested();
            var source = Path.Combine(pending, name);
            var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(source, MaximumDescriptorBytes, "staged cache entry");
            var target = Path.Combine(root, name);
            var current = ExistingHash(target);
            if (current == Hash(bytes)) return;
            if (current != "")
            {
                if (current != previousHash) throw Corrupt("A cache entry changed after publication planning; its bytes were preserved.");
                GeneratedOutputPathSafety.EnsureSafeAncestors(quarantine, includeLeaf: true, "cache quarantine");
                Directory.CreateDirectory(quarantine);
                var evidence = Path.Combine(quarantine, IntentFile);
                if (!GeneratedOutputPathSafety.PathExists(evidence)) WriteNew(evidence, intentBytes);
                else if (!SunderRpcDescriptorFileReader.ReadStableBytes(evidence, 65_536, "quarantine evidence").AsSpan().SequenceEqual(intentBytes))
                    throw Corrupt("Quarantine evidence changed.");
                var preserved = Path.Combine(quarantine, name);
                GeneratedOutputPathSafety.EnsureSafeAncestors(preserved, includeLeaf: true, "quarantine entry");
                File.Move(target, preserved);
                checkpoint?.Invoke("quarantined:" + name);
            }
            var temporary = Path.Combine(root, ".write-" + Guid.NewGuid().ToString("D"));
            try
            {
                WriteNew(temporary, bytes);
                GeneratedOutputPathSafety.EnsureSafeAncestors(target, includeLeaf: true, "cache entry installation");
                File.Move(temporary, target);
                checkpoint?.Invoke("published:" + name);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static void ValidatePair(string root, string hash)
    {
        var descriptor = SunderRpcDescriptorFileReader.Read(SunderRpcContractCache.GetDescriptorPath(root, hash), "staged cache descriptor", requireCanonical: true);
        var receipt = SunderRpcAuthoringDocuments.ReadContractCacheEntryReceipt(SunderRpcContractCache.GetReceiptPath(root, hash));
        if (descriptor.Descriptor.Sha256 != hash) throw Corrupt("Staged descriptor does not match its content address.");
        SunderRpcContractCache.ValidateEntry(new(receipt.ContractId, receipt.ContractVersion, receipt.DescriptorBytes, receipt.DescriptorSha256, receipt.AbiSha256), descriptor, receipt);
    }

    private static string ExistingHash(string path)
        => GeneratedOutputPathSafety.PathExists(path) ? Hash(SunderRpcDescriptorFileReader.ReadStableBytes(path, MaximumDescriptorBytes, "existing cache entry")) : "";

    private static void WriteNew(string path, byte[] bytes)
    {
        GeneratedOutputPathSafety.EnsureSafeAncestors(path, includeLeaf: true, "contract cache write");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    private static void RequireFlatFiles(string directory, IReadOnlySet<string> expected)
    {
        var files = Directory.EnumerateFileSystemEntries(directory).Take(258).ToArray();
        if (files.Length != expected.Count || files.Any(path => !expected.Contains(Path.GetFileName(path))))
            throw Corrupt("Cache transaction contains unexpected or missing files.");
        foreach (var path in files) GeneratedOutputPathSafety.EnsureRegularFile(path, "cache transaction file");
    }

    private static void DeleteFlatStage(string path)
    {
        GeneratedOutputPathSafety.EnsureRegularDirectory(path, "cache stage");
        var files = Directory.EnumerateFileSystemEntries(path).Take(258).ToArray();
        if (files.Length > 257) throw Corrupt("Cache stage exceeds its file count bound.");
        foreach (var file in files) GeneratedOutputPathSafety.EnsureRegularFile(file, "cache stage file");
        foreach (var file in files) File.Delete(file);
        Directory.Delete(path);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static InvalidDataException Corrupt(string message) => new("contract-cache.entry-corrupt: " + message);
    private sealed record CacheIntent(int SchemaVersion, string Token, CacheEntry[] Entries);
    private sealed record CacheEntry(string DescriptorSha256, string PreviousDescriptorSha256, string PreviousReceiptSha256);
}
