using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Build.Tasks;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class ServiceApiEditorGeneratorTests
{
    [Fact]
    public void PortableAnalyzerReferencesOnlyCompilerAndNetstandardContracts()
    {
        using var stream = File.OpenRead(AnalyzerPath);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var references = metadata.AssemblyReferences.Select(metadata.GetAssemblyReference).ToArray();
        Assert.Equal(new Version(2, 0, 0, 0), Assert.Single(references, value => metadata.GetString(value.Name) == "netstandard").Version);
        Assert.All(references, value => Assert.Contains(metadata.GetString(value.Name), new[]
        {
            "netstandard", "Microsoft.CodeAnalysis", "Microsoft.CodeAnalysis.CSharp", "System.Collections.Immutable",
        }));
        // Build tasks may use newer Roslyn, but an editor analyzer must still load
        // in the minimum supported SDK's compiler without CS9057 or missing contracts.
        foreach (var name in new[] { "Microsoft.CodeAnalysis", "Microsoft.CodeAnalysis.CSharp" })
        {
            Assert.Equal(new Version(5, 0, 0, 0),
                Assert.Single(references, value => metadata.GetString(value.Name) == name).Version);
        }
        Assert.Equal(new Version(9, 0, 0, 0),
            Assert.Single(references, value => metadata.GetString(value.Name) == "System.Collections.Immutable").Version);
    }

    [Fact]
    public void LiveApiMatchesBuildCompilerForContextDefaultsAndStreaming()
    {
        var source = Source;
        var input = Compilation(source);
        var driver = CreateDriver();
        driver.RunGeneratorsAndUpdateCompilation(input, out var live, out var diagnostics);
        Assert.Empty(diagnostics);
        Assert.Empty(live.GetDiagnostics().Where(value => value.Severity == DiagnosticSeverity.Error));
        var build = SunderRpcCSharpOwnedClientPrepass.Compile(new SunderRpcCSharpSourceCompiler(),
            new("example.editor", "1.0.0", [new("service.cs", source)], ReferencePaths, AssemblyName: "EditorPackage", GenerateServiceInterfaces: true));
        Assert.True(build.Compilation.Success, string.Join(Environment.NewLine, build.Compilation.Diagnostics.Select(value => value.Message)));
        Assert.Equal(Signatures(build.Compilation.SourceCompilation!), Signatures(live));
        var method = Assert.Single(live.GetTypeByMetadataName(ApiName)!.GetMembers("ReadAsync").OfType<IMethodSymbol>());
        var liveDocumentation = method.GetDocumentationCommentXml();
        var buildMethod = Assert.Single(build.Compilation.SourceCompilation!.GetTypeByMetadataName(ApiName)!.GetMembers("ReadAsync"));
        Assert.Equal(liveDocumentation, buildMethod.GetDocumentationCommentXml());
        Assert.Contains("Reads a note.", liveDocumentation);
        Assert.Contains("name=\"request\"", liveDocumentation);
        Assert.Contains("name=\"token\"", liveDocumentation);
        Assert.DoesNotContain("name=\"context\"", liveDocumentation);
        Assert.DoesNotContain("<include", liveDocumentation);
        Assert.DoesNotContain("<inheritdoc", liveDocumentation);
        Assert.Contains("Uses context metadata.", liveDocumentation);
        var editedComments = SunderRpcCSharpOwnedClientPrepass.Compile(new SunderRpcCSharpSourceCompiler(),
            new("example.editor", "1.0.0", [new("service.cs", source.Replace("Reads a note.", "Reads the current note.", StringComparison.Ordinal))],
                ReferencePaths, AssemblyName: "EditorPackage", GenerateServiceInterfaces: true));
        Assert.True(editedComments.Compilation.Success, string.Join(Environment.NewLine, editedComments.Compilation.Diagnostics));
        var originalDescriptor = Assert.Single(build.Compilation.Contracts).Descriptor;
        var editedDescriptor = Assert.Single(editedComments.Compilation.Contracts).Descriptor;
        Assert.Equal(originalDescriptor.GetCanonicalUtf8(), editedDescriptor.GetCanonicalUtf8());
        Assert.Equal(originalDescriptor.AbiSha256, editedDescriptor.AbiSha256);
        Assert.Null(live.GetTypeByMetadataName("Example.INotesService"));
        Assert.Equal(2, method.Parameters.Length);
        Assert.True(method.Parameters[1].HasExplicitDefaultValue);
        var sourceMethod = Assert.Single(input.GetTypeByMetadataName("Example.NotesService")!.GetMembers("ReadAsync"));
        Assert.Equal(sourceMethod.Locations[0].GetLineSpan().StartLinePosition.Line, method.Locations[0].GetMappedLineSpan().StartLinePosition.Line);
        Assert.Equal("service.cs", method.Locations[0].GetMappedLineSpan().Path);
    }

    [Fact]
    public void RetiredMarkerIsUnsupportedAndDoesNotGenerateAnEditorInterface()
    {
        var source = Source.Replace("[SunderService]", "[SunderRpcService]", StringComparison.Ordinal);
        CreateDriver().RunGeneratorsAndUpdateCompilation(Compilation(source), out var result, out _);
        Assert.Null(result.GetTypeByMetadataName(ApiName));
        Assert.Contains(result.GetDiagnostics(), diagnostic => diagnostic.Id == "CS0246"
            && diagnostic.GetMessage().Contains("SunderRpcService", StringComparison.Ordinal));
    }

    [Fact]
    public void ReusedDriverTracksUnsavedRenameRemovalAndIncompleteBody()
    {
        var driver = CreateDriver();
        driver = driver.RunGeneratorsAndUpdateCompilation(Compilation(Source), out var initial, out var diagnostics);
        Assert.Empty(diagnostics);
        Assert.Single(initial.GetTypeByMetadataName(ApiName)!.GetMembers("ReadAsync"));
        var renamed = Source.Replace("ReadAsync", "UnsavedAsync", StringComparison.Ordinal);
        driver = driver.RunGeneratorsAndUpdateCompilation(Compilation(renamed), out var updated, out diagnostics);
        Assert.Empty(diagnostics);
        Assert.Empty(updated.GetTypeByMetadataName(ApiName)!.GetMembers("ReadAsync"));
        Assert.Single(updated.GetTypeByMetadataName(ApiName)!.GetMembers("UnsavedAsync"));
        var incomplete = renamed.Replace("=> Task.FromResult(new Note { Number = request.Number });", "=> ;", StringComparison.Ordinal);
        driver = driver.RunGeneratorsAndUpdateCompilation(Compilation(incomplete), out var typing, out diagnostics);
        Assert.Empty(diagnostics);
        Assert.Contains(typing.GetDiagnostics(), value => value.Severity == DiagnosticSeverity.Error);
        Assert.Single(typing.GetTypeByMetadataName(ApiName)!.GetMembers("UnsavedAsync"));
        driver = driver.RunGeneratorsAndUpdateCompilation(Compilation(Source.Replace("[SunderService]", "", StringComparison.Ordinal)), out var removed, out diagnostics);
        Assert.Empty(diagnostics);
        Assert.Null(removed.GetTypeByMetadataName(ApiName));
        Assert.DoesNotContain(driver.GetRunResult().Results.SelectMany(value => value.GeneratedSources), value => value.SourceText.ToString().Contains("interface INotesService", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("")]
    public void ArtifactCompilationsDoNotReceiveEditorSymbols(string enabled)
    {
        var driver = CreateDriver(enabled);
        driver = driver.RunGeneratorsAndUpdateCompilation(Compilation(Source), out var output, out var diagnostics);
        Assert.Empty(diagnostics);
        Assert.Null(output.GetTypeByMetadataName(ApiName));
        Assert.Empty(driver.GetRunResult().Results.SelectMany(value => value.GeneratedSources));
    }

    [Theory]
    [InlineData("public sealed partial class NotesService", "public sealed class NotesService", "SUNDEREDIT001")]
    [InlineData("namespace Example;", "namespace Sunder.Generated.Packages.Example_Editor_Services; public interface INotesService { }", "SUNDEREDIT002")]
    public void InvalidDeclarationsReportLocatedDiagnostics(string oldText, string newText, string id)
    {
        var driver = CreateDriver();
        driver.RunGeneratorsAndUpdateCompilation(Compilation(Source.Replace(oldText, newText, StringComparison.Ordinal)), out _, out var diagnostics);
        var error = Assert.Single(diagnostics, value => value.Id == id);
        Assert.Equal("service.cs", error.Location.GetLineSpan().Path);
        Assert.True(error.Location.IsInSource);
    }

    [Fact]
    public void DeclarationOnlyInterfaceUsesPackageNamespaceWithoutDoubleIPrefix()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            [SunderService(SunderServiceExposure.Public, Key = "extension")]
            public interface IExtensionService { ValueTask<string> ReadAsync(string value, CancellationToken token = default); }
            """;
        CreateDriver().RunGeneratorsAndUpdateCompilation(Compilation(source), out var live, out var diagnostics);
        Assert.Empty(diagnostics);
        Assert.Empty(live.GetDiagnostics().Where(value => value.Severity == DiagnosticSeverity.Error));
        var api = live.GetTypeByMetadataName("Sunder.Generated.Packages.Example_Editor_Services.IExtensionService");
        Assert.NotNull(api);
        Assert.Null(live.GetTypeByMetadataName("Sunder.Generated.Packages.Example_Editor_Services.IIExtensionService"));
        var build = SunderRpcCSharpOwnedClientPrepass.Compile(new(), new("example.editor", "1.0.0",
            [new("extension.cs", source)], ReferencePaths, GenerateServiceInterfaces: true));
        Assert.True(build.Compilation.Success, string.Join(Environment.NewLine, build.Compilation.Diagnostics));
        Assert.Equal(api.GetMembers("ReadAsync").Single().ToDisplayString(),
            build.Compilation.SourceCompilation!.GetTypeByMetadataName(api.ToDisplayString())!.GetMembers("ReadAsync").Single().ToDisplayString());
    }

    [Fact]
    public void SourcePackageIdentityMustAgreeWithEvaluatedPackageIdentity()
    {
        var source = Source.Replace("namespace Example;",
            "[assembly: Sunder.Sdk.Packaging.SunderPackage(Id = \"example.other\", Name = \"Other\")]\nnamespace Example;", StringComparison.Ordinal);
        CreateDriver().RunGeneratorsAndUpdateCompilation(Compilation(source), out var live, out var diagnostics);
        Assert.Contains(diagnostics, value => value.Id == "SUNDEREDIT001" && value.GetMessage().Contains("package ID", StringComparison.Ordinal));
        Assert.Null(live.GetTypeByMetadataName(ApiName));
    }

    private const string ApiName = "Sunder.Generated.Packages.Example_Editor_Services.INotesService";
    private static string[] Signatures(Compilation compilation) => compilation.GetTypeByMetadataName(ApiName)!.GetMembers().OfType<IMethodSymbol>()
        .Select(value => value.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)).Order(StringComparer.Ordinal).ToArray();
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp14);
    private static GeneratorDriver CreateDriver(string enabled = "true")
    {
        var generators = new AnalyzerFileReference(AnalyzerPath, new Loader()).GetGenerators(LanguageNames.CSharp);
        Assert.Single(generators);
        return CSharpGeneratorDriver.Create(generators, parseOptions: ParseOptions, optionsProvider: new Options(enabled));
    }

    private sealed class Loader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath) { }
        public Assembly LoadFromPath(string fullPath) => AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
    }

    private sealed class Options(string enabled) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Values(enabled);
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;
    }

    private sealed class Values(string enabled) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            value = enabled;
            if (key == "build_property.SunderPackageId") { value = "example.editor"; return true; }
            return key == "build_property.SunderPackageEditorCompilation";
        }
    }
    private static string AnalyzerPath => Path.Combine(Path.GetDirectoryName(typeof(GenerateSunderRpcBindingsTask).Assembly.Location)!, "Sunder.Package.Build.Editor.dll");
    private static string[] ReferencePaths => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Append(typeof(SunderServiceAttribute).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static CSharpCompilation Compilation(string source) => CSharpCompilation.Create("EditorPackage",
        [CSharpSyntaxTree.ParseText(source, ParseOptions, "service.cs")], ReferencePaths.Select(path => MetadataReference.CreateFromFile(path)),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    private const string Source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using System.Collections.Generic;
        using Sunder.Sdk.Rpc;
        using Sunder.Sdk.Services;
        namespace Example;
        [SunderService]
        public sealed partial class NotesService
        {
            /// <summary>Reads a note.</summary>
            /// <remarks>Uses <paramref name="context"/> metadata.<include file="not-an-isolated-role-input.xml" path="docs"/><inheritdoc/></remarks>
            /// <param name="request">The note to read.</param>
            /// <param name="context">Host invocation context, not a caller argument.</param>
            /// <param name="token">Cancels the operation.</param>
            /// <returns>The matching note.</returns>
            public Task<Note> ReadAsync(NoteRequest request, SunderRpcInvocationContext context, CancellationToken token = default)
                => Task.FromResult(new Note { Number = request.Number });
            public IAsyncEnumerable<Note> WatchAsync(CancellationToken token = default) => throw new NotSupportedException();
        }
        public sealed record NoteRequest { public required long Number { get; init; } }
        public sealed record Note { public required long Number { get; init; } }
        """;
}
