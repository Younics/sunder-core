using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sunder.Sdk.Rpc;

internal static class SunderRpcCSharpGeneratorModelBuilder
{
    public static SunderRpcCSharpGeneratorModel Build(
        SunderRpcContractDescriptor descriptor,
        string accessibility)
    {
        var isV2 = descriptor.DescriptorVersion == SunderRpcProtocol.DescriptorVersionV2;
        if (isV2
            && !string.Equals(
                descriptor.SchemaProfile,
                SunderRpcProtocol.ValueSchemaProfile,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"C# generation supports RPC V2 schema profile '{SunderRpcProtocol.ValueSchemaProfile}'.");
        }

        var topLevelNames = new SymbolAllocator();
        var identityTypeName = topLevelNames.Allocate(
            SunderRpcCSharpSyntax.ContractIdentityTypeName(descriptor.ContractId, descriptor.Version));
        var serviceDrafts = descriptor.Services
            .OrderBy(static item => item.ServiceId, StringComparer.Ordinal)
            .Select(service =>
            {
                var stem = SunderRpcCSharpSyntax.Identifier(service.ServiceId);
                return new ServiceDraft(
                    service,
                    topLevelNames.Allocate("I" + stem + "Provider"),
                    topLevelNames.Allocate(stem + "Client"));
            })
            .ToArray();
        var handlerTypeName = topLevelNames.Allocate(
            SunderRpcCSharpSyntax.Identifier(descriptor.ContractId) + "RpcHandler");
        var orderedDefinitions = descriptor.Definitions
            .OrderBy(static item => item.Key, StringComparer.Ordinal)
            .ToArray();
        var generatedTypeNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var definitionDrafts = new List<DefinitionDraft>();
        foreach (var definition in orderedDefinitions)
        {
            var kind = GetDefinitionKind(definition.Value, descriptor.Definitions, isV2);
            if (kind is null) continue;
            var typeName = topLevelNames.Allocate(SunderRpcCSharpSyntax.Identifier(definition.Key));
            generatedTypeNames.Add(definition.Key, typeName);
            definitionDrafts.Add(new DefinitionDraft(definition.Key, typeName, definition.Value, kind.Value));
        }

        var inlineTypeNames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (isV2)
        {
            foreach (var definition in orderedDefinitions)
            {
                CollectInlineTaggedUnions(
                    definition.Value,
                    SunderRpcCSharpSyntax.Identifier(definition.Key),
                    "",
                    isRoot: true,
                    descriptor.Definitions,
                    inlineTypeNames,
                    definitionDrafts,
                    topLevelNames);
            }
            definitionDrafts = BuildUnionAndEnumDrafts(
                definitionDrafts,
                descriptor.Definitions,
                generatedTypeNames,
                topLevelNames);
        }

        var errorDrafts = isV2
            ? descriptor.Errors.Values
                .OrderBy(static declaration => declaration.Code, StringComparer.Ordinal)
                .Select(declaration => new ErrorDraft(
                    declaration,
                    topLevelNames.Allocate(SunderRpcCSharpSyntax.Identifier(declaration.Code) + "RpcError")))
                .ToArray()
            : [];

        var serviceMethodDrafts = new List<ServiceMethodDraft>(serviceDrafts.Length);
        foreach (var service in serviceDrafts)
        {
            var methodNames = new SymbolAllocator();
            var methods = service.Descriptor.Methods
                .OrderBy(static item => item.MethodId, StringComparer.Ordinal)
                .Select(method => new MethodDraft(
                    method,
                    methodNames.Allocate(SunderRpcCSharpSyntax.Identifier(method.MethodId) + "Async")))
                .ToArray();
            serviceMethodDrafts.Add(new ServiceMethodDraft(service, methods));
        }

        var context = new SemanticContext(
            descriptor.Definitions,
            generatedTypeNames,
            inlineTypeNames,
            isV2);
        var definitions = definitionDrafts
            .Select(definition => BuildDefinition(definition, context))
            .ToArray();
        var errors = errorDrafts
            .Select(error => BuildError(error, context))
            .ToArray();
        var services = serviceMethodDrafts
            .Select(service => BuildService(service, context))
            .ToArray();

        return new SunderRpcCSharpGeneratorModel(
            accessibility,
            isV2,
            identityTypeName,
            descriptor.DescriptorVersion,
            descriptor.SchemaProfile,
            descriptor.ContractId,
            descriptor.Version,
            descriptor.Sha256,
            descriptor.AbiSha256,
            Array.AsReadOnly(definitions),
            Array.AsReadOnly(errors),
            Array.AsReadOnly(services),
            handlerTypeName);
    }

