using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Sunder.Core.TestSupport;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class OfficialRuntimeMaterializerTests
{
    [Fact]
    public async Task Store_CorruptSameVersionReceiptCannotPermitDifferentVendorBytes()
    {
        using var fixture = await Archive.CreateAsync(ConfiguredOfficialSourceId.DotNet, PackageTargetSelection.GetCurrentRuntimeIdentifier());
        var layout = new ExecutionRuntimeStoreLayout(System.IO.Path.Combine(fixture.Root, "store"));
        var store = new ExecutionRuntimeStore(layout, new EmptyAuthority(), materializer: new(new Probe()));
        var initial = await store.EnsureAsync(new(fixture.Artifact), fixture.Path);
        await File.WriteAllTextAsync(layout.GetReceiptPath(initial.Receipt.Identity), "{");
        var different = fixture.Artifact with { Identity = fixture.Artifact.Identity with { VendorChecksumDigest = new('f', 128) } };
        var downloads = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => store.EnsureArchiveAsync(new(different), _ =>
        {
            downloads++;
            throw new InvalidOperationException("The conflicting acquisition must not open its source.");
        }));
        Assert.Equal(0, downloads);
        Assert.True(Directory.Exists(layout.GetContentRootPath(initial.Receipt.ContentIdentity)));
    }

    [Fact]
    public async Task Store_CacheAndExactRepairPreserveReceiptAcrossUnrelatedMetadataRefresh()
    {
        using var fixture = await Archive.CreateAsync(ConfiguredOfficialSourceId.DotNet, PackageTargetSelection.GetCurrentRuntimeIdentifier());
        var layout = new ExecutionRuntimeStoreLayout(System.IO.Path.Combine(fixture.Root, "store"));
        var store = new ExecutionRuntimeStore(layout, new EmptyAuthority(), materializer: new(new Probe()));
        var initial = await store.EnsureAsync(new(fixture.Artifact), fixture.Path);
        var receiptPath = layout.GetReceiptPath(initial.Receipt.Identity);
        var receiptBytes = await File.ReadAllBytesAsync(receiptPath);
        var refreshed = fixture.Artifact with
        {
            Distribution = fixture.Artifact.Distribution with { MetadataSha256 = new('c', 64) },
            Support = fixture.Artifact.Support with { ObservedAtUtc = fixture.Artifact.Support.ObservedAtUtc.AddMinutes(1) },
        };
        var cached = await store.EnsureAsync(new(refreshed), archivePath: null);
        Assert.True(cached.AlreadyPresent);
        Assert.Equal(receiptBytes, await File.ReadAllBytesAsync(receiptPath));

        var root = layout.GetContentRootPath(initial.Receipt.ContentIdentity);
        var launcher = System.IO.Path.Combine(root, initial.Receipt.LauncherPath);
        ExecutionRuntimeRootFileSystem.MakeOwnedTreeWritable(root);
        await File.WriteAllBytesAsync(launcher, [0]);
        ExecutionRuntimeRootFileSystem.Seal(root, launcher);
        var repaired = await store.EnsureAsync(new(refreshed), fixture.Path);
        Assert.False(repaired.AlreadyPresent);
        Assert.Equal(initial.Receipt.ContentIdentity, repaired.Receipt.ContentIdentity);
        Assert.Equal(receiptBytes, await File.ReadAllBytesAsync(receiptPath));
        Assert.True((await store.VerifyAsync(initial.Receipt.Identity, deep: true)).Healthy);
        ExecutionRuntimeRootFileSystem.MakeOwnedTreeWritable(fixture.Root);
    }

    [Theory]
    [InlineData(ConfiguredOfficialSourceId.DotNet, "win-x64")]
    [InlineData(ConfiguredOfficialSourceId.DotNet, "linux-arm64")]
    [InlineData(ConfiguredOfficialSourceId.Node, "win-arm64")]
    [InlineData(ConfiguredOfficialSourceId.Node, "osx-x64")]
    public async Task OfficialArchives_ValidateVendorAndLocalHashesAndInventoryModes(ConfiguredOfficialSourceId source, string rid)
    {
        using var fixture = await Archive.CreateAsync(source, rid);
        var probe = new Probe();
        var result = await fixture.MaterializeAsync(probe);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(fixture.Path))), result.ArchiveSha256);
        Assert.Equal(new FileInfo(fixture.Path).Length, result.ArchiveSize);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(OfficialRuntimeMaterializer.ComputeContentIdentity(fixture.Artifact.Distribution.MaterializationProfile, result.Content), result.ContentIdentity);
        Assert.All(result.Content, file => Assert.Equal(file.Path == result.LauncherPath
            ? OfficialRuntimeMaterializer.ExecutableMode : OfficialRuntimeMaterializer.ReadOnlyMode, file.UnixMode));
        Assert.Equal(source == ConfiguredOfficialSourceId.DotNet ? 128 : 64, fixture.Artifact.Identity.VendorChecksumDigest.Length);
        if (source == ConfiguredOfficialSourceId.DotNet) Assert.NotEqual(fixture.Artifact.Identity.VendorChecksumDigest, result.ArchiveSha256);
    }

    [Theory]
    [InlineData(ConfiguredOfficialSourceId.DotNet)]
    [InlineData(ConfiguredOfficialSourceId.Node)]
    public async Task ChecksumFailure_PrecedesExtractionAndNativeExecution(ConfiguredOfficialSourceId source)
    {
        using var fixture = await Archive.CreateAsync(source, "linux-x64");
        var probe = new Probe();
        var corrupt = fixture.Artifact with { Identity = fixture.Artifact.Identity with
            { VendorChecksumDigest = new string('f', source == ConfiguredOfficialSourceId.DotNet ? 128 : 64) } };
        var error = await Assert.ThrowsAsync<RuntimeExecutionRuntimeException>(() => fixture.MaterializeAsync(probe, corrupt));
        Assert.Equal(ExecutionRuntimeErrorCodes.IntegrityFailed, error.Code);
        Assert.Equal(0, probe.Calls);
        Assert.Empty(Directory.EnumerateFiles(fixture.OutputRoot, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("host\\escape")]
    [InlineData("dotnet.exe:alternate")]
    [InlineData("host/../escape")]
    [InlineData("host/CON.txt")]
    public async Task Zip_PathTraversalIsRejectedEvenWithCorrectOfficialChecksum(string entry)
    {
        using var fixture = await Archive.CreateAsync(ConfiguredOfficialSourceId.DotNet, "win-x64", entry);
        var probe = new Probe();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.MaterializeAsync(probe));
        Assert.Equal(0, probe.Calls);
        Assert.False(File.Exists(System.IO.Path.Combine(fixture.Root, "escape")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tar_LinksFollowOnlyClosedSkipProfileAndAreNeverMaterialized(bool approvedAuxiliary)
    {
        using var fixture = await Archive.CreateAsync(ConfiguredOfficialSourceId.Node, "linux-x64", link: approvedAuxiliary ? "npm" : "other");
        var probe = new Probe();
        if (approvedAuxiliary)
        {
            var result = await fixture.MaterializeAsync(probe);
            Assert.Equal(2, result.Content.Count);
            Assert.False(File.Exists(System.IO.Path.Combine(fixture.OutputRoot, "bin", "npm")));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.MaterializeAsync(probe));
            Assert.Equal(0, probe.Calls);
        }
    }

    private sealed class Probe : IOfficialRuntimePlatformProbe
    {
        internal int Calls { get; private set; }
        public Task VerifyAsync(RuntimeExecutionRuntimeVendorArtifact artifact, string rootPath, string launcherRelativePath, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; return Task.CompletedTask; }
    }

    private sealed class EmptyAuthority : IExecutionRuntimeGarbageCollectionAuthority
    {
        public Task<T> ExecuteAsync<T>(Func<ExecutionRuntimeGarbageCollectionAuthoritySnapshot, CancellationToken, Task<T>> operation, CancellationToken token)
            => operation(new([]), token);
    }

    private sealed class Archive(TemporaryDirectoryScope temporary, string path, RuntimeExecutionRuntimeVendorArtifact artifact) : IDisposable
    {
        internal string Root => temporary.RootPath;
        internal string OutputRoot => System.IO.Path.Combine(Root, "root");
        internal string Path => path;
        internal RuntimeExecutionRuntimeVendorArtifact Artifact => artifact;
        internal Task<OfficialRuntimeMaterializationResult> MaterializeAsync(Probe probe, RuntimeExecutionRuntimeVendorArtifact? selected = null)
            => new OfficialRuntimeMaterializer(probe).MaterializeAsync(selected ?? artifact, path,
                System.IO.Path.Combine(Root, "verified.archive"), OutputRoot, default);

        internal static async Task<Archive> CreateAsync(ConfiguredOfficialSourceId source, string rid, string? extraPath = null, string? link = null)
        {
            var temporary = TemporaryDirectoryScope.Create("official-materialization");
            try
            {
                var version = source == ConfiguredOfficialSourceId.DotNet ? "10.0.12" : "24.21.0";
                var windows = rid.StartsWith("win-", StringComparison.Ordinal);
                var prefix = source == ConfiguredOfficialSourceId.Node ? $"node-v{version}-{OfficialRuntimeSourcePolicy.NodePlatform(rid)}/" : "";
                var launcher = source == ConfiguredOfficialSourceId.DotNet ? windows ? "dotnet.exe" : "dotnet" : windows ? "node.exe" : "bin/node";
                var entries = new List<string> { prefix + launcher, prefix + (source == ConfiguredOfficialSourceId.DotNet
                    ? $"shared/Microsoft.NETCore.App/{version}/System.Private.CoreLib.dll" : "LICENSE") };
                if (extraPath is not null) entries.Add(extraPath);
                var path = System.IO.Path.Combine(temporary.RootPath, "source.archive");
                await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                {
                    if (windows)
                    {
                        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                        foreach (var name in entries)
                        {
                            await using var output = zip.CreateEntry(name).Open();
                            await output.WriteAsync(new byte[] { 1, 2, 3, 4 });
                        }
                    }
                    else
                    {
                        using var gzip = new GZipStream(stream, CompressionMode.Compress, leaveOpen: true);
                        using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);
                        foreach (var name in entries)
                        {
                            using var bytes = new MemoryStream([1, 2, 3, 4]);
                            await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = bytes });
                        }
                        if (link is not null) await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.SymbolicLink, prefix + "bin/" + link)
                            { LinkName = link == "npm" ? "../lib/node_modules/npm/bin/npm-cli.js" : "../../../escape" });
                    }
                }
                var archiveBytes = await File.ReadAllBytesAsync(path);
                var digest = Convert.ToHexStringLower(source == ConfiguredOfficialSourceId.DotNet ? SHA512.HashData(archiveBytes) : SHA256.HashData(archiveBytes));
                var identity = new RuntimeExecutionRuntimeIdentity(source, source == ConfiguredOfficialSourceId.DotNet ? "sunder.runtime.dotnet" : "sunder.runtime.node",
                    version, rid, source == ConfiguredOfficialSourceId.DotNet ? "sunder.dotnet-worker-module.v1" : "sunder.node-worker-module.v1",
                    source == ConfiguredOfficialSourceId.DotNet ? RuntimeExecutionRuntimeChecksumAlgorithm.Sha512 : RuntimeExecutionRuntimeChecksumAlgorithm.Sha256, digest);
                var url = source == ConfiguredOfficialSourceId.DotNet ? $"https://builds.dotnet.microsoft.com/dotnet/Runtime/{version}/" : $"https://nodejs.org/dist/v{version}/";
                return new(temporary, path, new(identity, new(url + OfficialRuntimeSourcePolicy.ArchiveName(source, version, rid),
                    OfficialRuntimeSourcePolicy.MetadataUri(source).AbsoluteUri, new('a', 64),
                    source == ConfiguredOfficialSourceId.Node ? OfficialRuntimeSourcePolicy.ChecksumUri(version).AbsoluteUri : null,
                    source == ConfiguredOfficialSourceId.Node ? new('b', 64) : null,
                    windows ? RuntimeExecutionRuntimeArchiveFormat.Zip : RuntimeExecutionRuntimeArchiveFormat.TarGzip,
                    OfficialRuntimeSourcePolicy.Profile(source), null),
                    new(RuntimeExecutionRuntimeSupportState.Supported, source == ConfiguredOfficialSourceId.DotNet ? "10.0 LTS" : "24 LTS",
                        DateTimeOffset.UtcNow, null, null)));
            }
            catch { temporary.Dispose(); throw; }
        }
        public void Dispose()
        {
            ExecutionRuntimeRootFileSystem.TryDeleteOwnedTree(temporary.RootPath);
            temporary.Dispose();
        }
    }
}
