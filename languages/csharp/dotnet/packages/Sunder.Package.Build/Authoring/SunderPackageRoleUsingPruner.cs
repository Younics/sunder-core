using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Sunder.Package.Build.Authoring;

// One immutable semantic frame per role. Never rebind the entire compilation for
// each authored file, or replace syntax trees whose imports did not change.
internal static class SunderPackageRoleUsingPruner
{
    internal sealed record Result(CSharpCompilation Compilation, IReadOnlyDictionary<string, string> UpdatedSources,
        IReadOnlySet<string> SemanticAssemblies);

    internal static Result Prune(CSharpCompilation compilation, IReadOnlySet<string> authoredPaths)
    {
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        var types = new HashSet<string>(StringComparer.Ordinal);
        var assemblies = new HashSet<string>(StringComparer.Ordinal);
        var roleAssembly = compilation.Assembly;
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes(descendIntoChildren: node => node is not UsingDirectiveSyntax))
            {
                if (node is UsingDirectiveSyntax) continue;
                if (node is TypeSyntax or ExpressionSyntax)
                {
                    var info = model.GetTypeInfo(node);
                    Type(info.Type);
                    Type(info.ConvertedType);
                    Assembly(info.Type);
                    Assembly(info.ConvertedType);
                }
                if (node is ExpressionSyntax or AttributeSyntax)
                {
                    var symbol = model.GetSymbolInfo(node).Symbol;
                    Type(symbol as ITypeSymbol);
                    Type(symbol?.ContainingType);
                    Assembly(symbol?.ContainingType);
                    if (symbol is IMethodSymbol method) foreach (var argument in method.TypeArguments) Assembly(argument);
                    if (symbol?.ContainingNamespace is { IsGlobalNamespace: false } ns) namespaces.Add(ns.ToDisplayString());
                }
            }
        }

        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        var replacements = new List<(SyntaxTree Original, SyntaxTree Updated)>();
        foreach (var tree in compilation.SyntaxTrees.Where(tree => authoredPaths.Contains(tree.FilePath)))
        {
            var directives = tree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>().ToArray();
            if (directives.Length == 0) continue;
            var model = compilation.GetSemanticModel(tree);
            char[]? text = null;
            foreach (var directive in directives)
            {
                var symbol = directive.Name is null ? null : model.GetSymbolInfo(directive.Name).Symbol;
                var used = symbol switch
                {
                    INamespaceSymbol ns => NamespaceUsed(ns.ToDisplayString()),
                    INamedTypeSymbol type => types.Contains(TypeKey(type)),
                    _ => true,
                };
                if (used) continue;
                text ??= tree.GetText().ToString().ToCharArray();
                for (var position = directive.SpanStart; position < directive.Span.End; position++)
                    if (text[position] is not ('\r' or '\n')) text[position] = ' ';
            }
            if (text is null) continue;
            var updated = new string(text);
            changes.Add(tree.FilePath, updated);
            replacements.Add((tree, CSharpSyntaxTree.ParseText(updated, (CSharpParseOptions)tree.Options, tree.FilePath)));
        }
        // All lookups above use the original semantic frame. The final role still
        // undergoes full emission/reference-closure validation after these edits.
        foreach (var (original, updated) in replacements) compilation = compilation.ReplaceSyntaxTree(original, updated);
        return new(compilation, changes, assemblies);

        bool NamespaceUsed(string name) => namespaces.Any(used => used == name || used.StartsWith(name + ".", StringComparison.Ordinal));

        void Type(ITypeSymbol? type)
        {
            if (type is IArrayTypeSymbol array) { Type(array.ElementType); return; }
            if (type is not INamedTypeSymbol named) return;
            types.Add(TypeKey(named));
            if (!named.ContainingNamespace.IsGlobalNamespace) namespaces.Add(named.ContainingNamespace.ToDisplayString());
            foreach (var argument in named.TypeArguments) if (!SymbolEqualityComparer.Default.Equals(argument, type)) Type(argument);
        }

        // Dependency planning needs the same body-only semantic frame. Collect
        // its assembly closure here instead of rebinding every body after import
        // pruning. Method type arguments must not change import-pruning policy.
        void Assembly(ITypeSymbol? type)
        {
            if (type is IArrayTypeSymbol array) { Assembly(array.ElementType); return; }
            if (type is not INamedTypeSymbol named) return;
            if (!SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, roleAssembly)) assemblies.Add(named.ContainingAssembly.Identity.Name);
            foreach (var argument in named.TypeArguments) if (!SymbolEqualityComparer.Default.Equals(argument, type)) Assembly(argument);
        }
    }

    private static string TypeKey(INamedTypeSymbol type)
        => type.ContainingAssembly.Identity + ":" + type.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
}
