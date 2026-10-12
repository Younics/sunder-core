using Microsoft.CodeAnalysis;
using Sunder.Sdk.Services;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal sealed partial class SunderRpcCSharpSourceCompiler
{
    internal static bool IsOptionalReference(IPropertySymbol property)
        => property.GetAttributes().Any(attribute => attribute.AttributeClass is { } type
            && type.ToDisplayString() == typeof(SunderOptionalServiceReferenceAttribute).FullName
            && type.ContainingAssembly.Identity.Name == SdkAssemblyIdentity.Name);

    private static void ValidateOptionalReferences(IReadOnlyList<INamedTypeSymbol> sourceTypes,
        IReadOnlyList<SunderRpcCSharpCompiledContract> contracts)
    {
        var transferred = contracts.SelectMany(contract => contract.SourceTypes)
            .Where(value => !value.IsTransparent && value.Union is null)
            .SelectMany(value => value.Properties).Where(property => !property.IsOptional)
            .Select(property => property.Source).ToHashSet<IPropertySymbol>(SymbolEqualityComparer.Default);
        foreach (var property in sourceTypes.SelectMany(type => type.GetMembers().OfType<IPropertySymbol>()).Where(IsOptionalReference))
        {
            if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public
                || property.GetMethod?.DeclaredAccessibility != Accessibility.Public || !transferred.Contains(property)
                || property.Type is not INamedTypeSymbol { Name: "ServiceReference", Arity: 1, NullableAnnotation: NullableAnnotation.Annotated } reference
                || reference.ContainingNamespace.ToDisplayString() != "Sunder.Sdk.Services"
                || reference.ContainingAssembly.Identity.Name != SdkAssemblyIdentity.Name)
                throw Failure("rpc-authoring.optional-reference-invalid",
                    "SunderOptionalServiceReference requires a transferred, direct nullable SDK ServiceReference<T> property on a record value; it cannot mark a container, optional wrapper or transparent value.", property);
        }
    }
}
