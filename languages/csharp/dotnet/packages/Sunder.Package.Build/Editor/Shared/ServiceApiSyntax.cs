using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Sunder.Package.Build.Editor;

// Compiled into both the build compiler and the portable editor analyzer. The
// editor adds declarations only; descriptors, authorization and role output are
// still derived and validated by the owning build compiler.
internal static class ServiceApiSyntax
{
    internal static readonly SymbolDisplayFormat SignatureFormat = new SymbolDisplayFormat(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
            | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    internal static void AppendInterface(StringBuilder output, INamedTypeSymbol type, IEnumerable<IMethodSymbol> methods, bool mapSourceLocations = false)
    {
        var ns = type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString();
        if (ns != null) output.Append("namespace ").Append(string.Join(".", ns.Split('.').Select(Escape))).AppendLine(" {");
        if (mapSourceLocations) AppendLocation(output, type);
        output.Append(type.DeclaredAccessibility == Accessibility.Public ? "public" : "internal")
            .Append(" interface ").Append(Escape("I" + type.Name)).AppendLine(" {");
        foreach (var method in methods)
        {
            if (mapSourceLocations) AppendLocation(output, method);
            output.Append("    ").Append(Signature(method)).AppendLine(";");
        }
        output.AppendLine("}");
        if (mapSourceLocations) output.AppendLine("#line default");
        if (ns != null) output.AppendLine("}");
    }

    private static void AppendLocation(StringBuilder output, ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(value => value.IsInSource);
        if (location == null) return;
        var span = location.GetLineSpan();
        var path = span.Path.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        output.Append("#line ").Append(span.StartLinePosition.Line + 1).Append(" \"").Append(path).AppendLine("\"");
    }

    internal static string Signature(IMethodSymbol method)
        => method.ReturnType.ToDisplayString(SignatureFormat) + " " + Escape(method.Name)
            + "(" + string.Join(", ", method.Parameters.Where(parameter => !IsContext(parameter)).Select(parameter => parameter.ToDisplayString(SignatureFormat))) + ")";

    internal static bool IsContext(IParameterSymbol parameter) => parameter.Type.ToDisplayString() == "Sunder.Sdk.Rpc.SunderRpcInvocationContext";
    private static string Escape(string value) => SyntaxFacts.GetKeywordKind(value) != SyntaxKind.None
        || SyntaxFacts.GetContextualKeywordKind(value) != SyntaxKind.None ? "@" + value : value;
}
