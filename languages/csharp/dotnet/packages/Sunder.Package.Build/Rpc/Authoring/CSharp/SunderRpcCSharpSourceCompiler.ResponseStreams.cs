using System.Text.Json;
using Microsoft.CodeAnalysis;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal sealed partial class SunderRpcCSharpSourceCompiler
{
    internal static IMethodSymbol ProviderMethod(INamedTypeSymbol provider, IMethodSymbol method)
    {
        if (method.ContainingType.TypeKind == TypeKind.Interface)
            return provider.FindImplementationForInterfaceMember(method) as IMethodSymbol ?? method;
        for (var type = provider; type is not null; type = type.BaseType)
            foreach (var candidate in type.GetMembers(method.Name).OfType<IMethodSymbol>())
                for (var current = candidate; current is not null; current = current.OverriddenMethod)
                    if (SymbolEqualityComparer.Default.Equals(current, method)) return candidate;
        return method;
    }

    internal static bool ResponseContentCompletion(IMethodSymbol source, SunderRpcContractDescriptor descriptor,
        string serviceId, string methodId)
    {
        var attribute = source.GetAttributes().SingleOrDefault(value =>
            value.AttributeClass is { } type && type.ToDisplayString() == typeof(SunderMethodAttribute).FullName
            && type.ContainingAssembly.Identity.Name == SdkAssemblyIdentity.Name);
        var option = attribute?.NamedArguments.FirstOrDefault(pair => pair.Key == nameof(SunderMethodAttribute.CompleteOnResponseContentRetirement));
        if (option is null || option.Value.Key is null) return false;
        if (option.Value.Value.Value is not bool enabled)
            throw Failure("rpc-authoring.method-invalid", "CompleteOnResponseContentRetirement requires a Boolean constant.", source);
        if (!enabled) return false;
        var method = descriptor.FindService(serviceId)!.Methods.Single(method => method.MethodId == methodId);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        if (source.IsAbstract || source.ContainingType.TypeKind == TypeKind.Interface
            || method.Kind != SunderRpcMethodKind.ServerStream || !Content(descriptor.Definitions[method.OutputSchemaReference[8..]]))
            throw Failure("rpc-authoring.method-invalid", "CompleteOnResponseContentRetirement belongs only on a provider implementation of a content-output server stream.", source);
        return true;

        bool Content(JsonElement schema)
        {
            if (schema.ValueKind != JsonValueKind.Object) return false;
            if (schema.TryGetProperty("$ref", out var reference))
            {
                var key = reference.GetString()![8..];
                return visited.Add(key) && Content(descriptor.Definitions[key]);
            }
            if (schema.TryGetProperty("logicalType", out var logical) && logical.GetString() == "content-reference") return true;
            return schema.TryGetProperty("properties", out var properties) && properties.EnumerateObject().Any(property => Content(property.Value))
                || schema.TryGetProperty("items", out var items) && Content(items)
                || schema.TryGetProperty("additionalProperties", out var additional) && Content(additional)
                || schema.TryGetProperty("oneOf", out var variants) && variants.EnumerateArray().Any(Content);
        }
    }
}
