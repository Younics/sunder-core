using System.Globalization;
using System.Text.Json;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring;

internal sealed record SunderRpcCacheCollectionResult(int RemovedEntries, int RemovedArtifacts, int RetainedUnprovenArtifacts, bool MoreCandidates);

internal static partial class SunderRpcContractCacheStore
{
    internal const string CollectionPending = ".gc-pending";
    private const int MaximumCollectionIntentBytes = 262_144;
    private const int MaximumCacheDirectoryEntries = 16_384;
    // A complete 128-pair descriptor repair artifact must fit in one batch.
    private const long MaximumCollectionBytes = 2_147_483_648;
    private static readonly StringComparer FilePathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static SunderRpcCacheCollectionResult Collect(string root, IReadOnlyList<string> reviewedLocks, int graceDays = 7,
        CancellationToken token = default, Action<string>? checkpoint = null, DateTimeOffset? observedNow = null)
    {
        if (reviewedLocks.Count is < 1 or > 128 || graceDays is < 1 or > 365)
            throw Corrupt("GC requires 1..128 explicit reviewed locks and a grace period of 1..365 days.");
        root = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(root);
        var paths = reviewedLocks.Select(GeneratedOutputPathSafety.NormalizeTrustedSystemAliases).Distinct(FilePathComparer).Order(StringComparer.Ordinal).ToArray();
        foreach (var path in paths)
        {
            if (GeneratedOutputPathSafety.IsDescendantOf(path, root) || FilePathComparer.Equals(path, root))
                throw Corrupt("Reviewed source locks must be outside the cache.");
            GeneratedOutputPathSafety.EnsureRegularFile(path, "reviewed contract lock");
        }
        // Selection commands also acquire source locks before reading the cache.
        using var sourceLease = GeneratedOutputLock.Acquire(paths, TimeSpan.FromSeconds(30), token);
        var marks = new HashSet<string>(StringComparer.Ordinal);
        var snapshots = new SunderRpcInputSnapshot();
        long total = 0;
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            var bytes = snapshots.Read(path, (int)Math.Min(33_554_432, 67_108_864 - total), "reviewed contract lock");
            total += bytes.Length;
            var value = SunderRpcAuthoringDocuments.ParseContractLock(bytes, allowDevelopmentRegistryOrigin: true);
            foreach (var artifact in value.PackageArtifacts.Values)
            {
                marks.Add(ProjectionKey(artifact.ModelProjectionSha256));
                foreach (var contract in artifact.Contracts.Values) marks.Add(contract.DescriptorSha256);
            }
            foreach (var contract in value.PlatformImports.Values) marks.Add(contract.DescriptorSha256);
            foreach (var catalog in SunderRpcPlatformCatalogCache.Requirements(value)) marks.Add(CatalogKey(catalog.ContentIdentity));
        }
        var now = (observedNow ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        var cutoff = now - graceDays * 86_400_000L;
        if (cutoff < 0) throw Corrupt("GC requires a valid current clock.");
        if (!GeneratedOutputPathSafety.PathExists(root)) return new(0, 0, 0, false);
        using var lease = GeneratedOutputLock.Acquire([root], TimeSpan.FromSeconds(30), token);
        ValidateRoot(root, requireComplete: true, allowCollectionRecovery: true);
        var names = DirectoryNames(root, MaximumCacheDirectoryEntries);
        foreach (var name in names) GeneratedOutputPathSafety.EnsureSafeAncestors(Path.Combine(root, name), includeLeaf: true, "cache inventory");
        foreach (var name in names.Where(name => TokenAfter(name, ".gc-trash-") is not null))
            CleanupCollectionTrash(Path.Combine(root, name), token, checkpoint);
        if (GeneratedOutputPathSafety.PathExists(Path.Combine(root, CollectionPending)))
            return ResumeCollection(root, paths, marks, VerifyLocks, now, token, checkpoint) with { MoreCandidates = true };

        var units = new List<CollectionUnit>();
        var unproven = 0;
        var more = false;
        var fileCount = 0;
        long selectedBytes = 0;
        void Select(CollectionUnit unit)
        {
            var bytes = unit.Files.Sum(file => (long)file.Bytes);
            if (units.Count == 128 || fileCount + unit.Files.Length > 1024 || selectedBytes + bytes > MaximumCollectionBytes) { more = true; return; }
            units.Add(unit); fileCount += unit.Files.Length; selectedBytes += bytes;
        }
        foreach (var name in names.Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (name.Length == 69 && name.EndsWith(".json", StringComparison.Ordinal) && Digest(name[..64]))
            {
                var hash = name[..64];
                if (marks.Contains(hash)) continue;
                var receipt = hash + SunderRpcContractCache.ReceiptSuffix;
                if (!GeneratedOutputPathSafety.PathExists(Path.Combine(root, receipt))) { unproven++; continue; }
                var files = new[] { name, receipt };
                if (files.Any(file => Modified(Path.Combine(root, file)) > cutoff)) continue;
                try { ValidatePair(root, hash); }
                catch (Exception error) when (error is InvalidDataException or SunderRpcDescriptorException) { unproven++; continue; }
                Select(new("entry", hash, DescribeFiles(root, files)));
            }
            else if (TokenAfter(name, ".stage-") is not null || TokenAfter(name, ".completed-") is not null || TokenAfter(name, ".gc-stage-") is not null)
                SelectArtifact(name);
            else if (name == ".quarantine")
            {
                GeneratedOutputPathSafety.EnsureRegularDirectory(Path.Combine(root, name), "cache quarantine");
                foreach (var child in DirectoryNames(Path.Combine(root, name), 4096).Order(StringComparer.Ordinal))
                    if (CanonicalToken(child)) SelectArtifact(name + "/" + child); else unproven++;
            }
            else if (name == SunderRpcPlatformCatalogCache.DirectoryName)
            {
                var directory = Path.Combine(root, name);
                foreach (var child in DirectoryNames(directory, MaximumCacheDirectoryEntries).Order(StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested();
                    GeneratedOutputPathSafety.EnsureSafeAncestors(Path.Combine(directory, child), includeLeaf: true, "catalog cache inventory");
                    if (child.Length == 69 && child.EndsWith(".json", StringComparison.Ordinal) && Digest(child[..64]))
                    {
                        var hash = child[..64]; var key = CatalogKey(hash);
                        if (marks.Contains(key)) continue;
                        var receipt = hash + SunderRpcPlatformCatalogCache.ReceiptSuffix;
                        if (!GeneratedOutputPathSafety.PathExists(Path.Combine(directory, receipt))) { unproven++; continue; }
                        var files = new[] { child, receipt };
                        if (files.Any(file => Modified(Path.Combine(directory, file)) > cutoff)) continue;
                        byte[] body;
                        try { body = SunderRpcPlatformCatalogCache.ReadOwnedPair(directory, hash); }
                        catch (InvalidDataException) { unproven++; continue; }
                        var receiptBytes = SunderRpcPlatformCatalogCache.ReceiptBytes(hash, body.Length);
                        Select(new("catalog", key, [new(child, hash, body.Length), new(receipt, Hash(receiptBytes), receiptBytes.Length)]));
                    }
                    else if (SunderRpcPlatformCatalogCache.IsPublicationArtifact(name + "/" + child)) SelectArtifact(name + "/" + child);
                    else if (!child.EndsWith(SunderRpcPlatformCatalogCache.ReceiptSuffix, StringComparison.Ordinal)
                        || !Digest(child[..^SunderRpcPlatformCatalogCache.ReceiptSuffix.Length])
                        || !GeneratedOutputPathSafety.PathExists(Path.Combine(directory, child[..^SunderRpcPlatformCatalogCache.ReceiptSuffix.Length] + ".json"))) unproven++;
                }
            }
            else if (name == SunderRpcContractCache.ModelProjectionDirectoryName)
            {
                var parents = DirectoryNames(Path.Combine(root, name), MaximumCacheDirectoryEntries);
                unproven += parents.Count(parent => parent != "sha256");
                if (!parents.Contains("sha256", StringComparer.Ordinal)) continue;
                var directory = Path.Combine(root, name, "sha256");
                foreach (var child in DirectoryNames(directory, MaximumCacheDirectoryEntries).Order(StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested();
                    GeneratedOutputPathSafety.EnsureSafeAncestors(Path.Combine(directory, child), includeLeaf: true, "model projection cache inventory");
                    if (child.Length == 69 && child.EndsWith(".json", StringComparison.Ordinal) && Digest(child[..64]))
                    {
                        var hash = child[..64]; var key = ProjectionKey(hash);
                        if (marks.Contains(key)) continue;
                        var receipt = hash + SunderRpcContractCache.ReceiptSuffix;
                        if (!GeneratedOutputPathSafety.PathExists(Path.Combine(directory, receipt))) { unproven++; continue; }
                        if (new[] { child, receipt }.Any(file => Modified(Path.Combine(directory, file)) > cutoff)) continue;
                        byte[] body;
                        try { body = SunderRpcContractCache.ReadOwnedModelProjectionPair(directory, hash); }
                        catch (InvalidDataException) { unproven++; continue; }
                        var ownership = SunderRpcContractCache.ModelProjectionReceiptBytes(hash, body.Length);
                        Select(new("projection", key, [new(child, hash, body.Length), new(receipt, Hash(ownership), ownership.Length)]));
                    }
                    else if (!child.EndsWith(SunderRpcContractCache.ReceiptSuffix, StringComparison.Ordinal)
                        || !Digest(child[..^SunderRpcContractCache.ReceiptSuffix.Length])
                        || !GeneratedOutputPathSafety.PathExists(Path.Combine(directory, child[..^SunderRpcContractCache.ReceiptSuffix.Length] + ".json"))) unproven++;
                }
            }
            else if (name.StartsWith(".write-", StringComparison.Ordinal) || name.StartsWith(".gc-stage-", StringComparison.Ordinal)) unproven++;
        }
        if (units.Count == 0) return new(0, 0, unproven, more);
        units.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        var intent = new CollectionIntent(1, Guid.NewGuid().ToString("D"), cutoff, paths, units.ToArray());
        var bytesToWrite = CollectionBytes(intent);
        if (bytesToWrite.Length > MaximumCollectionIntentBytes) throw Corrupt("GC intent exceeds its byte bound.");
        var stage = Path.Combine(root, ".gc-stage-" + intent.Token);
        Directory.CreateDirectory(stage);
        try
        {
            WriteNew(Path.Combine(stage, IntentFile), bytesToWrite);
            VerifyLocks(); token.ThrowIfCancellationRequested();
            Directory.Move(stage, Path.Combine(root, CollectionPending));
            checkpoint?.Invoke("gc-prepared");
            var result = ResumeCollection(root, paths, marks, VerifyLocks, now, token, checkpoint);
            return result with { RetainedUnprovenArtifacts = unproven, MoreCandidates = more };
        }
        finally { if (Directory.Exists(stage)) DeleteFlatStage(stage); }

        void VerifyLocks()
        {
            try { snapshots.Verify(); }
            catch (IOException) { throw Corrupt("Reviewed lock changed during GC; retry against current inputs."); }
        }

        void SelectArtifact(string key)
        {
            var path = ArtifactPath(root, key);
            GeneratedOutputPathSafety.EnsureRegularDirectory(path, "retained cache artifact");
            var files = DirectoryNames(path, 257);
            foreach (var file in files) GeneratedOutputPathSafety.EnsureRegularFile(Path.Combine(path, file), "retained artifact file");
            if (Modified(path) > cutoff || files.Any(file => Modified(Path.Combine(path, file)) > cutoff)) return;
            if (!TryProveArtifact(path, key, files)) { unproven++; return; }
            Select(new("artifact", key, DescribeFiles(path, files, MaximumFileBytes(key))));
        }
    }

    private static SunderRpcCacheCollectionResult ResumeCollection(string root, string[] paths, IReadOnlySet<string> marks,
        Action verifyLocks, long now, CancellationToken token, Action<string>? checkpoint)
    {
        var pending = Path.Combine(root, CollectionPending);
        var intent = ReadCollectionIntent(pending);
        if (intent.CutoffUnixMilliseconds > now - 86_400_000L || intent.ReviewedLocks.Any(path => !paths.Contains(path, FilePathComparer)))
            throw Corrupt("GC recovery requires the original reviewed lock set (additional locks are allowed) and a valid clock.");
        ValidateCollectionChildren(pending, intent);
        var retained = intent.Units.Select(unit => unit.Kind != "artifact" && marks.Contains(unit.Key)
            || unit.Files.Any(file => GeneratedOutputPathSafety.PathExists(SourceFile(root, unit, file.Name))
                && Modified(SourceFile(root, unit, file.Name)) > intent.CutoffUnixMilliseconds)).ToArray();
        // Preflight the entire interrupted set before making any further move.
        for (var index = 0; index < intent.Units.Length; index++)
            foreach (var file in intent.Units[index].Files)
            {
                var source = SourceFile(root, intent.Units[index], file.Name);
                var tombstone = Path.Combine(pending, UnitDirectory(index), file.Name);
                if (!VerifyOptional(source, file) && !GeneratedOutputPathSafety.PathExists(tombstone)) throw Corrupt("GC source and tombstone are both missing.");
                _ = VerifyOptional(tombstone, file);
            }
        verifyLocks();
        var removedEntries = 0; var removedArtifacts = 0;
        for (var index = 0; index < intent.Units.Length; index++)
        {
            var unit = intent.Units[index];
            var tombstone = Path.Combine(pending, UnitDirectory(index));
            foreach (var file in unit.Files)
            {
                token.ThrowIfCancellationRequested();
                var source = SourceFile(root, unit, file.Name);
                var moved = Path.Combine(tombstone, file.Name);
                if (retained[index])
                {
                    if (!VerifyOptional(source, file))
                    {
                        GeneratedOutputPathSafety.EnsureSafeAncestors(Path.GetDirectoryName(source)!, includeLeaf: true, "GC retained entry parent");
                        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                        GeneratedOutputPathSafety.EnsureSafeAncestors(source, includeLeaf: true, "GC retained entry");
                        File.Move(moved, source);
                    }
                    else if (VerifyOptional(moved, file)) File.Delete(moved);
                }
                else if (!VerifyOptional(moved, file))
                {
                    Directory.CreateDirectory(tombstone);
                    GeneratedOutputPathSafety.EnsureSafeAncestors(moved, includeLeaf: true, "GC tombstone");
                    File.Move(source, moved);
                    checkpoint?.Invoke("gc-tombstoned:" + unit.Key + "/" + file.Name);
                }
                else if (GeneratedOutputPathSafety.PathExists(source))
                    throw Corrupt("GC encountered duplicate source and tombstone ownership.");
            }
            if (retained[index]) { if (Directory.Exists(tombstone)) Directory.Delete(tombstone); }
            else if (unit.Kind != "artifact") removedEntries++;
            else
            {
                var source = ArtifactPath(root, unit.Key);
                if (Directory.Exists(source)) Directory.Delete(source);
                removedArtifacts++;
            }
        }
        verifyLocks(); token.ThrowIfCancellationRequested();
        checkpoint?.Invoke("gc-moved");
        verifyLocks(); token.ThrowIfCancellationRequested();
        var trash = Path.Combine(root, ".gc-trash-" + intent.Token);
        GeneratedOutputPathSafety.EnsureSafeAncestors(trash, includeLeaf: true, "GC completion");
        Directory.Move(pending, trash);
        checkpoint?.Invoke("gc-committed");
        CleanupCollectionTrash(trash, token, checkpoint);
        return new(removedEntries, removedArtifacts, 0, false);
    }

    private static void CleanupCollectionTrash(string path, CancellationToken token, Action<string>? checkpoint)
    {
        GeneratedOutputPathSafety.EnsureRegularDirectory(path, "GC trash");
        if (DirectoryNames(path, 129).Length == 0) { Directory.Delete(path); return; }
        var intent = ReadCollectionIntent(path);
        if (Path.GetFileName(path) != ".gc-trash-" + intent.Token) throw Corrupt("GC trash identity mismatch.");
        ValidateCollectionChildren(path, intent);
        for (var index = 0; index < intent.Units.Length; index++)
            foreach (var file in intent.Units[index].Files) _ = VerifyOptional(Path.Combine(path, UnitDirectory(index), file.Name), file);
        for (var index = 0; index < intent.Units.Length; index++)
        {
            var directory = Path.Combine(path, UnitDirectory(index));
            if (!Directory.Exists(directory)) continue;
            foreach (var file in intent.Units[index].Files)
            {
                token.ThrowIfCancellationRequested();
                var target = Path.Combine(directory, file.Name);
                if (VerifyOptional(target, file)) { File.Delete(target); checkpoint?.Invoke("gc-deleted:" + file.Name); }
            }
            Directory.Delete(directory);
        }
        File.Delete(Path.Combine(path, IntentFile));
        Directory.Delete(path);
    }

    private static CollectionIntent ReadCollectionIntent(string directory)
    {
        GeneratedOutputPathSafety.EnsureRegularDirectory(directory, "GC transaction");
        var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(Path.Combine(directory, IntentFile), MaximumCollectionIntentBytes, "GC intent");
        using var document = SunderRpcAuthoringJson.Parse(bytes, MaximumCollectionIntentBytes);
        SunderRpcAuthoringJson.RequireObject(document.RootElement, "schemaVersion", "token", "cutoffUnixMilliseconds", "reviewedLocks", "units");
        var intent = JsonSerializer.Deserialize<CollectionIntent>(bytes, Json) ?? throw Corrupt("GC intent is empty.");
        if (intent.SchemaVersion != 1 || !CanonicalToken(intent.Token) || intent.CutoffUnixMilliseconds is < 0 or > 9_007_199_254_740_991
            || intent.ReviewedLocks is not { Length: >= 1 and <= 128 } || intent.Units is not { Length: >= 1 and <= 128 }
            || !bytes.AsSpan().SequenceEqual(CollectionBytes(intent))) throw Corrupt("GC intent is not canonical and bounded.");
        RequireOrdered(intent.ReviewedLocks);
        if (intent.ReviewedLocks.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))) throw Corrupt("GC reviewed lock paths are invalid.");
        RequireOrdered(intent.Units.Select(unit => unit.Key));
        var count = 0;
        long totalBytes = 0;
        foreach (var unit in intent.Units)
        {
            if (unit.Kind is not ("entry" or "catalog" or "projection" or "artifact") || unit.Kind == "entry" && !Digest(unit.Key)
                || unit.Kind == "catalog" && !IsCatalogKey(unit.Key)
                || unit.Kind == "projection" && !IsProjectionKey(unit.Key)
                || unit.Kind == "artifact" && !IsArtifactKey(unit.Key) || unit.Files is not { Length: >= 1 and <= 257 }
                || (count += unit.Files.Length) > 1024) throw Corrupt("GC unit shape is invalid.");
            RequireOrdered(unit.Files.Select(file => file.Name));
            foreach (var file in unit.Files)
            {
                if (file.Name != IntentFile && !(file.Name.EndsWith(".json", StringComparison.Ordinal)
                    && Digest(file.Name.EndsWith(SunderRpcContractCache.ReceiptSuffix, StringComparison.Ordinal) ? file.Name[..^SunderRpcContractCache.ReceiptSuffix.Length] : file.Name[..^5]))
                    || !Digest(file.Sha256) || file.Bytes < 0 || file.Bytes > MaximumFileBytes(unit.Key)
                    || (totalBytes += file.Bytes) > MaximumCollectionBytes) throw Corrupt("GC file shape is invalid.");
            }
            var hash = unit.Kind == "catalog" ? unit.Key[(SunderRpcPlatformCatalogCache.DirectoryName.Length + 1)..]
                : unit.Kind == "projection" ? unit.Key[(SunderRpcContractCache.ModelProjectionCacheDirectory.Length + 1)..] : unit.Key;
            if (unit.Kind != "artifact" && !unit.Files.Select(file => file.Name).SequenceEqual(new[] { hash + ".json", hash + SunderRpcContractCache.ReceiptSuffix }, StringComparer.Ordinal))
                throw Corrupt("GC entry is not one complete content/receipt pair.");
            if (unit.Kind != "artifact" && unit.Files[0].Sha256 != hash) throw Corrupt("GC entry preimage differs from its content address.");
            if (unit.Kind == "artifact" && !unit.Files.Any(file => file.Name == IntentFile)) throw Corrupt("GC artifact lacks ownership evidence.");
        }
        return intent;
    }

