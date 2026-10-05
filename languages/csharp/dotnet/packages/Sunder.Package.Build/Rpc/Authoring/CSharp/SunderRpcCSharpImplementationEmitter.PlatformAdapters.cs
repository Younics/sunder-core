using System.Security.Cryptography;
using System.Text;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal static partial class SunderRpcCSharpImplementationEmitter
{
    private static void EmitPlatformAdapterSchema(StringBuilder output, SunderRpcCSharpImportedContract contract)
    {
        // One descriptor per imported contract, not per contributor instance. These
        // exact bytes are generated into the consumer from verified cache evidence.
        output.Append("namespace ").Append(EscapeNamespace(contract.GeneratedLogicalName)).AppendLine("\n{")
            .AppendLine("    internal static class __SunderPlatformAdapterSchema\n    {")
            .AppendLine("        private static readonly global::System.Lazy<global::Sunder.Sdk.Rpc.SunderRpcContractDescriptor> Schema = new(static () =>")
            .Append("            global::Sunder.Sdk.Rpc.SunderRpcContractDescriptor.Parse(global::System.Text.Encoding.UTF8.GetBytes(\"")
            .Append(EscapeString(Encoding.UTF8.GetString(contract.Descriptor.GetCanonicalUtf8()))).AppendLine("\")));")
            .AppendLine("        internal static global::Sunder.Sdk.Rpc.SunderRpcContractDescriptor Descriptor => Schema.Value;")
            .AppendLine("        private static readonly global::System.Text.Json.JsonSerializerOptions JsonOptions = new(global::System.Text.Json.JsonSerializerDefaults.Web)")
            .AppendLine("        {")
            .AppendLine("            PropertyNameCaseInsensitive = false,")
            .AppendLine("            NumberHandling = global::System.Text.Json.Serialization.JsonNumberHandling.Strict,")
            .AppendLine("            DefaultIgnoreCondition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,")
            .AppendLine("            UnmappedMemberHandling = global::System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,")
            .AppendLine("            Converters = { new global::Sunder.Sdk.Rpc.SunderRpcJsonConverterFactory() },")
            .AppendLine("        };")
            .AppendLine("        internal static global::System.Text.Json.JsonElement Serialize<T>(T value, string reference)")
            .AppendLine("            => Validate(global::System.Text.Json.JsonSerializer.SerializeToElement(value, JsonOptions), reference);")
            .AppendLine("        internal static T Convert<T>(global::System.Text.Json.JsonElement value, string reference)")
            .AppendLine("            => global::System.Text.Json.JsonSerializer.Deserialize<T>(Validate(value, reference), JsonOptions)")
            .AppendLine("                ?? throw new global::System.Text.Json.JsonException(\"A platform adapter value cannot be null.\");")
            .AppendLine("        private static global::System.Text.Json.JsonElement Validate(global::System.Text.Json.JsonElement value, string reference)")
            .AppendLine("        {")
            .AppendLine("            if (!Schema.Value.IsValid(reference, value, out var error))")
            .AppendLine("                throw new global::System.Text.Json.JsonException(error ?? \"The platform adapter value differs from the selected descriptor.\");")
            .AppendLine("            return value;")
            .AppendLine("        }\n    }\n}");
    }

    private static void EmitPlatformAdapter(StringBuilder output, SunderRpcCSharpImportedProvider imported, bool hidesRegistration = false)
    {
        var adapter = imported.SemanticAdapter!; var provider = imported.Provider; var contract = imported.Surface.Contract;
        var model = SunderRpcCSharpGeneratorModelBuilder.Build(contract.Descriptor, "public");
        var service = model.Services.Single();
        var bridge = "__SunderPlatformAdapter_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(provider.ProviderId)));
        var adapterType = adapter.AdapterType.ToDisplayString(TypeDisplayFormat);
        var providerNamespace = provider.SourceType.ContainingNamespace.IsGlobalNamespace ? null : EscapeNamespace(provider.SourceType.ContainingNamespace.ToDisplayString());
        if (providerNamespace is not null) output.Append("namespace ").Append(providerNamespace).AppendLine("\n{");
        output.Append("partial class ").Append(EscapeIdentifier(provider.SourceType.Name)).AppendLine("\n{")
            .Append("    public ").Append(hidesRegistration ? "new " : string.Empty)
            .AppendLine("global::Sunder.Sdk.Worker.SunderWorkerProviderRegistration CreateSunderRpcProviderRegistration()")
            .AppendLine("        => new(")
            .Append("            \"").Append(EscapeString(provider.ProviderId)).AppendLine("\",")
            .Append("            \"").Append(EscapeString(contract.Descriptor.ContractId)).AppendLine("\",")
            .Append("            \"").Append(EscapeString(contract.Descriptor.Version)).AppendLine("\",")
            .Append("            \"").Append(contract.Descriptor.Sha256).AppendLine("\",")
            .Append("            \"").Append(contract.Descriptor.AbiSha256).AppendLine("\",")
            .Append("            new global::").Append(EscapeNamespace(contract.GeneratedLogicalName)).Append('.').Append(model.HandlerTypeName)
            .Append("(new global::").Append(EscapeNamespace(contract.GeneratedLogicalName)).Append('.').Append(bridge)
            .Append("(new ").Append(adapterType).Append("((").Append(adapter.DomainInterface.ToDisplayString(TypeDisplayFormat)).AppendLine(")this))));");
        EmitProviderDeclaration(output, contract.Descriptor, provider.ProviderId, hidesRegistration);
        output.AppendLine("}");
        if (providerNamespace is not null) output.AppendLine("}");
        output.Append("namespace ").Append(EscapeNamespace(contract.GeneratedLogicalName)).AppendLine("\n{")
            .Append("    internal sealed class ").Append(bridge).Append(" : ").Append(service.ProviderTypeName).AppendLine("\n    {")
            .Append("        private readonly ").Append(adapterType).AppendLine(" _adapter;")
            .Append("        internal ").Append(bridge).Append('(').Append(adapterType).AppendLine(" adapter) => _adapter = adapter;");
        foreach (var method in service.Methods)
        {
            var mapping = adapter.Methods.Single(value => value.Descriptor.MethodId == method.MethodId);
            output.Append("        public async global::System.Threading.Tasks.ValueTask<").Append(method.OutputTypeName).Append("> ")
                .Append(method.MethodName).Append("(global::Sunder.Sdk.Rpc.SunderRpcInvocationContext context, ").Append(method.RequestTypeName)
                .AppendLine(" request, global::System.Threading.CancellationToken cancellationToken = default)")
                .AppendLine("        {")
                .AppendLine("            cancellationToken.ThrowIfCancellationRequested();")
                .Append("            var input = __SunderPlatformAdapterSchema.Serialize(request, \"").Append(EscapeString(mapping.Descriptor.RequestSchemaReference)).AppendLine("\");")
                .Append(mapping.OutputType is null ? "            await " : "            var result = await ")
                .Append("_adapter.").Append(EscapeIdentifier(mapping.Method.Name)).Append('(');
            output.AppendJoin(", ", mapping.Method.Parameters.Select(parameter =>
                parameter.Type.Name == "CancellationToken" && parameter.Type.ContainingNamespace.ToDisplayString() == "System.Threading" ? "cancellationToken"
                : parameter.Type.Name == "SunderRpcInvocationContext" && parameter.Type.ContainingNamespace.ToDisplayString() == "Sunder.Sdk.Rpc" ? "context"
                : "__SunderPlatformAdapterSchema.Convert<" + parameter.Type.ToDisplayString(TypeDisplayFormat) + ">(input, \"" + EscapeString(mapping.Descriptor.RequestSchemaReference) + "\")"));
            output.AppendLine(").ConfigureAwait(false);");
            if (mapping.OutputType is null) output.Append("            return new ").Append(method.OutputTypeName).AppendLine("();");
            else output.Append("            return __SunderPlatformAdapterSchema.Convert<").Append(method.OutputTypeName)
                .Append(">(__SunderPlatformAdapterSchema.Serialize(result, \"").Append(EscapeString(mapping.Descriptor.OutputSchemaReference))
                .Append("\"), \"").Append(EscapeString(mapping.Descriptor.OutputSchemaReference)).AppendLine("\");");
            output.AppendLine("        }");
        }
        output.AppendLine("    }\n}");
    }
}
