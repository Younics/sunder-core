using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Package.Build.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpImportPrepassTests
{
    [Theory]
    [InlineData("x:DataType", true)]
    [InlineData("TargetType", false)]
    [InlineData("DataType", false)]
    public void TrustedImportedModelsAreAdmittedOnlyAsBindingDataTypes(string attribute, bool permitted)
    {
        var imports = PrepareMigrationImports(BorrowedDeclaration);
        var analysis = SunderPackageAuthoringCompiler.Analyze(new("example.builder", "1.0.0", [new("builder.cs", BorrowingService)], NaturalReferences(),
            SupplementalSources: imports.Sources, ImportedPackageApis: imports.PackageApis));
        Assert.True(analysis.Success, string.Join(Environment.NewLine, analysis.Compilation.Diagnostics.Concat(analysis.RolePlan.Diagnostics)));
        var path = Path.Combine(_root, "View.axaml");
        File.WriteAllText(path, $$"""
            <DataTemplate xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                          xmlns:m="using:Sunder.Generated.Packages.Example_Dependency_Models" {{attribute}}="m:Entry" />
            """);
        var compilation = analysis.Compilation.SourceCompilation!;
        var markup = SunderPackageResourcePlanner.Markup(compilation,
            SunderPackageResourcePlanner.Read(_root, ("AvaloniaXaml", [new Microsoft.Build.Utilities.TaskItem(path)])));
        var plan = SunderPackageRolePlanner.Plan(compilation,
            compilation.SyntaxTrees.Where(tree => analysis.OrdinarySourcePaths.Contains(tree.FilePath)).ToArray(),
            markupTypes: markup.Types, trustedImportedModelTypes: analysis.Compilation.TrustedImportedModelTypes);
        Assert.Equal(permitted, plan.Success);
        Assert.DoesNotContain(plan.Types, type => type.MetadataName.Contains("Example_Dependency_Models", StringComparison.Ordinal));
        if (!permitted)
        {
            var error = Assert.Single(plan.Diagnostics, error => error.Id == "package-authoring.xaml-type-invalid");
            Assert.Equal(path, error.Path); Assert.True(error.Line > 0);
        }
    }

    private const string BorrowedDeclaration = """
        using System.Threading.Tasks;
        using Sunder.Sdk.Services;
        [SunderService(SunderServiceExposure.Public)] public interface ValuesService { ValueTask<Entry> ReadAsync(); }
        public enum State
        {
            [System.Text.Json.Serialization.JsonStringEnumMemberName("draft")] Draft,
            [System.Text.Json.Serialization.JsonStringEnumMemberName("ready")] Ready,
        }
        public sealed record Entry(string Value, State State);
        """;
    private const string BorrowingService = """
        using System.Threading.Tasks;
        using Sunder.Sdk.Services;
        using Sunder.Generated.Packages.Example_Dependency_Models;
        [SunderService]
        public sealed partial class BuilderService
        {
            public ValueTask<Entry> EchoAsync(Entry value) => ValueTask.FromResult(value with { Value = value.Value + "!", State = State.Ready });
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivateServiceReusesValidatedImportedRecordAndCodecWithoutClaimingItsOwnership(bool rebased)
    {
        var imports = PrepareMigrationImports(BorrowedDeclaration);
        var analysis = SunderPackageAuthoringCompiler.Analyze(new("example.builder", "1.0.0", [new("builder.cs", BorrowingService)], NaturalReferences(),
            SupplementalSources: rebased ? imports.Sources.Select(source => source with { Path = Path.Combine(_root, "generated", source.Path) }).ToArray() : imports.Sources,
            ImportedPackageApis: imports.PackageApis));
        Assert.True(analysis.Success, string.Join(Environment.NewLine, analysis.Compilation.Diagnostics.Concat(analysis.RolePlan.Diagnostics)));
        var owned = Assert.Single(analysis.Compilation.Contracts);
        Assert.Empty(owned.SourceTypes);
        Assert.Empty(owned.ModelProjection.Models);
        Assert.NotEmpty(owned.ImportedValueLayouts);
        using var bytes = new MemoryStream(); var emission = analysis.Compilation.SourceCompilation!.Emit(bytes);
        Assert.True(emission.Success, string.Join(Environment.NewLine, emission.Diagnostics)); bytes.Position = 0;
        var load = new AssemblyLoadContext("borrowed-private-model", isCollectible: true);
        try
        {
            var assembly = load.LoadFromStream(bytes);
            var type = assembly.GetType("BuilderService", true)!;
            var instance = Activator.CreateInstance(type)!;
            var registration = (SunderWorkerProviderRegistration)type.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(instance, null)!;
            var result = await registration.Handler.InvokeUnaryAsync(null!, "builder", "echo", JsonSerializer.SerializeToElement(new { value = "borrowed", state = "draft" }));
            Assert.Equal("borrowed!", result.GetProperty("value").GetString());
            Assert.Equal("ready", result.GetProperty("state").GetString());
            Assert.Equal("Sunder.Generated.Packages.Example_Dependency_Models.Entry", type.GetMethod("EchoAsync")!.GetParameters()[0].ParameterType.FullName);
        }
        finally { load.Unload(); }
    }

    [Fact]
    public void BorrowedOptionalReferencePolicyFollowsRebasedDefinitionWithoutClaimingModelOwnership()
    {
        var declaration = BorrowedDeclaration.Replace("public sealed record Entry(string Value, State State);", """
            [SunderService(SunderServiceExposure.Public)] public interface TargetService { ValueTask<bool> ReadyAsync(); }
            public sealed record Entry(string Value, State State) {
                [SunderOptionalServiceReference]
                public required ServiceReference<Sunder.Generated.Packages.Example_Dependency_Services.ITargetService>? OptionalTarget { get; init; }
                public required ServiceReference<Sunder.Generated.Packages.Example_Dependency_Services.ITargetService>? RequiredTarget { get; init; }
            }
            """, StringComparison.Ordinal);
        var imports = PrepareMigrationImports(declaration, generateServiceInterfaces: true);
        var analysis = SunderPackageAuthoringCompiler.Analyze(new("example.builder", "1.0.0", [new("builder.cs", BorrowingService)], NaturalReferences(),
            SupplementalSources: imports.Sources, ImportedPackageApis: imports.PackageApis));
        Assert.True(analysis.Success, string.Join(Environment.NewLine, analysis.Compilation.Diagnostics.Concat(analysis.RolePlan.Diagnostics)));
        var owned = Assert.Single(analysis.Compilation.Contracts);
        Assert.Empty(owned.ModelProjection.Models);
        var policy = Assert.Single(owned.ModelProjection.OptionalServiceReferenceMembers);
        Assert.StartsWith("Imported_", policy.Key, StringComparison.Ordinal);
        Assert.True(owned.Descriptor.Definitions.ContainsKey(policy.Key));
        Assert.Equal(new[] { "optionalTarget" }, policy.Value);
        var generated = string.Join('\n', analysis.Compilation.SourceCompilation!.SyntaxTrees.Select(tree => tree.ToString()));
        Assert.Contains("RegisterOptionalServiceReferenceAsync", generated, StringComparison.Ordinal);
        Assert.Contains("PrepareOptionalServiceReferenceAsync", generated, StringComparison.Ordinal);
        Assert.Contains("RegisterServiceReferenceAsync", generated, StringComparison.Ordinal);
        Assert.Contains("PrepareServiceReferenceAsync", generated, StringComparison.Ordinal);
        using var bytes = new MemoryStream();
        var emission = analysis.Compilation.SourceCompilation.Emit(bytes);
        Assert.True(emission.Success, string.Join(Environment.NewLine, emission.Diagnostics));
    }

    [Fact]
    public void BorrowedModelInPublicContractRequiresExplicitCrossAuthorityOwnership()
    {
        var imports = PrepareMigrationImports(BorrowedDeclaration);
        var result = new SunderRpcCSharpSourceCompiler().Compile(new("example.builder", "1.0.0",
            [new("builder.cs", BorrowingService.Replace("[SunderService]", "[SunderService(SunderServiceExposure.Public)]", StringComparison.Ordinal))], NaturalReferences(),
            SupplementalSources: imports.Sources, ImportedPackageApis: imports.PackageApis));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, value => value.Id == "rpc-authoring.imported-model-ownership" && value.Path == "builder.cs");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportedModelsRequireExactGeneratedSourceNotNamesOrReferenceAssembly(bool referencedAssembly)
    {
        var imports = PrepareMigrationImports(BorrowedDeclaration);
        var models = imports.Sources.Where(source => source.Path.StartsWith("models/", StringComparison.Ordinal)).ToArray();
        var supplemental = imports.Sources.ToArray();
        var references = NaturalReferences();
        if (referencedAssembly)
        {
            var assembly = CSharpCompilation.Create("UntrustedModels", models.Select(source => CSharpSyntaxTree.ParseText(source.Text,
                new CSharpParseOptions(LanguageVersion.CSharp14), source.Path)), references.Select(path => MetadataReference.CreateFromFile(path)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var path = Path.Combine(_root, "UntrustedModels.dll");
            var emitted = assembly.Emit(path);
            Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
            references = references.Append(path).ToArray();
            supplemental = supplemental.Except(models).ToArray();
        }
        else supplemental = supplemental.Select(source => source.Path.EndsWith("/Entry.g.cs", StringComparison.Ordinal)
            ? source with { Text = source.Text.Replace("JsonPropertyName(\"value\")", "JsonPropertyName(\"forged\")", StringComparison.Ordinal) } : source).ToArray();
        var result = new SunderRpcCSharpSourceCompiler().Compile(new("example.builder", "1.0.0", [new("builder.cs", BorrowingService)], references,
            SupplementalSources: supplemental, ImportedPackageApis: imports.PackageApis));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, value => value.Message.Contains("rpc-authoring.imported-model-invalid:", StringComparison.Ordinal));
    }
}
