using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcModelProjectionEvidenceTests
{
    private static readonly string Fingerprint = new('a', 64);
    private const string Source = """
        using System.Threading.Tasks;
        using Sunder.Sdk.Services;
        [SunderService(SunderServiceExposure.Public)]
        public sealed partial class FirstService { public ValueTask<SharedValue> ReadAsync() => throw new System.NotImplementedException(); }
        [SunderService(SunderServiceExposure.Public)]
        public sealed partial class SecondService { public ValueTask<SharedValue> ReadAsync() => throw new System.NotImplementedException(); }
        [SunderService(SunderServiceExposure.Package)]
        public sealed partial class PrivateService { public ValueTask<PrivateValue> ReadAsync() => throw new System.NotImplementedException(); }
        public sealed record SharedValue { public required string Text { get; init; } }
        public sealed record PrivateValue { public required string Secret { get; init; } }
        """;

    [Fact]
    public void SourceProjection_RoundtripsExportsAndReceiptsAndFiltersPrivateSidecar()
    {
        using var temporary = TemporaryDirectoryScope.Create("sunder-model-projection-evidence");
        var compilation = Compile();
        var contractLock = EmptyLock();
        var imports = SunderRpcCSharpImportPrepass.Prepare(contractLock, temporary.GetPath("cache"));
        var exportBytes = SunderRpcAuthoringDocuments.WritePackageExport("1.0.0", "Fixture", "Debug", "net10.0", Fingerprint,
            contractLock.Sha256, "example.projection", "1.0.0", compilation.Contracts);
        var exportPath = temporary.GetPath(SunderRpcAuthoringDocuments.PackageExportFileName);
        File.WriteAllBytes(exportPath, exportBytes);
        var exported = SunderRpcAuthoringDocuments.ReadPackageProjectExport(exportPath, "1.0.0", "Fixture", "Debug", "net10.0", "example.projection", "1.0.0");
        foreach (var contract in compilation.Contracts)
        {
            Assert.NotEmpty(contract.ModelProjection.Models);
            Assert.Equal(contract.GeneratedLogicalName, exported.Contracts.Single(value => value.ContractKey == contract.ContractKey).GeneratedLogicalName);
            Assert.True(SunderRpcAuthoringDocuments.SameModelProjection(contract.ModelProjection,
                exported.Contracts.Single(value => value.ContractKey == contract.ContractKey).ModelProjection));
        }
        var publicModels = compilation.Contracts.Where(contract => contract.Exposure == SunderServiceExposure.Public)
            .SelectMany(contract => contract.ModelProjection.Models.Values).Distinct(StringComparer.Ordinal).ToArray();
        Assert.Equal("SharedValue", Assert.Single(publicModels));
        var receipt = SunderRpcAuthoringDocuments.WriteAuthoringReceipt("1.0.0", Fingerprint, contractLock, "example.projection", "1.0.0", compilation, imports);
        var projected = SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(receipt, contractLock.Sha256, "example.projection", "1.0.0");
        Assert.Equal(3, projected.ModelProjections.Count);
        var staleNamespace = JsonNode.Parse(receipt)!;
        staleNamespace["generatedBindings"]![0]!["logicalName"] = "Sunder.Generated.Rpc.First";
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(
            SunderRpcAuthoringDocuments.WriteDeterministicDocument(JsonSerializer.SerializeToElement(staleNamespace)), contractLock.Sha256, "example.projection", "1.0.0"));
        var sidecarBytes = SunderRpcAuthoringDocuments.PublicModelProjectionBytes(projected);
        var sidecar = SunderRpcModelProjectionDocument.Parse(sidecarBytes);
        Assert.Equal(2, sidecar.Contracts.Count);
        Assert.DoesNotContain("PrivateValue", Encoding.UTF8.GetString(sidecarBytes), StringComparison.Ordinal);
        Assert.DoesNotContain(compilation.Contracts.Single(contract => contract.Exposure == SunderServiceExposure.Package).Descriptor.ContractId,
            Encoding.UTF8.GetString(sidecarBytes), StringComparison.Ordinal);

        var privateOnly = compilation with { Contracts = compilation.Contracts.Where(contract => contract.Exposure == SunderServiceExposure.Package).ToArray() };
        var privateReceipt = SunderRpcAuthoringDocuments.WriteAuthoringReceipt("1.0.0", Fingerprint, contractLock, "example.projection", "1.0.0", privateOnly, imports);
        Assert.Empty(SunderRpcModelProjectionDocument.Parse(SunderRpcAuthoringDocuments.PublicModelProjectionBytes(
            SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(privateReceipt, contractLock.Sha256, "example.projection", "1.0.0"))).Contracts);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("definition")]
    public void Receipt_RejectsMissingNullOrUnlinkedProjection(string change)
    {
        using var temporary = TemporaryDirectoryScope.Create("sunder-model-projection-invalid");
        var contractLock = EmptyLock();
        var bytes = SunderRpcAuthoringDocuments.WriteAuthoringReceipt("1.0.0", Fingerprint, contractLock, "example.projection", "1.0.0",
            Compile(), SunderRpcCSharpImportPrepass.Prepare(contractLock, temporary.GetPath("cache")));
        var document = JsonNode.Parse(bytes)!;
        var contract = document["contracts"]![0]!.AsObject();
        if (change == "missing") contract.Remove("modelProjection");
        else if (change == "null") contract["modelProjection"] = null;
        else contract["modelProjection"]!["models"] = new JsonObject { ["Absent"] = "Absent" };
        // Deliberately malformed reader input must not be rejected by the valid-document writer first.
        var mutated = Encoding.UTF8.GetBytes(document.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n");
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(mutated, contractLock.Sha256, "example.projection", "1.0.0"));
    }

    [Fact]
    public void ExactRidAgreement_RejectsChangedSourceProjectionWithIdenticalDescriptorAndAbi()
    {
        using var temporary = TemporaryDirectoryScope.Create("sunder-model-projection-rid");
        var compilation = Compile();
        var contractLock = EmptyLock();
        var path = temporary.GetPath(SunderRpcAuthoringDocuments.PackageExportFileName);
        File.WriteAllBytes(path, SunderRpcAuthoringDocuments.WritePackageExport("1.0.0", "Fixture", "Debug", "net10.0", Fingerprint,
            contractLock.Sha256, "example.projection", "1.0.0", compilation.Contracts));
        var changed = compilation.Contracts.Select(contract => contract with { ModelProjection = SunderRpcModelProjection.Empty }).ToArray();
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ValidatePackageProjectExport(path, "1.0.0", "Fixture", "Debug", "net10.0",
            "example.projection", "1.0.0", contractLock.Sha256, changed));
    }

    [Fact]
    public void Aggregate_UnionsRoleSubsetsAndRejectsConflictingProjectionWithoutPrivateEntries()
    {
        var compilation = Compile();
        var entries = compilation.Contracts.Where(contract => contract.Exposure == SunderServiceExposure.Public)
            .Select(contract => new SunderRpcModelProjectionContract(contract.Descriptor.ContractId, contract.Descriptor.Version,
                contract.Descriptor.Sha256, contract.Descriptor.AbiSha256, contract.ModelProjection)).ToArray();
        var manifest = new SunderPackageManifest
        {
            Id = "example.projection", Version = "1.0.0",
            ContractBundles = entries.Select(entry => new SunderPackageContractBundleManifest
            {
                ContractId = entry.ContractId, Version = entry.ContractVersion, DescriptorSha256 = entry.DescriptorSha256,
                AbiSha256 = entry.AbiSha256, Exposure = "public", Source = "owned", AuthorityKind = "package", AuthorityId = "example.projection",
            }).ToArray(),
        };
        var first = new SunderRpcModelProjectionDocument(1, manifest.Id, manifest.Version, [entries[0]]);
        var second = new SunderRpcModelProjectionDocument(1, manifest.Id, manifest.Version, entries);
        Assert.Equal(2, AggregateSunderPackageTask.MergeModelProjections(manifest, [first, second]).Contracts.Count);
        var conflict = new SunderRpcModelProjectionDocument(1, manifest.Id, manifest.Version,
            [new(entries[0].ContractId, entries[0].ContractVersion, entries[0].DescriptorSha256, entries[0].AbiSha256, SunderRpcModelProjection.Empty)]);
        Assert.Throws<InvalidDataException>(() => AggregateSunderPackageTask.MergeModelProjections(manifest, [second, conflict]));
    }

    [Fact]
    public void ServiceReferenceDataDependency_RequiresReviewedRoleUseAndPublicSidecarTarget()
    {
        var contracts = Compile().Contracts.Where(contract => contract.Exposure == SunderServiceExposure.Public).ToArray();
        var producer = contracts[0]; var target = contracts[1];
        var definition = Assert.Single(producer.ModelProjection.Models).Key;
        var descriptorJson = JsonNode.Parse(producer.Descriptor.GetCanonicalUtf8())!;
        descriptorJson["$defs"]![definition] = SunderRpcServiceReferenceSchema.Create();
        var descriptor = SunderRpcContractDescriptor.Parse(JsonSerializer.SerializeToUtf8Bytes(descriptorJson));
        var projection = new SunderRpcModelProjection(new Dictionary<string, string>(), new Dictionary<string, SunderRpcServiceReferenceProjection>
        {
            [definition] = new(target.Descriptor.ContractId, Assert.Single(target.Descriptor.Services).ServiceId),
        });
        var sourceExport = new SunderRpcPackageProjectExportContract(producer.ContractKey, producer.Exposure, producer.GeneratedLogicalName,
            descriptor, projection) { AppUse = new(false, SunderRpcAuthoringDocuments.ReceiptActions(descriptor).ToArray()) };
        var targetExport = new SunderRpcPackageProjectExportContract(target.ContractKey, target.Exposure, target.GeneratedLogicalName,
            target.Descriptor, target.ModelProjection);
        var contractLock = EmptyLock();
        SunderRpcAuthoringReceiptProjection Read(SunderRpcPackageProjectExportContract selectedTarget)
            => SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(SunderRpcAuthoringDocuments.WriteProjectImportReceipt(
                "1.0.0", Fingerprint, contractLock.Sha256, "example.projection", "1.0.0", [sourceExport, selectedTarget]),
                contractLock.Sha256, "example.projection", "1.0.0");
        Assert.StartsWith("rpc-authoring.service-reference-use-missing:", Assert.Throws<InvalidDataException>(() => Read(targetExport)).Message);
        targetExport = targetExport with { AppUse = new(false, SunderRpcAuthoringDocuments.ReceiptActions(target.Descriptor).ToArray()) };
        Assert.Equal(2, SunderRpcModelProjectionDocument.Parse(SunderRpcAuthoringDocuments.PublicModelProjectionBytes(Read(targetExport))).Contracts.Count);
        var privateTarget = Read(targetExport with { Exposure = SunderServiceExposure.Package });
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.PublicModelProjectionBytes(privateTarget));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("rename")]
    [InlineData("linked-add")]
    [InlineData("linked-remove")]
    [InlineData("evaluated-add")]
    public void InputSnapshot_RejectsChangedAuthoredOrLinkedMembership(string change)
    {
        using var temporary = TemporaryDirectoryScope.Create("sunder-source-membership");
        var project = temporary.CreateDirectory("project");
        var linked = temporary.CreateDirectory("linked");
        var source = Path.Combine(project, "Source.cs"); File.WriteAllText(source, "class Source {}\n");
        var link = Path.Combine(linked, "Shared.cs"); File.WriteAllText(link, "class Shared {}\n");
        var paths = new List<string> { source, link };
        var snapshot = new SunderRpcInputSnapshot();
        snapshot.CaptureSourceMembership(project, () => paths, [Path.Combine(project, "obj")]);
        snapshot.Read(source, 1024, "source"); snapshot.Read(link, 1024, "linked source"); snapshot.Verify();
        if (change == "add") File.WriteAllText(Path.Combine(project, "Added.cs"), "class Added {}\n");
        else if (change == "remove") File.Delete(source);
        else if (change == "rename") File.Move(source, Path.Combine(project, "Renamed.cs"));
        else if (change == "linked-add") File.WriteAllText(Path.Combine(linked, "Added.cs"), "class Added {}\n");
        else if (change == "linked-remove") File.Delete(link);
        else paths.Add(Path.Combine(project, "Evaluated.cs"));
        Assert.Throws<IOException>(snapshot.Verify);
    }

    [Fact]
    public void InputFingerprint_BindsNamesAndBytesInsteadOfOnlySourceContentMultiset()
    {
        var baseline = new SunderRpcOwnedPackageBaseline("example.projection", new Dictionary<string, SunderRpcOwnedContractBaseline>(), [], Fingerprint);
        string Compute(string path, string text) => SunderRpcAuthoringDocuments.ComputeInputFingerprint("example.projection", "1.0.0", "Fixture", "Debug", "net10.0", true,
            [], [new(path, text)], [], baseline, EmptyLock());
        Assert.NotEqual(Compute("Source.cs", "class Source {}\n"), Compute("Renamed.cs", "class Source {}\n"));
        Assert.NotEqual(Compute("Source.cs", "class Source {}\n"), Compute("Source.cs", "class Source {}\r\n"));
    }

    private static SunderRpcCSharpCompilationResult Compile(string source = Source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(SunderServiceAttribute).Assembly.Location).Distinct(StringComparer.Ordinal).ToArray();
        var compiled = new SunderRpcCSharpSourceCompiler().Compile(new("example.projection", "1.0.0", [new("Source.cs", source)], references));
        Assert.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics));
        return compiled;
    }

    private static SunderRpcContractLock EmptyLock() => SunderRpcAuthoringDocuments.ParseContractLock(
        "{\n  \"lockVersion\": 1,\n  \"packageArtifacts\": {},\n  \"packageImports\": {},\n  \"providerImports\": {},\n  \"platformImports\": {}\n}\n"u8.ToArray());
}
