using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Sunder.Package.Build.Rpc.Authoring.CSharp;

namespace Sunder.Package.Build.Authoring;

internal static class SunderPackageRoleProjection
{
    internal static IReadOnlyList<SunderRpcCSharpSourceFile> Project(SunderPackageAuthoringAnalysis analysis, SunderPackageRoleMembership role)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (!analysis.Success || analysis.Compilation.SourceCompilation is null)
            throw new InvalidOperationException("Only a successfully validated package role plan may select compile inputs.");
        if (role is not (SunderPackageRoleMembership.App or SunderPackageRoleMembership.Worker))
            throw new ArgumentOutOfRangeException(nameof(role));
        var compilation = analysis.Compilation.SourceCompilation;
        var planned = analysis.RolePlan.Types.Select(type => type.MetadataName).ToHashSet(StringComparer.Ordinal);
        var selected = analysis.RolePlan.Types.Where(type => (type.Roles & role) != 0).Select(type => type.MetadataName).ToHashSet(StringComparer.Ordinal);
        var namespaces = new Dictionary<INamespaceSymbol, bool>(SymbolEqualityComparer.Default);
        var ordinaryPaths = analysis.OrdinarySourcePaths;
        var result = new List<SunderRpcCSharpSourceFile>();
        foreach (var tree in compilation.SyntaxTrees.Where(tree => ordinaryPaths.Contains(tree.FilePath)).OrderBy(tree => tree.FilePath, StringComparer.Ordinal))
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();
            var text = tree.GetText().ToString().ToCharArray();
            var retained = root is CompilationUnitSyntax unit && (unit.AttributeLists.Count > 0 || unit.Usings.Any(value => value.GlobalKeyword.RawKind != 0))
                || !root.DescendantNodes().Any(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);
            foreach (var declaration in root.DescendantNodes().Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
            {
                if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol { ContainingType: null } type) continue;
                if (selected.Contains(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))) { retained = true; continue; }
                Blank(declaration);
            }
            foreach (var directive in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
            {
                // A valid mixed source may import a namespace or alias used only by
                // the other role. Do not leave a dangling source import after pruning.
                if (directive.Name is null) continue;
                var target = model.GetSymbolInfo(directive.Name).Symbol;
                if (target is INamespaceSymbol space && !RetainsNamespace(space)
                    || target is INamedTypeSymbol type && !RetainsType(type)) Blank(directive);
            }
            if (retained) result.Add(new(tree.FilePath, new string(text)));

            void Blank(SyntaxNode node)
            {
                // Preserve original line/column positions and directive balance. The
                // result is parsed again by the real role compilation before use.
                var directives = node.DescendantTrivia(descendIntoTrivia: true).Where(trivia => trivia.IsDirective).Select(trivia => trivia.FullSpan).ToArray();
                for (var index = node.FullSpan.Start; index < node.FullSpan.End; index++)
                    if (text[index] is not ('\r' or '\n') && !directives.Any(span => span.Contains(index))) text[index] = ' ';
            }
        }
        return result;

        bool RetainsType(INamedTypeSymbol type)
        {
            while (type.ContainingType is { } parent) type = parent;
            var name = type.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return !SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)
                || !planned.Contains(name) || selected.Contains(name);
        }

        bool RetainsNamespace(INamespaceSymbol space)
        {
            if (namespaces.TryGetValue(space, out var retained)) return retained;
            retained = space.ConstituentNamespaces.Any(part => !SymbolEqualityComparer.Default.Equals(part.ContainingAssembly, compilation.Assembly))
                || space.GetTypeMembers().Any(RetainsType) || space.GetNamespaceMembers().Any(RetainsNamespace);
            namespaces[space] = retained;
            return retained;
        }
    }
}