    private static DefinitionKind? GetDefinitionKind(
        JsonElement schema,
        IReadOnlyDictionary<string, JsonElement> schemas,
        bool isV2)
    {
        if (IsRecordSchema(schema)) return DefinitionKind.Record;
        if (!isV2) return null;
        if (IsStringEnumSchema(schema)) return DefinitionKind.StringEnum;
        return IsTaggedUnionSchema(schema, schemas) ? DefinitionKind.TaggedUnion : null;
    }

    private static List<DefinitionDraft> BuildUnionAndEnumDrafts(
        List<DefinitionDraft> definitions,
        IReadOnlyDictionary<string, JsonElement> schemas,
        IReadOnlyDictionary<string, string> generatedTypeNames,
        SymbolAllocator topLevelNames)
    {
        for (var index = 0; index < definitions.Count; index++)
        {
            if (definitions[index].Kind == DefinitionKind.StringEnum)
            {
                definitions[index] = definitions[index] with
                {
                    ConverterTypeName = topLevelNames.Allocate(definitions[index].TypeName + "JsonConverter"),
                };
            }
        }

        var variantMemberships = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var definition in definitions.Where(static item => item.Kind == DefinitionKind.TaggedUnion))
        {
            foreach (var variant in definition.Schema.GetProperty("oneOf").EnumerateArray())
            {
                _ = ResolveSchema(variant, schemas, out var definitionName);
                if (definitionName is not null)
                {
                    variantMemberships[definitionName] = variantMemberships.GetValueOrDefault(definitionName) + 1;
                }
            }
        }

        var recordDerivations = new Dictionary<string, RecordDerivationDraft>(StringComparer.Ordinal);
        for (var index = 0; index < definitions.Count; index++)
        {
            var definition = definitions[index];
            if (definition.Kind != DefinitionKind.TaggedUnion) continue;

            var resolvedVariants = definition.Schema.GetProperty("oneOf").EnumerateArray()
                .Select(variant =>
                {
                    var resolved = ResolveSchema(variant, schemas, out var definitionName);
                    return new ResolvedUnionVariant(resolved, definitionName);
                })
                .ToArray();
            var discriminator = FindTaggedUnionDiscriminator(
                resolvedVariants.Select(static item => item.Schema).ToArray());
            var orderedVariants = resolvedVariants
                .Select(variant => new ResolvedUnionVariant(
                    variant.Schema,
                    variant.DefinitionName,
                    GetDiscriminatorValue(variant.Schema, discriminator)))
                .OrderBy(static item => item.Tag, StringComparer.Ordinal)
                .ToArray();
            var sharesNamedRecords = orderedVariants.All(variant =>
                variant.DefinitionName is not null
                && variantMemberships.GetValueOrDefault(variant.DefinitionName) == 1
                && generatedTypeNames.ContainsKey(variant.DefinitionName));
            var variants = new List<UnionVariantDraft>(orderedVariants.Length);
            foreach (var variant in orderedVariants)
            {
                string typeName;
                var isSynthetic = !sharesNamedRecords;
                if (sharesNamedRecords)
                {
                    typeName = generatedTypeNames[variant.DefinitionName!];
                    recordDerivations.Add(
                        variant.DefinitionName!,
                        new RecordDerivationDraft(definition.TypeName, discriminator, variant.Tag!));
                }
                else
                {
                    typeName = topLevelNames.Allocate(
                        definition.TypeName + SunderRpcCSharpSyntax.Identifier(variant.Tag!));
                }
                variants.Add(new UnionVariantDraft(typeName, variant.Schema, variant.Tag!, isSynthetic));
            }

            definitions[index] = definition with
            {
                ConverterTypeName = topLevelNames.Allocate(definition.TypeName + "JsonConverter"),
                Union = new TaggedUnionDraft(discriminator, variants),
            };
        }

