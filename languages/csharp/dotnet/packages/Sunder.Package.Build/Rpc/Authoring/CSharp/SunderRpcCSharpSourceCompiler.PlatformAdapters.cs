using System.Globalization;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal sealed record SunderRpcCSharpPlatformAdapter(
    SunderRpcCSharpImportedProviderSurface Surface, INamedTypeSymbol AdapterType, INamedTypeSymbol DomainInterface,
    IReadOnlyList<SunderRpcCSharpPlatformAdapterMethod> Methods);

internal sealed record SunderRpcCSharpPlatformAdapterMethod(
    SunderRpcMethodDescriptor Descriptor, IMethodSymbol Method, ITypeSymbol? RequestType, ITypeSymbol? OutputType);

internal sealed partial class SunderRpcCSharpSourceCompiler
{
    // Marker metadata only selects a local domain adapter. The independently verified
    // platform surface remains the sole source of contract identity and wire schemas.
    private static SunderRpcCSharpPlatformAdapter? CompilePlatformAdapter(INamedTypeSymbol service, AttributeSymbols attributes,
        IEnumerable<SunderRpcCSharpImportedProviderSurface> surfaces, INamedTypeSymbol provider)
        => CompilePlatformAdapter(service, attributes.PlatformImport, attributes.Method, surfaces, provider);

    // Core engineering projects bind their own checked source descriptor; this is
    // not a package-author trust composition or a substitute for an imported lock.
    internal static SunderRpcCSharpPlatformAdapter CompileCorePlatformAdapter(INamedTypeSymbol service, SunderRpcCSharpImportedContract contract)
    {
        var marker = service.GetAttributes().Select(attribute => attribute.AttributeClass).FirstOrDefault(type =>
            type is not null && type.ToDisplayString() == typeof(SunderRpcPlatformImportAttribute).FullName
            && type.ContainingAssembly.Identity.Name == SdkAssemblyIdentity.Name && type.ContainingAssembly.Identity.Version == SdkAssemblyIdentity.Version)
            ?? throw new InvalidDataException("Core domain interface lacks the coordinated SDK platform marker.");
        var method = marker.ContainingAssembly.GetTypeByMetadataName(typeof(SunderRpcMethodAttribute).FullName!)
            ?? throw new InvalidDataException("Core domain interface lacks coordinated SDK method metadata.");
        return CompilePlatformAdapter(service, marker, method, [new("core", contract, "") { IsPlatform = true }], service)
            ?? throw new InvalidDataException("Core domain adapter could not be bound to its source descriptor.");
    }

