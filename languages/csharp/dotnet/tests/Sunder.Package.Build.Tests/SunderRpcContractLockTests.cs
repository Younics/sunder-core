using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed class SunderRpcContractLockTests
{
    [Theory]
    [InlineData("minimal-contract-lock.json", "48300c172fe8e323956ee95aa7cde02e30ca9ebd330179e328e3209fe31ead5a", 0, 0, 0, 0)]
    [InlineData("package-api-contract-lock.json", "0cf9e01de041b7430792a2813793a64a223b780800fc90bdbce0b39014784d6f", 1, 1, 0, 1)]
    [InlineData("package-api-mixed-authority-contract-lock.json", "0e6ae942272b88e8ffb91cc34aa3790652d65357b7f2dc3051ea14a8d15bfa47", 1, 1, 0, 1)]
    [InlineData("mixed-contract-lock.json", "4d2c20ba9591c04449ee4fe1e48fbbd1664642807e039a6984245317a9735c1d", 2, 1, 1, 1)]
    public void ReadContractLock_AcceptsConformingLocks(
        string fileName,
        string expectedSha256,
        int expectedBundles,
        int expectedUses,
        int expectedProviders,
        int expectedDependencies)
    {
        var contractLock = SunderRpcAuthoringDocuments.ReadContractLock(Fixture("valid", fileName));

        Assert.Equal(expectedSha256, contractLock.Sha256);
        Assert.Equal(expectedBundles, contractLock.Materialization.ContractBundles);
        Assert.Equal(expectedUses, contractLock.Materialization.RoleScopedUses);
        Assert.Equal(expectedProviders, contractLock.Materialization.Providers);
        Assert.Equal(expectedDependencies, contractLock.Materialization.PackageDependencies);
    }

    [Theory]
    [InlineData("contract-lock-missing-required.json")]
    [InlineData("contract-lock-missing-package-presence.json")]
    [InlineData("contract-lock-invalid-key-grammar.json")]
    public void ReadContractLock_RejectsSchemaInvalidLocks(string fileName)
        => Assert.Throws<InvalidDataException>(() =>
            SunderRpcAuthoringDocuments.ReadContractLock(Fixture("invalid", fileName)));

    [Theory]
    [InlineData("lock-count-mismatch.json", "rpc-authoring.count-mismatch")]
    [InlineData("lock-package-api-no-package-authority.json", "package-api.consumer-api-empty")]
    [InlineData("lock-unreferenced-artifact.json", "rpc-authoring.unreferenced-artifact")]
    public void ReadContractLock_RejectsSemanticInvalidLocks(string fileName, string diagnosticId)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            SunderRpcAuthoringDocuments.ReadContractLock(Fixture("semantic-invalid", fileName)));

        Assert.StartsWith(diagnosticId + ":", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ReadContractLock_CoalescesOwnerAndReexportEvidence(
        bool importReexportApi,
        bool importReexportProvider)
    {
        var document = CreateOwnerReexportLock(importReexportApi, importReexportProvider);
        var path = WriteMutation(SerializeLock(document), out var directory);
        try
        {
            var contractLock = SunderRpcAuthoringDocuments.ReadContractLock(path);

            Assert.Equal(1, contractLock.Materialization.ContractBundles);
            Assert.Equal(1, contractLock.Materialization.RoleScopedUses);
            Assert.Equal(importReexportProvider ? 1 : 0, contractLock.Materialization.Providers);
            Assert.Equal(2, contractLock.Materialization.PackageDependencies);
            Assert.Equal(2, contractLock.PackageArtifacts.Count);
            var owner = contractLock.PackageArtifacts["example.dependency"];
            var reexport = contractLock.PackageArtifacts["example.reexport"];
            var ownedContract = Assert.Single(owner.Contracts).Value;
            var importedContract = Assert.Single(reexport.Contracts).Value;
            Assert.Equal("owned", ownedContract.ArtifactSource);
            Assert.Equal("imported", importedContract.ArtifactSource);
            Assert.Equal(ownedContract with { ArtifactSource = "imported" }, importedContract);
            Assert.Equal(
                ComputeInventorySha256(owner.PackageId, document["packageArtifacts"]![owner.PackageId]!),
                owner.PublicInventorySha256);
            Assert.Equal(
                ComputeInventorySha256(reexport.PackageId, document["packageArtifacts"]![reexport.PackageId]!),
                reexport.PublicInventorySha256);
            Assert.NotEqual(owner.PublicInventorySha256, reexport.PublicInventorySha256);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("descriptorBytes", 488, "rpc-authoring.evidence-mismatch")]
    [InlineData("descriptorSha256", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "rpc-authoring.evidence-mismatch")]
    [InlineData("abiSha256", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "rpc-authoring.evidence-mismatch")]
    [InlineData("authorityId", "example.other", "rpc-authoring.evidence-mismatch")]
    [InlineData("selectedAuthorityRelease", "1.2.4", "rpc-authoring.authority-mismatch")]
    [InlineData("selectedAuthorityContentIdentity", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "rpc-authoring.authority-mismatch")]
    public void ReadContractLock_RejectsConflictingReexportEvidenceWithRecomputedInventory(
        string member,
        object value,
        string diagnosticId)
    {
        var document = CreateOwnerReexportLock();
        var artifact = document["packageArtifacts"]!["example.reexport"]!;
        var contract = artifact["contracts"]!["example.rpc.v1.minimal"]!;
        contract[member] = JsonSerializer.SerializeToNode(value);
        if (member == "descriptorSha256") contract["etag"] = $"\"{value}\"";
        artifact["publicInventorySha256"] = ComputeInventorySha256("example.reexport", artifact);

        AssertRejectedMutation(SerializeLock(document), diagnosticId);
    }

    [Theory]
    [InlineData("authorityId", "example.other")]
    [InlineData("selectedAuthorityRelease", "1.2.4")]
    [InlineData("selectedAuthorityContentIdentity", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void ReadContractLock_RejectsOwnedAuthorityMismatchWithRecomputedInventory(string member, string value)
    {
        var document = JsonNode.Parse(File.ReadAllText(Fixture("valid", "package-api-contract-lock.json")))!.AsObject();
        var artifact = document["packageArtifacts"]!["example.dependency"]!;
        artifact["contracts"]!["example.rpc.v1.minimal"]![member] = value;
        artifact["publicInventorySha256"] = ComputeInventorySha256("example.dependency", artifact);

        AssertRejectedMutation(SerializeLock(document), "rpc-authoring.authority-mismatch");
    }

    [Fact]
    public void ReadContractLock_AuthenticatesArtifactSourceAndRejectsFalseOwnership()
    {
        var document = CreateOwnerReexportLock();
        var artifact = document["packageArtifacts"]!["example.reexport"]!;
        var contract = artifact["contracts"]!["example.rpc.v1.minimal"]!;
        var importedHash = artifact["publicInventorySha256"]!.GetValue<string>();

        contract["artifactSource"] = "owned";
        var ownedHash = ComputeInventorySha256("example.reexport", artifact);
        Assert.NotEqual(importedHash, ownedHash);
        AssertRejectedMutation(SerializeLock(document), "rpc-authoring.hash-mismatch");

        artifact["publicInventorySha256"] = ownedHash;
        AssertRejectedMutation(SerializeLock(document), "rpc-authoring.authority-mismatch");

        contract["artifactSource"] = "imported";
        AssertRejectedMutation(SerializeLock(document), "rpc-authoring.hash-mismatch");
        artifact["publicInventorySha256"] = ComputeInventorySha256("example.reexport", artifact);
        var path = WriteMutation(SerializeLock(document), out var directory);
        try
        {
            var contractLock = SunderRpcAuthoringDocuments.ReadContractLock(path);
            Assert.Equal(importedHash, contractLock.PackageArtifacts["example.reexport"].PublicInventorySha256);
            Assert.Equal("imported", contractLock.PackageArtifacts["example.reexport"].Contracts["example.rpc.v1.minimal"].ArtifactSource);
            Assert.Equal(1, contractLock.Materialization.ContractBundles);
            Assert.Equal(1, contractLock.Materialization.RoleScopedUses);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("selectedAuthorityRelease", "1.1.1")]
    [InlineData("selectedAuthorityContentIdentity", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void ReadContractLock_RejectsConflictingPlatformAuthoritySnapshot(string member, object value)
    {
        var document = JsonNode.Parse(File.ReadAllText(Fixture("valid", "mixed-contract-lock.json")))!.AsObject();
        document["platformImports"]!["stackContributor"]![member] = JsonSerializer.SerializeToNode(value);

        AssertRejectedMutation(SerializeLock(document), "rpc-authoring.authority-mismatch");
    }

    [Theory]
    [InlineData("package-api-contract-lock.json")]
    [InlineData("mixed-contract-lock.json")]
    public void InventoryHash_MatchesCanonicalFixtures(string fileName)
    {
        var document = JsonNode.Parse(File.ReadAllText(Fixture("valid", fileName)))!;
        foreach (var artifact in document["packageArtifacts"]!.AsObject())
        {
            Assert.Equal(
                artifact.Value!["publicInventorySha256"]!.GetValue<string>(),
                ComputeInventorySha256(artifact.Key, artifact.Value!));
        }
    }

    [Fact]
    public void ReadContractLock_UsesSharedInventoryHashingWithoutEscapingExactVersionBuildMetadata()
    {
        var document = JsonNode.Parse(File.ReadAllText(Fixture("valid", "package-api-contract-lock.json")))!.AsObject();
        var artifact = document["packageArtifacts"]!["example.dependency"]!;
        artifact["selectedPackageVersion"] = "1.2.3+build.01";
        var contract = artifact["contracts"]!["example.rpc.v1.minimal"]!;
        contract["selectedAuthorityRelease"] = "1.2.3+build.01";
        contract["resolvedContractVersion"] = "2.0.0+build.02";
        artifact["publicInventorySha256"] = ComputeInventorySha256("example.dependency", artifact);
        var path = WriteMutation(SerializeLock(document), out var directory);
        try
        {
            var contractLock = SunderRpcAuthoringDocuments.ReadContractLock(path);
            var selected = contractLock.PackageArtifacts["example.dependency"];
            Assert.Equal(artifact["publicInventorySha256"]!.GetValue<string>(), selected.PublicInventorySha256);
            Assert.Equal("2.0.0+build.02", Assert.Single(selected.Contracts).Value.ResolvedContractVersion);
            Assert.Equal(1, contractLock.Materialization.ContractBundles);
            Assert.Equal(1, contractLock.Materialization.RoleScopedUses);
            Assert.Equal(1, contractLock.Materialization.PackageDependencies);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("resolvedContractVersion", "latest")]
    [InlineData("authorityId", "Example.owner")]
    [InlineData("abiSha256", "bad")]
    [InlineData("descriptorBytes", 1)]
    [InlineData("descriptorBytes", 4_194_305)]
    public void ReadContractLock_DelegatesInventoryFieldValidationToFormat(string member, object value)
    {
        var document = JsonNode.Parse(File.ReadAllText(Fixture("valid", "package-api-contract-lock.json")))!.AsObject();
        var contract = document["packageArtifacts"]!["example.dependency"]!["contracts"]!["example.rpc.v1.minimal"]!;
        contract[member] = JsonSerializer.SerializeToNode(value);

        AssertRejectedMutation(SerializeLock(document), "inventory");
    }

    [Theory]
    [InlineData("catalogSigningKeyId")]
    [InlineData("catalogTrustGeneration")]
    [InlineData("catalogTrustFingerprint")]
    public void ReadContractLock_RejectsRetiredSigningMetadataForBothAuthorityKinds(string member)
    {
        var document = JsonNode.Parse(File.ReadAllText(Fixture("valid", "package-api-mixed-authority-contract-lock.json")))!.AsObject();
        var contracts = document["packageArtifacts"]!["example.dependency"]!["contracts"]!.AsObject();
        var platform = contracts["sunder.stack.contributor"]!.AsObject();
        platform[member] = "obsolete";
        AssertRejectedMutation(SerializeLock(document), "unsupported");

        document = JsonNode.Parse(File.ReadAllText(Fixture("valid", "package-api-contract-lock.json")))!.AsObject();
        contracts = document["packageArtifacts"]!["example.dependency"]!["contracts"]!.AsObject();
        var package = new JsonObject();
        foreach (var property in contracts["example.rpc.v1.minimal"]!.AsObject())
        {
            package.Add(property.Key, property.Value!.DeepClone());
            if (property.Key == "selectedAuthorityContentIdentity") package.Add(member, "obsolete");
        }
        contracts["example.rpc.v1.minimal"] = package;
        AssertRejectedMutation(SerializeLock(document), "unsupported");
    }

    [Fact]
    public void ReadContractLock_RejectsVersionSelectionAndStrongEtagMismatch()
    {
        var source = File.ReadAllText(Fixture("valid", "package-api-contract-lock.json"));
        AssertRejectedMutation(
            source.Replace(">=1.0.0 <2.0.0", ">=2.0.0 <3.0.0", StringComparison.Ordinal),
            "rpc-authoring.version-selection-mismatch");
        AssertRejectedMutation(
            source.Replace(
                "\"etag\": \"\\\"709c513e8ce97618f8a5fefdf3b7de92c8ed673a7d657ba151b279d527c0d371\\\"\"",
                "\"etag\": \"\\\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\\\"\"",
                StringComparison.Ordinal),
            "rpc-authoring.hash-mismatch");
    }

    [Fact]
    public void ReadContractLock_LocalOriginsAreAbsentWhilePresentNullOrMalformedOriginsRemainInvalid()
    {
        var local = JsonNode.Parse(File.ReadAllText(Fixture("valid", "mixed-contract-lock.json")))!.AsObject();
        foreach (var artifact in local["packageArtifacts"]!.AsObject()) artifact.Value!.AsObject().Remove("registryOrigin");
        foreach (var platform in local["platformImports"]!.AsObject()) platform.Value!.AsObject().Remove("registryOrigin");
        var parsed = SunderRpcAuthoringDocuments.ParseContractLock(System.Text.Encoding.UTF8.GetBytes(SerializeLock(local)));
        Assert.All(parsed.PackageArtifacts.Values, artifact => Assert.Null(artifact.RegistryOrigin));
        Assert.All(parsed.PlatformImports.Values, platform => Assert.Null(platform.RegistryOrigin));
        foreach (var map in new[] { "packageArtifacts", "platformImports" })
            foreach (var origin in new string?[] { null, "", "https://registry.example/path", "https://REGISTRY.example/", "http://registry.example/" })
            {
                var invalid = local.DeepClone();
                // Reinsert at the canonical property position so this proves origin
                // validation rather than merely the deterministic-order check.
                var selected = invalid[map]!.AsObject().First();
                var record = new JsonObject();
                foreach (var property in selected.Value!.AsObject())
                {
                    record.Add(property.Key, property.Value?.DeepClone());
                    if (property.Key == (map == "packageArtifacts" ? "selectedPackageContentIdentity" : "resolvedContractVersion"))
                        record.Add("registryOrigin", origin);
                }
                invalid[map]![selected.Key] = record;
                Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ParseContractLock(
                    System.Text.Encoding.UTF8.GetBytes(SerializeLock(invalid.AsObject()))));
            }
    }

    [Fact]
    public void ReadContractLock_EnforcesCanonicalProductionAndDevelopmentOrigins()
    {
        var source = File.ReadAllText(Fixture("valid", "package-api-contract-lock.json"));
        AssertRejectedMutation(
            source.Replace("https://registry.example/", "https://1.2.3.4.5/", StringComparison.Ordinal),
            "registry origin");

        var development = source.Replace(
            "https://registry.example/",
            "http://localhost:5288/",
            StringComparison.Ordinal);
        AssertRejectedMutation(development, "registry origin");
        var path = WriteMutation(development, out var directory);
        try
        {
            var contractLock = SunderRpcAuthoringDocuments.ReadContractLock(
                path,
                allowDevelopmentRegistryOrigin: true);
            Assert.Equal("http://localhost:5288/", contractLock.PackageArtifacts["example.dependency"].RegistryOrigin);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ResolveContractCache_ValidatesSelectedDescriptorsAndIgnoresMetadataOnlyPlatformContracts()
    {
        var directory = CreateTempDirectory();
        try
        {
            var contractLock = SunderRpcAuthoringDocuments.ReadContractLock(
                Fixture("valid", "package-api-mixed-authority-contract-lock.json"));
            WriteMinimalCacheEntry(directory);

            var cache = SunderRpcContractCache.Resolve(contractLock, directory);

            var entry = Assert.Single(cache.EntriesByDescriptorSha256).Value;
            Assert.Equal("example.rpc.v1.minimal", entry.Descriptor.ContractId);
            Assert.Equal("2.0.0", entry.Descriptor.Version);
            Assert.Equal(487, entry.CanonicalUtf8.Length);
            Assert.False(File.Exists(SunderRpcContractCache.GetDescriptorPath(
                directory,
                "6865e949afb4e290d3c67f5868d33701184d5bb01441b69f5909d6f044d40567")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ResolveContractCache_ReportsMissingAndCorruptEntries()
    {
        var directory = CreateTempDirectory();
        try
        {
            SunderRpcContractCacheStore.InitializeRoot(directory);
            var contractLock = SunderRpcAuthoringDocuments.ReadContractLock(
                Fixture("valid", "package-api-contract-lock.json"));
            var missing = Assert.Throws<InvalidDataException>(() =>
                SunderRpcContractCache.Resolve(contractLock, directory));
            Assert.StartsWith("contract-cache.entry-missing:", missing.Message, StringComparison.Ordinal);

            WriteMinimalCacheEntry(directory);
            var receiptPath = SunderRpcContractCache.GetReceiptPath(
                directory,
                "709c513e8ce97618f8a5fefdf3b7de92c8ed673a7d657ba151b279d527c0d371");
            File.WriteAllText(
                receiptPath,
                File.ReadAllText(receiptPath).Replace(
                    "999c92cc5041ca77b2acabeab94a5bf0887117a8005ec3fa709564dc95902a42",
                    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    StringComparison.Ordinal));
            var corrupt = Assert.Throws<InvalidDataException>(() =>
                SunderRpcContractCache.Resolve(contractLock, directory));
            Assert.StartsWith("contract-cache.entry-corrupt:", corrupt.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ResolveContractCache_EmptyLockDoesNotRequireCacheDirectory()
    {
        var contractLock = SunderRpcAuthoringDocuments.ReadContractLock(Fixture("valid", "minimal-contract-lock.json"));
        var missing = Path.Combine(Path.GetTempPath(), "sunder-contract-lock-tests", Guid.NewGuid().ToString("N"));

        Assert.Empty(SunderRpcContractCache.Resolve(contractLock, missing).EntriesByDescriptorSha256);
    }

    private static JsonObject CreateOwnerReexportLock(bool importReexportApi = true, bool importReexportProvider = false)
    {
        var document = JsonNode.Parse(File.ReadAllText(Fixture("valid", "package-api-contract-lock.json")))!.AsObject();
        var artifacts = document["packageArtifacts"]!.AsObject();
        var reexport = artifacts["example.dependency"]!.DeepClone();
        reexport["selectedPackageVersion"] = "1.4.0";
        reexport["selectedPackageContentIdentity"] = new string('2', 64);
        reexport["contracts"]!["example.rpc.v1.minimal"]!["artifactSource"] = "imported";
        reexport["publicInventorySha256"] = ComputeInventorySha256("example.reexport", reexport);
        artifacts.Add("example.reexport", reexport);
        if (importReexportApi)
        {
            var import = document["packageImports"]!["example.dependency"]!.DeepClone();
            import["localName"] = "reexport";
            document["packageImports"]!["example.reexport"] = import;
        }
        if (importReexportProvider)
        {
            document["providerImports"]!["reexportProvider"] = new JsonObject
            {
                ["packageId"] = "example.reexport",
                ["contractId"] = "example.rpc.v1.minimal",
            };
        }
        return document;
    }

    private static string SerializeLock(JsonObject document)
        => document.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n";

    private static string ComputeInventorySha256(string packageId, JsonNode artifact)
    {
        // The inventory preimage follows schema order, not RFC 8785 object-key sorting.
        using var payload = new MemoryStream();
        using (var writer = new Utf8JsonWriter(payload, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("packageId", packageId);
            writer.WriteString("packageVersion", artifact["selectedPackageVersion"]!.GetValue<string>());
            writer.WriteString("packageContentIdentity", artifact["selectedPackageContentIdentity"]!.GetValue<string>());
            writer.WriteNumber("publicContractCount", artifact["publicContractCount"]!.GetValue<int>());
            writer.WriteStartArray("contracts");
            foreach (var contract in artifact["contracts"]!.AsObject().OrderBy(static entry => entry.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("contractId", contract.Key);
                writer.WriteString("contractVersion", contract.Value!["resolvedContractVersion"]!.GetValue<string>());
                foreach (var member in new[]
                {
                    "descriptorBytes", "descriptorSha256", "abiSha256", "artifactSource", "authorityKind", "authorityId",
                    "selectedAuthorityRelease", "selectedAuthorityContentIdentity",
                })
                {
                    if (contract.Value![member] is not { } value) continue;
                    writer.WritePropertyName(member);
                    value.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("sunder.package-public-api-inventory.v1\0"u8);
        hash.AppendData(payload.ToArray());
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AssertRejectedMutation(string contents, string diagnosticId)
    {
        var path = WriteMutation(contents, out var directory);
        try
        {
            var exception = Assert.Throws<InvalidDataException>(() =>
                SunderRpcAuthoringDocuments.ReadContractLock(path));
            Assert.Contains(diagnosticId, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string WriteMutation(string contents, out string directory)
    {
        directory = CreateTempDirectory();
        var path = Path.Combine(directory, "sunder-contracts.lock.json");
        File.WriteAllText(path, contents);
        return path;
    }

    private static void WriteMinimalCacheEntry(string cacheRoot)
    {
        SunderRpcContractCacheStore.InitializeRoot(cacheRoot);
        const string sha256 = "709c513e8ce97618f8a5fefdf3b7de92c8ed673a7d657ba151b279d527c0d371";
        var descriptor = SunderRpcContractDescriptor.Parse(File.ReadAllBytes(Path.Combine(
            FindRepositoryRoot(),
            "specs",
            "rpc",
            "v1",
            "conformance",
            "valid",
            "minimal.rpc.json")));
        File.WriteAllBytes(SunderRpcContractCache.GetDescriptorPath(cacheRoot, sha256), descriptor.GetCanonicalUtf8());
        File.Copy(
            Fixture("valid", "cache-entry-receipt.json"),
            SunderRpcContractCache.GetReceiptPath(cacheRoot, sha256));
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sunder-contract-lock-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string Fixture(string category, string fileName)
        => Path.Combine(
            FindRepositoryRoot(),
            "specs",
            "rpc",
            "v1",
            "authoring",
            "conformance",
            category,
            fileName);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Sunder Core repository root.");
    }
}
