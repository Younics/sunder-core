using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
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

    internal static void AppendInterface(StringBuilder output, string packageId, INamedTypeSymbol type, IEnumerable<IMethodSymbol> methods, bool mapSourceLocations = false, string? serviceId = null)
    {
        output.Append("namespace ").Append(GeneratedPackageNames.Services(packageId)).AppendLine(" {");
        AppendDocumentation(output, type, null);
        if (mapSourceLocations) AppendLocation(output, type);
        output.Append(type.DeclaredAccessibility == Accessibility.Public ? "public" : "internal")
            .Append(" interface ").Append(Escape(serviceId == null ? GeneratedPackageNames.ServiceInterfaceName(type) : GeneratedPackageNames.NaturalInterfaceName(serviceId))).AppendLine(" {");
        foreach (var method in methods)
        {
            AppendDocumentation(output, method, method.Parameters.Where(parameter => !IsContext(parameter)).Select(parameter => parameter.Name));
            if (mapSourceLocations) AppendLocation(output, method);
            output.Append("    ").Append(Signature(method)).AppendLine(";");
        }
        output.AppendLine("}");
        if (mapSourceLocations) output.AppendLine("#line default");
        output.AppendLine("}");
    }

    private static void AppendDocumentation(StringBuilder output, ISymbol symbol, IEnumerable<string>? parameters)
    {
        var xml = symbol.GetDocumentationCommentXml(expandIncludes: false);
        if (string.IsNullOrWhiteSpace(xml) || xml!.Length > 65536) return;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 65536,
            });
            var document = XElement.Load(reader);
            var names = parameters == null ? null : new HashSet<string>(parameters, System.StringComparer.Ordinal);
            foreach (var element in document.Elements())
            {
                // Include/inheritdoc directives could resolve different source in an isolated role.
                if (!(new[] { "summary", "remarks", "example", "param", "returns", "exception", "seealso" }).Contains(element.Name.LocalName)) continue;
                if (element.Name.LocalName == "param" && (names == null || !names.Contains((string?)element.Attribute("name") ?? ""))) continue;
                foreach (var directive in element.Descendants().Where(value => value.Name.LocalName is "include" or "inheritdoc").ToArray())
                    directive.Remove();
                foreach (var reference in element.Descendants("paramref").ToArray())
                    if (names == null || !names.Contains((string?)reference.Attribute("name") ?? ""))
                        reference.ReplaceWith(new XText((string?)reference.Attribute("name") ?? "invocation context"));
                foreach (var line in element.ToString(SaveOptions.DisableFormatting).Replace("\r\n", "\n").Split('\n'))
                    output.Append("/// ").AppendLine(line);
            }
        }
        catch (XmlException)
        {
            // The source compiler owns malformed author comments; generation must not invent XML.
        }
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
