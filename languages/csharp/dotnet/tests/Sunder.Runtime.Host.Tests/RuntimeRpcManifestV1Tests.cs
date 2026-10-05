using System.Security.Cryptography;
using System.Text;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeRpcManifestV1Tests
{
    [Fact]
    public async Task DirectLoaders_AcceptIndependentContentDescriptorAndAbiHashes()
    {
        var root = CreateRoot();
        try
        {
            var bytes = DescriptorBytes();
            var descriptor = SunderRpcContractDescriptor.Parse(bytes);
            var rawHash = Hash(bytes);
            Assert.NotEqual(rawHash, descriptor.Sha256);
            var bundle = Bundle(descriptor);
            var (manifest, source) = WritePackageMetadata(root, bytes, bundle, rawHash);

            var prepared = await PackageSessionPreparer.LoadRpcContractsAsync(
                root,
                manifest,
                CancellationToken.None);
            var app = await RuntimeRpcPackageMetadata.LoadContractsAsync(source, CancellationToken.None);

            Assert.Equal(SunderRpcProtocol.DescriptorVersion, Assert.Single(prepared).Value.DescriptorVersion);
            Assert.Equal(descriptor.Sha256, Assert.Single(prepared).Value.Sha256);
            Assert.Equal(descriptor.AbiSha256, Assert.Single(app).Value.AbiSha256);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData("descriptor-version")]
    [InlineData("contract-id")]
    [InlineData("contract-version")]
    [InlineData("descriptor-sha256")]
    [InlineData("abi-sha256")]
    public async Task DirectLoaders_RejectEveryV1DescriptorFence(string mismatch)
    {
        var root = CreateRoot();
        try
        {
            var canonicalBytes = DescriptorBytes();
            var descriptor = SunderRpcContractDescriptor.Parse(canonicalBytes);
            var bytes = mismatch == "descriptor-version"
                ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(canonicalBytes).Replace(
                    "\"descriptorVersion\": 1",
                    "\"descriptorVersion\": 0",
                    StringComparison.Ordinal))
                : canonicalBytes;
            var bundle = Bundle(
                descriptor,
                contractId: mismatch == "contract-id" ? "other.rpc" : descriptor.ContractId,
                version: mismatch == "contract-version" ? "1.0.0+other" : descriptor.Version,
                descriptorSha256: mismatch == "descriptor-sha256" ? new string('a', 64) : descriptor.Sha256,
                abiSha256: mismatch == "abi-sha256"
                    ? new string('b', 64)
                    : descriptor.AbiSha256);
            var (manifest, source) = WritePackageMetadata(root, bytes, bundle, Hash(bytes));

            await Assert.ThrowsAsync<InvalidDataException>(() => PackageSessionPreparer.LoadRpcContractsAsync(
                root,
                manifest,
                CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeRpcPackageMetadata.LoadContractsAsync(
                source,
                CancellationToken.None));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AppLoader_RejectsContentIndexMismatchWithoutChangingDescriptorFences()
    {
        var root = CreateRoot();
        try
        {
            var bytes = DescriptorBytes();
            var descriptor = SunderRpcContractDescriptor.Parse(bytes);
            var bundle = Bundle(descriptor);
            var (manifest, source) = WritePackageMetadata(root, bytes, bundle, new string('d', 64));

            Assert.Single(await PackageSessionPreparer.LoadRpcContractsAsync(
                root,
                manifest,
                CancellationToken.None));
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                RuntimeRpcPackageMetadata.LoadContractsAsync(source, CancellationToken.None));
            Assert.Contains("content-index", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static (SunderPackageManifest Manifest, RuntimePackageSource Source) WritePackageMetadata(
        string root,
        byte[] descriptorBytes,
        SunderPackageContractBundleManifest bundle,
        string indexedSha256)
    {
        var projectionPath = Path.Combine(root, "contracts", "rpc.json");
        Directory.CreateDirectory(Path.GetDirectoryName(projectionPath)!);
        File.WriteAllBytes(projectionPath, descriptorBytes);
        var packagePath = Path.Combine(root, "payload", "shared", "contracts", "rpc.json");
        Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
        File.WriteAllBytes(packagePath, descriptorBytes);
        var manifest = new SunderPackageManifest
        {
            Id = "test.package",
            ContractBundles = [bundle],
        };
        var contentIndex = new SunderPackageContentIndex(
            SunderPackageFormat.CurrentContentIndexVersion,
            [
                new SunderPackageContentIndexEntry(
                    SunderPackageFormat.SharedPayloadRoot + "contracts/rpc.json",
                    indexedSha256,
                    descriptorBytes.LongLength),
            ]);
        return (
            manifest,
            new RuntimePackageSource(
                "test.package",
                PackageSourceKind.Installed,
                root,
                Manifest: manifest,
                ContentIndex: contentIndex));
    }

    private static SunderPackageContractBundleManifest Bundle(
        SunderRpcContractDescriptor descriptor,
        string? contractId = null,
        string? version = null,
        string? descriptorSha256 = null,
        string? abiSha256 = null)
        => new()
        {
            Source = "owned",
            AuthorityKind = "package",
            AuthorityId = "test.package",
            ContractId = contractId ?? descriptor.ContractId,
            Version = version ?? descriptor.Version,
            DescriptorPath = "contracts/rpc.json",
            DescriptorSha256 = descriptorSha256 ?? descriptor.Sha256,
            AbiSha256 = abiSha256 ?? descriptor.AbiSha256,
            Exposure = SunderPackageFormat.PublicContractExposure,
        };

    private static byte[] DescriptorBytes() => Encoding.UTF8.GetBytes("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "descriptorVersion": 1,
          "schemaProfile": "sunder.rpc.values.v1",
          "contractId": "example.rpc",
          "version": "1.0.0",
          "errors": {},
          "services": {
            "example": {
              "methods": {
                "ping": {
                  "kind": "unary",
                  "idempotency": "idempotent",
                  "errors": [],
                  "requestSchema": { "$ref": "#/$defs/Empty" },
                  "responseSchema": { "$ref": "#/$defs/Empty" }
                }
              }
            }
          },
          "$defs": {
            "Empty": {
              "type": "object",
              "properties": {},
              "required": [],
              "additionalProperties": false
            }
          }
        }
        """);

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-runtime-rpc-v1-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
        }
    }
}
