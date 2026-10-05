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
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed class ServiceApiEditorGeneratorTests
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
    }

    [Fact]
    public void LiveApiMatchesBuildCompilerForContextDefaultsAndStreaming()
    {
        var input = Compilation(Source);
        var driver = CreateDriver();
        driver.RunGeneratorsAndUpdateCompilation(input, out var live, out var diagnostics);
        Assert.Empty(diagnostics);
        Assert.Empty(live.GetDiagnostics().Where(value => value.Severity == DiagnosticSeverity.Error));
        var build = SunderRpcCSharpOwnedClientPrepass.Compile(new SunderRpcCSharpSourceCompiler(),
            new("example.editor", "1.0.0", [new("service.cs", Source)], ReferencePaths, AssemblyName: "EditorPackage", GenerateServiceInterfaces: true));
        Assert.True(build.Compilation.Success, string.Join(Environment.NewLine, build.Compilation.Diagnostics.Select(value => value.Message)));
        Assert.Equal(Signatures(build.Compilation.SourceCompilation!), Signatures(live));
        var method = Assert.Single(live.GetTypeByMetadataName("Example.INotesService")!.GetMembers("ReadAsync").OfType<IMethodSymbol>());
        Assert.Equal(2, method.Parameters.Length);
        Assert.True(method.Parameters[1].HasExplicitDefaultValue);
        var sourceMethod = Assert.Single(input.GetTypeByMetadataName("Example.NotesService")!.GetMembers("ReadAsync"));
        Assert.Equal(sourceMethod.Locations[0].GetLineSpan().StartLinePosition.Line, method.Locations[0].GetMappedLineSpan().StartLinePosition.Line);
        Assert.Equal("service.cs", method.Locations[0].GetMappedLineSpan().Path);
    }

    [Fact]
    public void ReusedDriverTracksUnsavedRenameRemovalAndIncompleteBody()
    {
        var driver = CreateDriver();
        driver = driver.RunGeneratorsAndUpdateCompilation(Compilation(Source), out var initial, out var diagnostics);
        Assert.Empty(diagnostics);
        Assert.Single(initial.GetTypeByMetadataName("Example.INotesService")!.GetMembers("ReadAsync"));
        var renamed = Source.Replace("ReadAsync", "UnsavedAsync", StringComparison.Ordinal);
        driver = driver.RunGeneratorsAndUpdateCompilation(Compilation(renamed), out var updated, out diagnostics);
        Assert.Empty(diagnostics);
        Assert.Empty(updated.GetTypeByMetadataName("Example.INotesService")!.GetMembers("ReadAsync"));
        Assert.Single(updated.GetTypeByMetadataName("Example.INotesService")!.GetMembers("UnsavedAsync"));
        var incomplete = renamed.Replace("=> Task.FromResult(new Note { Number = request.Number });", "=> ;", StringComparison.Ordinal);
        driver = driver.RunGeneratorsAndUpdateCompilation(Compilation(incomplete), out var typing, out diagnostics);
        Assert.Empty(diagnostics);
        Assert.Contains(typing.GetDiagnostics(), value => value.Severity == DiagnosticSeverity.Error);
        Assert.Single(typing.GetTypeByMetadataName("Example.INotesService")!.GetMembers("UnsavedAsync"));
        driver = driver.RunGeneratorsAndUpdateCompilation(Compilation(Source.Replace("[SunderRpcService]", "", StringComparison.Ordinal)), out var removed, out diagnostics);
        Assert.Empty(diagnostics);
        Assert.Null(removed.GetTypeByMetadataName("Example.INotesService"));
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
        Assert.Null(output.GetTypeByMetadataName("Example.INotesService"));
        Assert.Empty(driver.GetRunResult().Results.SelectMany(value => value.GeneratedSources));
    }

    [Theory]
    [InlineData("public sealed partial class NotesService", "public sealed class NotesService", "SUNDEREDIT001")]
    [InlineData("public sealed record NoteRequest", "public interface INotesService { }\npublic sealed record NoteRequest", "SUNDEREDIT002")]
    public void InvalidDeclarationsReportLocatedDiagnostics(string oldText, string newText, string id)
    {
        var driver = CreateDriver();
        driver.RunGeneratorsAndUpdateCompilation(Compilation(Source.Replace(oldText, newText, StringComparison.Ordinal)), out _, out var diagnostics);
        var error = Assert.Single(diagnostics, value => value.Id == id);
        Assert.Equal("service.cs", error.Location.GetLineSpan().Path);
        Assert.True(error.Location.IsInSource);
    }

    private static string[] Signatures(Compilation compilation) => compilation.GetTypeByMetadataName("Example.INotesService")!.GetMembers().OfType<IMethodSymbol>()
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
            return key == "build_property.SunderPackageEditorCompilation";
        }
    }
    private static string AnalyzerPath => Path.Combine(Path.GetDirectoryName(typeof(GenerateSunderRpcBindingsTask).Assembly.Location)!, "Sunder.Package.Build.Editor.dll");
    private static string[] ReferencePaths => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Append(typeof(SunderRpcServiceAttribute).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static CSharpCompilation Compilation(string source) => CSharpCompilation.Create("EditorPackage",
        [CSharpSyntaxTree.ParseText(source, ParseOptions, "service.cs")], ReferencePaths.Select(path => MetadataReference.CreateFromFile(path)),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    private const string Source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using System.Collections.Generic;
        using Sunder.Sdk.Rpc;
        namespace Example;
        [SunderRpcService]
        public sealed partial class NotesService
        {
            [SunderRpcMethod]
            public Task<Note> ReadAsync(NoteRequest request, SunderRpcInvocationContext context, CancellationToken token = default)
                => Task.FromResult(new Note { Number = request.Number });
            [SunderRpcMethod]
            public IAsyncEnumerable<Note> WatchAsync(CancellationToken token = default) => throw new NotSupportedException();
        }
        public sealed record NoteRequest { public required long Number { get; init; } }
        public sealed record Note { public required long Number { get; init; } }
        """;
}
