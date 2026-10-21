using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcAuthoringReceiptTests : IDisposable
{
    private const string PackageId = "example.consumer";
    private const string PackageVersion = "9.8.7";
    private const string ContractId = "example.rpc.v1.minimal";
    private const string ProviderId = "example.consumer.minimal.provider";
    private static readonly string Fingerprint = new('a', 64);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private const string OwnedSource = """
        using System.Threading.Tasks;
        using Sunder.Sdk.Services;
        namespace Example.Owned;
        [SunderService(SunderServiceExposure.Public)]
        [SunderServiceProvider(ProviderId = "example.consumer.owned.provider")]
        public sealed partial class OwnedService
        {
            public ValueTask<Response> ReadAsync() => throw new System.NotImplementedException();
        }
        public sealed record Response
        {
            public required string Value { get; init; }
        }
        """;

    private readonly TemporaryDirectoryScope _temporary = TemporaryDirectoryScope.Create("sunder-rpc-authoring-receipt-tests");
    private readonly string _cacheRoot;
    private readonly SunderRpcContractDescriptor _descriptor;

    public SunderRpcAuthoringReceiptTests()
    {
        _cacheRoot = _temporary.CreateDirectory("cache");
        _descriptor = SunderRpcContractDescriptor.Parse(File.ReadAllBytes(Fixture("conformance/valid/minimal.rpc.json")));
        Cache(_descriptor);
    }

    [Fact]
    public void Write_EmptyCompilationPreservesExactExistingReceiptShape()
    {
        var contractLock = ReadLock("minimal-contract-lock.json");
        var imports = Prepare(contractLock);
        var compilation = Compile(imports);

        var bytes = SunderRpcAuthoringDocuments.WriteAuthoringReceipt(
            "1.0.0", Fingerprint, contractLock, "example.owner", "1.0.0", compilation, imports);

        Assert.Equal(File.ReadAllBytes(Fixture("authoring/conformance/valid/minimal-authoring-receipt.json")), bytes);
        AssertReceipt(bytes);
        Assert.Empty(compilation.Contracts);
        Assert.Empty(compilation.ImportedProviders);
        Assert.Empty(imports.Contracts);
    }

    [Theory]
    [InlineData("Public", "public", "public")]
    [InlineData("Package", "package", "owner")]
    public void Write_OwnedOnlyPreservesContractsProvidersAndEmptyImportArrays(string sourceExposure, string exposure, string audience)
    {
        var contractLock = ReadLock("minimal-contract-lock.json");
        var imports = Prepare(contractLock);
        var compilation = Compile(imports, new SunderRpcCSharpSourceFile("Owned.cs", OwnedSource.Replace("Exposure.Public", "Exposure." + sourceExposure, StringComparison.Ordinal)));
        var compiled = Assert.Single(compilation.Contracts);

        var root = AssertReceipt(Write(contractLock, compilation, imports));

        Assert.Equal(PackageId, root.GetProperty("packageId").GetString());
        Assert.Equal(PackageVersion, root.GetProperty("packageVersion").GetString());
        Assert.Equal(Fingerprint, root.GetProperty("inputFingerprint").GetString());
        Assert.Equal(contractLock.Sha256, root.GetProperty("lockSha256").GetString());
        var contract = Assert.Single(root.GetProperty("contracts").EnumerateArray());
        Assert.Equal(new[] { "contractKey", "contractId", "contractVersion", "exposure", "descriptorBytes", "canonicalDescriptor", "descriptorSha256", "abiSha256", "modelProjection" }
                .Concat(sourceExposure == "Public" ? ["appUse"] : Array.Empty<string>()),
            contract.EnumerateObject().Select(property => property.Name));
        if (sourceExposure == "Public")
        {
            Assert.False(contract.GetProperty("appUse").GetProperty("required").GetBoolean());
            Assert.Equal(new[] { "discover", "invoke" }, contract.GetProperty("appUse").GetProperty("actions").EnumerateArray().Select(action => action.GetString()));
        }
        Assert.Equal(compiled.ContractKey, contract.GetProperty("contractKey").GetString());
        Assert.Equal(compiled.Descriptor.ContractId, contract.GetProperty("contractId").GetString());
        Assert.Equal(sourceExposure == "Package" ? PackageVersion : "1.0.0", contract.GetProperty("contractVersion").GetString());
        Assert.Equal(exposure, contract.GetProperty("exposure").GetString());
        Assert.Equal(compiled.Descriptor.GetCanonicalUtf8().Length, contract.GetProperty("descriptorBytes").GetInt32());
        Assert.Equal(Encoding.UTF8.GetString(compiled.Descriptor.GetCanonicalUtf8()), contract.GetProperty("canonicalDescriptor").GetString());
        Assert.Equal(compiled.Descriptor.Sha256, contract.GetProperty("descriptorSha256").GetString());
        Assert.Equal(compiled.Descriptor.AbiSha256, contract.GetProperty("abiSha256").GetString());
        AssertJsonEqual(new JsonObject
        {
            ["contractKey"] = compiled.ContractKey, ["source"] = "owned", ["authorityKind"] = "package", ["authorityId"] = PackageId,
        }, Assert.Single(root.GetProperty("bundles").EnumerateArray()));
        AssertJsonEqual(new JsonObject
        {
            ["providerId"] = "example.consumer.owned.provider", ["role"] = "runtime", ["audience"] = audience, ["contractKey"] = compiled.ContractKey,
        }, Assert.Single(root.GetProperty("providers").EnumerateArray()));
        AssertJsonEqual(new JsonObject
        {
            ["contractKey"] = compiled.ContractKey, ["logicalName"] = compiled.GeneratedLogicalName,
        }, Assert.Single(root.GetProperty("generatedBindings").EnumerateArray()));
        AssertEmpty(root, "packageArtifacts", "packageArtifactImports", "packageApiImports", "providerImports", "platformImports", "uses", "generatedPackageFacades");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_RealImportsMatchEveryCurrentReviewedRecord(bool providerOnly)
    {
        using var review = JsonDocument.Parse(File.ReadAllBytes(Fixture("authoring/current-parity/import-receipt-records.json")));
        var input = review.RootElement;
        var document = ReadJson(Fixture("authoring/current-parity/" + input.GetProperty("lockPath").GetString()));
        var binding = input.GetProperty("providerBinding");
        if (providerOnly)
        {
            document["packageImports"] = new JsonObject();
            document["providerImports"]![binding.GetProperty("key").GetString()!] = new JsonObject
            { ["packageId"] = "example.dependency", ["contractId"] = ContractId };
        }
        var contractLock = ReadLock(document);
        var imports = Prepare(contractLock);
        var compilation = providerOnly
            ? Compile(imports, ProviderSource("LocalProvider", binding.GetProperty("key").GetString()!, binding.GetProperty("providerId").GetString()!))
            : Compile(imports);

        var root = AssertReceipt(Write(contractLock, compilation, imports));

        Assert.Empty(compilation.Contracts);
        var expected = input.GetProperty("common").EnumerateObject()
            .Concat(input.GetProperty(providerOnly ? "provider" : "consumer").EnumerateObject()).ToArray();
        Assert.Equal(expected.Select(property => property.Name).Order(StringComparer.Ordinal), root.EnumerateObject().Skip(6).Select(property => property.Name).Order(StringComparer.Ordinal));
        foreach (var record in expected)
            Assert.True(JsonElement.DeepEquals(record.Value, root.GetProperty(record.Name)), $"{record.Name} differs from the current reviewed record.");
        if (providerOnly)
        {
            Assert.Single(compilation.ImportedProviders);
            AssertEmpty(root, "uses", "packageApiImports", "generatedPackageFacades");
        }
        else
        {
            Assert.Empty(compilation.ImportedProviders);
            var artifact = Assert.Single(root.GetProperty("packageArtifacts").EnumerateArray());
            Assert.Equal(2, artifact.GetProperty("publicContractCount").GetInt32());
            Assert.Equal(2, artifact.GetProperty("contracts").GetArrayLength());
            foreach (var excluded in input.GetProperty("excludedContracts").EnumerateArray())
            {
                var excludedId = excluded.GetProperty("contractId").GetString();
                Assert.DoesNotContain(imports.Contracts, contract => contract.ContractKey == excludedId);
                Assert.DoesNotContain(root.GetProperty("bundles").EnumerateArray(), bundle => bundle.GetProperty("contractKey").GetString() == excludedId);
                Assert.DoesNotContain(root.GetProperty("uses").EnumerateArray(), use => use.GetProperty("contractKey").GetString() == excludedId);
                Assert.True(File.Exists(SunderRpcContractCache.GetDescriptorPath(_cacheRoot, excluded.GetProperty("descriptorSha256").GetString()!)));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_ConsumerAndProviderShareOneImportedBindingAndUnionWithOwnedOutput(bool includeOwned)
    {
        var contractLock = ReadLock(ProviderLock(consumer: true));
        var imports = Prepare(contractLock);
        var sources = new[] { ProviderSource("LocalProvider") };
        if (includeOwned) sources = sources.Append(new SunderRpcCSharpSourceFile("Owned.cs", OwnedSource)).ToArray();
        var compilation = Compile(imports, sources);

        var bytes = Write(contractLock, compilation, imports);
        var root = AssertReceipt(bytes);

        Assert.Equal(includeOwned ? 1 : 0, compilation.Contracts.Count);
        Assert.Single(compilation.ImportedProviders);
        Assert.Single(imports.Contracts);
        Assert.Equal(includeOwned ? 2 : 1, root.GetProperty("contracts").GetArrayLength());
        Assert.Equal(includeOwned ? 2 : 1, root.GetProperty("providers").GetArrayLength());
        foreach (var member in new[] { "contracts", "bundles", "generatedBindings" })
        {
            var keys = root.GetProperty(member).EnumerateArray().Select(record => record.GetProperty("contractKey").GetString()!).ToArray();
            Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
            Assert.Single(keys, key => key == ContractId);
            Assert.Equal(includeOwned ? 2 : 1, keys.Length);
        }
        Assert.Single(root.GetProperty("uses").EnumerateArray());
        Assert.Single(root.GetProperty("generatedPackageFacades").EnumerateArray());
        var provider = Assert.Single(root.GetProperty("providerImports").EnumerateArray());
        var api = Assert.Single(root.GetProperty("packageApiImports").EnumerateArray());
        Assert.True(JsonElement.DeepEquals(provider.GetProperty("bundleRef"), Assert.Single(api.GetProperty("bundleRefs").EnumerateArray())));
        Assert.Equal(bytes, Write(contractLock, compilation with { Contracts = compilation.Contracts.Reverse().ToArray() },
            imports with { Contracts = imports.Contracts.Reverse().ToArray(), Sources = imports.Sources.Reverse().ToArray() }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_ProviderReferencesContainAllMatchingRegistrationsIncludingReexports(bool reexport)
    {
        var document = ProviderLock();
        if (reexport) AddReexport(document, consumer: false, provider: true);
        var contractLock = ReadLock(document);
        var imports = Prepare(contractLock);
        var compilation = Compile(imports,
            ProviderSource("ZuluProvider", reexport ? "reexportProvider" : "minimalProvider", "example.consumer.zulu", "Owner"),
            ProviderSource("AlphaProvider", providerId: "example.consumer.alpha"));

        var bytes = Write(contractLock, compilation, imports);
        var root = AssertReceipt(bytes);

        Assert.Single(root.GetProperty("contracts").EnumerateArray());
        var manifest = SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(
            bytes, contractLock.Sha256, PackageId, PackageVersion).Manifest;
        Assert.Equal(reexport ? 2 : 1, manifest.ProviderImports!.Count);
        Assert.All(manifest.ProviderImports, import => Assert.Equal(2, import!.ProviderRefs!.Count));
        Assert.Single(root.GetProperty("bundles").EnumerateArray());
        Assert.Single(root.GetProperty("generatedBindings").EnumerateArray());
        AssertEmpty(root, "packageApiImports", "uses", "generatedPackageFacades");
        var expected = new[] { "example.consumer.alpha", "example.consumer.zulu" };
        Assert.Equal(expected, root.GetProperty("providers").EnumerateArray().Select(provider => provider.GetProperty("providerId").GetString()));
        Assert.Equal(new[] { "public", "owner" }, root.GetProperty("providers").EnumerateArray().Select(provider => provider.GetProperty("audience").GetString()));
        Assert.Equal(reexport ? 2 : 1, root.GetProperty("providerImports").GetArrayLength());
        foreach (var import in root.GetProperty("providerImports").EnumerateArray())
        {
            Assert.Equal(expected, import.GetProperty("providerRefs").EnumerateArray().Select(reference => reference.GetProperty("providerId").GetString()));
            Assert.All(import.GetProperty("providerRefs").EnumerateArray(), reference =>
            {
                Assert.Equal("runtime", reference.GetProperty("role").GetString());
                Assert.Equal(ContractId, reference.GetProperty("contractId").GetString());
                Assert.Equal(_descriptor.Version, reference.GetProperty("contractVersion").GetString());
            });
        }
        Assert.Equal(bytes, Write(contractLock, compilation with { ImportedProviders = compilation.ImportedProviders.Reverse().ToArray() },
            imports with { ProviderSurfaces = imports.ProviderSurfaces.Reverse().ToArray() }));
    }

    [Theory]
    [InlineData("required", false)]
    [InlineData("required", true)]
    [InlineData("optional", false)]
    [InlineData("optional", true)]
    public void Write_RequiredContractsAndPackagePresenceAreIndependent(string presence, bool required)
    {
        var document = LockDocument();
        var intent = document["packageImports"]!["example.dependency"]!;
        intent["presence"] = presence;
        intent["requiredContracts"] = required ? new JsonArray(ContractId) : new JsonArray();
        var contractLock = ReadLock(document);
        var imports = Prepare(contractLock);

        var root = AssertReceipt(Write(contractLock, Compile(imports), imports));

        Assert.Equal(presence, Assert.Single(root.GetProperty("packageApiImports").EnumerateArray()).GetProperty("presence").GetString());
        var use = Assert.Single(root.GetProperty("uses").EnumerateArray());
        Assert.Equal(required, use.GetProperty("required").GetBoolean());
        Assert.Equal(new[] { "discover", "invoke" }, use.GetProperty("actions").EnumerateArray().Select(action => action.GetString()));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Write_ReexportConsumersDeduplicateUsesAndCombineRequiredContracts(bool ownerRequired, bool reexportRequired)
    {
        var document = LockDocument();
        AddReexport(document, consumer: true, provider: false);
        document["packageImports"]!["example.dependency"]!["requiredContracts"] = ownerRequired ? new JsonArray(ContractId) : new JsonArray();
        document["packageImports"]!["example.reexport"]!["requiredContracts"] = reexportRequired ? new JsonArray(ContractId) : new JsonArray();
        var contractLock = ReadLock(document);
        var imports = Prepare(contractLock);

        var root = AssertReceipt(Write(contractLock, Compile(imports), imports));

        Assert.Single(root.GetProperty("contracts").EnumerateArray());
        Assert.Single(root.GetProperty("bundles").EnumerateArray());
        Assert.Single(root.GetProperty("generatedBindings").EnumerateArray());
        Assert.Equal(ownerRequired || reexportRequired, Assert.Single(root.GetProperty("uses").EnumerateArray()).GetProperty("required").GetBoolean());
        Assert.Equal(2, root.GetProperty("packageArtifacts").GetArrayLength());
        Assert.Equal(2, root.GetProperty("packageArtifactImports").GetArrayLength());
        Assert.Equal(2, root.GetProperty("generatedPackageFacades").GetArrayLength());
        Assert.Equal("Sunder.Generated.Packages.Example_Dependency_Internal.CExample_Rpc_V1_Minimal",
            Assert.Single(root.GetProperty("generatedBindings").EnumerateArray()).GetProperty("logicalName").GetString());
        Assert.Equal(new[] { "Sunder.Generated.Packages.Example_Dependency_Internal.DependencyPackageApi",
                "Sunder.Generated.Packages.Example_Reexport_Internal.ReexportPackageApi" },
            root.GetProperty("generatedPackageFacades").EnumerateArray().Select(facade => facade.GetProperty("logicalName").GetString()));
        _ = SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(SunderRpcAuthoringDocuments.WriteDeterministicDocument(root),
            contractLock.Sha256, PackageId, PackageVersion);
        var stale = JsonNode.Parse(root.GetRawText())!;
        stale["generatedBindings"]![0]!["logicalName"] = "Sunder.Generated.Rpc.Imports.CExample_Rpc_V1_Minimal";
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(
            SunderRpcAuthoringDocuments.WriteDeterministicDocument(JsonSerializer.SerializeToElement(stale)), contractLock.Sha256, PackageId, PackageVersion));
        var apis = root.GetProperty("packageApiImports").EnumerateArray().ToArray();
        Assert.Equal(new[] { "example.dependency", "example.reexport" }, apis.Select(api => api.GetProperty("packageId").GetString()));
        Assert.True(JsonElement.DeepEquals(apis[0].GetProperty("bundleRefs"), apis[1].GetProperty("bundleRefs")));
        Assert.True(JsonElement.DeepEquals(apis[0].GetProperty("useRefs"), apis[1].GetProperty("useRefs")));
    }

    [Theory]
    [InlineData("unary", "discover,invoke")]
    [InlineData("stream", "discover,subscribe")]
    [InlineData("mixed", "discover,invoke,subscribe")]
    public void Write_ConsumerActionsAreTheExactDescriptorActionSubset(string methods, string expectedActions)
    {
        var descriptor = ChangeDescriptor(json =>
        {
            var map = json["services"]!["example"]!["methods"]!.AsObject();
            if (methods == "unary") return;
            var stream = map["ping"]!.DeepClone();
            stream["kind"] = "server-stream";
            stream["eventSchema"] = stream["responseSchema"]!.DeepClone();
            stream.AsObject().Remove("responseSchema");
            if (methods == "stream") map.Clear();
            map["watch"] = stream;
        });
        var document = LockDocument();
        SetArtifactContracts(document, descriptor);
        Cache(descriptor);
        var contractLock = ReadLock(document);
        var imports = Prepare(contractLock);

        var root = AssertReceipt(Write(contractLock, Compile(imports), imports));

        Assert.Equal(expectedActions.Split(','), Assert.Single(root.GetProperty("uses").EnumerateArray())
            .GetProperty("actions").EnumerateArray().Select(action => action.GetString()));
    }

    [Fact]
    public void Write_ProviderOnlyRetainsFullInventoryButMaterializesOnlyTheSelectedContract()
    {
        var extra = ChangeDescriptor(json => json["contractId"] = "example.rpc.v1.unselected");
        var document = ProviderLock();
        SetArtifactContracts(document, _descriptor, extra);
        Cache(extra);
        var contractLock = ReadLock(document);
        var imports = Prepare(contractLock);

        var root = AssertReceipt(Write(contractLock, Compile(imports, ProviderSource("LocalProvider")), imports));

        Assert.Single(imports.Contracts);
        Assert.Single(root.GetProperty("contracts").EnumerateArray());
        Assert.Single(root.GetProperty("bundles").EnumerateArray());
        Assert.Single(root.GetProperty("generatedBindings").EnumerateArray());
        Assert.Equal(2, Assert.Single(root.GetProperty("packageArtifacts").EnumerateArray()).GetProperty("contracts").GetArrayLength());
        var artifactImport = Assert.Single(root.GetProperty("packageArtifactImports").EnumerateArray());
        Assert.Equal(2, artifactImport.GetProperty("publicContractCount").GetInt32());
        Assert.Single(artifactImport.GetProperty("authorityConstraints").EnumerateArray());
        AssertEmpty(root, "uses", "generatedPackageFacades", "packageApiImports");
        Assert.True(File.Exists(SunderRpcContractCache.GetDescriptorPath(_cacheRoot, extra.Sha256)));
        Assert.DoesNotContain(imports.Contracts, contract => contract.Descriptor.ContractId == extra.ContractId);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("key")]
    [InlineData("logical-name")]
    [InlineData("identity")]
    [InlineData("version")]
    [InlineData("descriptor")]
    [InlineData("abi")]
    public void Write_RejectsInexactImportedContractProjection(string mutation)
    {
        var contractLock = ReadLock();
        var imports = Prepare(contractLock);
        var compilation = Compile(imports);
        var contract = Assert.Single(imports.Contracts);
        if (mutation == "extra")
        {
            var extra = ChangeDescriptor(json =>
            {
                json["contractId"] = "example.rpc.v1.unselected";
                var services = json["services"]!.AsObject(); var service = services["example"]!;
                services.Remove("example"); services["unselected"] = service;
            });
            var document = LockDocument();
            SetArtifactContracts(document, _descriptor, extra);
            Cache(extra);
            imports = Prepare(ReadLock(document));
        }
        else
        {
            var changed = mutation switch
            {
                "key" => contract with { ContractKey = "localAlias" },
                "logical-name" => contract with { GeneratedLogicalName = "Sunder.Generated.Rpc.LocalAlias" },
                "identity" => contract with { Descriptor = ChangeDescriptor(json => json["contractId"] = "example.other") },
                "version" => contract with { Descriptor = ChangeDescriptor(json => json["version"] = "2.0.1") },
                "descriptor" => contract with { Descriptor = ChangeDescriptor(json => json["$defs"]!["Empty"]!["title"] = "Other title") },
                "abi" => contract with { Descriptor = ChangeDescriptor(json => json["services"]!["example"]!["methods"]!["ping"]!["idempotency"] = "none") },
                _ => contract,
            };
            if (mutation == "descriptor")
            {
                Assert.NotEqual(contract.Descriptor.Sha256, changed.Descriptor.Sha256);
                Assert.Equal(contract.Descriptor.AbiSha256, changed.Descriptor.AbiSha256);
            }
            if (mutation == "abi") Assert.NotEqual(contract.Descriptor.AbiSha256, changed.Descriptor.AbiSha256);
            imports = imports with { Contracts = mutation == "missing" ? [] : [changed] };
        }

        AssertRejected(() => Write(contractLock, compilation, imports), "rpc-authoring.evidence-mismatch");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("key")]
    [InlineData("interface")]
    [InlineData("contract")]
    [InlineData("registration")]
    public void Write_RejectsInexactImportedProviderSurfaceProjection(string mutation)
    {
        var contractLock = ReadLock(ProviderLock());
        var imports = Prepare(contractLock);
        var compilation = Compile(imports, ProviderSource("LocalProvider"));
        var surface = Assert.Single(imports.ProviderSurfaces);
        var foreign = surface with { Key = "foreignProvider", InterfaceMetadataName = "Sunder.Generated.Rpc.IForeignProviderService" };
        if (mutation == "registration")
        {
            compilation = compilation with { ImportedProviders = [compilation.ImportedProviders[0] with { Surface = foreign }] };
        }
        else
        {
            imports = imports with
            {
                ProviderSurfaces = mutation switch
                {
                    "missing" => [],
                    "extra" => [surface, foreign],
                    "key" => [foreign],
                    "interface" => [surface with { InterfaceMetadataName = "Example.IMinimalProviderService" }],
                    _ => [surface with { Contract = surface.Contract with { GeneratedLogicalName = "Example.Foreign" } }],
                },
            };
        }

        AssertRejected(() => Write(contractLock, compilation, imports), "rpc-authoring.evidence-mismatch");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_EveryProviderImportRequiresItsOwnBoundSurfaceEvenWhenAnotherSurfaceMatchesTheContract(bool reexport)
    {
        var document = ProviderLock();
        if (reexport) AddReexport(document, consumer: false, provider: true);
        var contractLock = ReadLock(document);
        var imports = Prepare(contractLock);
        var sources = new[] { ProviderSource("LocalProvider") };
        if (reexport) sources = sources.Append(ProviderSource("ReexportProvider", "reexportProvider", "example.consumer.reexport")).ToArray();
        var compilation = Compile(imports, sources);
        compilation = compilation with
        {
            ImportedProviders = compilation.ImportedProviders.Where(provider => provider.Surface.Key != "minimalProvider").ToArray(),
        };

        AssertRejected(() => Write(contractLock, compilation, imports), "rpc-authoring.handler-incomplete");
    }

    [Fact]
    public void Write_RejectsFailedRealProviderCompilation()
    {
        var contractLock = ReadLock(ProviderLock());
        var imports = Prepare(contractLock);
        var source = ProviderSource("LocalProvider");
        var compilation = new SunderRpcCSharpSourceCompiler().Compile(new(PackageId, PackageVersion,
            [source with { Text = source.Text.Replace("PingAsync", "NotPingAsync", StringComparison.Ordinal) }], References(),
            SupplementalSources: imports.Sources, ImportedProviderSurfaces: imports.ProviderSurfaces));
        Assert.False(compilation.Success);

        AssertRejected(() => Write(contractLock, compilation, imports), "rpc-authoring.receipt-invalid");
    }

    [Fact]
    public void Write_ReparsesExactLockBytesRatherThanTrustingMutableModelProjections()
    {
        var contractLock = ReadLock();
        var imports = Prepare(contractLock);
        var compilation = Compile(imports);
        var forged = contractLock with
        {
            PackageArtifacts = new Dictionary<string, SunderRpcLockedPackageArtifact>(),
            PackageImports = new Dictionary<string, SunderRpcLockedPackageImport>(),
            Materialization = new(0, 0, 0, 0),
        };

        Assert.Equal(Write(contractLock, compilation, imports), Write(forged, compilation, imports));
        var empty = Prepare(ReadLock("minimal-contract-lock.json"));
        AssertRejected(() => Write(forged, compilation, empty), "rpc-authoring.evidence-mismatch");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_RejectsLockShaOrExactBytesChangedAfterReading(bool changeBytes)
    {
        var contractLock = ReadLock();
        var imports = Prepare(contractLock);
        var compilation = Compile(imports);
        var changed = changeBytes
            ? contractLock with { ExactBytes = ReadLock("minimal-contract-lock.json").ExactBytes }
            : contractLock with { Sha256 = new string('f', 64) };

        AssertRejected(() => Write(changed, compilation, imports), "rpc-authoring.evidence-mismatch");
    }

    [Theory]
    [InlineData("count", "rpc-authoring.count-mismatch")]
    [InlineData("orphan", "rpc-authoring.unreferenced-artifact")]
    [InlineData("required-contract", "rpc-authoring.root-mismatch")]
    public void Write_RevalidatesLockInputGraphEvenWhenTamperedBytesHaveAFreshSha(string mutation, string diagnostic)
    {
        var contractLock = ReadLock();
        var imports = Prepare(contractLock);
        var compilation = Compile(imports);
        var document = LockDocument();
        if (mutation == "count") document["packageArtifacts"]!["example.dependency"]!["publicContractCount"] = 2;
        else if (mutation == "orphan") document["packageImports"]!.AsObject().Clear();
        else document["packageImports"]!["example.dependency"]!["requiredContracts"] = new JsonArray("example.other");
        var bytes = Serialize(document);

        AssertRejected(() => Write(contractLock with { ExactBytes = bytes, Sha256 = Sha256(bytes) }, compilation, imports), diagnostic);
    }

    [Fact]
    public void Write_ExplicitPlatformImportsRequireVerifiedCatalogEvidence()
    {
        var contractLock = ReadLock("mixed-contract-lock.json");
        var empty = Prepare(ReadLock("minimal-contract-lock.json"));

        AssertRejected(() => Write(contractLock, Compile(empty), empty), "rpc-authoring.authority-mismatch");
    }

    [Theory]
    [InlineData("producer")]
    [InlineData("package-id")]
    [InlineData("package-version")]
    [InlineData("fingerprint")]
    public void Write_RejectsInvalidReceiptIdentity(string mutation)
    {
        var contractLock = ReadLock("minimal-contract-lock.json");
        var imports = Prepare(contractLock);
        var compilation = Compile(imports);

        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.WriteAuthoringReceipt(
            mutation == "producer" ? "1" : "1.0.0", mutation == "fingerprint" ? new string('A', 64) : Fingerprint,
            contractLock, mutation == "package-id" ? "Not.Canonical" : PackageId,
            mutation == "package-version" ? "latest" : PackageVersion, compilation, imports));
    }

    [Theory]
    [InlineData(127)]
    [InlineData(128)]
    public void Write_ContractLimitCountsTheUnionOfOwnedAndImportedContracts(int ownedCount)
    {
        var contractLock = ReadLock();
        var imports = Prepare(contractLock);
        var source = "using System.Threading.Tasks; using Sunder.Sdk.Services; namespace Example.Many;\n"
            + "public sealed record Response { public required string Value { get; init; } }\n"
            + string.Join('\n', Enumerable.Range(0, ownedCount).Select(index => $$"""
                [SunderService(SunderServiceExposure.Public)]
                public interface Owned{{index:D3}}Service
                {
                    ValueTask<Response> ReadAsync();
                }
                """));
        var compilation = Compile(imports, new SunderRpcCSharpSourceFile("Owned.cs", source));
        Assert.Equal(ownedCount, compilation.Contracts.Count);

        if (ownedCount == 128)
            AssertRejected(() => Write(contractLock, compilation, imports), "rpc-authoring.aggregate-materializability-overflow");
        else
        {
            var root = AssertReceipt(Write(contractLock, compilation, imports));
            foreach (var member in new[] { "contracts", "bundles", "generatedBindings" }) Assert.Equal(128, root.GetProperty(member).GetArrayLength());
            Assert.Single(root.GetProperty("uses").EnumerateArray());
        }
    }

    [Theory]
    [InlineData(127)]
    [InlineData(128)]
    public void Write_ProviderLimitCountsOwnedAndImportedRegistrationsNotImportGroups(int importedCount)
    {
        var contractLock = ReadLock(ProviderLock());
        var imports = Prepare(contractLock);
        var imported = Compile(imports, Enumerable.Range(0, importedCount)
            .Select(index => ProviderSource("Provider" + index, providerId: $"example.consumer.p{index:D3}")).ToArray());
        var owned = Compile(Prepare(ReadLock("minimal-contract-lock.json")), new SunderRpcCSharpSourceFile("Owned.cs", OwnedSource));
        // Each compiler result is valid independently; the receipt owns the final aggregate limit.
        var combined = owned with { ImportedProviders = imported.ImportedProviders };
        Assert.Single(contractLock.ProviderImports);
        Assert.Equal(importedCount, combined.ImportedProviders.Count);

        if (importedCount == 128)
            AssertRejected(() => Write(contractLock, combined, imports), "rpc-authoring.aggregate-materializability-overflow");
        else
        {
            var root = AssertReceipt(Write(contractLock, combined, imports));
            Assert.Equal(128, root.GetProperty("providers").GetArrayLength());
            Assert.Equal(127, Assert.Single(root.GetProperty("providerImports").EnumerateArray()).GetProperty("providerRefs").GetArrayLength());
        }
    }

    [Theory]
    [InlineData("key")]
    [InlineData("folded-key")]
    [InlineData("identity")]
    [InlineData("logical-name")]
    [InlineData("folded-logical-name")]
    public void Write_RejectsOwnedImportedContractIdentityAndNameCollisions(string collision)
    {
        var contractLock = ReadLock();
        var imports = Prepare(contractLock);
        var compilation = Compile(imports, new SunderRpcCSharpSourceFile("Owned.cs", OwnedSource));
        var owned = Assert.Single(compilation.Contracts);
        var imported = Assert.Single(imports.Contracts);
        var changed = collision switch
        {
            "key" => owned with { ContractKey = imported.ContractKey },
            "folded-key" => owned with { ContractKey = imported.ContractKey.ToUpperInvariant() },
            "identity" => owned with { Descriptor = imported.Descriptor },
            "logical-name" => owned with { GeneratedLogicalName = imported.GeneratedLogicalName },
            _ => owned with { GeneratedLogicalName = imported.GeneratedLogicalName.ToUpperInvariant() },
        };

        AssertRejected(() => Write(contractLock, compilation with { Contracts = [changed] }, imports), "rpc-authoring.identity-collision");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_RejectsOwnedImportedProviderIdentityCollisions(bool foldCase)
    {
        var contractLock = ReadLock(ProviderLock());
        var imports = Prepare(contractLock);
        var imported = Compile(imports, ProviderSource("LocalProvider"));
        var owned = Compile(Prepare(ReadLock("minimal-contract-lock.json")), new SunderRpcCSharpSourceFile("Owned.cs",
            OwnedSource.Replace("example.consumer.owned.provider", ProviderId, StringComparison.Ordinal)));
        var registration = Assert.Single(imported.ImportedProviders);
        if (foldCase) registration = registration with { Provider = registration.Provider with { ProviderId = ProviderId.ToUpperInvariant() } };

        AssertRejected(() => Write(contractLock, owned with { ImportedProviders = [registration] }, imports), "rpc-authoring.identity-collision");
    }

    [Fact]
    public void Parse_RejectsImportedContractAliasEvenWhenAllKeyedReferencesAgree()
    {
        var contractLock = ReadLock(ProviderLock(consumer: true));
        var imports = Prepare(contractLock);
        var bytes = Write(contractLock, Compile(imports, ProviderSource("LocalProvider")), imports);
        var valid = SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(bytes, contractLock.Sha256, PackageId, PackageVersion);
        Assert.Equal(ContractId, Assert.Single(valid.Contracts).Key);
        var receipt = JsonNode.Parse(bytes)!.AsObject();
        foreach (var member in new[] { "contracts", "bundles", "providers", "uses", "generatedBindings" })
            Assert.Single(receipt[member]!.AsArray())!["contractKey"] = "localAlias";

        AssertRejected(() => SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(
            Serialize(receipt), contractLock.Sha256, PackageId, PackageVersion), "rpc-authoring.evidence-mismatch");
    }

    [Fact]
    public void Parse_RejectsInventoryDescriptorByteCountEvenWithMatchingInventoryHashes()
    {
        var document = LockDocument();
        var contractLock = ReadLock(document);
        var imports = Prepare(contractLock);
        var bytes = Write(contractLock, Compile(imports), imports);
        var valid = SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(bytes, contractLock.Sha256, PackageId, PackageVersion);
        var actualBytes = valid.Contracts[ContractId].GetCanonicalUtf8().Length;
        var receipt = JsonNode.Parse(bytes)!.AsObject();
        Assert.Equal(actualBytes, receipt["contracts"]![0]!["descriptorBytes"]!.GetValue<int>());

        var artifact = document["packageArtifacts"]!["example.dependency"]!;
        artifact["contracts"]![ContractId]!["descriptorBytes"] = actualBytes + 1;
        var inventorySha256 = InventorySha256("example.dependency", artifact);
        receipt["packageArtifacts"]![0]!["contracts"]![0]!["descriptorBytes"] = actualBytes + 1;
        foreach (var member in new[] { "packageArtifacts", "packageArtifactImports" })
            receipt[member]![0]!["publicInventorySha256"] = inventorySha256;

        AssertRejected(() => SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(
            Serialize(receipt), contractLock.Sha256, PackageId, PackageVersion), "rpc-authoring.evidence-mismatch");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_RejectsCrossArtifactSelectionsEvenWhenAuthorityCountAndFormatGraphMatch(bool provider)
    {
        var other = ChangeDescriptor(json =>
        {
            json["contractId"] = "example.rpc.v1.other";
            var services = json["services"]!.AsObject(); var service = services["example"]!;
            services.Remove("example"); services["other"] = service;
        });
        Cache(other);
        var document = LockDocument();
        AddReexport(document, consumer: !provider, provider: provider);
        SetArtifactContracts(document, _descriptor, other);
        var otherArtifact = document["packageArtifacts"]!["example.reexport"]!.DeepClone();
        otherArtifact["selectedPackageVersion"] = "1.5.0";
        otherArtifact["selectedPackageContentIdentity"] = new string('3', 64);
        var otherEvidence = document["packageArtifacts"]!["example.dependency"]!["contracts"]![other.ContractId]!.DeepClone();
        otherEvidence["artifactSource"] = "imported";
        otherArtifact["contracts"] = new JsonObject { [other.ContractId] = otherEvidence };
        otherArtifact["publicInventorySha256"] = InventorySha256("example.reexport.other", otherArtifact);
        ModelProjectionTestEvidence.PinEmptySourceProjection("example.reexport.other", otherArtifact);
        document["packageArtifacts"]!["example.reexport.other"] = otherArtifact;
        document["packageImports"]!["example.reexport.other"] = new JsonObject
        {
            ["localName"] = "other", ["presence"] = "required", ["requiredContracts"] = new JsonArray(other.ContractId),
            ["consumerRoles"] = new JsonArray("runtime"),
        };
        var contractLock = ReadLock(document);
        var imports = Prepare(contractLock);
        var compilation = provider ? Compile(imports, ProviderSource("LocalProvider", "reexportProvider")) : Compile(imports);
        var bytes = Write(contractLock, compilation, imports);
        var valid = SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(bytes, contractLock.Sha256, PackageId, PackageVersion);
        Assert.Equal(new[] { ContractId, other.ContractId }, valid.Contracts.Keys);
        Assert.Equal(new[] { "example.dependency", "example.reexport", "example.reexport.other" },
            valid.Manifest.DependsOn!.Select(dependency => dependency.PackageId));
        Assert.All(valid.Manifest.DependsOn!, dependency =>
        {
            Assert.Equal("required", dependency.Presence);
            Assert.Equal(contractLock.PackageArtifacts[dependency.PackageId!].RequestedVersionRange, dependency.VersionRange);
        });
        var receipt = JsonNode.Parse(bytes)!.AsObject();
        var artifactImports = receipt["packageArtifactImports"]!.AsArray();
        Assert.Equal(2, artifactImports[0]!["publicContractCount"]!.GetValue<int>());
        Assert.Equal(1, artifactImports[1]!["publicContractCount"]!.GetValue<int>());
        Assert.Equal(1, artifactImports[2]!["publicContractCount"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(artifactImports[1]!["authorityConstraints"], artifactImports[2]!["authorityConstraints"]));

        // Retain the parser's normalized dependencies when checking the inventory-free Format graph.
        var manifest = JsonSerializer.SerializeToNode(valid.Manifest, JsonOptions)!.AsObject();
        if (provider)
        {
            Assert.Equal(_descriptor.Version, other.Version);
            var import = Assert.Single(receipt["providerImports"]!.AsArray())!;
            import["contractId"] = other.ContractId;
            import["bundleRef"]!["contractId"] = other.ContractId;
            Assert.Single(import["providerRefs"]!.AsArray())!["contractId"] = other.ContractId;
            Assert.Single(receipt["providers"]!.AsArray())!["contractKey"] = other.ContractId;
            var registration = Assert.Single(manifest["provides"]!.AsArray())!;
            registration["contractId"] = other.ContractId;
            registration["descriptorSha256"] = other.Sha256;
            registration["abiSha256"] = other.AbiSha256;
        }
        else
        {
            var apis = receipt["packageApiImports"]!.AsArray();
            var first = apis.Single(import => import!["packageId"]!.GetValue<string>() == "example.reexport")!;
            var second = apis.Single(import => import!["packageId"]!.GetValue<string>() == "example.reexport.other")!;
            foreach (var member in new[] { "bundleRefs", "useRefs" })
            {
                var references = first[member]!.DeepClone();
                first[member] = second[member]!.DeepClone();
                second[member] = references;
            }
        }
        foreach (var member in new[] { "packageApiImports", "providerImports" })
            manifest[member] = receipt[member]!.DeepClone();
        var errors = new List<string>();
        typeof(SunderPackageManifest).Assembly.GetType("Sunder.Package.Format.PackageContractValidator", throwOnError: true)!
            .GetMethod("Validate")!.Invoke(null, [manifest.Deserialize<SunderPackageManifest>()!, new Dictionary<string, string>(), errors, false]);
        Assert.Empty(errors);

        AssertRejected(() => SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(
            Serialize(receipt), contractLock.Sha256, PackageId, PackageVersion), "rpc-authoring.evidence-mismatch");
    }

    [Fact]
    public void Validate_CanonicalReceiptIsANoopAndWriterDoesNotMutateInputs()
    {
        var contractLock = ReadLock(ProviderLock(consumer: true));
        var imports = Prepare(contractLock);
        var compilation = Compile(imports, ProviderSource("LocalProvider"));
        var bytes = Write(contractLock, compilation, imports);
        var path = _temporary.GetPath("receipt.json");
        File.WriteAllBytes(path, bytes);
        var before = SnapshotFiles();

        SunderRpcAuthoringDocuments.ValidateAuthoringReceipt(path, bytes);
        Assert.Equal(bytes, Write(contractLock, compilation, imports));
        SunderRpcAuthoringDocuments.ValidateAuthoringReceipt(path, bytes);

        Assert.Equal(before, SnapshotFiles());
        Assert.Equal(contractLock.Sha256, Sha256(contractLock.ExactBytes));
    }

    [Theory]
    [InlineData("required")]
    [InlineData("actions")]
    [InlineData("authority")]
    [InlineData("provider-ref")]
    [InlineData("inventory")]
    [InlineData("facade")]
    public void Validate_RejectsCanonicalSchemaValidSemanticChanges(string mutation)
    {
        var contractLock = ReadLock(ProviderLock(consumer: true));
        var imports = Prepare(contractLock);
        var expected = Write(contractLock, Compile(imports, ProviderSource("LocalProvider")), imports);
        var document = JsonNode.Parse(expected)!.AsObject();
        switch (mutation)
        {
            case "required": document["uses"]![0]!["required"] = false; break;
            case "actions": document["uses"]![0]!["actions"]!.AsArray().Add("subscribe"); break;
            case "authority": document["bundles"]![0]!["authorityId"] = "example.other"; break;
            case "provider-ref": document["providerImports"]![0]!["providerRefs"]![0]!["providerId"] = "example.other.provider"; break;
            case "inventory": document["packageArtifacts"]![0]!["publicInventorySha256"] = new string('f', 64); break;
            default: document["generatedPackageFacades"]![0]!["logicalName"] = "Sunder.Generated.Rpc.OtherPackageApi"; break;
        }
        var actual = Serialize(document);
        using var schema = JsonDocument.Parse(File.ReadAllBytes(Fixture("authoring/authoring-receipt.schema.json")));
        Assert.True(MatchesAuthoringSchema(schema.RootElement, JsonSerializer.SerializeToElement(document)));
        var path = _temporary.GetPath("tampered-receipt.json");
        File.WriteAllBytes(path, actual);
        var timestamp = File.GetLastWriteTimeUtc(path);

        AssertRejected(() => SunderRpcAuthoringDocuments.ValidateAuthoringReceipt(path, expected), "rpc-authoring.evidence-mismatch");

        Assert.Equal(actual, File.ReadAllBytes(path));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
    }

    [Theory]
    [InlineData("bom")]
    [InlineData("crlf")]
    [InlineData("missing-lf")]
    [InlineData("extra-lf")]
    [InlineData("compact")]
    [InlineData("duplicate-member")]
    [InlineData("invalid-utf8")]
    public void Validate_RejectsNoncanonicalRepresentationEvenWhenExpectedBytesMatch(string mutation)
    {
        var bytes = File.ReadAllBytes(Fixture("authoring/conformance/valid/minimal-authoring-receipt.json"));
        var text = Encoding.UTF8.GetString(bytes);
        var invalid = mutation switch
        {
            "bom" => new byte[] { 0xef, 0xbb, 0xbf }.Concat(bytes).ToArray(),
            "crlf" => Encoding.UTF8.GetBytes(text.Replace("\n", "\r\n", StringComparison.Ordinal)),
            "missing-lf" => bytes[..^1],
            "extra-lf" => bytes.Append((byte)'\n').ToArray(),
            "compact" => Encoding.UTF8.GetBytes(JsonNode.Parse(bytes)!.ToJsonString() + "\n"),
            "duplicate-member" => Encoding.UTF8.GetBytes(text.Replace("  \"schemaVersion\": 1,", "  \"schemaVersion\": 1,\n  \"schemaVersion\": 1,", StringComparison.Ordinal)),
            _ => bytes.Select((value, index) => index == text.IndexOf("example.owner", StringComparison.Ordinal) ? (byte)0xff : value).ToArray(),
        };
        var path = _temporary.GetPath("invalid-receipt.json");
        File.WriteAllBytes(path, invalid);

        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ValidateAuthoringReceipt(path, invalid));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("directory")]
    [InlineData("file-ancestor")]
    public void Validate_RequiresAnExistingRegularFileWithoutCreatingOrReplacingPaths(string kind)
    {
        var bytes = File.ReadAllBytes(Fixture("authoring/conformance/valid/minimal-authoring-receipt.json"));
        var path = kind switch
        {
            "directory" => _temporary.CreateDirectory("directory.json"),
            "file-ancestor" => Path.Combine(_temporary.WriteAllText("ancestor", "sentinel"), "receipt.json"),
            _ => _temporary.GetPath("missing", "receipt.json"),
        };
        var before = SnapshotFiles();

        Assert.ThrowsAny<IOException>(() => SunderRpcAuthoringDocuments.ValidateAuthoringReceipt(path, bytes));

        Assert.Equal(before, SnapshotFiles());
        Assert.False(Directory.Exists(_temporary.GetPath("missing")));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("ancestor")]
    [InlineData("dangling")]
    public void Validate_RejectsSymbolicLinksWithoutTouchingTheirTargets(string kind)
    {
        var bytes = File.ReadAllBytes(Fixture("authoring/conformance/valid/minimal-authoring-receipt.json"));
        var targetDirectory = _temporary.CreateDirectory("target");
        var target = Path.Combine(targetDirectory, "receipt.json");
        File.WriteAllBytes(target, bytes);
        var timestamp = File.GetLastWriteTimeUtc(target);
        var link = _temporary.GetPath("link");
        try
        {
            if (kind == "ancestor") Directory.CreateSymbolicLink(link, targetDirectory);
            else File.CreateSymbolicLink(link, kind == "dangling" ? _temporary.GetPath("absent.json") : target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            return;
        }
        var path = kind == "ancestor" ? Path.Combine(link, "receipt.json") : link;

        var error = Assert.ThrowsAny<IOException>(() => SunderRpcAuthoringDocuments.ValidateAuthoringReceipt(path, bytes));

        Assert.Contains("symbolic link", error.Message, StringComparison.Ordinal);
        Assert.Equal(bytes, File.ReadAllBytes(target));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(target));
        Assert.False(File.Exists(_temporary.GetPath("absent.json")));
    }

    private static SunderRpcCSharpSourceFile ProviderSource(
        string className, string key = "minimalProvider", string providerId = ProviderId, string audience = "Public")
    {
        var interfaceName = "I" + char.ToUpperInvariant(key[0]) + key[1..] + "Service";
        var servicesNamespace = key == "reexportProvider" ? "Example_Reexport_Services" : "Example_Dependency_Services";
        return new(className + ".cs", $$"""
            using System.Threading;
            using System.Threading.Tasks;
            using Sunder.Sdk.Rpc;
            using Sunder.Sdk.Services;
            using Sunder.Generated.Packages.{{servicesNamespace}};
            namespace Example.Imported;
            [SunderServiceProvider(typeof({{interfaceName}}), ProviderId = "{{providerId}}", Audience = SunderServiceAudience.{{audience}})]
            public sealed partial class {{className}} : {{interfaceName}}
            {
                public ValueTask PingAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
            }
            """);
    }

    private static SunderRpcCSharpCompilationResult Compile(SunderRpcCSharpImportPrepassResult imports, params SunderRpcCSharpSourceFile[] sources)
    {
        var compiler = new SunderRpcCSharpSourceCompiler();
        var input = new SunderRpcCSharpCompilerInput(PackageId, PackageVersion, sources, References(),
            SupplementalSources: imports.Sources, ImportedProviderSurfaces: imports.ProviderSurfaces);
        var compiled = compiler.Compile(input);
        Assert.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics));
        if (compiled.Contracts.All(contract => contract.Providers.Count == 0) && compiled.ImportedProviders.Count == 0) return compiled;
        var verified = compiler.Compile(input with
        {
            SupplementalSources = imports.Sources.Append(new SunderRpcCSharpSourceFile("authoring.g.cs",
                SunderRpcCSharpImplementationEmitter.Emit(compiled.Contracts, compiled.ImportedProviders))).ToArray(),
            RequireGeneratedMembersResolved = true,
        });
        Assert.True(verified.Success, string.Join(Environment.NewLine, verified.Diagnostics));
        return verified;
    }

    private static string[] References()
        => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(typeof(SunderServiceAttribute).Assembly.Location)
            .Append(typeof(SunderWorkerProviderRegistration).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private SunderRpcCSharpImportPrepassResult Prepare(SunderRpcContractLock contractLock)
    {
        foreach (var artifact in contractLock.PackageArtifacts.Values) ModelProjectionTestEvidence.Seed(_cacheRoot, artifact);
        var requirements = SunderRpcPlatformCatalogCache.Requirements(contractLock);
        if (requirements.Count == 0) return SunderRpcCSharpImportPrepass.Prepare(contractLock, _cacheRoot);
        var catalog = SunderRpcAuthoringJson.WriteCompact(JsonSerializer.SerializeToElement(ReadJson(Fixture("authoring/conformance/valid/platform-catalog.json"))), SunderRpcPlatformCatalogCache.MaximumBytes);
        foreach (var descriptor in SunderPlatformContractCatalog.Verify(catalog).Contracts) Cache(descriptor);
        SunderRpcPlatformCatalogCache.Publish(_cacheRoot, new Dictionary<string, byte[]> { [requirements[0].ContentIdentity] = catalog }, requirements);
        return SunderRpcCSharpImportPrepass.Prepare(contractLock, _cacheRoot);
    }

    private static byte[] Write(SunderRpcContractLock contractLock, SunderRpcCSharpCompilationResult compilation, SunderRpcCSharpImportPrepassResult imports)
        => SunderRpcAuthoringDocuments.WriteAuthoringReceipt("1.0.0", Fingerprint, contractLock, PackageId, PackageVersion, compilation, imports);

    private JsonElement AssertReceipt(byte[] bytes)
    {
        using var receipt = JsonDocument.Parse(bytes);
        using var schema = JsonDocument.Parse(File.ReadAllBytes(Fixture("authoring/authoring-receipt.schema.json")));
        Assert.True(MatchesAuthoringSchema(schema.RootElement, receipt.RootElement), "Generated receipt does not conform to authoring-receipt.schema.json.");
        Assert.Equal(schema.RootElement.GetProperty("required").EnumerateArray().Select(value => value.GetString()),
            receipt.RootElement.EnumerateObject().Select(property => property.Name));
        var path = _temporary.GetPath("validated-receipt.json");
        File.WriteAllBytes(path, bytes);
        SunderRpcAuthoringDocuments.ValidateAuthoringReceipt(path, bytes);
        Assert.DoesNotContain(_temporary.RootPath, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        return receipt.RootElement.Clone();
    }

    private static void AssertEmpty(JsonElement root, params string[] members)
    {
        foreach (var member in members) Assert.Empty(root.GetProperty(member).EnumerateArray());
    }

    private static void AssertJsonEqual(JsonNode expected, JsonElement actual)
        => Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected), actual), $"Expected {expected}, got {actual}.");

    private static void AssertRejected(Action action, string diagnostic)
        => Assert.StartsWith(diagnostic + ":", Assert.Throws<InvalidDataException>(action).Message, StringComparison.Ordinal);

    private static JsonObject LockDocument() => ReadJson(Fixture("authoring/conformance/valid/package-api-contract-lock.json"));

    private static SunderRpcContractLock ReadLock(string fileName = "package-api-contract-lock.json")
        => SunderRpcAuthoringDocuments.ReadContractLock(Fixture("authoring/conformance/valid/" + fileName));

    private SunderRpcContractLock ReadLock(JsonObject document)
    {
        var path = _temporary.GetPath(Guid.NewGuid().ToString("N") + ".lock.json");
        File.WriteAllBytes(path, Serialize(document));
        return SunderRpcAuthoringDocuments.ReadContractLock(path);
    }

    private static JsonObject ProviderLock(bool consumer = false)
    {
        var document = LockDocument();
        if (!consumer) document["packageImports"]!.AsObject().Clear();
        document["providerImports"]!["minimalProvider"] = new JsonObject { ["packageId"] = "example.dependency", ["contractId"] = ContractId };
        return document;
    }

    private static void AddReexport(JsonObject document, bool consumer, bool provider)
    {
        var artifact = document["packageArtifacts"]!["example.dependency"]!.DeepClone();
        artifact["selectedPackageVersion"] = "1.4.0";
        artifact["selectedPackageContentIdentity"] = new string('2', 64);
        artifact["contracts"]![ContractId]!["artifactSource"] = "imported";
        artifact["publicInventorySha256"] = InventorySha256("example.reexport", artifact);
        ModelProjectionTestEvidence.PinEmptySourceProjection("example.reexport", artifact);
        document["packageArtifacts"]!["example.reexport"] = artifact;
        if (consumer)
        {
            var intent = document["packageImports"]!["example.dependency"]!.DeepClone();
            intent["localName"] = "reexport";
            document["packageImports"]!["example.reexport"] = intent;
        }
        if (provider) document["providerImports"]!["reexportProvider"] = new JsonObject { ["packageId"] = "example.reexport", ["contractId"] = ContractId };
    }

    private static void SetArtifactContracts(JsonObject document, params SunderRpcContractDescriptor[] descriptors)
    {
        var artifact = document["packageArtifacts"]!["example.dependency"]!;
        var template = artifact["contracts"]![ContractId]!.DeepClone();
        var contracts = new JsonObject();
        foreach (var descriptor in descriptors.OrderBy(value => value.ContractId, StringComparer.Ordinal))
        {
            var evidence = template.DeepClone();
            evidence["resolvedContractVersion"] = descriptor.Version;
            evidence["descriptorBytes"] = descriptor.GetCanonicalUtf8().Length;
            evidence["descriptorSha256"] = descriptor.Sha256;
            evidence["abiSha256"] = descriptor.AbiSha256;
            evidence["etag"] = "\"" + descriptor.Sha256 + "\"";
            contracts[descriptor.ContractId] = evidence;
        }
        artifact["contracts"] = contracts;
        artifact["publicContractCount"] = descriptors.Length;
        artifact["publicInventorySha256"] = InventorySha256("example.dependency", artifact);
        ModelProjectionTestEvidence.PinEmptySourceProjection("example.dependency", artifact);
    }

    private static string InventorySha256(string packageId, JsonNode artifact)
    {
        // Inventory hash preimages use schema order and a domain prefix, not sorted JSON keys.
        var contracts = new JsonArray();
        foreach (var entry in artifact["contracts"]!.AsObject().OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var contract = new JsonObject { ["contractId"] = entry.Key, ["contractVersion"] = entry.Value!["resolvedContractVersion"]!.DeepClone() };
            foreach (var member in new[]
            {
                "descriptorBytes", "descriptorSha256", "abiSha256", "artifactSource", "authorityKind", "authorityId",
                "selectedAuthorityRelease", "selectedAuthorityContentIdentity",
            })
                if (entry.Value![member] is { } value) contract[member] = value.DeepClone();
            contracts.Add(contract);
        }
        var inventory = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["packageId"] = packageId,
            ["packageVersion"] = artifact["selectedPackageVersion"]!.DeepClone(),
            ["packageContentIdentity"] = artifact["selectedPackageContentIdentity"]!.DeepClone(),
            ["publicContractCount"] = artifact["publicContractCount"]!.DeepClone(),
            ["contracts"] = contracts,
        };
        return Sha256(Encoding.UTF8.GetBytes("sunder.package-public-api-inventory.v1\0"
            + inventory.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })));
    }

    private SunderRpcContractDescriptor ChangeDescriptor(Action<JsonObject> change)
    {
        var document = JsonNode.Parse(_descriptor.GetCanonicalUtf8())!.AsObject();
        change(document);
        return SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(document.ToJsonString()));
    }

    private void Cache(SunderRpcContractDescriptor descriptor)
    {
        SunderRpcContractCacheStore.InitializeRoot(_cacheRoot);
        File.WriteAllBytes(SunderRpcContractCache.GetDescriptorPath(_cacheRoot, descriptor.Sha256), descriptor.GetCanonicalUtf8());
        File.WriteAllBytes(SunderRpcContractCache.GetReceiptPath(_cacheRoot, descriptor.Sha256), Serialize(new JsonObject
        {
            ["schemaVersion"] = 1, ["contractId"] = descriptor.ContractId, ["contractVersion"] = descriptor.Version,
            ["descriptorBytes"] = descriptor.GetCanonicalUtf8().Length, ["descriptorSha256"] = descriptor.Sha256, ["abiSha256"] = descriptor.AbiSha256,
        }));
    }

    private (string Path, string Hash, DateTime Modified)[] SnapshotFiles()
        => Directory.GetFiles(_temporary.RootPath, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => (path, Sha256(File.ReadAllBytes(path)), File.GetLastWriteTimeUtc(path))).ToArray();

    private static byte[] Serialize(JsonObject document) => Encoding.UTF8.GetBytes(document.ToJsonString(JsonOptions) + "\n");
    private static bool MatchesAuthoringSchema(JsonElement schema, JsonElement instance)
        => ModelProjectionTestEvidence.MatchesAuthoringSchema(schema, instance, Fixture("authoring/rpc-model-projection.schema.json"));
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static JsonObject ReadJson(string path) => JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();

    private static string Fixture(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx"))) directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the Sunder Core repository root."), "specs/rpc/v1", relativePath);
    }

    public void Dispose() => _temporary.Dispose();
}
