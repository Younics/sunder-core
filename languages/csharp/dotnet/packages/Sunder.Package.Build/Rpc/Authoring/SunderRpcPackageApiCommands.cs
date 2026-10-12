using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Build.Tasks;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Build.Rpc.Authoring;

internal sealed record SunderRpcPackageApiRequest(
    string Operation,
    string? PackageId = null,
    string? ContractId = null,
    string? VersionRange = null,
    bool? IncludePrerelease = null,
    string? RegistryOrigin = null,
    string? Presence = null,
    string? LocalName = null,
    string? ProviderKey = null,
    string? Candidate = null,
    string? PlatformCatalog = null,
    string? ConsumerRoles = null);

internal sealed record SunderRpcPackageApiResult(bool Changed, SunderRpcContractLock ContractLock, bool CandidateAuthorityUnconfirmed);

// Selection is committed only by the final whole-lock rename. Generated source is never an output of a command.
internal sealed class SunderRpcPackageApiCommands(SunderRpcContractRestore restore)
{
    internal async Task<SunderRpcPackageApiResult> ExecuteAsync(
        string lockPath,
        string cacheRoot,
        SunderRpcPackageApiRequest request,
        Action<SunderRpcContractLock, SunderRpcContractLock, string>? validateSource = null,
        CancellationToken token = default,
        Action<string>? report = null)
    {
        ValidateRequest(request);
        lockPath = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(lockPath);
        if (request.Operation == "MigrateConsumerRoles") return SunderRpcConsumerRoleMigration.Execute(lockPath, request.PackageId, request.ConsumerRoles,
            restore.AllowDevelopmentRegistryOrigin, validateSource, report, token);
        var before = ReadBefore();
        if (request.Operation is "List" or "PlatformList") return new(false, before, false);
        var document = JsonNode.Parse(before.ExactBytes)!.AsObject();
        var artifacts = document["packageArtifacts"]!.AsObject();
        var consumers = document["packageImports"]!.AsObject();
        var providers = document["providerImports"]!.AsObject();
        var platforms = document["platformImports"]!.AsObject();
        var packageId = request.PackageId;
        var hydrate = false;

        switch (request.Operation)
        {
            case "PlatformImplement":
            {
                var existing = before.PlatformImports.Values.SingleOrDefault(value => value.ContractId == request.ContractId);
                var key = request.LocalName ?? existing?.Key ?? SunderRpcPlatformCatalogCache.DefaultKey(request.ContractId!);
                if (existing is not null)
                {
                    if (existing.Key != key || request.RegistryOrigin is not null && existing.RegistryOrigin != request.RegistryOrigin)
                        throw Error("platform-api.local-name-collision", "Existing platform intent differs; use the current key and locked origin.");
                    if (request.PlatformCatalog is not null && !JsonNode.DeepEquals(platforms[existing.Key],
                        await restore.SelectPlatformAsync(existing.ContractId, existing.RegistryOrigin, token, request.PlatformCatalog)))
                        throw Error("platform-api.catalog-invalid", "The local catalog differs from the existing selection; use targeted update.");
                    return new(false, before, false);
                }
                if (request.RegistryOrigin is null && request.PlatformCatalog is null)
                    throw Error("platform-api.local-input-required", "Select an explicit local catalog or a canonical Registry origin.");
                if (providers.Concat(platforms).Any(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)))
                    throw Error("platform-api.local-name-collision", "The local provider key is already in use.");
                platforms[key] = await restore.SelectPlatformAsync(request.ContractId!, request.RegistryOrigin, token, request.PlatformCatalog);
                hydrate = true;
                break;
            }
            case "PlatformUpdate":
            {
                var selected = before.PlatformImports.Values.Where(value => request.ContractId is null || value.ContractId == request.ContractId).ToArray();
                if (selected.Length == 0) throw Error("platform-api.import-not-found", "The selected platform intent does not exist.");
                foreach (var entry in selected)
                {
                    if (entry.RegistryOrigin is not null && request.RegistryOrigin is not null)
                        throw Error("package-api.registry-origin-invalid", "Update retains an existing Registry origin and rejects an override.");
                    platforms[entry.Key] = await restore.SelectPlatformAsync(entry.ContractId, entry.RegistryOrigin ?? request.RegistryOrigin, token, request.PlatformCatalog);
                }
                hydrate = true;
                break;
            }
            case "PlatformUnimplement":
            {
                var existing = before.PlatformImports.Values.SingleOrDefault(value => value.ContractId == request.ContractId)
                    ?? throw Error("platform-api.import-not-found", "The selected platform intent does not exist.");
                if (validateSource is null) throw Error("platform-api.remove-still-referenced", "Unimplement requires bounded source validation.");
                platforms.Remove(existing.Key);
                break;
            }
            case "Add":
            case "Implement":
            {
                if (before.PackageArtifacts.TryGetValue(packageId!, out var selected))
                {
                    RequireSameSelection(selected, request);
                    if (request.Candidate is not null)
                    {
                        var candidate = await restore.SelectArtifactAsync(packageId!, selected.RequestedVersionRange, selected.IncludePrerelease,
                            selected.RegistryOrigin, request.Candidate, token);
                        if (!JsonNode.DeepEquals(candidate, artifacts[packageId!]))
                            throw Error("package-api.public-inventory-invalid", "Candidate differs from the existing selection; use targeted update.");
                    }
                }
                else
                {
                    if (request.VersionRange is null || request.RegistryOrigin is null && request.Candidate is null)
                        throw Error("package-api.local-input-required", "A new artifact selection requires a reviewed version range and either a local candidate or Registry origin.");
                    artifacts[packageId!] = await restore.SelectArtifactAsync(packageId!, request.VersionRange, request.IncludePrerelease ?? false,
                        request.RegistryOrigin, request.Candidate, token);
                }
                if (request.Operation == "Add")
                {
                    var name = request.LocalName ?? (before.PackageImports.TryGetValue(packageId!, out var existing) ? existing.LocalName : DefaultName(packageId!));
                    var presence = request.Presence ?? "required";
                    if (consumers[packageId!] is { } consumer)
                    {
                        if (consumer["localName"]!.GetValue<string>() != name || consumer["presence"]!.GetValue<string>() != presence
                            || request.ConsumerRoles is not null && !before.PackageImports[packageId!].ConsumerRoles.SequenceEqual(SunderRpcAuthoringDocuments.ParseConsumerRolesOption(request.ConsumerRoles)))
                            throw Error("package-api.local-name-collision", "An existing consumer intent differs; use targeted update for presence changes.");
                        return new(false, before, request.Candidate is not null);
                    }
                    consumers[packageId!] = new JsonObject { ["localName"] = name, ["presence"] = presence, ["requiredContracts"] = new JsonArray(),
                        ["consumerRoles"] = new JsonArray(SunderRpcAuthoringDocuments.ParseConsumerRolesOption(request.ConsumerRoles ?? "app,runtime").Select(role => (JsonNode?)JsonValue.Create(role)).ToArray()) };
                }
                else
                {
                    RequirePublicContract(artifacts[packageId!]!, request.ContractId!);
                    var existing = before.ProviderImports.Values.SingleOrDefault(value => value.PackageId == packageId && value.ContractId == request.ContractId);
                    var key = request.ProviderKey ?? existing?.Key ?? DefaultName(request.ContractId!) + "Provider";
                    if (existing is not null)
                    {
                        if (existing.Key != key) throw Error("package-api.local-name-collision", "The provider tuple already has a different local key.");
                        return new(false, before, request.Candidate is not null);
                    }
                    if (providers.ContainsKey(key)) throw Error("package-api.local-name-collision", "The requested local provider key is already in use.");
                    providers[key] = new JsonObject { ["packageId"] = packageId, ["contractId"] = request.ContractId };
                }
                hydrate = true;
                break;
            }
            case "Update":
            {
                var selectedIds = packageId is null ? before.PackageArtifacts.Keys.Order(StringComparer.Ordinal).ToArray() : [packageId];
                if (selectedIds.Length == 0 && before.PlatformImports.Count == 0) throw Missing();
                foreach (var id in selectedIds)
                {
                    if (!before.PackageArtifacts.TryGetValue(id, out var previous)) throw Missing();
                    if (previous.RegistryOrigin is not null && request.RegistryOrigin is not null)
                        throw Error("package-api.registry-origin-invalid", "Update retains an existing Registry origin and rejects an override.");
                    if (request.Presence is not null && !consumers.ContainsKey(id)) throw Missing();
                    if (request.ConsumerRoles is not null && !consumers.ContainsKey(id)) throw Missing();
                    var next = await restore.SelectArtifactAsync(id, request.VersionRange ?? previous.RequestedVersionRange,
                        request.IncludePrerelease ?? previous.IncludePrerelease, previous.RegistryOrigin ?? request.RegistryOrigin, request.Candidate, token);
                    if (before.PackageImports.TryGetValue(id, out var consumer))
                        foreach (var required in consumer.RequiredContracts)
                            RequirePreserved(next, required, "package-api.required-contract-removed", "Run api unrequire before removing a required contract.");
                    foreach (var provider in before.ProviderImports.Values.Where(value => value.PackageId == id))
                        RequirePreserved(next, provider.ContractId, "package-api.provider-contract-removed", "Remove the provider source and intent before removing its contract.");
                    artifacts[id] = next;
                    if (request.Presence is not null) consumers[id]!["presence"] = request.Presence;
                    if (request.ConsumerRoles is not null) consumers[id]!["consumerRoles"] = new JsonArray(SunderRpcAuthoringDocuments.ParseConsumerRolesOption(request.ConsumerRoles).Select(role => (JsonNode?)JsonValue.Create(role)).ToArray());
                }
                if (packageId is null)
                    foreach (var entry in before.PlatformImports.Values)
                        platforms[entry.Key] = await restore.SelectPlatformAsync(entry.ContractId, entry.RegistryOrigin, token, request.PlatformCatalog);
                hydrate = true;
                break;
            }
            case "Remove":
                if (consumers[packageId!] is not { } removed) throw Missing();
                if (removed["requiredContracts"]!.AsArray().Count != 0)
                    throw Error("package-api.remove-still-referenced", "Run api unrequire for every required contract before removing the consumer intent.");
                consumers.Remove(packageId!);
                break;
            case "Require":
            case "Unrequire":
            {
                if (consumers[packageId!] is not { } consumer) throw Missing();
                RequirePublicContract(artifacts[packageId!]!, request.ContractId!);
                var required = consumer["requiredContracts"]!.AsArray().Select(value => value!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
                if (request.Operation == "Require") required.Add(request.ContractId!);
                else required.Remove(request.ContractId!);
                consumer["requiredContracts"] = new JsonArray(required.Order(StringComparer.Ordinal).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
                break;
            }
            case "Unimplement":
            {
                var existing = before.ProviderImports.Values.SingleOrDefault(value => value.PackageId == packageId && value.ContractId == request.ContractId);
                if (existing is null) throw Missing();
                if (validateSource is null) throw Error("package-api.remove-still-referenced", "Unimplement requires the bounded language frontend to verify provider bindings.");
                providers.Remove(existing.Key);
                break;
            }
        }

        // Artifact selection is shared; removing a consumer must retain a still-referenced provider artifact.
        var referenced = consumers.Select(pair => pair.Key).Concat(providers.Select(pair => pair.Value!["packageId"]!.GetValue<string>())).ToHashSet(StringComparer.Ordinal);
        foreach (var id in artifacts.Select(pair => pair.Key).Where(id => !referenced.Contains(id)).ToArray()) artifacts.Remove(id);
        foreach (var mapName in new[] { "packageArtifacts", "packageImports", "providerImports", "platformImports" })
            document[mapName] = new JsonObject(document[mapName]!.AsObject().OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value!.DeepClone())));
        var bytes = SunderRpcAuthoringDocuments.WriteDeterministicDocument(JsonSerializer.SerializeToElement(document));
        var after = SunderRpcAuthoringDocuments.ParseContractLock(bytes, restore.AllowDevelopmentRegistryOrigin);
        ValidateReviewedAuthorities(after);
        var changed = !before.ExactBytes.AsSpan().SequenceEqual(bytes);
        if (!changed && !hydrate) return new(false, before, request.Candidate is not null);

