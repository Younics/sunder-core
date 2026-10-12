using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring;

internal static partial class SunderRpcAuthoringDocuments
{
    // The only reader admitting the historical missing-field entry shape. Normal
    // readers never call this path, including Restore and ordinary Build.
    internal static SunderRpcContractLock ReadConsumerRoleMigrationInput(string path, bool allowDevelopmentRegistryOrigin)
    {
        var bytes = SunderRpcDescriptorFileReader.ReadStableBytes(path, MaximumContractLockBytes, "consumer role migration input");
        var document = ParseStrictDocument(bytes, MaximumContractLockBytes, "consumer role migration input", ValidatePresentConsumerRoles);
        using (document)
        {
            var node = JsonNode.Parse(bytes)!.AsObject();
            foreach (var import in node["packageImports"]!.AsObject())
            {
                var value = import.Value!.AsObject();
                using var entry = JsonDocument.Parse(value.ToJsonString());
                RequireObject(entry.RootElement, $"consumer role migration '{import.Key}'",
                    ["localName", "presence", "requiredContracts", "consumerRoles"], ["localName", "presence", "requiredContracts"], ["consumerRoles"]);
                if (!value.ContainsKey("consumerRoles")) value.Add("consumerRoles", new JsonArray("runtime"));
            }
            var normalized = ParseContractLock(WriteDeterministicDocument(JsonSerializer.SerializeToElement(node)), allowDevelopmentRegistryOrigin);
            return normalized with { ExactBytes = bytes, Sha256 = LowerSha256(bytes) };
        }
    }
}