        for (var index = 0; index < definitions.Count; index++)
        {
            var definition = definitions[index];
            if (definition.Kind == DefinitionKind.Record
                && recordDerivations.TryGetValue(definition.Name, out var derivation))
            {
                definitions[index] = definition with
                {
                    BaseTypeName = derivation.BaseTypeName,
                    DiscriminatorName = derivation.DiscriminatorName,
                    DiscriminatorValue = derivation.DiscriminatorValue,
                };
            }
        }
        return definitions;
    }

    private static void CollectInlineTaggedUnions(
        JsonElement schema,
        string ownerName,
        string pathName,
        bool isRoot,
        IReadOnlyDictionary<string, JsonElement> schemas,
        Dictionary<string, string> inlineTypeNames,
        List<DefinitionDraft> definitions,
        SymbolAllocator topLevelNames)
    {
        if (schema.TryGetProperty("$ref", out _)) return;
        if (!isRoot && IsTaggedUnionSchema(schema, schemas))
        {
            var key = schema.GetRawText();
            if (!inlineTypeNames.ContainsKey(key))
            {
                var typeName = topLevelNames.Allocate(ownerName + pathName + "Union");
                inlineTypeNames.Add(key, typeName);
                definitions.Add(new DefinitionDraft("\0" + typeName, typeName, schema, DefinitionKind.TaggedUnion));
            }
        }
        if (schema.TryGetProperty("oneOf", out var oneOf))
        {
            var index = 0;
            foreach (var variant in oneOf.EnumerateArray())
            {
                CollectInlineTaggedUnions(
                    variant,
                    ownerName,
                    pathName + "Variant" + (index++).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    isRoot: false,
                    schemas,
                    inlineTypeNames,
                    definitions,
                    topLevelNames);
            }
        }
        if (schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject().OrderBy(static item => item.Name, StringComparer.Ordinal))
            {
                CollectInlineTaggedUnions(
                    property.Value,
                    ownerName,
                    pathName + SunderRpcCSharpSyntax.Identifier(property.Name),
                    isRoot: false,
                    schemas,
                    inlineTypeNames,
                    definitions,
                    topLevelNames);
            }
        }
        if (schema.TryGetProperty("items", out var items))
        {
            CollectInlineTaggedUnions(
                items,
                ownerName,
                pathName + "Item",
                isRoot: false,
                schemas,
                inlineTypeNames,
                definitions,
                topLevelNames);
        }
        if (schema.TryGetProperty("additionalProperties", out var additionalProperties)
            && additionalProperties.ValueKind == JsonValueKind.Object)
        {
            CollectInlineTaggedUnions(
                additionalProperties,
                ownerName,
                pathName + "Value",
                isRoot: false,
                schemas,
                inlineTypeNames,
                definitions,
                topLevelNames);
        }
    }

    private static SunderRpcCSharpDefinitionModel BuildDefinition(
        DefinitionDraft definition,
        SemanticContext context)
        => definition.Kind switch
        {
            DefinitionKind.Record => new SunderRpcCSharpRecordDefinitionModel(
                BuildRecord(
                    definition.TypeName,
                    definition.Schema,
                    context,
                    definition.BaseTypeName,
                    definition.DiscriminatorName,
                    definition.DiscriminatorValue)),
            DefinitionKind.StringEnum => BuildStringEnum(definition),
            DefinitionKind.TaggedUnion => BuildTaggedUnion(definition, context),
            _ => throw new InvalidOperationException(
                $"Unsupported generated definition kind '{definition.Kind}'."),
        };

    private static SunderRpcCSharpRecordModel BuildRecord(
        string typeName,
        JsonElement schema,
        SemanticContext context,
        string? baseTypeName,
        string? discriminatorName,
        string? discriminatorValue)
    {
        var properties = schema.GetProperty("properties");
        var required = schema.TryGetProperty("required", out var requiredElement)
            ? requiredElement.EnumerateArray().Select(static item => item.GetString()!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var propertyNames = new SymbolAllocator();
        propertyNames.Reserve(typeName);
        var propertyModels = new List<SunderRpcCSharpPropertyModel>();

        foreach (var property in properties.EnumerateObject().OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            var propertyName = propertyNames.Allocate(SunderRpcCSharpSyntax.Identifier(property.Name));
            if (baseTypeName is not null
                && string.Equals(property.Name, discriminatorName, StringComparison.Ordinal))
            {
                propertyModels.Add(new SunderRpcCSharpPropertyModel(
                    property.Name,
                    propertyName,
                    "string",
                    IsRequired: false,
                    SunderRpcCSharpPropertySerialization.Default,
                    discriminatorValue));
                continue;
            }

            var propertyType = GetTypeName(property.Value, context);
            var isRequired = required.Contains(property.Name);
            var allowsNull = SchemaAllowsNull(property.Value, context);
            SunderRpcCSharpPropertySerialization serialization;
            if (!isRequired && context.IsV2 && allowsNull)
            {
                serialization = SunderRpcCSharpPropertySerialization.WhenWritingDefault;
                propertyType = "global::Sunder.Sdk.Rpc.SunderRpcOptional<" + propertyType + ">";
            }
            else if (!isRequired)
            {
                serialization = SunderRpcCSharpPropertySerialization.WhenWritingNull;
                propertyType = MakeNullable(propertyType);
            }
            else if (allowsNull)
            {
                serialization = SunderRpcCSharpPropertySerialization.Never;
            }
            else
            {
                serialization = SunderRpcCSharpPropertySerialization.Default;
            }
            propertyModels.Add(new SunderRpcCSharpPropertyModel(
                property.Name,
                propertyName,
                propertyType,
                isRequired,
                serialization,
                ConstantValue: null));
        }

        return new SunderRpcCSharpRecordModel(
            typeName,
            baseTypeName,
            Array.AsReadOnly(propertyModels.ToArray()));
    }

    private static SunderRpcCSharpStringEnumDefinitionModel BuildStringEnum(DefinitionDraft definition)
    {
        var values = definition.Schema.GetProperty("enum").EnumerateArray()
            .Select(static item => item.GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var memberNames = new SymbolAllocator();
        memberNames.Reserve(definition.TypeName);
        var members = values.Select((value, index) => new SunderRpcCSharpEnumMemberModel(
            value,
            memberNames.Allocate(SunderRpcCSharpSyntax.Identifier(value)),
            index)).ToArray();
        return new SunderRpcCSharpStringEnumDefinitionModel(
            definition.TypeName,
            definition.ConverterTypeName
            ?? throw new InvalidOperationException("A string enum definition is missing its converter name."),
            Array.AsReadOnly(members));
    }

    private static SunderRpcCSharpTaggedUnionDefinitionModel BuildTaggedUnion(
        DefinitionDraft definition,
        SemanticContext context)
    {
        var union = definition.Union
                    ?? throw new InvalidOperationException("A tagged union definition is missing its generator model.");
        var variants = union.Variants.Select(variant => new SunderRpcCSharpUnionVariantModel(
            variant.TypeName,
            variant.Tag,
            variant.IsSynthetic
                ? BuildRecord(
                    variant.TypeName,
                    variant.Schema,
                    context,
                    definition.TypeName,
                    union.DiscriminatorName,
                    variant.Tag)
                : null)).ToArray();
        return new SunderRpcCSharpTaggedUnionDefinitionModel(
            definition.TypeName,
            definition.ConverterTypeName
            ?? throw new InvalidOperationException("A tagged union definition is missing its converter name."),
            union.DiscriminatorName,
            Array.AsReadOnly(variants));
    }

    private static SunderRpcCSharpErrorModel BuildError(ErrorDraft error, SemanticContext context)
        => new(
            error.Declaration.Code,
            error.TypeName,
            DefaultErrorMessage(error.Declaration.Description),
            error.Declaration.DetailsSchema is null
                ? null
                : ReferenceType(error.Declaration.DetailsSchema, context));

    private static SunderRpcCSharpServiceModel BuildService(
        ServiceMethodDraft service,
        SemanticContext context)
    {
        var methods = service.Methods.Select(method =>
        {
            var descriptor = method.Descriptor;
            return new SunderRpcCSharpMethodModel(
                descriptor.MethodId,
                descriptor.Kind,
                method.MethodName,
                ReferenceType(descriptor.RequestSchemaReference, context),
                ReferenceType(descriptor.OutputSchemaReference, context),
                ReferenceAllowsNull(descriptor.RequestSchemaReference, context),
                ReferenceAllowsNull(descriptor.OutputSchemaReference, context),
                Array.AsReadOnly(descriptor.Errors.ToArray()));
        }).ToArray();
        return new SunderRpcCSharpServiceModel(
            service.Service.Descriptor.ServiceId,
            service.Service.ProviderTypeName,
            service.Service.ClientTypeName,
            Array.AsReadOnly(methods));
    }

    private static string DefaultErrorMessage(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return "The provider rejected the request.";
        var message = new string(description
            .Select(static character => char.IsControl(character) ? ' ' : character)
            .ToArray()).Trim();
        if (message.Length == 0) return "The provider rejected the request.";
        return message.Length <= 512 ? message : message[..512];
    }

    private static string FindTaggedUnionDiscriminator(IReadOnlyList<JsonElement> variants)
    {
        var candidates = GetTagCandidates(variants[0]).ToHashSet(StringComparer.Ordinal);
        foreach (var variant in variants.Skip(1)) candidates.IntersectWith(GetTagCandidates(variant));
        var valid = candidates.Where(candidate =>
        {
            var tags = new HashSet<string>(StringComparer.Ordinal);
            return variants.All(variant => TryGetDiscriminatorValue(variant, candidate, out var tag)
                                           && tags.Add(tag));
        }).ToArray();
        return valid.Length == 1
            ? valid[0]
            : throw new InvalidOperationException("A validated RPC tagged union has no unique discriminator.");
    }

    private static IEnumerable<string> GetTagCandidates(JsonElement schema)
    {
        var required = schema.GetProperty("required").EnumerateArray()
            .Select(static item => item.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var property in schema.GetProperty("properties").EnumerateObject())
        {
            if (required.Contains(property.Name)
                && property.Value.TryGetProperty("type", out var type)
                && type.GetString() == "string"
                && property.Value.TryGetProperty("const", out var constant)
                && constant.ValueKind == JsonValueKind.String)
            {
                yield return property.Name;
            }
        }
    }

    private static bool TryGetDiscriminatorValue(
        JsonElement schema,
        string propertyName,
        out string value)
    {
        if (schema.GetProperty("properties").TryGetProperty(propertyName, out var property)
            && property.TryGetProperty("const", out var constant)
            && constant.ValueKind == JsonValueKind.String)
        {
            value = constant.GetString()!;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static string GetDiscriminatorValue(JsonElement schema, string propertyName)
    {
        _ = TryGetDiscriminatorValue(schema, propertyName, out var value);
        return value;
    }

    private static JsonElement ResolveSchema(
        JsonElement schema,
        IReadOnlyDictionary<string, JsonElement> schemas,
        out string? definitionName)
    {
        definitionName = null;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (schema.TryGetProperty("$ref", out var reference))
        {
            SunderRpcDescriptorParser.TryGetReferenceName(reference.GetString(), out var name);
            if (!visited.Add(name))
            {
                throw new InvalidOperationException("A tagged-union variant contains a non-productive reference cycle.");
            }
            definitionName = name;
            schema = schemas[name];
        }
        return schema;
    }

    private static string GetTypeName(JsonElement schema, SemanticContext context)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            return ReferenceType(reference.GetString()!, context);
        }
        if (context.IsV2 && schema.TryGetProperty("contentEncoding", out var contentEncoding))
        {
            return contentEncoding.GetString() == "base64"
                ? "byte[]"
                : throw new InvalidOperationException("A validated RPC byte schema has an unsupported content encoding.");
        }
        if (context.IsV2 && schema.TryGetProperty("format", out var format))
        {
            return format.GetString() switch
            {
                "uuid" => "global::System.Guid",
                "date" => "global::System.DateOnly",
                "date-time" => "global::Sunder.Sdk.Rpc.SunderRpcOffsetDateTime",
                "duration" => "global::Sunder.Sdk.Rpc.SunderRpcDuration",
                "uri" => "global::Sunder.Sdk.Rpc.SunderRpcUri",
                "big-integer" => "global::System.Numerics.BigInteger",
                "decimal" => "global::Sunder.Sdk.Rpc.SunderRpcDecimal",
                "float32" => "float",
                "float64" => "double",
                _ => throw new InvalidOperationException("A validated RPC schema has an unsupported asserted format."),
            };
        }
        if (context.IsV2 && schema.TryGetProperty("logicalType", out var logicalType))
        {
            return logicalType.GetString() switch
            {
                "content-reference" => "global::Sunder.Sdk.Rpc.SunderRpcContentReference",
                _ => throw new InvalidOperationException("A validated RPC schema has an unsupported logical type."),
            };
        }
        if (schema.TryGetProperty("oneOf", out var oneOf))
        {
            if (context.InlineTypeNames.TryGetValue(schema.GetRawText(), out var inlineTypeName))
            {
                return inlineTypeName;
            }
            var variants = oneOf.EnumerateArray().ToArray();
            if (variants.Length == 2)
            {
                var nullCount = variants.Count(variant => IsNullSchema(variant, context));
                if (nullCount == 1)
                {
                    var valueSchema = variants.Single(variant => !IsNullSchema(variant, context));
                    return MakeNullable(GetTypeName(valueSchema, context));
                }
            }
            if (!context.IsV2) return "global::System.Text.Json.JsonElement";
            throw new InvalidOperationException(
                "Inline tagged unions cannot be represented by a stable generated C# type; place the union in $defs.");
        }
        if (context.IsV2 && IsUnconstrainedJsonSchema(schema))
        {
            return "global::System.Text.Json.JsonElement";
        }

        return schema.GetProperty("type").GetString() switch
        {
            "string" => "string",
            "integer" => "long",
            "number" when !context.IsV2 => "decimal",
            "number" => throw new InvalidOperationException(
                "A bare RPC number reached C# generation; V2 number schemas must assert float32 or float64."),
            "boolean" => "bool",
            "null" => "object?",
            "array" => "global::System.Collections.Generic.IReadOnlyList<" + GetTypeName(schema.GetProperty("items"), context) + ">",
            "object" when schema.TryGetProperty("additionalProperties", out var additionalProperties)
                          && additionalProperties.ValueKind == JsonValueKind.Object
                => "global::System.Collections.Generic.IReadOnlyDictionary<string, "
                   + GetTypeName(additionalProperties, context)
                   + ">",
            _ => "global::System.Text.Json.JsonElement",
        };
    }

    private static string ReferenceType(string reference, SemanticContext context)
    {
        var name = reference["#/$defs/".Length..];
        if (context.GeneratedTypeNames.TryGetValue(name, out var generatedTypeName)) return generatedTypeName;
        var schema = context.Schemas[name];
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (schema.TryGetProperty("$ref", out var nestedReference))
        {
            if (!visited.Add(name))
            {
                throw new InvalidOperationException("A non-productive RPC definition alias cycle reached C# generation.");
            }
            name = nestedReference.GetString()!["#/$defs/".Length..];
            if (context.GeneratedTypeNames.TryGetValue(name, out generatedTypeName)) return generatedTypeName;
            schema = context.Schemas[name];
        }
        return GetTypeName(schema, context);
    }

    private static bool ReferenceAllowsNull(string reference, SemanticContext context)
    {
        SunderRpcDescriptorParser.TryGetReferenceName(reference, out var name);
        return SchemaAllowsNull(context.Schemas[name], context);
    }

    private static bool SchemaAllowsNull(JsonElement schema, SemanticContext context)
        => SchemaAllowsNull(schema, context, new HashSet<string>(StringComparer.Ordinal));

    private static bool SchemaAllowsNull(
        JsonElement schema,
        SemanticContext context,
        HashSet<string> referenceStack)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            SunderRpcDescriptorParser.TryGetReferenceName(reference.GetString(), out var name);
            if (!referenceStack.Add(name)) return false;
            var allowsNull = SchemaAllowsNull(context.Schemas[name], context, referenceStack);
            referenceStack.Remove(name);
            return allowsNull;
        }
        if (context.IsV2 && IsUnconstrainedJsonSchema(schema))
        {
            return true;
        }
        if (schema.TryGetProperty("type", out var type) && type.GetString() == "null")
        {
            return true;
        }
        return schema.TryGetProperty("oneOf", out var oneOf)
               && oneOf.EnumerateArray().Count(variant =>
                   SchemaAllowsNull(variant, context, referenceStack)) == 1;
    }

    private static bool IsNullSchema(JsonElement schema, SemanticContext context)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (schema.TryGetProperty("$ref", out var reference))
        {
            SunderRpcDescriptorParser.TryGetReferenceName(reference.GetString(), out var name);
            if (!visited.Add(name)) return false;
            schema = context.Schemas[name];
        }
        return schema.TryGetProperty("type", out var type) && type.GetString() == "null";
    }

    private static bool IsRecordSchema(JsonElement schema)
        => schema.TryGetProperty("type", out var type)
           && type.GetString() == "object"
           && schema.TryGetProperty("properties", out _)
           && schema.GetProperty("additionalProperties").ValueKind == JsonValueKind.False;

    private static bool IsStringEnumSchema(JsonElement schema)
        => schema.TryGetProperty("type", out var type)
           && type.GetString() == "string"
           && schema.TryGetProperty("enum", out var values)
           && values.ValueKind == JsonValueKind.Array;

    private static bool IsTaggedUnionSchema(
        JsonElement schema,
        IReadOnlyDictionary<string, JsonElement> schemas)
    {
        if (!schema.TryGetProperty("oneOf", out var oneOf)
            || oneOf.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        return oneOf.EnumerateArray().All(variant =>
        {
            var resolved = ResolveSchema(variant, schemas, out _);
            return !resolved.TryGetProperty("type", out var type) || type.GetString() != "null";
        });
    }

    private static bool IsUnconstrainedJsonSchema(JsonElement schema)
        => schema.EnumerateObject().All(static property => property.Name is "title" or "description");

    private static string MakeNullable(string typeName)
        => typeName.EndsWith("?", StringComparison.Ordinal) ? typeName : typeName + "?";

    private sealed class SymbolAllocator
    {
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);

        public void Reserve(string value) => _used.Add(value);

        public string Allocate(string preferred)
        {
            preferred = SunderRpcCSharpSyntax.EscapeIdentifier(preferred);
            if (_used.Add(preferred)) return preferred;
            var unescaped = preferred.StartsWith('@') ? preferred[1..] : preferred;
            for (var suffix = 2; ; suffix++)
            {
                var candidate = SunderRpcCSharpSyntax.EscapeIdentifier(
                    unescaped + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (_used.Add(candidate)) return candidate;
            }
        }
    }

    private enum DefinitionKind
    {
        Record,
        StringEnum,
        TaggedUnion,
    }

    private sealed record DefinitionDraft(
        string Name,
        string TypeName,
        JsonElement Schema,
        DefinitionKind Kind,
        string? ConverterTypeName = null,
        TaggedUnionDraft? Union = null,
        string? BaseTypeName = null,
        string? DiscriminatorName = null,
        string? DiscriminatorValue = null);

    private sealed record TaggedUnionDraft(
        string DiscriminatorName,
        IReadOnlyList<UnionVariantDraft> Variants);

    private sealed record UnionVariantDraft(
        string TypeName,
        JsonElement Schema,
        string Tag,
        bool IsSynthetic);

    private sealed record ResolvedUnionVariant(
        JsonElement Schema,
        string? DefinitionName,
        string? Tag = null);

    private sealed record RecordDerivationDraft(
        string BaseTypeName,
        string DiscriminatorName,
        string DiscriminatorValue);

    private sealed record ServiceDraft(
        SunderRpcServiceDescriptor Descriptor,
        string ProviderTypeName,
        string ClientTypeName);

    private sealed record MethodDraft(
        SunderRpcMethodDescriptor Descriptor,
        string MethodName);

    private sealed record ServiceMethodDraft(
        ServiceDraft Service,
        IReadOnlyList<MethodDraft> Methods);

    private sealed record ErrorDraft(
        SunderRpcErrorDeclaration Declaration,
        string TypeName);

    private sealed record SemanticContext(
        IReadOnlyDictionary<string, JsonElement> Schemas,
        IReadOnlyDictionary<string, string> GeneratedTypeNames,
        IReadOnlyDictionary<string, string> InlineTypeNames,
        bool IsV2);
}

