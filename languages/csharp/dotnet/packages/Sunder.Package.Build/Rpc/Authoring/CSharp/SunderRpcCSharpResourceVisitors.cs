using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal sealed record SunderRpcCSharpResourceMember(string WireName, string MemberName, bool OptionalWrapper, bool NullableValue, string TypeName,
    bool OptionalServiceReference = false)
{
    internal string? ConstantValue { get; init; }
    internal bool IgnoreWhenNull { get; init; }
}
internal sealed record SunderRpcCSharpResourceLayout(string TypeName, bool ValueType,
    IReadOnlyList<SunderRpcCSharpResourceMember> Members, string? TransparentMember = null,
    IReadOnlyDictionary<string, string>? Variants = null, string? TransparentTypeName = null)
{
    internal string? ConverterTypeName { get; init; }
}

// Generates statically typed member/container traversal. The runtime context
// owns resources; this emitter never asks it to inspect arbitrary object graphs.
internal sealed class SunderRpcCSharpResourceVisitors(SunderRpcContractDescriptor descriptor, SunderRpcModelProjection projection,
    IReadOnlyDictionary<string, SunderRpcCSharpResourceLayout> layouts,
    IReadOnlyDictionary<string, string> referenceServices, string schemaExpression)
{
    private readonly Dictionary<string, bool> _resources = new(StringComparer.Ordinal);

    internal string? Send(string definition) => Contains(definition) ? "__Send_" + Symbol(definition) : null;
    internal string? Receive(string definition) => ContainsReference(definition, []) ? "__Receive_" + Symbol(definition) : null;

    internal void Emit(StringBuilder output)
    {
        output.AppendLine("private sealed class __ResourceWalk {")
            .AppendLine("    private readonly global::System.Collections.Generic.HashSet<object> _path = new(global::System.Collections.Generic.ReferenceEqualityComparer.Instance);")
            .AppendLine("    private int _depth; private int _nodes;")
            .AppendLine("    internal void Enter(object value) { if (++_nodes > 65536 || ++_depth > 64 || !_path.Add(value)) throw new global::System.Text.Json.JsonException(\"The resource value graph is cyclic or exceeds its bound.\"); }")
            .AppendLine("    internal void Exit(object value) { _path.Remove(value); _depth--; }")
            .AppendLine("}");
        foreach (var definition in descriptor.Definitions.Keys.Where(layouts.ContainsKey).Order(StringComparer.Ordinal))
        {
            if (!Contains(definition)) continue;
            var layout = layouts[definition];
            var name = "__Send_" + Symbol(definition);
            output.Append("private static global::System.Threading.Tasks.ValueTask ").Append(name)
                .Append("(global::Sunder.Sdk.Rpc.SunderRpcMarshallingContext context, ").Append(layout.TypeName)
                .AppendLine(" value, global::System.Threading.CancellationToken token) => " + name + "(context, value, token, new __ResourceWalk());")
                .Append("private static async global::System.Threading.Tasks.ValueTask ").Append(name)
                .Append("(global::Sunder.Sdk.Rpc.SunderRpcMarshallingContext context, ").Append(layout.TypeName)
                .AppendLine(" value, global::System.Threading.CancellationToken token, __ResourceWalk walk) {")
                .AppendLine("    token.ThrowIfCancellationRequested();");
            output.AppendLine("    await global::System.Threading.Tasks.ValueTask.CompletedTask;");
            if (!layout.ValueType) output.AppendLine("    if (value is null) return;");
            var tracked = !descriptor.Definitions[definition].TryGetProperty("$ref", out _) && !descriptor.Definitions[definition].TryGetProperty("oneOf", out _);
            if (tracked) output.AppendLine("    object node = value!; walk.Enter(node); try {");
            if (projection.ServiceReferences.ContainsKey(definition))
                output.AppendLine("    await context.RegisterServiceReferenceAsync(value, token).ConfigureAwait(false);");
            else EmitSend(output, descriptor.Definitions[definition], layout.TransparentMember is null ? "value" : "value." + layout.TransparentMember,
                layout.TransparentMember is null ? layout : Layout(layout.TransparentTypeName!), "r");
            if (tracked) output.AppendLine("    } finally { walk.Exit(node); }");
            output.AppendLine("}");
            if (Receive(definition) is not { } receive) continue;
            output.Append("private static global::System.Threading.Tasks.ValueTask ").Append(receive)
                .AppendLine("(global::Sunder.Sdk.Rpc.SunderRpcMarshallingContext context, global::System.Text.Json.JsonElement value, global::System.Threading.CancellationToken token) => " + receive + "(context, value, token, 0);")
                .Append("private static async global::System.Threading.Tasks.ValueTask ").Append(receive)
                .AppendLine("(global::Sunder.Sdk.Rpc.SunderRpcMarshallingContext context, global::System.Text.Json.JsonElement value, global::System.Threading.CancellationToken token, int depth) {")
                .AppendLine("    token.ThrowIfCancellationRequested(); if (depth > 64) throw new global::System.Text.Json.JsonException(\"The resource JSON graph exceeds its depth bound.\");")
                .AppendLine("    if (value.ValueKind == global::System.Text.Json.JsonValueKind.Null) return;");
            if (projection.ServiceReferences.TryGetValue(definition, out var target))
                output.Append("    await context.PrepareServiceReferenceAsync<").Append(referenceServices[target.ContractId + "/" + target.ServiceId]).AppendLine(">(value, token).ConfigureAwait(false);");
            else EmitReceive(output, descriptor.Definitions[definition], "value", "j", definition);
            output.AppendLine("}");
        }
    }

    private void EmitSend(StringBuilder output, JsonElement schema, string value, SunderRpcCSharpResourceLayout? layout, string prefix)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var key = Key(reference.GetString()!);
            if (Contains(key)) output.Append("    await __Send_").Append(Symbol(key)).Append("(context, ").Append(value).AppendLine(", token, walk).ConfigureAwait(false);");
            return;
        }
        if (schema.TryGetProperty("logicalType", out var logical) && logical.GetString() == "content-reference")
        { if (layout?.TypeName != "global::Sunder.Sdk.Rpc.SunderRpcContentReference") output.Append("    await context.RegisterContentAsync(").Append(value).AppendLine(", token).ConfigureAwait(false);"); return; }
        if (schema.TryGetProperty("oneOf", out var variants))
        {
            var nonnull = variants.EnumerateArray().Where(variant => !IsNull(variant)).ToArray();
            if (nonnull.Length == 1)
            {
                output.Append("    if (").Append(value).AppendLine(" is not null) {");
                var nullableValue = nonnull[0].TryGetProperty("$ref", out var nullableReference) && layouts[Key(nullableReference.GetString()!)].ValueType;
                EmitSend(output, nonnull[0], nullableValue ? value + ".Value" : value, layout, prefix + "n"); output.AppendLine("    }"); return;
            }
            if (layout?.Variants is null) throw new InvalidDataException("rpc-authoring.model-projection-invalid: A resource union has no generated variant layout.");
            var index = 0;
            output.Append("    switch (").Append(value).AppendLine(") {");
            foreach (var (definition, type) in layout.Variants)
            {
                var local = prefix + index++;
                output.Append("    case ").Append(type).Append(' ').Append(local).AppendLine(":");
                if (definition.StartsWith('{'))
                {
                    using var variantSchema = JsonDocument.Parse(definition);
                    output.Append("        walk.Enter(").Append(local).AppendLine("); try {");
                    EmitSend(output, variantSchema.RootElement, local, layouts.Values.Single(candidate => candidate.TypeName == type), local + "v");
                    output.Append("        } finally { walk.Exit(").Append(local).AppendLine("); }");
                }
                else if (Contains(definition)) output.Append("        await __Send_").Append(Symbol(definition)).Append("(context, ").Append(local).AppendLine(", token, walk).ConfigureAwait(false);");
                output.AppendLine("        break;");
            }
            output.AppendLine("    }"); return;
        }
        if (schema.TryGetProperty("items", out var items))
        {
            if (!ContainsSchema(items, [])) return;
            output.Append("    if (").Append(value).Append(" is not null) foreach (var ").Append(prefix).Append(" in ").Append(value).AppendLine(") {");
            EmitSend(output, items, prefix, ChildLayout(layout, 0), prefix + "i"); output.AppendLine("    }"); return;
        }
        if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.Object)
        {
            if (!ContainsSchema(additional, [])) return;
            output.Append("    if (").Append(value).Append(" is not null) foreach (var ").Append(prefix).Append(" in ").Append(value).AppendLine(".Values) {");
            EmitSend(output, additional, prefix, ChildLayout(layout, 1), prefix + "m"); output.AppendLine("    }"); return;
        }
        if (!schema.TryGetProperty("properties", out var properties)) return;
        if (layout is null) throw new InvalidDataException("rpc-authoring.model-projection-invalid: A resource record has no generated member layout.");
        var propertyIndex = 0;
        foreach (var property in properties.EnumerateObject())
        {
            if (!ContainsSchema(property.Value, [])) continue;
            var member = layout.Members.Single(field => field.WireName == property.Name);
            var expression = value + "." + member.MemberName;
            if (member.OptionalServiceReference)
            {
                output.Append("    await context.RegisterOptionalServiceReferenceAsync(").Append(expression).AppendLine(", token).ConfigureAwait(false);");
                continue;
            }
            if (member.OptionalWrapper) { output.Append("    if (").Append(expression).AppendLine(".IsPresent) {"); expression += ".Value"; }
            if (member.NullableValue) { output.Append("    if (").Append(expression).AppendLine(".HasValue) {"); expression += ".Value"; }
            var fieldSchema = member.NullableValue && property.Value.TryGetProperty("oneOf", out var nullableVariants)
                ? nullableVariants.EnumerateArray().Single(variant => !IsNull(variant)) : property.Value;
            var memberType = member.OptionalWrapper ? ChildType(member.TypeName, 0) : member.TypeName;
            EmitSend(output, fieldSchema, expression, Layout(memberType), prefix + "p" + propertyIndex++);
            if (member.NullableValue) output.AppendLine("    }");
            if (member.OptionalWrapper) output.AppendLine("    }");
        }
    }

    private void EmitReceive(StringBuilder output, JsonElement schema, string value, string prefix, string? ownerDefinition = null)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var key = Key(reference.GetString()!);
            if (ContainsReference(key, [])) output.Append("    await __Receive_").Append(Symbol(key)).Append("(context, ").Append(value).AppendLine(", token, depth + 1).ConfigureAwait(false);");
            return;
        }
        if (schema.TryGetProperty("oneOf", out var variants))
        {
            var index = 0;
            foreach (var variant in variants.EnumerateArray())
            {
                if (IsNull(variant)) continue;
                if (variant.TryGetProperty("$ref", out var target))
                {
                    output.Append("    if (").Append(schemaExpression).Append(".IsValid(").Append(Literal(target.GetString()!)).Append(", ").Append(value).AppendLine(", out _)) {");
                    EmitReceive(output, variant, value, prefix + index++); output.AppendLine("    }");
                }
                else
                {
                    var branch = prefix + index++;
                    var condition = InlineCondition(variant, value, branch);
                    if (condition is not null) output.Append("    if (").Append(condition).AppendLine(") {");
                    EmitReceive(output, variant, value, branch);
                    if (condition is not null) output.AppendLine("    }");
                }
            }
            return;
        }
        if (schema.TryGetProperty("items", out var items))
        {
            output.Append("    if (").Append(value).Append(".ValueKind == global::System.Text.Json.JsonValueKind.Array) foreach (var ").Append(prefix).Append(" in ").Append(value).AppendLine(".EnumerateArray()) {");
            EmitReceive(output, items, prefix, prefix + "i"); output.AppendLine("    }"); return;
        }
        if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.Object)
        {
            output.Append("    if (").Append(value).Append(".ValueKind == global::System.Text.Json.JsonValueKind.Object) foreach (var ").Append(prefix).Append(" in ").Append(value).AppendLine(".EnumerateObject()) {");
            EmitReceive(output, additional, prefix + ".Value", prefix + "m"); output.AppendLine("    }"); return;
        }
        if (!schema.TryGetProperty("properties", out var properties)) return;
        var fieldIndex = 0;
        foreach (var property in properties.EnumerateObject())
        {
            var local = prefix + fieldIndex++;
            output.Append("    if (").Append(value).Append(".ValueKind == global::System.Text.Json.JsonValueKind.Object && ").Append(value)
                .Append(".TryGetProperty(").Append(Literal(property.Name)).Append(", out var ").Append(local).AppendLine(")) {");
            if (ownerDefinition is not null && projection.OptionalServiceReferenceMembers.TryGetValue(ownerDefinition, out var optional)
                && optional.Contains(property.Name, StringComparer.Ordinal))
            {
                var target = OptionalReferenceTarget(property.Value);
                output.Append("    await context.PrepareOptionalServiceReferenceAsync<")
                    .Append(referenceServices[target.ContractId + "/" + target.ServiceId]).Append(">(").Append(local).AppendLine(", token).ConfigureAwait(false);");
            }
            else EmitReceive(output, property.Value, local, local + "p");
            output.AppendLine("    }");
        }
    }

    private SunderRpcServiceReferenceProjection OptionalReferenceTarget(JsonElement schema)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            if (schema.TryGetProperty("$ref", out var reference))
            {
                var key = Key(reference.GetString()!);
                if (projection.ServiceReferences.TryGetValue(key, out var target)) return target;
                if (!visited.Add(key)) throw new InvalidDataException("rpc-authoring.optional-reference-invalid: Reference alias cycle.");
                schema = descriptor.Definitions[key];
            }
            else schema = schema.GetProperty("oneOf").EnumerateArray().Single(variant => !IsNull(variant));
        }
    }

    private bool Contains(string definition)
    {
        if (_resources.TryGetValue(definition, out var value)) return value;
        return _resources[definition] = projection.ServiceReferences.ContainsKey(definition) || ContainsSchema(descriptor.Definitions[definition], new HashSet<string>(StringComparer.Ordinal) { definition });
    }

    private bool ContainsSchema(JsonElement schema, HashSet<string> path)
    {
        if (schema.ValueKind != JsonValueKind.Object) return schema.ValueKind == JsonValueKind.Array && schema.EnumerateArray().Any(item => ContainsSchema(item, path));
        if (schema.TryGetProperty("$ref", out var reference) && reference.ValueKind == JsonValueKind.String)
        {
            var key = Key(reference.GetString()!);
            if (projection.ServiceReferences.ContainsKey(key)) return true;
            if (!path.Add(key)) return false;
            var result = ContainsSchema(descriptor.Definitions[key], path); path.Remove(key); return result;
        }
        // Property-name maps can contain these names with schema objects as values.
        return schema.TryGetProperty("logicalType", out var logical) && logical.ValueKind == JsonValueKind.String && logical.GetString() == "content-reference"
            || schema.EnumerateObject().Any(property => ContainsSchema(property.Value, path));
    }

    private bool ContainsReference(string definition, HashSet<string> path)
    {
        if (projection.ServiceReferences.ContainsKey(definition)) return true;
        if (!path.Add(definition)) return false;
        var found = Visit(descriptor.Definitions[definition]); path.Remove(definition); return found;
        bool Visit(JsonElement value) => value.ValueKind == JsonValueKind.Object
            ? value.TryGetProperty("$ref", out var reference) && reference.ValueKind == JsonValueKind.String ? ContainsReference(Key(reference.GetString()!), path) : value.EnumerateObject().Any(property => Visit(property.Value))
            : value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(Visit);
    }
    private static string Key(string reference) => reference["#/$defs/".Length..];
    private static string? InlineCondition(JsonElement schema, string value, string prefix)
    {
        if (!schema.TryGetProperty("properties", out var properties)) return null;
        var tags = properties.EnumerateObject().Where(property => property.Value.TryGetProperty("const", out var constant) && constant.ValueKind == JsonValueKind.String).ToArray();
        if (tags.Length == 0) return null;
        return value + ".ValueKind == global::System.Text.Json.JsonValueKind.Object && " + string.Join(" && ", tags.Select((tag, index) =>
            value + ".TryGetProperty(" + Literal(tag.Name) + ", out var " + prefix + "tag" + index + ") && " + prefix + "tag" + index
                + ".ValueKind == global::System.Text.Json.JsonValueKind.String && " + prefix + "tag" + index + ".GetString() == " + Literal(tag.Value.GetProperty("const").GetString()!)));
    }
    private SunderRpcCSharpResourceLayout Layout(string type)
        => layouts.Values.FirstOrDefault(candidate => candidate.TypeName == type.TrimEnd('?')) ?? new(type, false, []);
    private SunderRpcCSharpResourceLayout? ChildLayout(SunderRpcCSharpResourceLayout? layout, int index)
        => layout is null ? null : Layout(ChildType(layout.TypeName, index));
    private static string ChildType(string type, int index)
    {
        TypeSyntax syntax = SyntaxFactory.ParseTypeName(type);
        if (syntax is NullableTypeSyntax nullable) syntax = nullable.ElementType;
        if (syntax is ArrayTypeSyntax array) return array.ElementType.ToString();
        var generic = syntax.DescendantNodesAndSelf().OfType<GenericNameSyntax>().FirstOrDefault();
        return generic is not null && generic.TypeArgumentList.Arguments.Count > index ? generic.TypeArgumentList.Arguments[index].ToString() : type;
    }
    private static string Symbol(string definition) => SunderRpcCSharpSyntax.Identifier(definition).TrimStart('@');
    private static string Literal(string value) => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true);
    private bool IsNull(JsonElement schema)
    {
        if (schema.TryGetProperty("$ref", out var reference)) return IsNull(descriptor.Definitions[Key(reference.GetString()!)]);
        return schema.TryGetProperty("type", out var type) && type.GetString() == "null";
    }
}
