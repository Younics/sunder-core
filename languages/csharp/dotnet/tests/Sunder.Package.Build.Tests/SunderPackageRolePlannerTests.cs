using System.Collections.Immutable;
using Avalonia.Controls;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Package.Build.Authoring;
using Sunder.Sdk.Packaging;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed class SunderPackageRolePlannerTests
{
    [Fact]
    public void Plan_UsesSemanticRootsAndBodiesInsteadOfFoldersOrFileBoundaries()
    {
        var compilation = Compile(("mixed.cs", """
            using Sunder.Sdk.Packaging;
            using Sunder.Sdk.Services;
            using System.Threading.Tasks;
            namespace Example;
            [SunderService]
            public sealed partial class NotesService
            {
                public Task<Note> GetAsync() => Task.FromResult(new Note { Text = Store.Read() });
            }
            public static class Store { public static string Read() => "worker"; }
            public sealed record Note { public required string Text { get; init; } }
            [SunderPackageView("example.notes", "Notes", ViewModel = typeof(NotesViewModel), IconPath = "icons/notes.png")]
            public sealed class NotesView : Avalonia.Controls.UserControl;
            public sealed class NotesViewModel { public string Label => UiHelper.Read(); public Note? Selection { get; set; } }
            public static class UiHelper { public static string Read() => "ui"; }
            public sealed class UnusedSource;
            """));
        var plan = SunderPackageRolePlanner.Plan(compilation, compilation.SyntaxTrees.ToArray(), [compilation.GetTypeByMetadataName("Example.Note")!]);
        Assert.True(plan.Success, Errors(plan));
        Assert.Equal(SunderPackageRoleMembership.Worker, Type(plan, "NotesService").Roles);
        Assert.Equal(SunderPackageRoleMembership.Worker, Type(plan, "Store").Roles);
        Assert.Equal(SunderPackageRoleMembership.Worker, Type(plan, "UnusedSource").Roles);
        Assert.Equal(SunderPackageRoleMembership.App, Type(plan, "NotesView").Roles);
        Assert.Equal(SunderPackageRoleMembership.App, Type(plan, "NotesViewModel").Roles);
        Assert.Equal(SunderPackageRoleMembership.App, Type(plan, "UiHelper").Roles);
        Assert.Equal(SunderPackageRoleMembership.Shared, Type(plan, "Note").Roles);
        Assert.True(Type(plan, "Note").IsPortableValue);
        Assert.Equal("global::Example.NotesViewModel", Assert.Single(plan.Views).ViewModelMetadataName);
        Assert.All(plan.Types, type => Assert.Equal(["mixed.cs"], type.SourcePaths));
    }

    [Theory]
    [InlineData("public sealed class Ui { private Backend? Value; }")]
    [InlineData("public sealed class Ui { public object Value() => new Backend(); }")]
    [InlineData("public sealed class Ui { public string Value() => Backend.Read(); }")]
    [InlineData("public sealed class Ui { public System.Collections.Generic.List<Backend> Value = []; }")]
    public void Plan_RejectsCrossRoleReferencesInSignaturesBodiesAndGenericArguments(string consumer)
    {
        var compilation = Compile(("boundary.cs", $$"""
            using Sunder.Sdk.Packaging;
            namespace Example;
            [SunderPackageCodeRole(SunderPackageCodeRole.Worker)]
            public sealed class Backend { public static string Read() => "backend"; }
            [SunderPackageCodeRole(SunderPackageCodeRole.App)]
            {{consumer}}
            """));
        var plan = SunderPackageRolePlanner.Plan(compilation, compilation.SyntaxTrees.ToArray());
        Assert.False(plan.Success);
        var error = Assert.Single(plan.Diagnostics, error => error.Id == "package-authoring.role-conflict");
        Assert.Equal("boundary.cs", error.Path); Assert.NotNull(error.Line); Assert.NotNull(error.Column);
        Assert.Contains("Example.Ui", error.Message); Assert.Contains("Example.Backend", error.Message);
    }

    [Fact]
    public void Plan_DoesNotSilentlyDuplicateHelperStateAcrossProcesses()
    {
        const string source = """
            using Sunder.Sdk.Packaging;
            namespace Example;
            [SunderPackageCodeRole(SunderPackageCodeRole.App)] public sealed class Ui { public int Read() => Counter.Value; }
            [SunderPackageCodeRole(SunderPackageCodeRole.Worker)] public sealed class Worker { public int Read() => Counter.Value; }
            public static class Counter { public static int Value; }
            """;
        var compilation = Compile(("state.cs", source));
        var plan = SunderPackageRolePlanner.Plan(compilation, compilation.SyntaxTrees.ToArray());
        Assert.Equal("package-authoring.role-ambiguous", Assert.Single(plan.Diagnostics).Id);
        var explicitCompilation = Compile(("state.cs", source.Replace("public static class Counter", "[SunderPackageCodeRole(SunderPackageCodeRole.Shared)] public static class Counter", StringComparison.Ordinal)));
        var explicitPlan = SunderPackageRolePlanner.Plan(explicitCompilation, explicitCompilation.SyntaxTrees.ToArray());
        Assert.True(explicitPlan.Success, Errors(explicitPlan)); Assert.True(Type(explicitPlan, "Counter").HasExplicitRole);
        Assert.Equal(SunderPackageRoleMembership.Shared, Type(explicitPlan, "Counter").Roles);
    }

    [Fact]
    public void Plan_RejectsSharedClosureThatReachesAWorkerImplementation()
    {
        var compilation = Compile(("shared.cs", """
            using Sunder.Sdk.Packaging;
            namespace Example;
            [SunderPackageCodeRole(SunderPackageCodeRole.Shared)] public static class Helper { public static int Read() => Backend.Read(); }
            [SunderPackageCodeRole(SunderPackageCodeRole.Worker)] public static class Backend { public static int Read() => 1; }
            """));
        var plan = SunderPackageRolePlanner.Plan(compilation, compilation.SyntaxTrees.ToArray());
        Assert.Equal("package-authoring.role-conflict", Assert.Single(plan.Diagnostics).Id);
    }

    [Fact]
    public void Plan_PartialTypesAndNestedDeclarationsStayTogetherAndOrderIsDeterministic()
    {
        var compilation = Compile(("z.cs", """
            using Sunder.Sdk.Packaging;
            namespace Example;
            [SunderPackageCodeRole(SunderPackageCodeRole.App)] public partial class Ui { public sealed class Nested { public string Read() => Helper.Value; } }
            public static class Helper { public static string Value => "app"; }
            """), ("a.cs", "namespace Example; public partial class Ui { public string Read() => Helper.Value; }"));
        var first = SunderPackageRolePlanner.Plan(compilation, compilation.SyntaxTrees.ToArray());
        var second = SunderPackageRolePlanner.Plan(compilation, compilation.SyntaxTrees.Reverse().ToArray());
        Assert.True(first.Success, Errors(first));
        Assert.Equal(["a.cs", "z.cs"], Type(first, "Ui").SourcePaths);
        Assert.Equal(first.Types.Select(type => (type.MetadataName, type.Roles)), second.Types.Select(type => (type.MetadataName, type.Roles)));
        Assert.DoesNotContain(first.Types, type => type.MetadataName.Contains("Nested", StringComparison.Ordinal));
        Assert.Equal(SunderPackageRoleMembership.App, Type(first, "Helper").Roles);
    }

    [Fact]
    public void Plan_ViewModelRoleConflictDoesNotDependOnTypeSortOrder()
    {
        foreach (var name in new[] { "AView", "ZView" })
        {
            var compilation = Compile(("views.cs", $$"""
                using Sunder.Sdk.Packaging;
                namespace Example;
                [SunderPackageView("example.view", "View", ViewModel = typeof(Model))] public sealed class {{name}} : Avalonia.Controls.UserControl;
                [SunderPackageCodeRole(SunderPackageCodeRole.Worker)] public sealed class Model;
                """));
            var plan = SunderPackageRolePlanner.Plan(compilation, compilation.SyntaxTrees.ToArray());
            Assert.False(plan.Success); Assert.Contains(plan.Diagnostics, error => error.Id == "package-authoring.role-conflict");
        }
    }

    [Theory]
    [InlineData("[SunderPackageView(\"\", \"View\")] public sealed class View : Avalonia.Controls.UserControl;", "view-invalid")]
    [InlineData("[SunderPackageView(\"example.view\", \"View\")] public sealed class View;", "view-invalid")]
    [InlineData("[SunderPackageView(\"example.view\", \"View\")] public abstract class View : Avalonia.Controls.UserControl;", "view-invalid")]
    [InlineData("[SunderPackageView(\"example.view\", \"View\", IconPath = \"../escape.png\")] public sealed class View : Avalonia.Controls.UserControl;", "view-invalid")]
    [InlineData("[SunderPackageCodeRole((SunderPackageCodeRole)42)] public sealed class Value;", "role-invalid")]
    [InlineData("public class Outer { [SunderPackageCodeRole(SunderPackageCodeRole.App)] public class Inner; }", "role-invalid")]
    [InlineData("[SunderPackageSettingsView] public class First : Avalonia.Controls.UserControl; [SunderPackageSettingsView] public class Second : Avalonia.Controls.UserControl;", "settings-view-duplicate")]
    [InlineData("[SunderPackageView(\"same\", \"First\")] public class First : Avalonia.Controls.UserControl; [SunderPackageView(\"same\", \"Second\")] public class Second : Avalonia.Controls.UserControl;", "view-id-duplicate")]
    public void Plan_RejectsInvalidIntentWithStableDiagnostics(string source, string code)
    {
        var compilation = Compile(("invalid.cs", "using Sunder.Sdk.Packaging; namespace Example; " + source));
        var plan = SunderPackageRolePlanner.Plan(compilation, compilation.SyntaxTrees.ToArray());
        Assert.False(plan.Success); Assert.Contains(plan.Diagnostics, error => error.Id == "package-authoring." + code);
    }

    [Fact]
    public void Plan_DoesNotExecuteStaticInitializersAndIgnoresLookalikeAttributes()
    {
        var compilation = Compile(("source.cs", """
            namespace Example;
            [System.AttributeUsage(System.AttributeTargets.Class)] public sealed class SunderPackageViewAttribute : System.Attribute;
            [SunderPackageView] public sealed class Lookalike { static Lookalike() => throw new System.Exception("must not run"); }
            """));
        var plan = SunderPackageRolePlanner.Plan(compilation, compilation.SyntaxTrees.ToArray());
        Assert.True(plan.Success, Errors(plan)); Assert.Empty(plan.Views);
        Assert.Equal(SunderPackageRoleMembership.Worker, Type(plan, "Lookalike").Roles);
    }

    [Fact]
    public void Plan_RejectsInvalidCompilationAndForeignTrees()
    {
        var invalid = Compile(("invalid.cs", "public sealed class Invalid { public int Value => \"text\"; }"));
        var failed = SunderPackageRolePlanner.Plan(invalid, invalid.SyntaxTrees.ToArray());
        Assert.False(failed.Success); Assert.Empty(failed.Types); Assert.Contains(failed.Diagnostics, error => error.Id == "package-authoring.source-invalid");
        var valid = Compile(("valid.cs", "public sealed class Valid;"));
        Assert.False(SunderPackageRolePlanner.Plan(valid, invalid.SyntaxTrees.ToArray()).Success);
    }

    private static readonly ImmutableArray<MetadataReference> References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator).Append(typeof(SunderPackageCodeRoleAttribute).Assembly.Location)
        .Append(typeof(Control).Assembly.Location).Append(typeof(Avalonia.AvaloniaObject).Assembly.Location)
        .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToImmutableArray();
    private static CSharpCompilation Compile(params (string Path, string Source)[] source)
        => CSharpCompilation.Create("PackageRoleFixture", source.Select(value => CSharpSyntaxTree.ParseText(value.Source,
                new CSharpParseOptions(LanguageVersion.CSharp14), value.Path)), References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    private static SunderPackageRoleType Type(SunderPackageRolePlan plan, string name) => Assert.Single(plan.Types, value => value.MetadataName == "global::Example." + name);
    private static string Errors(SunderPackageRolePlan plan) => string.Join(Environment.NewLine, plan.Diagnostics.Select(value => value.Message));
}
