using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcImportedBuildTests
{
    [Fact]
    public async Task MethodPolicies_CandidateCacheAndReceiptUseSidecarPinsWithoutDescriptorChanges()
    {
        using var policyJson = JsonDocument.Parse("{\"example\":{\"ping\":{\"timeoutSeconds\":120}}}");
        var policies = SunderRpcMethodPolicies.Parse(policyJson.RootElement);
        var candidate = Path.Combine(_root, "timed-candidate.sunderpkg");
        File.WriteAllBytes(candidate, CanonicalCandidateBytes("1.2.3", methodPolicies: policies));
        using var http = new HttpClient(new RestoreHandler(_ => throw new InvalidOperationException("Local metadata selection attempted HTTP.")));
        var result = await new SunderRpcPackageApiCommands(new(http)).ExecuteAsync(LockPath, _cache, new("Update", DependencyId, Candidate: candidate));
        var cached = SunderRpcContractCache.Resolve(result.ContractLock, _cache);
        Assert.Equal(_descriptor.Sha256, Assert.Single(cached.EntriesByDescriptorSha256).Value.Descriptor.Sha256);
        Assert.True(SunderRpcAuthoringDocuments.SameMethodPolicies(policies, cached.MethodPoliciesByContractId[ContractId]));
        var generated = Generate();
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(Assert.Single(generated.AuthoringReceipts).ItemSpec));
        var contract = Assert.Single(receipt.RootElement.GetProperty("contracts").EnumerateArray());
        Assert.Equal(120, contract.GetProperty("methodPolicies").GetProperty("example").GetProperty("ping").GetProperty("timeoutSeconds").GetInt32());
        Assert.Equal(_descriptor.AbiSha256, contract.GetProperty("abiSha256").GetString());
        Assert.Equal(result.ContractLock.PackageArtifacts[DependencyId].ModelProjectionSha256,
            Assert.Single(receipt.RootElement.GetProperty("packageArtifacts").EnumerateArray()).GetProperty("modelProjectionSha256").GetString());
    }

    [Fact]
    public async Task ModelProjectionPins_RequireExplicitReviewedReselectionOfPriorLock()
    {
        var archive = await CreateLockedCandidateAsync();
        var before = ReadJson(LockPath);
        var artifact = before["packageArtifacts"]![DependencyId]!.AsObject();
        artifact.Remove("modelProjectionBytes"); artifact.Remove("modelProjectionSha256"); artifact.Remove("registryOrigin");
        WriteJson(LockPath, before);
        var prior = File.ReadAllBytes(LockPath);
        Assert.Contains("reselection-required", Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ReadContractLock(LockPath)).Message);
        using var http = new HttpClient(new RestoreHandler(_ => throw new InvalidOperationException("Local reselection cannot use HTTP.")));
        var restore = new SunderRpcContractRestore(http);
        var commands = new SunderRpcPackageApiCommands(restore);
        var cache = Path.Combine(_root, "projection-reselection-cache");
        await Assert.ThrowsAsync<InvalidDataException>(() => restore.RestoreAsync(LockPath, cache, [archive]));
        await Assert.ThrowsAsync<InvalidDataException>(() => commands.ExecuteAsync(LockPath, cache, new("Add", DependencyId, Candidate: archive)));
        Assert.False(Directory.Exists(cache)); Assert.Equal(prior, File.ReadAllBytes(LockPath));
        var validatedCurrentInputs = false;
        var request = new SunderRpcPackageApiRequest("Update", DependencyId, Candidate: archive);
        var result = await commands.ExecuteAsync(LockPath, cache, request, (old, current, stage) =>
        {
            Assert.Equal(0, old.PackageArtifacts[DependencyId].ModelProjectionBytes);
            Assert.True(current.PackageArtifacts[DependencyId].ModelProjectionBytes > 0);
            CreatePackageApiTask("Update").ValidateSource(old, stage, cache, request);
            validatedCurrentInputs = true;
        });
        Assert.True(validatedCurrentInputs); Assert.True(result.Changed);
        var selected = SunderRpcAuthoringDocuments.ReadContractLock(LockPath).PackageArtifacts[DependencyId];
        Assert.Null(selected.RegistryOrigin);
        var resolved = SunderRpcContractCache.Resolve(result.ContractLock, cache);
        Assert.Single(resolved.ModelProjectionsByContractId);
        Assert.Equal(selected.ModelProjectionSha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(
            SunderRpcContractCache.GetModelProjectionPath(cache, selected.ModelProjectionSha256)))));
    }

    [Theory]
    [InlineData("missing-body")]
    [InlineData("missing-receipt")]
    [InlineData("body")]
    [InlineData("receipt")]
    [InlineData("pin-bytes")]
    [InlineData("pin-hash")]
    public async Task ModelProjectionCache_RejectsMissingTamperedOrUnselectedEvidence(string change)
    {
        var archive = await CreateLockedCandidateAsync();
        using var http = new HttpClient(new RestoreHandler(_ => throw new InvalidOperationException("Offline restore attempted HTTP.")));
        var cache = Path.Combine(_root, "projection-tamper-cache");
        await new SunderRpcContractRestore(http).RestoreAsync(LockPath, cache, [archive]);
        var contractLock = SunderRpcAuthoringDocuments.ReadContractLock(LockPath);
        var artifact = contractLock.PackageArtifacts[DependencyId];
        var path = SunderRpcContractCache.GetModelProjectionPath(cache, artifact.ModelProjectionSha256);
        var receipt = Path.Combine(Path.GetDirectoryName(path)!, artifact.ModelProjectionSha256 + SunderRpcContractCache.ReceiptSuffix);
        if (change == "missing-body") File.Delete(path);
        else if (change == "missing-receipt") File.Delete(receipt);
        else if (change == "body") File.AppendAllText(path, "\n");
        else if (change == "receipt") File.AppendAllText(receipt, "\n");
        else
        {
            var document = ReadJson(LockPath);
            if (change == "pin-bytes") document["packageArtifacts"]![DependencyId]!["modelProjectionBytes"] = artifact.ModelProjectionBytes + 1;
            else document["packageArtifacts"]![DependencyId]!["modelProjectionSha256"] = new string('a', 64);
            WriteJson(LockPath, document); contractLock = SunderRpcAuthoringDocuments.ReadContractLock(LockPath);
        }
        Assert.Throws<InvalidDataException>(() => SunderRpcContractCache.Resolve(contractLock, cache));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    public async Task CandidateInspection_EmitsExactPublicSidecarAndPinsBesideDescriptors(int contractCount)
    {
        var archive = Path.Combine(_root, "inspection-candidate.sunderpkg");
        File.WriteAllBytes(archive, CanonicalCandidateBytes("1.2.3", contractCount: contractCount));
        var output = Path.Combine(_root, "projection-inspection");
        Assert.True((await SunderPackagePublicApiInspection.ExportArchiveAsync(archive, output)).Success);
        var names = Directory.GetFiles(output).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "inspection.json")));
        Assert.Equal(contractCount + 2, names.Length);
        Assert.Equal(receipt.RootElement.GetProperty("inventory").GetProperty("contracts").EnumerateArray()
            .Select(contract => contract.GetProperty("descriptorSha256").GetString() + ".json")
            .Append("inspection.json").Append("rpc-model-projection.v1.json").Order(StringComparer.Ordinal), names);
        var bytes = File.ReadAllBytes(Path.Combine(output, "rpc-model-projection.v1.json"));
        Assert.Equal(bytes.Length, receipt.RootElement.GetProperty("modelProjectionBytes").GetInt32());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), receipt.RootElement.GetProperty("modelProjectionSha256").GetString());
        Assert.Equal(contractCount, SunderRpcModelProjectionDocument.Parse(bytes).Contracts.Count);
    }

    [Fact]
    public async Task ModelProjectionGc_UsesArtifactPinsAndRetainsIncompleteOrUnprovenPairs()
    {
        var archive = await CreateLockedCandidateAsync();
        using var http = new HttpClient(new RestoreHandler(_ => throw new InvalidOperationException("Offline restore attempted HTTP.")));
        var cache = Path.Combine(_root, "projection-gc-cache");
        await new SunderRpcContractRestore(http).RestoreAsync(LockPath, cache, [archive]);
        var selected = SunderRpcAuthoringDocuments.ReadContractLock(LockPath).PackageArtifacts[DependencyId];
        var pinned = SunderRpcContractCache.GetModelProjectionPath(cache, selected.ModelProjectionSha256);
        var unused = new SunderRpcModelProjectionDocument(1, "example.unused", "1.0.0", []);
        var path = SunderRpcContractCache.GetModelProjectionPath(cache, unused.ModelProjectionSha256);
        var receipt = Path.Combine(Path.GetDirectoryName(path)!, unused.ModelProjectionSha256 + SunderRpcContractCache.ReceiptSuffix);
        File.WriteAllBytes(path, unused.ToCanonicalUtf8());
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
        Assert.Equal(1, SunderRpcContractCacheStore.Collect(cache, [LockPath]).RetainedUnprovenArtifacts);
        File.WriteAllBytes(receipt, SunderRpcContractCache.ModelProjectionReceiptBytes(unused.ModelProjectionSha256, unused.ModelProjectionBytes));
        File.SetLastWriteTimeUtc(receipt, DateTime.UtcNow.AddDays(-10));
        Assert.Equal(1, SunderRpcContractCacheStore.Collect(cache, [LockPath]).RemovedEntries);
        Assert.False(File.Exists(path)); Assert.False(File.Exists(receipt)); Assert.True(File.Exists(pinned));
    }
}
