using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpModelProjectionTests
{
    [Fact]
    public void WireMinorOptionalOutputRemovalBreaksAnUnchangedProjectedModelConsumer()
    {
        const string declaration = """
            using System.Threading.Tasks;
            using Sunder.Sdk.Rpc;
            using Sunder.Sdk.Services;
            [SunderService(SunderServiceExposure.Public)] public interface ResultsService { ValueTask<Result> ReadAsync(); }
            public sealed record Result
            {
                public required string Value { get; init; }
                public SunderRpcOptional<string> Legacy { get; init; }
            }
            """;
        const string consumer = """
            using Sunder.Generated.Packages.Example_Shared_Models;
            public static class ExistingConsumer { public static object? ReadLegacy(Result value) => value.Legacy; }
            """;
        var before = Compile(declaration);
        var after = Compile(declaration.Replace("public SunderRpcOptional<string> Legacy { get; init; }", "", StringComparison.Ordinal));
        Assert.True(before.Success, string.Join(Environment.NewLine, before.Diagnostics));
        Assert.True(after.Success, string.Join(Environment.NewLine, after.Diagnostics));
        var oldContract = Assert.Single(before.Contracts); var newContract = Assert.Single(after.Contracts);
        Assert.Equal(oldContract.ModelProjection.ToCanonicalUtf8(), newContract.ModelProjection.ToCanonicalUtf8());
        Assert.Equal(SunderRpcContractChangeKind.Minor, SunderRpcContractCompatibility.Classify(oldContract.Descriptor, newContract.Descriptor).Kind);
        AssertCompiles(SunderRpcCSharpImportPrepass.PrepareOwnedPackageApi(ProjectExport(before), "1.0.0").Append(new("consumer.cs", consumer)));
        var sources = SunderRpcCSharpImportPrepass.PrepareOwnedPackageApi(ProjectExport(after), "1.0.0").Append(new("consumer.cs", consumer));
        var compilation = CSharpCompilation.Create("RemovedMemberProbe", sources.Select(source => CSharpSyntaxTree.ParseText(source.Text,
            new CSharpParseOptions(LanguageVersion.CSharp14), source.Path)), References().Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        Assert.Contains(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Id == "CS1061"
            && diagnostic.GetMessage().Contains("Legacy", StringComparison.Ordinal));
    }
}
