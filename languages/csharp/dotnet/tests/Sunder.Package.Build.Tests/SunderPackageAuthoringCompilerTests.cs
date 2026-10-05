using Avalonia.Controls;
using Sunder.Package.Build.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed class SunderPackageAuthoringCompilerTests
{
    [Fact]
    public void Analyze_BootstrapsTypedInterfaceAndRolesFromCurrentSource()
    {
        var analysis = Analyze(Service + Ui);
        Assert.True(analysis.Success, Errors(analysis));
        Assert.Contains(analysis.GeneratedSources, source => source.Path == "service-apis.g.cs" && source.Text.Contains("interface INotesService", StringComparison.Ordinal));
        Assert.Equal(SunderPackageRoleMembership.Worker, Type(analysis, "NotesService").Roles);
        Assert.Equal(SunderPackageRoleMembership.App, Type(analysis, "NotesViewModel").Roles);
        Assert.Equal(SunderPackageRoleMembership.Shared, Type(analysis, "Note").Roles);
        Assert.True(Type(analysis, "Note").IsPortableValue);
        var original = new SunderRpcCSharpSourceCompiler().Compile(Input(Service));
        Assert.Equal(Assert.Single(original.Contracts).Descriptor.GetCanonicalUtf8(), Assert.Single(analysis.Compilation.Contracts).Descriptor.GetCanonicalUtf8());
        Assert.Null(Assert.Single(analysis.Compilation.Contracts).RuntimeUse);
    }

    [Fact]
    public void Analyze_RejectsDirectUiImplementationAccessAndInvalidGeneratedCalls()
    {
        var direct = Analyze(Service + Ui.Replace("INotesService", "NotesService", StringComparison.Ordinal));
        Assert.False(direct.Success); Assert.Contains(direct.RolePlan.Diagnostics, value => value.Id == "package-authoring.role-conflict");
        var invalid = Analyze(Service + Ui.Replace("service.GetAsync(", "service.MissingAsync(", StringComparison.Ordinal));
        Assert.False(invalid.Success); Assert.NotEmpty(invalid.Compilation.Diagnostics);
        Assert.Empty(invalid.GeneratedSources);
    }

    [Fact]
    public void Analyze_RejectsAuthoredGeneratedInterfaceCollision()
    {
        var invalid = Analyze(Service + "public interface INotesService { }\n" + Ui);
        Assert.False(invalid.Success); Assert.NotEmpty(invalid.Compilation.Diagnostics);
    }

    [Fact]
    public void Analyze_GeneratedInterfaceOmitsHostContextAndPreservesTaskAndStreamShapes()
    {
        var source = Service.Replace("public Task<Note> GetAsync(NoteRequest request, CancellationToken token = default)",
            "public Task<Note> GetAsync(NoteRequest request, SunderRpcInvocationContext invocation, CancellationToken token = default)", StringComparison.Ordinal)
            .Replace("public sealed partial class NotesService\n{", "public sealed partial class NotesService\n{\n"
                + "[SunderRpcMethod] public System.Collections.Generic.IAsyncEnumerable<Note> WatchAsync(CancellationToken token = default) => throw new System.NotSupportedException();", StringComparison.Ordinal);
        var analysis = Analyze(source + Ui);
        Assert.True(analysis.Success, Errors(analysis));
        var api = Assert.Single(analysis.GeneratedSources, value => value.Path == "service-apis.g.cs").Text;
        Assert.DoesNotContain("SunderRpcInvocationContext", api, StringComparison.Ordinal);
        Assert.Contains("Task<global::Example.Note>", api, StringComparison.Ordinal);
        Assert.Contains("IAsyncEnumerable<global::Example.Note>", api, StringComparison.Ordinal);
    }

    private static SunderPackageAuthoringAnalysis Analyze(string source) => SunderPackageAuthoringCompiler.Analyze(Input(source));
    private static SunderRpcCSharpCompilerInput Input(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat([typeof(SunderPackageCodeRoleAttribute).Assembly.Location, typeof(SunderWorker).Assembly.Location,
                typeof(Control).Assembly.Location, typeof(Avalonia.AvaloniaObject).Assembly.Location])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new("example.notes", "1.0.0", [new("package.cs", source)], references, AssemblyName: "Example.Notes");
    }
    private static SunderPackageRoleType Type(SunderPackageAuthoringAnalysis analysis, string name)
        => Assert.Single(analysis.RolePlan.Types, value => value.MetadataName == "global::Example." + name);
    private static string Errors(SunderPackageAuthoringAnalysis analysis)
        => string.Join(Environment.NewLine, analysis.Compilation.Diagnostics.Concat(analysis.RolePlan.Diagnostics).Select(value => value.Message));

    private const string Service = """
        using System.Threading;
        using System.Threading.Tasks;
        using Sunder.Sdk.Packaging;
        using Sunder.Sdk.Rpc;
        namespace Example;
        [SunderRpcService]
        public sealed partial class NotesService
        {
            [SunderRpcMethod]
            public Task<Note> GetAsync(NoteRequest request, CancellationToken token = default)
                => Task.FromResult(new Note { Number = request.Number });
        }
        public sealed record NoteRequest { public required long Number { get; init; } }
        public sealed record Note { public required long Number { get; init; } }

        """;
    private const string Ui = """
        [SunderPackageView("example.notes", "Notes", ViewModel = typeof(NotesViewModel))]
        public sealed class NotesView : Avalonia.Controls.UserControl;
        public sealed class NotesViewModel(INotesService service)
        {
            public Task<Note> LoadAsync() => service.GetAsync(new NoteRequest { Number = 7 });
        }
        """;
}