internal static class SunderRpcConsumerRoleMigration
{
    internal static SunderRpcPackageApiResult Execute(string lockPath, string? packageId, string? reviewedRoles,
        bool allowDevelopmentRegistryOrigin, Action<SunderRpcContractLock, SunderRpcContractLock, string>? validateSource,
        Action<string>? report, CancellationToken token)
    {
        if (reviewedRoles is not null && packageId is null) throw Invalid("invalid", "A reviewed role override requires one package selection.");
        var roles = reviewedRoles is null ? null : SunderRpcAuthoringDocuments.ParseConsumerRolesOption(reviewedRoles);
        var before = SunderRpcAuthoringDocuments.ReadConsumerRoleMigrationInput(lockPath, allowDevelopmentRegistryOrigin);
        if (packageId is not null && !before.PackageImports.ContainsKey(packageId)) throw Invalid("invalid", $"Package selection '{packageId}' does not exist.");
        var node = JsonNode.Parse(before.ExactBytes)!;
        foreach (var import in node["packageImports"]!.AsObject())
        {
            var value = import.Value!.AsObject();
            var previous = value["consumerRoles"]?.ToJsonString() ?? "<missing: runtime>";
            var next = import.Key == packageId && roles is not null ? roles : before.PackageImports[import.Key].ConsumerRoles;
            value["consumerRoles"] = new JsonArray(next.Select(role => (JsonNode?)JsonValue.Create(role)).ToArray());
            report?.Invoke($"{import.Key}: {previous} -> {value["consumerRoles"]!.ToJsonString()}");
        }
        var bytes = SunderRpcAuthoringDocuments.WriteDeterministicDocument(JsonSerializer.SerializeToElement(node));
        var after = SunderRpcAuthoringDocuments.ParseContractLock(bytes, allowDevelopmentRegistryOrigin);
        var inputs = new SunderRpcInputSnapshot(); inputs.Record(lockPath, before.ExactBytes);
        ValidateRetainedEvidence(Path.GetDirectoryName(lockPath)!, before, after, packageId, roles, inputs, token);
        using var lease = GeneratedOutputLock.Acquire([lockPath], cancellationToken: token);
        ValidateRetainedEvidence(Path.GetDirectoryName(lockPath)!, before, after, packageId, roles, inputs, token);
        inputs.Verify(); token.ThrowIfCancellationRequested();
        if (bytes.AsSpan().SequenceEqual(before.ExactBytes)) return new(false, after, false);
        var stage = lockPath + ".stage-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            validateSource?.Invoke(before, after, stage);
            ValidateRetainedEvidence(Path.GetDirectoryName(lockPath)!, before, after, packageId, roles, inputs, token);
            inputs.Verify(); token.ThrowIfCancellationRequested();
            if (!SunderRpcDescriptorFileReader.ReadStableBytes(stage, bytes.Length, "consumer migration proposal").AsSpan().SequenceEqual(bytes))
                throw Invalid("migration-ambiguous", "The staged lock changed; no migration was published.");
            File.Move(stage, lockPath, overwrite: true);
            return new(true, after, false);
        }
        finally
        {
            if (File.Exists(stage) && !GeneratedOutputPathSafety.IsReparsePoint(stage)
                && SunderRpcDescriptorFileReader.ReadStableBytes(stage, bytes.Length, "consumer migration stage").AsSpan().SequenceEqual(bytes)) File.Delete(stage);
        }
    }

    private static void ValidateRetainedEvidence(string root, SunderRpcContractLock before, SunderRpcContractLock after,
        string? reviewedPackage, IReadOnlyList<string>? reviewedRoles, SunderRpcInputSnapshot inputs, CancellationToken token)
    {
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((root, 0)); var entries = 0;
        while (pending.TryPop(out var directory))
        foreach (var path in Directory.EnumerateFileSystemEntries(directory.Path))
        {
            token.ThrowIfCancellationRequested();
            if (++entries > 32768 || directory.Depth > 32) throw Invalid("migration-ambiguous", "Retained role evidence exceeds its bounded discovery limit.");
            var name = Path.GetFileName(path); var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if ((attributes & FileAttributes.ReparsePoint) == 0 && name is not (".git" or "node_modules" or "bin")) pending.Push((path, directory.Depth + 1));
                continue;
            }
            if (name is not (SunderRpcAuthoringDocuments.AuthoringReceiptFileName or SunderRpcAuthoringDocuments.AppAuthoringReceiptFileName)) continue;
            var bytes = inputs.Read(path, 67_108_864, "retained role receipt");
            using var document = SunderRpcAuthoringJson.ParseRegistryMetadata(bytes, 67_108_864);
            var receipt = document.RootElement;
            if (receipt.GetProperty("lockSha256").GetString() != before.Sha256) continue;
            var descriptors = receipt.GetProperty("contracts").EnumerateArray().ToDictionary(value => value.GetProperty("contractKey").GetString()!,
                value => SunderRpcContractDescriptor.Parse(System.Text.Encoding.UTF8.GetBytes(value.GetProperty("canonicalDescriptor").GetString()!)), StringComparer.Ordinal);
            foreach (var group in receipt.GetProperty("packageApiImports").EnumerateArray())
            {
                var id = group.GetProperty("packageId").GetString()!;
                if (!before.PackageImports.TryGetValue(id, out var selection)) continue;
                var allowed = id == reviewedPackage && reviewedRoles is not null ? after.PackageImports[id].ConsumerRoles : selection.ConsumerRoles;
                var actualRoles = group.GetProperty("useRefs").EnumerateArray().Select(value => value.GetProperty("role").GetString()!).Distinct(StringComparer.Ordinal).ToArray();
                if (actualRoles.Any(role => !allowed.Contains(role, StringComparer.Ordinal))) throw Invalid("migration-ambiguous", $"'{id}' retained roles conflict with {string.Join(',', allowed)}; supply explicit reviewed reconciliation.");
                var ids = before.PackageArtifacts[id].Contracts.Keys.ToHashSet(StringComparer.Ordinal);
                foreach (var use in receipt.GetProperty("uses").EnumerateArray())
                {
                    var descriptor = descriptors[use.GetProperty("contractKey").GetString()!];
                    if (!ids.Contains(descriptor.ContractId)) continue;
                    var expected = SunderRpcAuthoringDocuments.ReceiptActions(descriptor);
                    if (!expected.SequenceEqual(use.GetProperty("actions").EnumerateArray().Select(value => value.GetString()), StringComparer.Ordinal))
                        throw Invalid("migration-ambiguous", $"'{id}' has retained narrowed actions that this role-only migration cannot represent; preserve and explicitly reconcile its source policy.");
                }
            }
        }
    }

    private static InvalidDataException Invalid(string suffix, string message) => new("package-api.consumer-roles-" + suffix + ": " + message);
}
