using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Authoring;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Services;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class ImplementationFirstRpcBuildTests
{
    private const string TypedUseProvider = """
        using System.Threading;
        using System.Threading.Tasks;
        using Sunder.Sdk.Services;
        using Sunder.Generated.Packages.Example_Models_Models;
        using Sunder.Generated.Packages.Example_Models_Services;
        [SunderRuntimeUse(typeof(ITargetService), Actions = ["discover"])]
        [SunderServiceProvider(typeof(IHolderService), ProviderId = "example.consumer.holder")]
        public sealed partial class HolderProvider : IHolderService
        {
            public ValueTask<bool> HandleAsync(HolderContext request, CancellationToken cancellationToken = default)
                => ValueTask.FromResult(true);
        }
        """;

    [Fact]
    public async Task TypedRuntimeUses_PreserveOwnPolicyAndMaterializeOnlyExplicitCatalogs()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-typed-runtime-use");
        var (selected, imports) = await TypedUseImports(scope);
        var source = TypedUseProvider.Replace("[SunderRuntimeUse(typeof(ITargetService)", "[SunderService, SunderRuntimeUse(Actions = [\"discover\"])]\n[SunderRuntimeUse(typeof(ILeafService), Actions = [\"discover\"])]\n[SunderRuntimeUse(typeof(ITargetService)", StringComparison.Ordinal);
        var analysis = SunderPackageAuthoringCompiler.Analyze(TypedUseInput(imports, source));
        Assert.True(analysis.Success, string.Join(Environment.NewLine, analysis.Compilation.Diagnostics.Concat(analysis.RolePlan.Diagnostics)));
        Assert.Equal(2, analysis.Compilation.ImportedUses.Count);
        Assert.Equal(["discover"], Assert.Single(analysis.Compilation.Contracts).RuntimeUse!.Actions);
        Assert.Empty(analysis.Compilation.ImportedPackageApis);
        var composition = SunderPackageCompositionEmitter.EmitWorker(analysis);
        Assert.Contains("ITargetService>", composition.Text, StringComparison.Ordinal);
        Assert.Contains("ILeafService>", composition.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("IUnrelatedService", composition.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("IPackageApi", composition.Text, StringComparison.Ordinal);
        var compilation = analysis.Compilation.SourceCompilation!.AddSyntaxTrees(CSharpSyntaxTree.ParseText(composition.Text,
            new CSharpParseOptions(LanguageVersion.CSharp14), composition.Path))
            .AddSyntaxTrees(SunderPackageCompositionEmitter.EmitWorkerClients(analysis, "example.consumer", "1.0.0")
                .Select(file => CSharpSyntaxTree.ParseText(file.Text, new CSharpParseOptions(LanguageVersion.CSharp14), file.Path)));
        using var bytes = new MemoryStream();
        var emitted = compilation.Emit(bytes);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        Assert.Empty(selected.PackageImports);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedRuntimeUses_CoalesceIdenticalDeclarationsAndRejectCrossProviderPolicyConflicts(bool conflict)
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-typed-runtime-use-coalescing");
        var (_, imports) = await TypedUseImports(scope);
        var second = TypedUseProvider.Replace("class HolderProvider", "class SecondProvider", StringComparison.Ordinal)
            .Replace("example.consumer.holder", "example.consumer.second", StringComparison.Ordinal);
        if (conflict) second = second.Replace("[\"discover\"]", "[\"invoke\"]", StringComparison.Ordinal);
        var input = TypedUseInput(imports, TypedUseProvider);
        var result = new SunderRpcCSharpSourceCompiler().Compile(input with { Sources = [.. input.Sources, new("second.cs", second)] });
        Assert.Equal(!conflict, result.Success);
        if (conflict) Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "rpc-authoring.runtime-use-invalid");
        else { Assert.Single(result.ImportedUses); Assert.Equal(2, result.ImportedProviders.Count); }
    }

    [Theory]
    [InlineData("lookalike")]
    [InlineData("unselected")]
    [InlineData("private")]
    [InlineData("foreign")]
    [InlineData("modified-generated-interface")]
    [InlineData("duplicate")]
    [InlineData("conflicting")]
    [InlineData("empty-actions")]
    [InlineData("unknown-action")]
    [InlineData("inapplicable-action")]
    [InlineData("duplicate-action")]
    [InlineData("null-target")]
    public async Task TypedRuntimeUses_RejectUntrustedTargetsAndConflictingOrInvalidIntents(string fault)
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-typed-runtime-use-invalid");
        var (_, imports) = await TypedUseImports(scope);
        var source = TypedUseProvider;
        var input = TypedUseInput(imports, source);
        switch (fault)
        {
            case "lookalike":
                source = source.Replace("typeof(ITargetService)", "typeof(Lookalike.ITargetService)", StringComparison.Ordinal)
                    + "\nnamespace Lookalike { public interface ITargetService { } }";
                break;
            case "unselected":
                source = source.Replace("typeof(ITargetService)", "typeof(IUnrelatedService)", StringComparison.Ordinal)
                    + "\nnamespace Sunder.Generated.Packages.Example_Models_Services { public interface IUnrelatedService { } }";
                break;
            case "private":
                source = source.Replace("typeof(ITargetService)", "typeof(PrivateService)", StringComparison.Ordinal)
                    + "\n[SunderService] public interface PrivateService { ValueTask<bool> ReadyAsync(); }";
                break;
            case "foreign":
                var path = Path.Combine(scope.RootPath, "Foreign.dll");
                var foreign = CSharpCompilation.Create("Foreign", [CSharpSyntaxTree.ParseText("namespace Sunder.Generated.Packages.Example_Models_Services { public interface ITargetService { } }")],
                    input.ReferencePaths.Select(path => MetadataReference.CreateFromFile(path)), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
                Assert.True(foreign.Emit(path).Success);
                input = input with { ReferencePaths = input.ReferencePaths.Append(path).ToArray(),
                    SupplementalSources = imports.Sources.Where(file => file.Path != "natural-services/example.models.target.g.cs").ToArray() };
                break;
            case "modified-generated-interface":
                input = input with { SupplementalSources = imports.Sources.Select(file => file.Path == "natural-services/example.models.target.g.cs"
                    ? file with { Text = file.Text.Replace("ITargetService {", "ITargetService { void Counterfeit();", StringComparison.Ordinal) } : file).ToArray() };
                break;
            case "duplicate": case "conflicting":
                source = source.Replace("[SunderServiceProvider", "[SunderRuntimeUse(typeof(ITargetService), Actions = [\"" + (fault == "duplicate" ? "discover" : "invoke") + "\"])]\n[SunderServiceProvider", StringComparison.Ordinal);
                break;
            case "empty-actions": source = source.Replace("[\"discover\"]", "[]", StringComparison.Ordinal); break;
            case "unknown-action": source = source.Replace("[\"discover\"]", "[\"publish\"]", StringComparison.Ordinal); break;
            case "inapplicable-action": source = source.Replace("[\"discover\"]", "[\"subscribe\"]", StringComparison.Ordinal); break;
            case "duplicate-action": source = source.Replace("[\"discover\"]", "[\"discover\", \"discover\"]", StringComparison.Ordinal); break;
            case "null-target": source = source.Replace("typeof(ITargetService)", "null!", StringComparison.Ordinal); break;
        }
        var result = new SunderRpcCSharpSourceCompiler().Compile(input with { Sources = [new("provider.cs", source)] });
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "rpc-authoring.runtime-use-invalid");
    }

    [Fact]
    public async Task TypedRuntimeUses_ReceiptRequiresExactSourceIntentAndDoesNotAuthorizeInvocation()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-typed-runtime-use-receipt");
        var (selected, imports) = await TypedUseImports(scope);
        var compiled = SunderRpcCSharpOwnedClientPrepass.Compile(new(), TypedUseInput(imports, TypedUseProvider)).Compilation;
        Assert.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics));
        var receiptBytes = SunderRpcAuthoringDocuments.WriteAuthoringReceipt("1.1.0", new string('a', 64), selected, "example.consumer", "1.0.0", compiled, imports);
        var projection = SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(receiptBytes, selected.Sha256, "example.consumer", "1.0.0");
        var use = Assert.Single(projection.Manifest.UsesContracts!)!;
        Assert.False(use.Required); Assert.Equal(["discover"], use.Actions);
        var exportPath = scope.GetPath("package-export.v1.json");
        var evidence = compiled.ImportedUses.Select(intent => intent.Evidence).ToArray();
        await File.WriteAllBytesAsync(exportPath, SunderRpcAuthoringDocuments.WritePackageExport("1.1.0", "consumer", "Debug", "net10.0", new string('a', 64),
            selected.Sha256, "example.consumer", "1.0.0", compiled.Contracts, evidence));
        var export = SunderRpcAuthoringDocuments.ReadPackageProjectExport(exportPath, "1.1.0", "consumer", "Debug", "net10.0", "example.consumer", "1.0.0");
        var schemaRoot = Path.Combine(FindRepositoryRoot(), "specs/rpc/v1/authoring");
        foreach (var (name, json) in new[] { ("authoring-receipt.schema.json", receiptBytes), ("package-project-export.schema.json", await File.ReadAllBytesAsync(exportPath)) })
        {
            using var schema = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(schemaRoot, name)));
            using var instance = JsonDocument.Parse(json);
            Assert.True(ModelProjectionTestEvidence.MatchesAuthoringSchema(schema.RootElement, instance.RootElement, Path.Combine(schemaRoot, "rpc-model-projection.schema.json")), name);
        }
        Assert.Equal(evidence.Select(SunderRpcAuthoringDocuments.RoleUseIdentity), export.RoleUses.Select(SunderRpcAuthoringDocuments.RoleUseIdentity));
        Assert.Equal(export.Sha256, SunderRpcAuthoringDocuments.ValidatePackageProjectExport(exportPath, "1.1.0", "consumer", "Debug", "net10.0",
            "example.consumer", "1.0.0", selected.Sha256, compiled.Contracts, evidence));
        Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ValidatePackageProjectExport(exportPath, "1.1.0", "consumer", "Debug", "net10.0",
            "example.consumer", "1.0.0", selected.Sha256, compiled.Contracts));
        var appBytes = SunderRpcAuthoringDocuments.WriteAuthoringReceipt("1.1.0", new string('a', 64), selected, "example.consumer", "1.0.0", compiled, imports, "app");
        var app = SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(appBytes, selected.Sha256, "example.consumer", "1.0.0");
        Assert.Empty(app.RoleUses); Assert.Empty(app.Manifest.UsesContracts!); Assert.Empty(app.Manifest.ContractBundles!);
        foreach (var mutation in new[] { "omit-intent", "widen-actions", "required", "tuple", "service", "omit-use" })
        {
            var document = JsonNode.Parse(receiptBytes)!;
            switch (mutation)
            {
                case "omit-intent": document.AsObject().Remove("roleUses"); break;
                case "widen-actions": document["uses"]![0]!["actions"] = new JsonArray("discover", "invoke"); break;
                case "required": document["uses"]![0]!["required"] = true; break;
                case "tuple": document["roleUses"]![0]!["descriptorSha256"] = new string('b', 64); break;
                case "service": document["roleUses"]![0]!["serviceType"] = "Lookalike.ITargetService"; break;
                case "omit-use": document["uses"] = new JsonArray(); break;
            }
            Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(
                SunderRpcAuthoringDocuments.WriteDeterministicDocument(JsonSerializer.SerializeToElement(document)), selected.Sha256, "example.consumer", "1.0.0"));
        }
        var invoke = new SunderRpcCSharpSourceCompiler().Compile(TypedUseInput(imports, TypedUseProvider.Replace("[\"discover\"]", "[\"invoke\"]", StringComparison.Ordinal)));
        Assert.True(invoke.Success, string.Join(Environment.NewLine, invoke.Diagnostics));
        Assert.Contains("service-reference-use-missing", Assert.Throws<InvalidDataException>(() =>
            SunderRpcAuthoringDocuments.ValidateImportedProviderReferenceUses(selected, invoke)).Message, StringComparison.Ordinal);
        var callable = new SunderRpcCSharpSourceCompiler().Compile(TypedUseInput(imports, TypedUseProvider.Replace("[\"discover\"]", "[\"discover\", \"invoke\"]", StringComparison.Ordinal)));
        Assert.True(callable.Success, string.Join(Environment.NewLine, callable.Diagnostics));
        Assert.Contains("example.models.leaf", Assert.Throws<InvalidDataException>(() =>
            SunderRpcAuthoringDocuments.ValidateImportedProviderReferenceUses(selected, callable)).Message, StringComparison.Ordinal);
    }

    private static SunderRpcCSharpCompilerInput TypedUseInput(SunderRpcCSharpImportPrepassResult imports, string source)
        => new("example.consumer", "1.0.0", [new("provider.cs", source)], ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat(new[] { typeof(SunderServiceAttribute).Assembly.Location, typeof(SunderWorkerProviderRegistration).Assembly.Location }).Distinct(StringComparer.Ordinal).ToArray(),
            SupplementalSources: imports.Sources, ImportedProviderSurfaces: imports.ProviderSurfaces, GenerateServiceInterfaces: true);

    private static async Task<(SunderRpcContractLock Lock, SunderRpcCSharpImportPrepassResult Imports)> TypedUseImports(TemporaryDirectoryScope scope)
    {
        var root = scope.CreateDirectory("consumer"); var cache = scope.CreateDirectory("cache");
        var candidate = await WriteProjectedCandidateAsync(scope.CreateDirectory("candidate"), "2.0.0", ReferenceClosureDeclaration);
        _ = await CreateProjectedConsumerAsync(root, cache, "", app: false);
        using var http = new HttpClient(new OfflineProjectionHandler());
        var selected = (await new SunderRpcPackageApiCommands(new(http)).ExecuteAsync(Path.Combine(root, SunderRpcAuthoringDocuments.ContractLockFileName), cache,
            new("Implement", "example.models", "example.models.holder", ">=1.0.0 <3.0.0", ProviderKey: "holder", Candidate: candidate))).ContractLock;
        return (selected, SunderRpcCSharpImportPrepass.Prepare(selected, cache));
    }
}