internal sealed record SunderRpcCSharpGeneratorModel(
    string Accessibility,
    bool IsV2,
    string IdentityTypeName,
    int DescriptorVersion,
    string? SchemaProfile,
    string ContractId,
    string ContractVersion,
    string DescriptorSha256,
    string? AbiSha256,
    IReadOnlyList<SunderRpcCSharpDefinitionModel> Definitions,
    IReadOnlyList<SunderRpcCSharpErrorModel> Errors,
    IReadOnlyList<SunderRpcCSharpServiceModel> Services,
    string HandlerTypeName);

internal abstract record SunderRpcCSharpDefinitionModel(string TypeName);

internal sealed record SunderRpcCSharpRecordDefinitionModel(SunderRpcCSharpRecordModel Record)
    : SunderRpcCSharpDefinitionModel(Record.TypeName);

internal sealed record SunderRpcCSharpStringEnumDefinitionModel(
    string TypeName,
    string ConverterTypeName,
    IReadOnlyList<SunderRpcCSharpEnumMemberModel> Members)
    : SunderRpcCSharpDefinitionModel(TypeName);

internal sealed record SunderRpcCSharpTaggedUnionDefinitionModel(
    string TypeName,
    string ConverterTypeName,
    string DiscriminatorName,
    IReadOnlyList<SunderRpcCSharpUnionVariantModel> Variants)
    : SunderRpcCSharpDefinitionModel(TypeName);

