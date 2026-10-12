using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Sunder.Package.Build.Authoring;

internal static partial class SunderPackageResourcePlanner
{
    internal static SunderPackageMarkupPlan Markup(CSharpCompilation compilation, IReadOnlyList<SunderPackageResourceInput> resources)
    {
        const string xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var references = compilation.References.Select(compilation.GetAssemblyOrModuleSymbol).OfType<IAssemblySymbol>().ToArray();
        var definitions = references.SelectMany(assembly => assembly.GetAttributes()
            .Where(attribute => attribute.AttributeClass?.ToDisplayString() == "Avalonia.Metadata.XmlnsDefinitionAttribute"
                && attribute.ConstructorArguments.Length >= 2)
            .Select(attribute => (Xml: attribute.ConstructorArguments[0].Value as string, Clr: attribute.ConstructorArguments[1].Value as string, Assembly: assembly))).ToArray();
        var types = new List<SunderPackageMarkupType>();
        var assemblies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in resources.Where(resource => resource.Kind is "AvaloniaXaml" or "AvaloniaResource"
                     && Path.GetExtension(resource.SourcePath).ToLowerInvariant() is ".axaml" or ".xaml" or ".paml"))
        {
            var document = Xml(resource.Contents, resource.SourcePath);
            foreach (var element in document.Descendants())
            {
                if (element.Attribute(XName.Get("TypeArguments", xaml)) is not null)
                    throw Invalid("xaml-type-unsupported", $"'{resource.SourcePath}' requires generic XAML type-argument ownership support.");
                Resolve(element.Name.NamespaceName, element.Name.LocalName.Split('.')[0], element, required: false);
                foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
                {
                    if (attribute.Name == XName.Get("Class", xaml))
                    {
                        var type = compilation.Assembly.GetTypeByMetadataName(attribute.Value)
                            ?? throw Invalid("xaml-type-invalid", $"'{resource.SourcePath}' names missing ordinary x:Class '{attribute.Value}'.");
                        Record(type, attribute);
                    }
                    else if (attribute.Name.LocalName is "DataType" or "TargetType") Qualified(attribute.Value, element, attribute, required: true,
                        bindingDataType: attribute.Name == XName.Get("DataType", xaml));
                    else if (attribute.Value.StartsWith("{x:Type ", StringComparison.Ordinal)) Qualified(attribute.Value, element, attribute, required: true);
                    else if (attribute.Value.StartsWith("{x:Static ", StringComparison.Ordinal) && attribute.Value.EndsWith('}'))
                    {
                        var value = attribute.Value[10..^1].Trim(); var dot = value.LastIndexOf('.');
                        if (dot > 0) Qualified(value[..dot], element, attribute, required: true);
                    }
                    if (Uri.TryCreate(attribute.Value, UriKind.Absolute, out var uri) && uri.Scheme == "avares" && uri.Host.Length != 0)
                    {
                        var assembly = references.SingleOrDefault(assembly => assembly.Name.Equals(uri.Host, StringComparison.OrdinalIgnoreCase));
                        if (assembly is not null) assemblies.Add(assembly.Name);
                    }
                }
            }

            void Qualified(string value, XElement element, XObject location, bool required, bool bindingDataType = false)
            {
                if (value.StartsWith("{x:Type ", StringComparison.Ordinal) && value.EndsWith('}')) value = value[8..^1].Trim();
                if (value.StartsWith('{')) throw Invalid("xaml-type-unsupported", $"'{resource.SourcePath}' has a nonliteral XAML type '{value}'.");
                var parts = value.Split(':');
                var ns = parts.Length == 2 ? element.GetNamespaceOfPrefix(parts[0]) : element.GetDefaultNamespace();
                if (ns is null || parts.Length > 2) throw Invalid("xaml-type-invalid", $"'{resource.SourcePath}' has an unresolved XAML type '{value}'.");
                Resolve(ns.NamespaceName, parts[^1], location, required, bindingDataType);
            }
            void Resolve(string xmlns, string name, XObject location, bool required, bool bindingDataType = false)
            {
                if (xmlns == xaml || xmlns is "http://schemas.microsoft.com/expression/blend/2008" or "http://schemas.openxmlformats.org/markup-compatibility/2006") return;
                IEnumerable<(string Namespace, IAssemblySymbol? Assembly)> scopes;
                if (xmlns.StartsWith("using:", StringComparison.Ordinal)) scopes = [(xmlns[6..], null)];
                else if (xmlns.StartsWith("clr-namespace:", StringComparison.Ordinal))
                {
                    var parts = xmlns[14..].Split(';');
                    var owner = parts.Skip(1).SingleOrDefault(part => part.StartsWith("assembly=", StringComparison.Ordinal))?[9..];
                    var assembly = owner is null || owner == compilation.AssemblyName ? compilation.Assembly : references.SingleOrDefault(assembly => assembly.Name == owner);
                    if (assembly is null) throw Invalid("xaml-type-invalid", $"'{resource.SourcePath}' references unavailable assembly '{owner}'.");
                    scopes = [(parts[0], assembly)];
                }
                else scopes = definitions.Where(definition => definition.Xml == xmlns && definition.Clr is not null).Select(definition => (definition.Clr!, (IAssemblySymbol?)definition.Assembly));
                var candidates = scopes.Select(scope => scope.Assembly?.GetTypeByMetadataName(scope.Namespace + "." + name)
                    ?? (scope.Assembly is null ? compilation.GetTypeByMetadataName(scope.Namespace + "." + name) : null))
                    .OfType<INamedTypeSymbol>().Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).ToArray();
                if (candidates.Length == 1) Record(candidates[0], location, bindingDataType);
                else if (required || candidates.Length > 1)
                    throw Invalid("xaml-type-invalid", $"'{resource.SourcePath}' has no unique type '{xmlns}:{name}'.");
            }
            void Record(INamedTypeSymbol type, XObject location, bool bindingDataType = false)
            {
                var line = (IXmlLineInfo)location;
                if (SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly))
                    types.Add(new(type.ToDisplayString(), null, resource.SourcePath, line.LineNumber, line.LinePosition) { IsBindingDataType = bindingDataType });
                else assemblies.Add(type.ContainingAssembly.Name);
            }
        }
        return new(types.Distinct().ToArray(), assemblies.Order(StringComparer.Ordinal).ToArray());
    }
}
