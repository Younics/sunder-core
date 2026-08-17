using System.Security.Cryptography;
using System.Text;
using Sunder.App.Services;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppWebRpcContractCatalogTests
{
    [Fact]
    public void Load_AcceptsIndependentContentDescriptorAndAbiHashes()
    {
        var root = CreateRoot();
        try
        {
            var bytes = V2DescriptorBytes();
            var descriptor = SunderRpcContractDescriptor.Parse(bytes);
            var contentHash = Hash(bytes);
            Assert.NotEqual(contentHash, descriptor.Sha256);
            WriteDescriptor(root, bytes);

            var catalog = AppWebRpcContractCatalog.Load(
                root,
                Manifest(Bundle(descriptor)),
                ContentIndex(bytes, contentHash));

            var method = catalog.GetMethod(
                Provider(descriptor),
                "example",
                "ping",
                SunderRpcMethodKind.Unary);
            Assert.Equal("ping", method.MethodId);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Load_RejectsContentIndexHashMismatchBeforeDescriptorFences()
    {
        var root = CreateRoot();
        try
        {
            var bytes = V2DescriptorBytes();
            var descriptor = SunderRpcContractDescriptor.Parse(bytes);
            WriteDescriptor(root, bytes);

            var exception = Assert.Throws<InvalidDataException>(() => AppWebRpcContractCatalog.Load(
                root,
                Manifest(Bundle(descriptor)),
                ContentIndex(bytes, new string('a', 64))));

            Assert.Contains("content-index", exception.Message, StringComparison.Ordinal);
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
    public void Load_RejectsEachDescriptorV2Fence(string mismatch)
    {
        var root = CreateRoot();
        try
        {
            var bytes = mismatch == "descriptor-version" ? V1DescriptorBytes() : V2DescriptorBytes();
            var descriptor = SunderRpcContractDescriptor.Parse(bytes);
            WriteDescriptor(root, bytes);
            var bundle = Bundle(
                descriptor,
                contractId: mismatch == "contract-id" ? "other.rpc" : descriptor.ContractId,
                contractVersion: mismatch == "contract-version" ? "1.0.0+other" : descriptor.Version,
                descriptorSha256: mismatch == "descriptor-sha256" ? new string('b', 64) : descriptor.Sha256,
                abiSha256: descriptor.AbiSha256 ?? new string('c', 64));

            Assert.Throws<InvalidDataException>(() => AppWebRpcContractCatalog.Load(
                root,
                Manifest(bundle),
                ContentIndex(bytes, Hash(bytes))));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Load_RejectsAbiSha256Mismatch()
    {
        var root = CreateRoot();
        try
        {
            var bytes = V2DescriptorBytes();
            var descriptor = SunderRpcContractDescriptor.Parse(bytes);
            WriteDescriptor(root, bytes);

            var exception = Assert.Throws<InvalidDataException>(() => AppWebRpcContractCatalog.Load(
                root,
                Manifest(Bundle(descriptor, abiSha256: new string('d', 64))),
                ContentIndex(bytes, Hash(bytes))));

            Assert.Contains("abiSha256", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static SunderPackageManifest Manifest(SunderPackageContractBundleManifest bundle)
        => new()
        {
            ArchiveFormatVersion = SunderPackageFormat.CurrentArchiveFormatVersion,
            ManifestVersion = SunderPackageFormat.CurrentManifestVersion,
            Id = "caller.package",
            Name = "Caller Package",
            Version = "1.0.0",
            DependsOn = [],
            Targets = [],
            ContractBundles = [bundle],
            UsesContracts = [],
            Provides = [],
        };

    private static SunderPackageContractBundleManifest Bundle(
        SunderRpcContractDescriptor descriptor,
        string? contractId = null,
        string? contractVersion = null,
        string? descriptorSha256 = null,
        string? abiSha256 = null)
        => new()
        {
            ContractId = contractId ?? descriptor.ContractId,
            Version = contractVersion ?? descriptor.Version,
            DescriptorPath = "contracts/example.rpc.json",
            DescriptorSha256 = descriptorSha256 ?? descriptor.Sha256,
            AbiSha256 = abiSha256 ?? descriptor.AbiSha256,
            Exposure = SunderPackageFormat.PublicContractExposure,
        };

    private static SunderPackageContentIndex ContentIndex(byte[] descriptorBytes, string hash)
        => new(
            SunderPackageFormat.CurrentContentIndexVersion,
            [new SunderPackageContentIndexEntry("contracts/example.rpc.json", hash, descriptorBytes.LongLength)]);

    private static SunderRpcProviderSnapshot Provider(SunderRpcContractDescriptor descriptor)
        => new(
            "provider.package",
            "1.0.0",
            "example.provider",
            descriptor.ContractId,
            descriptor.Version,
            descriptor.Sha256,
            descriptor.AbiSha256,
            Guid.NewGuid(),
            1,
            1,
            new SunderRpcEndpointReference("rpc1_example_provider"),
            1,
            SunderRpcProviderState.Active);

    private static void WriteDescriptor(string root, byte[] bytes)
    {
        var path = Path.Combine(root, "contracts", "example.rpc.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static byte[] V2DescriptorBytes() => Encoding.UTF8.GetBytes("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "descriptorVersion": 2,
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

    private static byte[] V1DescriptorBytes() => Encoding.UTF8.GetBytes("""
        {
          "descriptorVersion": 1,
          "contractId": "example.rpc",
          "version": "1.0.0",
          "services": [
            {
              "serviceId": "example",
              "methods": [
                {
                  "methodId": "ping",
                  "kind": "unary",
                  "requestSchema": { "$ref": "#/$defs/Empty" },
                  "responseSchema": { "$ref": "#/$defs/Empty" }
                }
              ]
            }
          ],
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
        var root = Path.Combine(Path.GetTempPath(), "sunder-app-rpc-v2-tests", Guid.NewGuid().ToString("N"));
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