    private static bool TryProveArtifact(string path, string key, string[] names)
    {
        if (!names.Contains(IntentFile, StringComparer.Ordinal)) return false;
        try
        {
            if (SunderRpcPlatformCatalogCache.IsPublicationArtifact(key)) return SunderRpcPlatformCatalogCache.ProvePublicationArtifact(path, key, names);
            if (key.StartsWith(".gc-stage-", StringComparison.Ordinal))
                return names.Length == 1 && names[0] == IntentFile && key == ".gc-stage-" + ReadCollectionIntent(path).Token;
            var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(Path.Combine(path, IntentFile), 65_536, "retained restore intent");
            using var document = SunderRpcAuthoringJson.Parse(bytes, 65_536);
            var intent = JsonSerializer.Deserialize<CacheIntent>(bytes, Json);
            if (intent is null || intent.SchemaVersion != 1 || !CanonicalToken(intent.Token) || !key.EndsWith(intent.Token, StringComparison.Ordinal)
                || intent.Entries is not { Length: >= 1 and <= 128 } || !bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(intent, Json))) return false;
            RequireOrdered(intent.Entries.Select(entry => entry.DescriptorSha256));
            var allowed = new Dictionary<string, string>(StringComparer.Ordinal) { [IntentFile] = Hash(bytes) };
            var quarantine = key.StartsWith(".quarantine/", StringComparison.Ordinal);
            foreach (var entry in intent.Entries)
            {
                if (!Digest(entry.DescriptorSha256) || entry.PreviousDescriptorSha256 != "" && !Digest(entry.PreviousDescriptorSha256)
                    || entry.PreviousReceiptSha256 != "" && !Digest(entry.PreviousReceiptSha256)) return false;
                allowed[entry.DescriptorSha256 + ".json"] = quarantine ? entry.PreviousDescriptorSha256 : entry.DescriptorSha256;
                allowed[entry.DescriptorSha256 + SunderRpcContractCache.ReceiptSuffix] = quarantine ? entry.PreviousReceiptSha256 : "receipt";
                if (key.StartsWith(".stage-", StringComparison.Ordinal)) ValidatePair(path, entry.DescriptorSha256);
            }
            foreach (var name in names)
            {
                if (!allowed.TryGetValue(name, out var expected)) return false;
                if (expected == "receipt")
                {
                    var receipt = SunderRpcAuthoringDocuments.ReadContractCacheEntryReceipt(Path.Combine(path, name));
                    if (receipt.DescriptorSha256 + SunderRpcContractCache.ReceiptSuffix != name) return false;
                }
                else if (ExistingHash(Path.Combine(path, name)) != expected) return false;
            }
            return !key.StartsWith(".stage-", StringComparison.Ordinal) || names.Length == allowed.Count;
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or SunderRpcDescriptorException or FileNotFoundException) { return false; }
    }

    private static CollectionFile[] DescribeFiles(string root, IEnumerable<string> names, int maximum = MaximumDescriptorBytes) => names.Order(StringComparer.Ordinal).Select(name =>
    {
        var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(Path.Combine(root, name), maximum, "GC candidate");
        return new CollectionFile(name, Hash(bytes), bytes.Length);
    }).ToArray();
    private static bool VerifyOptional(string path, CollectionFile file)
    {
        if (!GeneratedOutputPathSafety.PathExists(path)) return false;
        var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(path, file.Bytes, "GC file preimage");
        if (bytes.Length != file.Bytes || Hash(bytes) != file.Sha256) throw Corrupt("GC file changed; unexpected bytes were preserved.");
        return true;
    }
    private static void ValidateCollectionChildren(string root, CollectionIntent intent)
    {
        var allowed = Enumerable.Range(0, intent.Units.Length).Select(UnitDirectory).ToHashSet(StringComparer.Ordinal);
        foreach (var name in DirectoryNames(root, 129))
        {
            if (name == IntentFile) continue;
            if (!allowed.Contains(name)) throw Corrupt("Unexpected GC transaction child.");
            var directory = Path.Combine(root, name); GeneratedOutputPathSafety.EnsureRegularDirectory(directory, "GC unit");
            var unit = intent.Units[int.Parse(name, CultureInfo.InvariantCulture)];
            var files = unit.Files.Select(file => file.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var file in DirectoryNames(directory, 257))
            { if (!files.Contains(file)) throw Corrupt("Unexpected GC tombstone file."); GeneratedOutputPathSafety.EnsureRegularFile(Path.Combine(directory, file), "GC tombstone file"); }
        }
    }
    private static string[] DirectoryNames(string path, int maximum)
    {
        GeneratedOutputPathSafety.EnsureRegularDirectory(path, "bounded cache directory");
        var values = Directory.EnumerateFileSystemEntries(path).Take(maximum + 1).Select(path => Path.GetFileName(path)!).ToArray();
        if (values.Length > maximum) throw Corrupt("Cache directory exceeds its enumeration bound.");
        return values;
    }
    private static long Modified(string path)
    { GeneratedOutputPathSafety.EnsureSafeAncestors(path, includeLeaf: true, "GC age evidence"); return new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds(); }
    private static byte[] CollectionBytes(CollectionIntent intent) => SunderRpcAuthoringDocuments.WriteDeterministicDocument(JsonSerializer.SerializeToElement(intent, Json));
    private static bool CanonicalToken(string? value) => Guid.TryParseExact(value, "D", out var token) && token.ToString("D") == value;
    private static string? TokenAfter(string value, string prefix) => value.StartsWith(prefix, StringComparison.Ordinal) && CanonicalToken(value[prefix.Length..]) ? value[prefix.Length..] : null;
    private static string CatalogKey(string hash) => SunderRpcPlatformCatalogCache.DirectoryName + "/" + hash;
    private static string ProjectionKey(string hash) => SunderRpcContractCache.ModelProjectionCacheDirectory + "/" + hash;
    private static bool IsProjectionKey(string key) => key.StartsWith(SunderRpcContractCache.ModelProjectionCacheDirectory + "/", StringComparison.Ordinal)
        && Digest(key[(SunderRpcContractCache.ModelProjectionCacheDirectory.Length + 1)..]);
    private static bool IsCatalogKey(string key) => key.StartsWith(SunderRpcPlatformCatalogCache.DirectoryName + "/", StringComparison.Ordinal)
        && Digest(key[(SunderRpcPlatformCatalogCache.DirectoryName.Length + 1)..]);
    private static int MaximumFileBytes(string key) => IsCatalogKey(key) || SunderRpcPlatformCatalogCache.IsPublicationArtifact(key)
        ? SunderRpcPlatformCatalogCache.MaximumBytes : MaximumDescriptorBytes;
    private static bool IsArtifactKey(string key) => TokenAfter(key, ".stage-") is not null || TokenAfter(key, ".completed-") is not null || TokenAfter(key, ".quarantine/") is not null || TokenAfter(key, ".gc-stage-") is not null || SunderRpcPlatformCatalogCache.IsPublicationArtifact(key);
    private static string ArtifactPath(string root, string key) => IsArtifactKey(key) ? Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)) : throw Corrupt("Invalid cache artifact path.");
    private static string SourceFile(string root, CollectionUnit unit, string name) => Path.Combine(unit.Kind == "entry" ? root
        : unit.Kind == "catalog" ? Path.Combine(root, SunderRpcPlatformCatalogCache.DirectoryName)
        : unit.Kind == "projection" ? Path.Combine(root, SunderRpcContractCache.ModelProjectionDirectoryName, "sha256") : ArtifactPath(root, unit.Key), name);
    private static string UnitDirectory(int index) => index.ToString("D4", CultureInfo.InvariantCulture);
    private static void RequireOrdered(IEnumerable<string> values)
    { string? previous = null; foreach (var value in values) { if (value is null || previous is not null && string.CompareOrdinal(previous, value) >= 0) throw Corrupt("GC sets must be ascending and unique."); previous = value; } }
    private sealed record CollectionIntent(int SchemaVersion, string Token, long CutoffUnixMilliseconds, string[] ReviewedLocks, CollectionUnit[] Units);
    private sealed record CollectionUnit(string Kind, string Key, CollectionFile[] Files);
    private sealed record CollectionFile(string Name, string Sha256, int Bytes);
}
