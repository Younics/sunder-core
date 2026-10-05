using System.Diagnostics;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Tasks;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcImportedBuildTests
{
    [ExternalArtifactFact("SUNDER_REQUIRE_NODE_OUTPUT_LOCK_INTEROP")]
    [Trait("Category", "ExternalArtifact")]
    public async Task CacheStore_CSharpAndNodeShareReaderLeasesAndRecoverEachOthersInterruptedPublications()
    {
        var helper = Path.Combine(RepositoryRoot(), "languages/typescript/node/packages/package-tool/dist-test/test/contract-cache-child.js");
        Assert.True(File.Exists(helper), "Compile the package-tool test helpers before selecting the Node interop lane.");
        var contractLock = SunderRpcAuthoringDocuments.ReadContractLock(LockPath);
        var required = SunderRpcContractCache.GetRequirements(contractLock);
        var entries = new Dictionary<string, byte[]> { [_descriptor.Sha256] = _descriptor.GetCanonicalUtf8() };
        var descriptor = SunderRpcContractCache.GetDescriptorPath(_cache, _descriptor.Sha256);
        var receipt = SunderRpcContractCache.GetReceiptPath(_cache, _descriptor.Sha256);
        File.WriteAllText(descriptor, "preserved Node crash evidence");
        Assert.Equal(86, await Node("crash"));
        Assert.Throws<InvalidDataException>(() => SunderRpcContractCache.Resolve(contractLock, _cache));
        SunderRpcContractCacheStore.Publish(_cache, entries, required);
        Assert.Single(SunderRpcContractCache.Resolve(contractLock, _cache).EntriesByDescriptorSha256);

        File.Delete(receipt);
        Assert.Throws<IOException>(() => SunderRpcContractCacheStore.Publish(_cache, entries, required,
            checkpoint: point => { if (point == "prepared") throw new IOException("C# interrupted."); }));
        Assert.Equal(0, await Node("publish"));
        Assert.Single(SunderRpcContractCache.Resolve(contractLock, _cache).EntriesByDescriptorSha256);
        using (var held = GeneratedOutputLock.Acquire([_cache]))
        using (var reader = Start("read"))
        {
            try
            {
                Assert.Equal("reading", await reader.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
                await Task.Delay(200);
                Assert.False(reader.HasExited);
                held.Dispose();
                await reader.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(reader.ExitCode == 0, await reader.StandardError.ReadToEndAsync());
            }
            finally { if (!reader.HasExited) reader.Kill(entireProcessTree: true); }
        }
        var before = Snapshot(_cache);
        Assert.Equal(0, await Node("publish"));
        Assert.Equal(before, Snapshot(_cache));

        Process Start(string mode)
        {
            var start = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { helper, mode, LockPath, _cache, Fixture("conformance/valid/minimal.rpc.json") }) start.ArgumentList.Add(argument);
            return Process.Start(start)!;
        }

        async Task<int> Node(string mode)
        {
            using var process = Start(mode);
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(process.ExitCode is 0 or 86, await stdout + await stderr);
                return process.ExitCode;
            }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("quarantined-descriptor")]
    [InlineData("published-descriptor")]
    [InlineData("published-receipt")]
    [InlineData("installed")]
    [InlineData("committed")]
    public void CacheStore_InterruptedRepairResumesAndPreservesCorruptEvidence(string point)
    {
        var contractLock = SunderRpcAuthoringDocuments.ReadContractLock(LockPath);
        var required = SunderRpcContractCache.GetRequirements(contractLock);
        var descriptorPath = SunderRpcContractCache.GetDescriptorPath(_cache, _descriptor.Sha256);
        var receiptPath = SunderRpcContractCache.GetReceiptPath(_cache, _descriptor.Sha256);
        File.WriteAllText(descriptorPath, "corrupt descriptor");
        File.WriteAllText(receiptPath, "corrupt receipt");
        var descriptorBefore = File.ReadAllBytes(descriptorPath);
        var receiptBefore = File.ReadAllBytes(receiptPath);
        var expected = point switch
        {
            "quarantined-descriptor" => "quarantined:" + _descriptor.Sha256 + ".json",
            "published-descriptor" => "published:" + _descriptor.Sha256 + ".json",
            "published-receipt" => "published:" + _descriptor.Sha256 + SunderRpcContractCache.ReceiptSuffix,
            _ => point,
        };
        var entries = new Dictionary<string, byte[]> { [_descriptor.Sha256] = _descriptor.GetCanonicalUtf8() };
        Assert.Throws<IOException>(() => SunderRpcContractCacheStore.Publish(_cache, entries, required,
            checkpoint: checkpoint => { if (checkpoint == expected) throw new IOException("Injected process interruption."); }));
        if (point != "committed")
        {
            var interrupted = Snapshot(_cache);
            var error = Assert.Throws<InvalidDataException>(() => SunderRpcContractCache.Resolve(contractLock, _cache));
            Assert.Contains("incomplete", error.Message, StringComparison.Ordinal);
            Assert.Equal(interrupted, Snapshot(_cache));
        }
        SunderRpcContractCacheStore.Publish(_cache, entries, required);
        Assert.Single(SunderRpcContractCache.Resolve(contractLock, _cache).EntriesByDescriptorSha256);
        Assert.False(Directory.Exists(Path.Combine(_cache, SunderRpcContractCacheStore.Pending)));
        var quarantine = Assert.Single(Directory.GetDirectories(Path.Combine(_cache, ".quarantine")));
        Assert.Equal(descriptorBefore, File.ReadAllBytes(Path.Combine(quarantine, Path.GetFileName(descriptorPath))));
        Assert.Equal(receiptBefore, File.ReadAllBytes(Path.Combine(quarantine, Path.GetFileName(receiptPath))));
        var snapshot = Snapshot(_cache);
        SunderRpcContractCacheStore.Publish(_cache, entries, required);
        Assert.Equal(snapshot, Snapshot(_cache));
    }

    [Fact]
    public async Task CacheStore_ReadersWaitForTheCompletePublicationAndUseDetachedBytes()
    {
        var contractLock = SunderRpcAuthoringDocuments.ReadContractLock(LockPath);
        var required = SunderRpcContractCache.GetRequirements(contractLock);
        File.Delete(SunderRpcContractCache.GetReceiptPath(_cache, _descriptor.Sha256));
        using var prepared = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writer = Task.Run(() => SunderRpcContractCacheStore.Publish(_cache,
            new Dictionary<string, byte[]> { [_descriptor.Sha256] = _descriptor.GetCanonicalUtf8() }, required,
            checkpoint: point => { if (point == "prepared") { prepared.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); } }));
        Assert.True(prepared.Wait(TimeSpan.FromSeconds(10)));
        var reader = Task.Run(() => SunderRpcContractCache.Resolve(contractLock, _cache));
        try
        {
            Assert.NotSame(reader, await Task.WhenAny(reader, Task.Delay(200)));
        }
        finally { release.Set(); }
        await writer.WaitAsync(TimeSpan.FromSeconds(10));
        var resolved = await reader.WaitAsync(TimeSpan.FromSeconds(10));
        var entry = Assert.Single(resolved.EntriesByDescriptorSha256).Value;
        File.Delete(SunderRpcContractCache.GetDescriptorPath(_cache, _descriptor.Sha256));
        Assert.Equal(_descriptor.GetCanonicalUtf8(), entry.CanonicalUtf8);
    }

    [Fact]
    public void CacheStore_InvalidBatchNeverPublishesValidEarlierEntries()
    {
        var root = Path.Combine(_root, "invalid-cache");
        var contractLock = SunderRpcAuthoringDocuments.ReadContractLock(LockPath);
        var required = SunderRpcContractCache.GetRequirements(contractLock).ToDictionary();
        var second = _descriptor.GetCanonicalUtf8().Concat(new byte[] { 10 }).ToArray();
        Assert.Throws<InvalidDataException>(() => SunderRpcContractCacheStore.Publish(root,
            new Dictionary<string, byte[]> { [_descriptor.Sha256] = second }, required));
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("marker")]
    [InlineData("descriptor-link")]
    [InlineData("pending-extra")]
    [InlineData("pending-changed")]
    public void CacheStore_UnprovenPathsOrPendingBytesCannotBeRepaired(string failure)
    {
        var contractLock = SunderRpcAuthoringDocuments.ReadContractLock(LockPath);
        var required = SunderRpcContractCache.GetRequirements(contractLock);
        var entries = new Dictionary<string, byte[]> { [_descriptor.Sha256] = _descriptor.GetCanonicalUtf8() };
        var path = SunderRpcContractCache.GetDescriptorPath(_cache, _descriptor.Sha256);
        if (failure == "marker") File.Delete(Path.Combine(_cache, SunderRpcContractCacheStore.Marker));
        else if (failure == "descriptor-link")
        {
            var external = Path.Combine(_root, "external.json");
            File.Move(path, external);
            File.CreateSymbolicLink(path, external);
        }
        else
        {
            File.Delete(path);
            Assert.Throws<IOException>(() => SunderRpcContractCacheStore.Publish(_cache, entries, required,
                checkpoint: point => { if (point == "prepared") throw new IOException("Interrupted."); }));
            if (failure == "pending-extra") File.WriteAllText(Path.Combine(_cache, ".pending", "unowned.txt"), "preserve");
            else File.AppendAllText(SunderRpcContractCache.GetDescriptorPath(Path.Combine(_cache, ".pending"), _descriptor.Sha256), "\n");
        }
        var before = Snapshot(_cache);
        var error = Record.Exception(() => SunderRpcContractCacheStore.Publish(_cache, entries, required));
        Assert.True(error is IOException or InvalidDataException or SunderRpcDescriptorException, error?.ToString());
        Assert.Equal(before, Snapshot(_cache));
    }
}
