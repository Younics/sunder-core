using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed partial class SunderPackageArchiveInspectorTests
{
    [Fact]
    public async Task PublicApiInspection_ExportsCanonicalPublicInventoryAndRawContentIdentityWithoutPrivateBodies()
    {
        var root = CreateTempDirectory();
        try
        {
            var visible = CreateContractDescriptor("example.public", "2.0.0"); var hidden = CreateContractDescriptor("example.private", "1.0.0");
            var manifest = CreateManifest([], [CreateContractBundle(visible, "contracts/public.json"), CreateContractBundle(hidden, "contracts/private.json", "package")]);
            var archive = CreatePackageArchive(root, manifest, new Dictionary<string, byte[]>
            { ["payload/shared/contracts/public.json"] = visible, ["payload/shared/contracts/private.json"] = hidden });
            var output = Path.Combine(root, "api");
            var before = File.ReadAllBytes(archive);
            var result = await SunderPackagePublicApiInspection.ExportArchiveAsync(archive, output);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Equal(before, File.ReadAllBytes(archive));
            var expected = SunderRpcContractDescriptor.Parse(visible);
            Assert.Equal(expected.GetCanonicalUtf8(), File.ReadAllBytes(Path.Combine(output, expected.Sha256 + ".json")));
            Assert.False(File.Exists(Path.Combine(output, DescriptorHash(hidden) + ".json")));
            Assert.Equal(2, Directory.GetFiles(output).Length);
            using var inspection = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, SunderPackagePublicApiInspection.ReceiptFileName)));
            var receipt = inspection.RootElement;
            Assert.Equal(Sha256(before), receipt.GetProperty("archiveSha256").GetString());
            var inventory = SunderPackagePublicApiInventory.ParseVerifyHash(System.Text.Encoding.UTF8.GetBytes(receipt.GetProperty("inventory").GetRawText()), receipt.GetProperty("publicInventorySha256").GetString()!);
            var contentIdentity = Convert.ToHexStringLower(SHA256.HashData([
                .. Convert.FromHexString(receipt.GetProperty("manifestSha256").GetString()!), .. Convert.FromHexString(receipt.GetProperty("contentIndexSha256").GetString()!),
            ]));
            Assert.Equal(contentIdentity, inventory.Payload.GetProperty("packageContentIdentity").GetString());
            var contract = Assert.Single(inventory.Payload.GetProperty("contracts").EnumerateArray());
            Assert.Equal("example.public", contract.GetProperty("contractId").GetString());
            Assert.Equal(contentIdentity, contract.GetProperty("selectedAuthorityContentIdentity").GetString());
            Assert.Equal("test.package", contract.GetProperty("authorityId").GetString());
            var snapshot = Directory.GetFiles(output).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
            await Assert.ThrowsAsync<IOException>(() => SunderPackagePublicApiInspection.ExportArchiveAsync(archive, output));
            foreach (var (name, bytes) in snapshot) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(output, name!)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("unindexed")]
    [InlineData("missing")]
    [InlineData("traversal")]
    [InlineData("linked-output")]
    [InlineData("linked-input")]
    [InlineData("oversize")]
    public async Task PublicApiInspection_RejectsInvalidArchivesAndPathsBeforeExport(string failure)
    {
        var root = CreateTempDirectory();
        try
        {
            var descriptor = CreateContractDescriptor("example.public", "2.0.0");
            var archive = CreatePackageArchive(root, CreateManifest([], [CreateContractBundle(descriptor, "contracts/public.json")]),
                new Dictionary<string, byte[]> { ["payload/shared/contracts/public.json"] = descriptor });
            var output = Path.Combine(root, "api");
            if (failure == "oversize")
            {
                using var oversized = File.OpenWrite(archive); oversized.SetLength(SunderPackageFormat.MaxArchiveBytes + 1);
            }
            else if (failure is "unindexed" or "missing" or "traversal")
            {
                using var zip = ZipFile.Open(archive, ZipArchiveMode.Update);
                if (failure == "missing") zip.GetEntry("payload/shared/contracts/public.json")!.Delete();
                else { using var writer = new StreamWriter(zip.CreateEntry(failure == "traversal" ? "../escape.txt" : "payload/shared/unindexed.txt").Open()); writer.Write("invalid"); }
            }
            else if (failure == "linked-input")
            {
                var linked = Path.Combine(root, "linked.sunderpkg"); File.CreateSymbolicLink(linked, archive); archive = linked;
            }
            else
            {
                var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
                var linked = Path.Combine(root, "linked"); Directory.CreateSymbolicLink(linked, outside); output = Path.Combine(linked, "api");
            }
            try { Assert.False((await SunderPackagePublicApiInspection.ExportArchiveAsync(archive, output)).Success); }
            catch (Exception error) when (error is IOException or InvalidDataException) { }
            Assert.False(Directory.Exists(output));
            Assert.False(File.Exists(Path.Combine(root, "escape.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
