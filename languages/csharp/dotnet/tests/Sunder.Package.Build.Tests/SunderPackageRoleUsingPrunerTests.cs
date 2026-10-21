using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Package.Build.Authoring;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed class SunderPackageRoleUsingPrunerTests
{
    [Fact]
    public void UnchangedRoleKeepsItsCompilationAndSyntaxTreeIdentity()
    {
        var tree = CSharpSyntaxTree.ParseText("using System; public sealed class Model { public Guid Id => Guid.Empty; }", path: "model.cs");
        var compilation = Compile(tree);
        var result = SunderPackageRoleUsingPruner.Prune(compilation, new HashSet<string> { tree.FilePath });
        Assert.Same(compilation, result.Compilation);
        Assert.Same(tree, Assert.Single(result.Compilation.SyntaxTrees));
        Assert.Empty(result.UpdatedSources);
    }

    [Fact]
    public void OnlyUnusedAuthoredImportsChangeAndAllLookupsShareTheOriginalFrame()
    {
        var first = CSharpSyntaxTree.ParseText("using System;\npublic sealed class First { }", path: "first.cs");
        var second = CSharpSyntaxTree.ParseText("using System;\npublic sealed class Second { }", path: "second.cs");
        var used = CSharpSyntaxTree.ParseText("using Id = System.Guid;\npublic sealed class Used { public Id Value => Id.Empty; }", path: "used.cs");
        var generated = CSharpSyntaxTree.ParseText("using System;\npublic sealed class Generated { }", path: "generated.cs");
        var compilation = Compile(first, second, used, generated);
        var result = SunderPackageRoleUsingPruner.Prune(compilation, new HashSet<string> { first.FilePath, second.FilePath, used.FilePath });
        // Guid in a different file conservatively keeps System imports, as before.
        Assert.Same(compilation, result.Compilation);
        var isolated = Compile(first, second, generated);
        result = SunderPackageRoleUsingPruner.Prune(isolated, new HashSet<string> { first.FilePath, second.FilePath });
        Assert.Equal(new[] { "first.cs", "second.cs" }, result.UpdatedSources.Keys.Order(StringComparer.Ordinal));
        Assert.Same(generated, result.Compilation.SyntaxTrees.Single(tree => tree.FilePath == generated.FilePath));
        foreach (var original in new[] { first, second })
        {
            var updated = result.UpdatedSources[original.FilePath];
            Assert.Equal(original.GetText().Length, updated.Length);
            Assert.Equal(original.GetText().ToString().IndexOf('\n'), updated.IndexOf('\n'));
            Assert.DoesNotContain("using System;", updated);
        }
        using var image = new MemoryStream();
        Assert.True(result.Compilation.Emit(image).Success);
    }

    [Fact]
    public void BodyAssemblyClosureExcludesUnusedImportsAndIsLocalToEachImmutableRoleFrame()
    {
        var used = Reference("RoleUsed", """
            namespace Used;
            public sealed class Value { public static implicit operator Value(string text) => new Value(); }
            public static class Maker { public static void Consume<T>(T value) { } }
            """);
        var unused = Reference("RoleUnused", "namespace Unused; public sealed class OnlyImport { }");
        var authored = CSharpSyntaxTree.ParseText("""
            global using Used;
            using Unused;
            public sealed class Consumer
            {
                public object Run()
                {
                    Value value = "converted";
                    Maker.Consume(value);
                    return new System.Collections.Generic.List<Value[]>();
                }
            }
            """, path: "consumer.cs");
        var generated = CSharpSyntaxTree.ParseText("public sealed class Generated { public Value[] Values => new Value[0]; }", path: "generated.cs");
        var paths = new HashSet<string> { authored.FilePath };
        var result = SunderPackageRoleUsingPruner.Prune(Compile(authored, generated).AddReferences(used, unused), paths);
        Assert.Contains("global using Used;", Assert.Single(result.UpdatedSources).Value, StringComparison.Ordinal);
        Assert.DoesNotContain("using Unused;", result.UpdatedSources[authored.FilePath], StringComparison.Ordinal);
        Assert.Equal(new[] { typeof(object).Assembly.GetName().Name!, "RoleUsed" }.Order(StringComparer.Ordinal), result.SemanticAssemblies.Order(StringComparer.Ordinal));
        Assert.Same(generated, result.Compilation.SyntaxTrees.Single(tree => tree.FilePath == generated.FilePath));
        var afterPruning = SunderPackageRoleUsingPruner.Prune(result.Compilation, paths);
        Assert.Equal(result.SemanticAssemblies.Order(StringComparer.Ordinal), afterPruning.SemanticAssemblies.Order(StringComparer.Ordinal));
        Assert.Same(result.Compilation, afterPruning.Compilation);
        using var image = new MemoryStream();
        Assert.True(result.Compilation.Emit(image).Success);

        // Reuse a path and the same metadata symbols, never the prior role's uses.
        var other = CSharpSyntaxTree.ParseText("using Used; using Unused; public sealed class Other { public string Name => string.Empty; }", path: authored.FilePath);
        var otherRole = SunderPackageRoleUsingPruner.Prune(Compile(other).AddReferences(used, unused), paths);
        Assert.DoesNotContain("RoleUsed", otherRole.SemanticAssemblies);
        Assert.DoesNotContain("RoleUnused", otherRole.SemanticAssemblies);
        Assert.DoesNotContain("using Used;", otherRole.UpdatedSources[other.FilePath], StringComparison.Ordinal);
    }

    private static PortableExecutableReference Reference(string name, string source)
    {
        using var image = new MemoryStream();
        Assert.True(Compile(CSharpSyntaxTree.ParseText(source)).WithAssemblyName(name).Emit(image).Success);
        return MetadataReference.CreateFromImage(image.ToArray());
    }

    private static CSharpCompilation Compile(params SyntaxTree[] trees)
        => CSharpCompilation.Create("RoleUsingPruner", trees,
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
}
