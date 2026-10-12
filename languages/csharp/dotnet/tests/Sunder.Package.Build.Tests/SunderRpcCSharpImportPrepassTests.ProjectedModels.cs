using System.Text.Json;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpImportPrepassTests
{
    [Fact]
    public void SharedAuthorityModelsAreBatchedAcrossReviewedReexportGroups()
    {
        const string source = """
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            [SunderService(SunderServiceExposure.Public)] public interface FirstService { ValueTask<Message> ReadAsync(); }
            [SunderService(SunderServiceExposure.Public)] public interface SecondService { ValueTask<Message> EchoAsync(Message value); }
            public sealed record Message(string Text);
            """;
        var compiled = new SunderRpcCSharpSourceCompiler().Compile(new("example.dependency", "1.2.3", [new("source.cs", source)], NaturalReferences()));
        Assert.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics));
        var original = _lock.PackageArtifacts["example.dependency"];
        var template = original.Contracts[_descriptor.ContractId];
        var entries = new Dictionary<string, SunderRpcLockedArtifactContract>(StringComparer.Ordinal);
        foreach (var contract in compiled.Contracts)
        {
            var descriptor = contract.Descriptor;
            File.WriteAllBytes(SunderRpcContractCache.GetDescriptorPath(_root, descriptor.Sha256), descriptor.GetCanonicalUtf8());
            File.WriteAllBytes(SunderRpcContractCache.GetReceiptPath(_root, descriptor.Sha256), SunderRpcAuthoringDocuments.WriteDeterministicDocument(JsonSerializer.SerializeToElement(new
            {
                schemaVersion = 1, contractId = descriptor.ContractId, contractVersion = descriptor.Version,
                descriptorBytes = descriptor.GetCanonicalUtf8().Length, descriptorSha256 = descriptor.Sha256, abiSha256 = descriptor.AbiSha256,
            })));
            entries.Add(descriptor.ContractId, template with
            {
                ContractId = descriptor.ContractId, ResolvedContractVersion = descriptor.Version, DescriptorBytes = descriptor.GetCanonicalUtf8().Length,
                DescriptorSha256 = descriptor.Sha256, AbiSha256 = descriptor.AbiSha256, Etag = "\"" + descriptor.Sha256 + "\"",
            });
        }
        var projections = compiled.Contracts.Select(SunderRpcCSharpModelProjection.Entry).ToArray();
        var authority = SeedProjectedArtifact(original with { Contracts = entries, PublicContractCount = entries.Count }, projections);
        var reexport = SeedProjectedArtifact(authority with
        {
            PackageId = "example.reexport", SelectedPackageContentIdentity = new string('2', 64),
            Contracts = entries.ToDictionary(pair => pair.Key, pair => pair.Value with { ArtifactSource = "imported" }, StringComparer.Ordinal),
        }, projections);
        var selection = _lock.PackageImports[authority.PackageId] with { RequiredContracts = entries.Keys.Order(StringComparer.Ordinal).ToArray() };
        var selected = _lock with
        {
            PackageArtifacts = new Dictionary<string, SunderRpcLockedPackageArtifact> { [authority.PackageId] = authority, [reexport.PackageId] = reexport },
            PackageImports = new Dictionary<string, SunderRpcLockedPackageImport>
            {
                [authority.PackageId] = selection,
                [reexport.PackageId] = selection with { PackageId = reexport.PackageId, LocalName = "reexport" },
            },
        };
        var imports = SunderRpcCSharpImportPrepass.Prepare(selected, _root);
        Assert.Single(imports.Sources, file => file.Path == "models/example.dependency/Message.g.cs");
        Assert.DoesNotContain(imports.Sources, file => file.Path.StartsWith("models/example.reexport/", StringComparison.Ordinal));
        Assert.Equal(2, imports.Contracts.Count);
        Assert.Equal(imports.PackageApis[0].Services, imports.PackageApis[1].Services);
        Assert.Equal(imports.Sources, SunderRpcCSharpImportPrepass.Prepare(selected with
        {
            PackageArtifacts = selected.PackageArtifacts.Reverse().ToDictionary(),
            PackageImports = selected.PackageImports.Reverse().ToDictionary(),
        }, _root).Sources);
        var consumer = new SunderRpcCSharpSourceCompiler().Compile(new("example.consumer", "1.0.0", [], References(), SupplementalSources: imports.Sources));
        Assert.True(consumer.Success, string.Join(Environment.NewLine, consumer.Diagnostics));
    }
}