        var stage = lockPath + ".stage-" + Guid.NewGuid().ToString("N");
        GeneratedOutputPathSafety.EnsureSafeAncestors(lockPath, includeLeaf: true, "contract lock selection");
        var stageCreated = false;
        try
        {
            using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stageCreated = true; stream.Write(bytes); stream.Flush(flushToDisk: true); }
            if (hydrate)
            {
                await restore.RestoreAsync(stage, cacheRoot, request.Candidate is null ? [] : [request.Candidate], token, request.PlatformCatalog);
                _ = SunderRpcCSharpImportPrepass.Prepare(after, cacheRoot);
            }
            token.ThrowIfCancellationRequested();
            using var lease = GeneratedOutputLock.Acquire([lockPath], TimeSpan.FromSeconds(30), token);
            validateSource?.Invoke(before, after, stage);
            if (request.Candidate is not null) await restore.VerifyCandidateSelectionAsync(request.Candidate, after, token);
            if (!ReadBefore().ExactBytes.AsSpan().SequenceEqual(before.ExactBytes)
                || !SunderRpcAuthoringDocuments.ReadContractLock(stage, restore.AllowDevelopmentRegistryOrigin).ExactBytes.AsSpan().SequenceEqual(bytes))
                throw Error("rpc-authoring.evidence-mismatch", "The contract lock changed during selection; retry against the current lock.");
            GeneratedOutputPathSafety.EnsureSafeAncestors(lockPath, includeLeaf: true, "contract lock selection");
            token.ThrowIfCancellationRequested();
            if (!changed) return new(false, before, request.Candidate is not null);
            File.Move(stage, lockPath, overwrite: true);
            return new(true, after, request.Candidate is not null);
        }
        finally
        {
            if (stageCreated && GeneratedOutputPathSafety.PathExists(stage))
            {
                try
                {
                    GeneratedOutputPathSafety.EnsureRegularFile(stage, "contract lock stage");
                    if (SunderRpcDescriptorFileReader.ReadStableBytes(stage, bytes.Length, "contract lock stage").AsSpan().SequenceEqual(bytes)) File.Delete(stage);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
            }
        }

        SunderRpcContractLock ReadBefore() => request.Operation == "Update"
            ? SunderRpcAuthoringDocuments.ReadContractLockForArtifactUpdate(lockPath, request.PackageId, restore.AllowDevelopmentRegistryOrigin)
            : SunderRpcAuthoringDocuments.ReadContractLock(lockPath, restore.AllowDevelopmentRegistryOrigin);
    }

    private static void ValidateReviewedAuthorities(SunderRpcContractLock value)
    {
        var requirements = SunderRpcContractCache.GetRequirements(value);
        foreach (var artifact in value.PackageArtifacts.Values)
            foreach (var contract in artifact.Contracts.Values.Where(contract => requirements.ContainsKey(contract.DescriptorSha256)))
                if (contract.AuthorityKind == "package" && !value.PackageArtifacts.ContainsKey(contract.AuthorityId))
                    throw Error("package-api.authority-release-conflict", "An ultimate package authority has no explicitly reviewed range selection; select it first.");
    }

    private static void RequireSameSelection(SunderRpcLockedPackageArtifact selected, SunderRpcPackageApiRequest request)
    {
        if (request.VersionRange is not null && selected.RequestedVersionRange != request.VersionRange
            || request.IncludePrerelease is not null && selected.IncludePrerelease != request.IncludePrerelease
            || request.RegistryOrigin is not null && selected.RegistryOrigin != request.RegistryOrigin)
            throw Error("package-api.authority-release-conflict", "The shared artifact already has different reviewed intent; use targeted update.");
    }

    private static void RequirePublicContract(JsonNode artifact, string contractId)
        => RequirePreserved(artifact, contractId, "package-api.provider-contract-not-public", "The contract must be a package-authority member of the selected public inventory.");

    private static void RequirePreserved(JsonNode artifact, string contractId, string code, string message)
    {
        if (artifact["contracts"]![contractId]?["authorityKind"]?.GetValue<string>() != "package") throw Error(code, message);
    }

    private static string DefaultName(string identity) => identity.Split('.')[^1];

    private static void ValidateRequest(SunderRpcPackageApiRequest request)
    {
        if (request.ConsumerRoles is not null)
        {
            _ = SunderRpcAuthoringDocuments.ParseConsumerRolesOption(request.ConsumerRoles);
            if (request.Operation is not ("Add" or "Update" or "MigrateConsumerRoles") || request.PackageId is null)
                throw Error("package-api.consumer-roles-invalid", "A reviewed consumer-role override requires a targeted Add, Update or MigrateSunderPackageApiConsumerRoles operation.");
        }
        if (request.Operation.StartsWith("Platform", StringComparison.Ordinal))
        {
            if (request.Operation is not ("PlatformImplement" or "PlatformUpdate" or "PlatformUnimplement" or "PlatformList")
                || request.PackageId is not null || request.VersionRange is not null || request.IncludePrerelease is not null
                || request.Presence is not null || request.ProviderKey is not null || request.Candidate is not null)
                throw Error("platform-api.import-not-found", "Platform commands do not accept package, range, prerelease, presence or candidate overrides.");
            foreach (var value in new[] { request.ContractId, request.RegistryOrigin, request.LocalName, request.PlatformCatalog })
                if (value is not null && string.IsNullOrWhiteSpace(value)) throw Error("platform-api.import-not-found", "Platform arguments must be nonempty.");
            if (request.Operation == "PlatformUpdate" && request.ContractId is null && request.RegistryOrigin is not null)
                throw Error("package-api.registry-origin-invalid", "Binding a local selection to a Registry requires a specific platform contract.");
            if (request.ContractId is not null && !PackageId.TryParse(request.ContractId, out _)
                || request.Operation is "PlatformImplement" or "PlatformUnimplement" && request.ContractId is null
                || request.Operation == "PlatformList" && request.ContractId is not null
                || request.Operation != "PlatformImplement" && request.LocalName is not null
                || request.Operation is not ("PlatformImplement" or "PlatformUpdate") && (request.RegistryOrigin is not null || request.PlatformCatalog is not null))
                throw Error("platform-api.import-not-found", "Platform command target or options do not apply; update uses locked origins.");
            return;
        }
        if (request.Operation is not ("Add" or "Update" or "Remove" or "Require" or "Unrequire" or "Implement" or "Unimplement" or "List" or "MigrateConsumerRoles"))
            throw Error("package-api.import-not-found", "Unknown package API operation.");
        foreach (var value in new[] { request.PackageId, request.ContractId, request.VersionRange, request.RegistryOrigin, request.Presence, request.LocalName, request.ProviderKey, request.Candidate, request.PlatformCatalog })
            if (value is not null && string.IsNullOrWhiteSpace(value)) throw Error("package-api.import-not-found", "Supplied command values must be nonempty.");
        if (request.Presence is not null and not ("required" or "optional")) throw Error("package-api.import-not-found", "Presence must be exactly required or optional.");
        if (request.PackageId is not null && !PackageId.TryParse(request.PackageId, out _)
            || request.ContractId is not null && !PackageId.TryParse(request.ContractId, out _)) throw Missing();
        if (request.Operation is not ("Update" or "List" or "MigrateConsumerRoles") && request.PackageId is null) throw Missing();
        var needsContract = request.Operation is "Require" or "Unrequire" or "Implement" or "Unimplement";
        if (needsContract != (request.ContractId is not null)) throw Missing();
        if (request.Operation == "List" && request.PackageId is not null) throw Missing();
        if (request.Operation is not ("Add" or "Update" or "Implement")
            && (request.VersionRange is not null || request.IncludePrerelease is not null || request.RegistryOrigin is not null || request.Candidate is not null || request.PlatformCatalog is not null))
            throw Error("package-api.import-not-found", "This offline operation does not accept selection inputs.");
        if (request.Operation is not ("Add" or "Update") && request.Presence is not null
            || request.Operation != "Add" && request.LocalName is not null || request.Operation != "Implement" && request.ProviderKey is not null)
            throw Error("package-api.import-not-found", "The supplied intent option does not apply to this operation.");
        if (request.Operation == "Update" && request.PackageId is null && request.RegistryOrigin is not null)
            throw Error("package-api.registry-origin-invalid", "Binding a local selection to a Registry requires a specific package.");
        if (request.Operation == "Update" && request.PackageId is null
            && (request.VersionRange is not null || request.IncludePrerelease is not null || request.Presence is not null || request.Candidate is not null))
            throw Error("package-api.import-not-found", "Global update rejects range, prerelease, presence and candidate overrides.");
    }

    private static InvalidDataException Missing() => Error("package-api.import-not-found", "The exact requested import or command target does not exist.");
    private static InvalidDataException Error(string code, string message) => new(code + ": " + message);
}