    private static SunderRpcCSharpPlatformAdapter? CompilePlatformAdapter(INamedTypeSymbol service, INamedTypeSymbol platformAttribute,
        INamedTypeSymbol methodAttribute, IEnumerable<SunderRpcCSharpImportedProviderSurface> surfaces, INamedTypeSymbol provider)
    {
        var declarations = service.AllInterfaces.Prepend(service)
            .Select(type => (Type: type, Attribute: GetAttribute(type, platformAttribute)))
            .Where(item => item.Attribute is not null).ToArray();
        if (declarations.Length == 0) return null;
        if (declarations.Length != 1 || service.TypeKind != TypeKind.Interface)
            throw Failure("rpc-authoring.provider-invalid", "A domain provider must select exactly one platform-import interface.", provider);
        var declaration = declarations[0]; var attribute = declaration.Attribute!;
        if (attribute.ConstructorArguments.Length != 2 || attribute.ConstructorArguments[0].Value is not string id
            || !PackageId.TryParse(id, out _) || attribute.ConstructorArguments[1].Value is not INamedTypeSymbol adapter
            || adapter.TypeKind != TypeKind.Class || adapter.IsAbstract || adapter.IsGenericType || adapter.ContainingType is not null
            || adapter.DeclaredAccessibility != Accessibility.Public
            || !adapter.InstanceConstructors.Any(constructor => constructor.DeclaredAccessibility == Accessibility.Public
                && constructor.Parameters.Length == 1 && constructor.Parameters[0].RefKind == RefKind.None
                && SymbolEqualityComparer.Default.Equals(constructor.Parameters[0].Type, declaration.Type)))
            throw Failure("rpc-authoring.provider-invalid", "A platform domain adapter must be a public non-generic class with a constructor accepting its declared domain interface.", provider);
        var matching = surfaces.Where(surface => surface.IsPlatform && surface.Contract.Descriptor.ContractId == id).ToArray();
        if (matching.Length != 1)
            throw Failure("platform-api.import-not-found", $"Domain provider '{provider.Name}' requires an explicit authenticated platform import for '{id}'.", provider);
        var surface = matching[0]; var descriptor = surface.Contract.Descriptor;
        if (descriptor.Services.Count != 1 || descriptor.Services[0].Methods.Any(method => method.Kind != SunderRpcMethodKind.Unary))
            throw Failure("rpc-authoring.handler-incomplete", "The bounded semantic adapter profile supports exactly one unary service.", provider);
        var declared = adapter.GetMembers().OfType<IMethodSymbol>().Where(method => HasAttribute(method, methodAttribute)).ToArray();
        if (declared.Length != descriptor.Services[0].Methods.Count)
            throw Failure("rpc-authoring.handler-incomplete", "The semantic adapter must cover the complete selected platform service.", provider);
        var methods = new List<SunderRpcCSharpPlatformAdapterMethod>();
        var graph = new PlatformAdapterValueGraph(descriptor, provider);
        foreach (var method in declared)
        {
            var metadata = GetAttribute(method, methodAttribute)!;
            var methodId = GetNamedString(metadata, nameof(SunderRpcMethodAttribute.MethodId));
            var wire = descriptor.Services[0].Methods.SingleOrDefault(value => value.MethodId == methodId);
            if (wire is null || methods.Any(value => value.Descriptor.MethodId == methodId)
                || method.MethodKind != MethodKind.Ordinary || method.IsStatic || method.IsGenericMethod
                || method.DeclaredAccessibility != Accessibility.Public || method.ReturnType.NullableAnnotation == NullableAnnotation.Annotated
                || GetNamedString(metadata, nameof(SunderRpcMethodAttribute.RequestDefinitionName)) is not null
                || GetNamedString(metadata, nameof(SunderRpcMethodAttribute.OutputDefinitionName)) is not null
                || GetNamedInt(metadata, nameof(SunderRpcMethodAttribute.Idempotency)) != (int)wire.Idempotency
                || !GetNamedStrings(metadata, nameof(SunderRpcMethodAttribute.Errors)).Order(StringComparer.Ordinal).SequenceEqual(wire.Errors.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                throw Failure("rpc-authoring.handler-incomplete", "Semantic adapter methods must match every selected method, idempotency and domain-error declaration.", provider);
            ITypeSymbol? request = null; var context = false; var cancellation = false;
            foreach (var parameter in method.Parameters)
            {
                if (parameter.RefKind != RefKind.None || parameter.IsParams)
                    throw Failure("rpc-authoring.handler-incomplete", "Semantic adapter parameters cannot use ref/in/out or params.", provider);
                if (IsInvocationContext(parameter.Type) && !context && !cancellation) context = true;
                else if (IsCancellationToken(parameter.Type) && !cancellation) cancellation = true;
                else if (request is null && !context && !cancellation && !parameter.IsOptional) request = parameter.Type;
                else throw Failure("rpc-authoring.handler-incomplete", "A semantic adapter accepts at most one request followed by context and cancellation.", provider);
            }
            ITypeSymbol? output = null;
            if (method.ReturnType is INamedTypeSymbol { Name: "Task" or "ValueTask", Arity: 0 } unit
                && unit.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks") graph.RequireEmpty(wire.OutputSchemaReference);
            else
            {
                var shape = UnwrapOutput(method.ReturnType, method);
                if (shape.Kind != "unary") throw Failure("rpc-authoring.handler-incomplete", "Semantic adapters require unary asynchronous methods.", provider);
                output = shape.OutputType; graph.Validate(wire.OutputSchemaReference, output);
            }
            if (request is null) graph.RequireEmpty(wire.RequestSchemaReference); else graph.Validate(wire.RequestSchemaReference, request);
            methods.Add(new(wire, method, request, output));
        }
        return new(surface, adapter, declaration.Type, methods.OrderBy(value => value.Descriptor.MethodId, StringComparer.Ordinal).ToArray());
    }

    // Domain DTOs are local implementation inputs, never descriptor authorities.
    // Prove their complete JSON representation against the selected descriptor without
    // loading/executing a referenced assembly. Generated codecs enforce value bounds.
    private sealed class PlatformAdapterValueGraph(SunderRpcContractDescriptor descriptor, ISymbol provider)
    {
        private readonly HashSet<string> _visited = new(StringComparer.Ordinal);
        private int _nodes;
        private CompilerFailure Invalid() => Failure("rpc-authoring.handler-incomplete",
            "The semantic adapter's closed domain DTO representation does not match the selected platform descriptor.", provider);

        internal void RequireEmpty(string reference)
        {
            var value = Resolve(reference);
            if (!value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "object"
                || !value.TryGetProperty("properties", out var properties) || properties.EnumerateObject().Any()
                || !value.TryGetProperty("additionalProperties", out var additional) || additional.ValueKind != JsonValueKind.False) throw Invalid();
        }

        internal void Validate(string reference, ITypeSymbol type) => Visit(Resolve(reference), type, false, 0);

        private JsonElement Resolve(string reference)
        {
            if (!reference.StartsWith("#/$defs/", StringComparison.Ordinal)
                || !descriptor.Definitions.TryGetValue(reference[8..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal), out var value)) throw Invalid();
            return value;
        }

        private void Visit(JsonElement schema, ITypeSymbol type, bool optional, int depth)
        {
            if (++_nodes > 4096 || depth > 64) throw Invalid();
            var nullable = type.NullableAnnotation == NullableAnnotation.Annotated;
            if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } wrapper)
            { nullable = true; type = wrapper.TypeArguments[0]; }
            if (nullable && !optional) throw Invalid();
            if (schema.TryGetProperty("$ref", out var reference)) { Visit(Resolve(reference.GetString()!), type.WithNullableAnnotation(NullableAnnotation.NotAnnotated), false, depth + 1); return; }
            if (schema.TryGetProperty("logicalType", out var logical))
            {
                if (logical.GetString() != "content-reference" || type.ToDisplayString() != "Sunder.Sdk.Rpc.SunderRpcContentReference"
                    || type.ContainingAssembly.Identity.Name != SdkAssemblyIdentity.Name || type.ContainingAssembly.Identity.Version != SdkAssemblyIdentity.Version) throw Invalid();
                return;
            }
            if (!schema.TryGetProperty("type", out var kind) || kind.ValueKind != JsonValueKind.String) throw Invalid();
            if (type.GetAttributes().Any(IsJsonAttribute)) throw Invalid();
            switch (kind.GetString())
            {
                case "string":
                    if (type.SpecialType != SpecialType.System_String || schema.TryGetProperty("enum", out _)) throw Invalid();
                    return;
                case "boolean":
                    if (type.SpecialType != SpecialType.System_Boolean) throw Invalid();
                    return;
                case "integer":
                    if (type.TypeKind == TypeKind.Enum)
                    {
                        if (!schema.TryGetProperty("enum", out var values)) throw Invalid();
                        var actual = ((INamedTypeSymbol)type).GetMembers().OfType<IFieldSymbol>().Where(field => field.HasConstantValue)
                            .Select(field => Convert.ToInt64(field.ConstantValue, CultureInfo.InvariantCulture)).Order().ToArray();
                        if (!actual.SequenceEqual(values.EnumerateArray().Select(value => value.GetInt64()).Order())) throw Invalid();
                        return;
                    }
                    if (type.SpecialType != SpecialType.System_Int32
                        || !schema.TryGetProperty("minimum", out var minimum) || minimum.GetDouble() < int.MinValue
                        || !schema.TryGetProperty("maximum", out var maximum) || maximum.GetDouble() > int.MaxValue) throw Invalid();
                    return;
                case "array":
                    ITypeSymbol element;
                    if (type is IArrayTypeSymbol { Rank: 1 } array) element = array.ElementType;
                    else if (type is INamedTypeSymbol { Arity: 1, Name: "IReadOnlyList" } list
                             && list.ContainingNamespace.ToDisplayString() == "System.Collections.Generic") element = list.TypeArguments[0];
                    else throw Invalid();
                    Visit(schema.GetProperty("items"), element, false, depth + 1);
                    return;
                case "object":
                    if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.Object)
                    {
                        if (type is not INamedTypeSymbol { Arity: 2, Name: "IReadOnlyDictionary" } map
                            || map.ContainingNamespace.ToDisplayString() != "System.Collections.Generic"
                            || map.TypeArguments[0].SpecialType != SpecialType.System_String) throw Invalid();
                        Visit(additional, map.TypeArguments[1], false, depth + 1); return;
                    }
                    if (type is not INamedTypeSymbol { IsGenericType: false, TypeKind: TypeKind.Class, DeclaredAccessibility: Accessibility.Public } record
                        || record.IsAbstract || record.BaseType?.SpecialType != SpecialType.System_Object || !schema.TryGetProperty("properties", out var fields)
                        || record.GetMembers().Any(member => member.GetAttributes().Any(IsJsonAttribute))) throw Invalid();
                    if (!_visited.Add(record.ToDisplayString() + "\0" + schema.GetRawText())) return;
                    var properties = record.GetMembers().OfType<IPropertySymbol>().Where(property => !property.IsStatic
                        && property.GetMethod?.DeclaredAccessibility == Accessibility.Public).ToArray();
                    if (properties.Length != fields.EnumerateObject().Count() || properties.Any(property => property.IsIndexer || property.GetAttributes().Any(IsJsonAttribute))) throw Invalid();
                    var constructors = record.InstanceConstructors.Where(constructor => constructor.DeclaredAccessibility == Accessibility.Public).ToArray();
                    if (constructors.Any(constructor => constructor.Parameters.Length == 0))
                    {
                        if (properties.Any(property => property.SetMethod?.DeclaredAccessibility != Accessibility.Public)) throw Invalid();
                    }
                    else if (constructors.Length != 1 || constructors[0].Parameters.Length != properties.Length
                        || constructors[0].Parameters.Any(parameter => parameter.RefKind != RefKind.None || !properties.Any(property =>
                            string.Equals(property.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)
                            && SymbolEqualityComparer.Default.Equals(property.Type, parameter.Type)))) throw Invalid();
                    var required = schema.TryGetProperty("required", out var names) ? names.EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal) : [];
                    var mapped = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in properties)
                    {
                        var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                        if (!mapped.Add(name) || !fields.TryGetProperty(name, out var value)) throw Invalid();
                        Visit(value, property.Type, !required.Contains(name), depth + 1);
                    }
                    return;
                default: throw Invalid();
            }
        }

        private static bool IsJsonAttribute(AttributeData attribute)
            => attribute.AttributeClass?.ContainingNamespace.ToDisplayString() == "System.Text.Json.Serialization";
    }
}