internal sealed record SunderRpcCSharpRecordModel(
    string TypeName,
    string? BaseTypeName,
    IReadOnlyList<SunderRpcCSharpPropertyModel> Properties);

internal sealed record SunderRpcCSharpPropertyModel(
    string WireName,
    string Name,
    string TypeName,
    bool IsRequired,
    SunderRpcCSharpPropertySerialization Serialization,
    string? ConstantValue);

internal enum SunderRpcCSharpPropertySerialization
{
    Default,
    WhenWritingDefault,
    WhenWritingNull,
    Never,
}

internal sealed record SunderRpcCSharpEnumMemberModel(string WireValue, string Name, int Ordinal);

internal sealed record SunderRpcCSharpUnionVariantModel(
    string TypeName,
    string Tag,
    SunderRpcCSharpRecordModel? SyntheticRecord);

internal sealed record SunderRpcCSharpErrorModel(
    string Code,
    string TypeName,
    string DefaultMessage,
    string? DetailsTypeName);

internal sealed record SunderRpcCSharpMethodModel(
    string MethodId,
    SunderRpcMethodKind Kind,
    string MethodName,
    string RequestTypeName,
    string OutputTypeName,
    bool RequestAllowsNull,
    bool OutputAllowsNull,
    IReadOnlyList<string> Errors);

