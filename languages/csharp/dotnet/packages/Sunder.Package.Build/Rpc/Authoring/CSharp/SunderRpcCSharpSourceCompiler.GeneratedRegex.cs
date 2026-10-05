using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal sealed partial class SunderRpcCSharpSourceCompiler
{
    // The SDK emits these bodies during ordinary CoreCompile, after the RPC
    // prepass. Recognize only the fixed BCL shape and validate its literal regex;
    // unrelated partial methods and user-defined lookalike attributes still fail.
    private static bool IsSdkGeneratedRegexDefinition(IMethodSymbol method, CSharpCompilation compilation)
    {
        if (method is not { IsPartialDefinition: true, IsStatic: true, Arity: 0, Parameters.Length: 0,
                ReturnsByRef: false, ReturnsByRefReadonly: false, IsAbstract: false, IsExtern: false }
            || !method.DeclaringSyntaxReferences.All(reference => reference.GetSyntax() is MethodDeclarationSyntax { Body: null, ExpressionBody: null }))
            return false;
        var attributeType = compilation.GetTypeByMetadataName(typeof(GeneratedRegexAttribute).FullName!);
        var regexType = compilation.GetTypeByMetadataName(typeof(Regex).FullName!);
        if (attributeType is null || regexType is null || !SymbolEqualityComparer.Default.Equals(method.ReturnType, regexType)
            || !SymbolEqualityComparer.Default.Equals(regexType.ContainingAssembly, attributeType.ContainingAssembly)
            || attributeType.ContainingAssembly.Identity.Name != typeof(Regex).Assembly.GetName().Name
            || !attributeType.ContainingAssembly.Identity.PublicKeyToken.SequenceEqual(typeof(Regex).Assembly.GetName().GetPublicKeyToken()!))
            return false;
        var attributes = method.GetAttributes().Where(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType)).ToArray();
        if (attributes.Length != 1 || attributes[0].NamedArguments.Length != 0) return false;
        var declaration = attributes[0];
        if (declaration.AttributeConstructor is null) return false;
        var parameters = declaration.AttributeConstructor.Parameters;
        if (parameters.Length != declaration.ConstructorArguments.Length) return false;
        string? pattern = null;
        var options = RegexOptions.None;
        var timeout = Regex.InfiniteMatchTimeout;
        try
        {
            for (var index = 0; index < parameters.Length; index++)
            {
                var value = declaration.ConstructorArguments[index].Value;
                switch (parameters[index].Name)
                {
                    case "pattern" when value is string text: pattern = text; break;
                    case "options" when value is int flags: options = (RegexOptions)flags; break;
                    case "matchTimeoutMilliseconds" when value is int milliseconds:
                        timeout = milliseconds == -1 ? Regex.InfiniteMatchTimeout : TimeSpan.FromMilliseconds(milliseconds); break;
                    case "cultureName" when value is string culture: _ = CultureInfo.GetCultureInfo(culture); break;
                    default: return false;
                }
            }
            if (pattern is null) return false;
            // Parse without compiling dynamic IL or matching package data.
            _ = new Regex(pattern, options & ~RegexOptions.Compiled, timeout);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return false; }
    }
}