internal sealed record SunderRpcCSharpServiceModel(
    string ServiceId,
    string ProviderTypeName,
    string ClientTypeName,
    IReadOnlyList<SunderRpcCSharpMethodModel> Methods);

internal static class SunderRpcCSharpSyntax
{
    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while", "add", "alias", "and", "ascending", "async", "await", "by",
        "descending", "dynamic", "equals", "file", "from", "get", "global", "group", "init", "into",
        "join", "let", "managed", "nameof", "not", "notnull", "on", "or", "orderby", "partial",
        "record", "remove", "required", "scoped", "select", "set", "unmanaged", "value", "var", "when",
        "where", "with", "yield",
    };

    public static string ContractIdentityTypeName(string contractId, string contractVersion)
    {
        var identity = Encoding.UTF8.GetBytes(contractId + "\0" + contractVersion);
        return "SunderRpcContractIdentity_"
               + Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant();
    }

    public static string Identifier(string value)
    {
        var output = new StringBuilder();
        var capitalize = true;
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character))
            {
                capitalize = true;
                continue;
            }
            output.Append(capitalize ? char.ToUpperInvariant(character) : character);
            capitalize = false;
        }
        if (output.Length == 0) output.Append("Value");
        if (char.IsAsciiDigit(output[0])) output.Insert(0, '_');
        return EscapeIdentifier(output.ToString());
    }

    public static string EscapeIdentifier(string value)
        => CSharpKeywords.Contains(value) ? "@" + value : value;

    public static string EscapeNamespace(string value)
        => string.Join('.', value.Split('.').Select(EscapeIdentifier));

    public static bool IsNamespace(string value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length <= 512
           && value.Split('.').All(static segment => segment.Length > 0
               && (char.IsAsciiLetter(segment[0]) || segment[0] == '_')
               && segment.All(static character => char.IsAsciiLetterOrDigit(character) || character == '_'));

    public static string EscapeStringLiteral(string value)
    {
        var output = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\': output.Append("\\\\"); break;
                case '"': output.Append("\\\""); break;
                case '\0': output.Append("\\0"); break;
                case '\a': output.Append("\\a"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                case '\v': output.Append("\\v"); break;
                default:
                    if (char.IsControl(character) || character is '\u2028' or '\u2029')
                    {
                        output.Append("\\u").Append(((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        output.Append(character);
                    }
                    break;
            }
        }
        return output.ToString();
    }
}
