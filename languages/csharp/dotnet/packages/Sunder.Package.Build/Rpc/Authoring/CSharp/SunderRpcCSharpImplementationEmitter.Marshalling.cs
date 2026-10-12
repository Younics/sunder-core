using System.Text;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal static partial class SunderRpcCSharpImplementationEmitter
{
    private static void EmitOwnedMarshalledDispatch(StringBuilder output, SunderRpcCSharpCompiledContract contract, SunderRpcCSharpMarshallingPlan plan, Microsoft.CodeAnalysis.INamedTypeSymbol providerType)
    {
        var operations = contract.Methods.Select((method, index) => new MarshalledOperation(
            contract.Descriptor.Services.Single().ServiceId, method.MethodId, method.RequestDefinition, method.OutputDefinition, method.ReturnsUnit, method.Kind == "server-stream", index,
            input => {
                var parameters = SunderRpcCSharpMarshallingPlan.WireParameters(method.Source);
                var valueIndex = 0;
                return "((" + contract.ServiceType.ToDisplayString(TypeDisplayFormat) + ")_implementation)." + EscapeIdentifier(method.Source.Name) + "(" + string.Join(", ", method.Source.Parameters.Select(parameter =>
                    EscapeIdentifier(parameter.Name) + ": " + (IsInvocationContext(parameter.Type) ? "context" : IsCancellationToken(parameter.Type) ? "token"
                        : parameters.Count == 1 ? input : input + ".P" + valueIndex++))) + ")";
            }) { CompleteOnResponseContentRetirement = SunderRpcCSharpSourceCompiler.ResponseContentCompletion(
                SunderRpcCSharpSourceCompiler.ProviderMethod(providerType, method.Source), contract.Descriptor,
                contract.Descriptor.Services.Single().ServiceId, method.MethodId) }).ToArray();
        EmitMarshalledOperations(output, operations, plan);
    }

    private static void EmitNaturalMarshalledHandler(StringBuilder output, SunderRpcCSharpImportedProvider imported)
    {
        var contract = imported.Surface.Contract;
        var plan = SunderRpcCSharpMarshallingPlan.Imported(contract);
        output.Append("private sealed class ").Append(imported.Provider.NaturalHandlerTypeName).AppendLine(" : global::Sunder.Sdk.Rpc.ISunderRpcServiceHandler, global::Sunder.Sdk.Rpc.ISunderRpcDeclaredErrorMetadata {")
            .Append("    private readonly ").Append(imported.Provider.SourceType.ToDisplayString(TypeDisplayFormat)).AppendLine(" _implementation;")
            .AppendLine("    private readonly global::Sunder.Sdk.Rpc.ISunderRpcClient? _receiver;")
            .AppendLine("    private readonly global::System.IServiceProvider? __services;")
            .AppendLine("    private readonly global::Sunder.Sdk.Rpc.SunderRpcServiceBindingLifetime? __bindings;")
            .Append("    internal ").Append(imported.Provider.NaturalHandlerTypeName).Append('(').Append(imported.Provider.SourceType.ToDisplayString(TypeDisplayFormat))
            .AppendLine(" implementation, global::Sunder.Sdk.Rpc.ISunderRpcClient? receiver, global::System.IServiceProvider? services) { _implementation = implementation; _receiver = receiver; __services = services; __bindings = services?.GetService(typeof(global::Sunder.Sdk.Rpc.SunderRpcServiceBindingLifetime)) as global::Sunder.Sdk.Rpc.SunderRpcServiceBindingLifetime; }");
        EmitDeclaredErrors(output, contract.Descriptor);
        if (!plan.HasResources)
            output.AppendLine("private static readonly global::System.Text.Json.JsonSerializerOptions JsonOptions = new(global::System.Text.Json.JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = global::System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, TypeInfoResolver = new __SunderJsonResolver(), Converters = { new global::Sunder.Sdk.Rpc.SunderRpcJsonConverterFactory() } };");
        plan.EmitConfiguration(output, sourceCodec: false, provider: true);
        var operations = imported.NaturalMethods.Select((binding, index) => {
            var method = contract.Descriptor.FindService(binding.ServiceId)!.Methods.Single(method => method.MethodId == binding.MethodId);
            return new MarshalledOperation(binding.ServiceId, binding.MethodId, method.RequestSchemaReference[8..], method.OutputSchemaReference[8..], binding.Completion, binding.Stream, index,
                input => "((" + binding.InvocationMethod.ContainingType.ToDisplayString(TypeDisplayFormat) + ")_implementation)." + EscapeIdentifier(binding.InvocationMethod.Name) + "("
                    + string.Join(", ", binding.Arguments.Select((argument, parameter) => EscapeIdentifier(binding.InvocationMethod.Parameters[parameter].Name) + ": " + (argument switch {
                        SunderRpcCSharpProviderArgument.Request => input, SunderRpcCSharpProviderArgument.Context => "context", SunderRpcCSharpProviderArgument.Cancellation => "token", _ => throw new InvalidOperationException(),
                     }))) + ")") { CompleteOnResponseContentRetirement = binding.CompleteOnResponseContentRetirement };
        }).ToArray();
        EmitMarshalledOperations(output, operations, plan);
        output.AppendLine("}");
    }

    private sealed record MarshalledOperation(string ServiceId, string MethodId, string Request, string Output,
        bool Unit, bool Stream, int Index, Func<string, string> Invoke)
    {
        internal bool CompleteOnResponseContentRetirement { get; init; }
    }

    private static void EmitMarshalledOperations(StringBuilder output, IReadOnlyList<MarshalledOperation> operations, SunderRpcCSharpMarshallingPlan plan)
    {
        output.AppendLine("public global::System.Threading.Tasks.ValueTask<global::System.Text.Json.JsonElement> InvokeUnaryAsync(global::Sunder.Sdk.Rpc.SunderRpcInvocationContext context, string serviceId, string methodId, global::System.Text.Json.JsonElement request, global::System.Threading.CancellationToken cancellationToken = default) {");
        foreach (var operation in operations.Where(operation => !operation.Stream))
            output.Append("    if (serviceId == ").Append(Quoted(operation.ServiceId)).Append(" && methodId == ").Append(Quoted(operation.MethodId)).Append(") return __Invoke_").Append(operation.Index).AppendLine("(context, request, cancellationToken);");
        output.AppendLine("    return global::System.Threading.Tasks.ValueTask.FromException<global::System.Text.Json.JsonElement>(new global::System.InvalidOperationException(\"Unknown service method.\"));").AppendLine("}")
            .AppendLine("public global::System.Collections.Generic.IAsyncEnumerable<global::System.Text.Json.JsonElement> InvokeServerStreamAsync(global::Sunder.Sdk.Rpc.SunderRpcInvocationContext context, string serviceId, string methodId, global::System.Text.Json.JsonElement request, global::System.Threading.CancellationToken cancellationToken = default) {");
        foreach (var operation in operations.Where(operation => operation.Stream))
            output.Append("    if (serviceId == ").Append(Quoted(operation.ServiceId)).Append(" && methodId == ").Append(Quoted(operation.MethodId)).Append(") return __Invoke_").Append(operation.Index).AppendLine("(context, request, cancellationToken);");
        output.AppendLine("    throw new global::System.InvalidOperationException(\"Unknown service stream.\");").AppendLine("}");
        foreach (var operation in operations)
        {
            output.Append("private async ").Append(operation.Stream ? "global::System.Collections.Generic.IAsyncEnumerable<global::System.Text.Json.JsonElement> " : "global::System.Threading.Tasks.ValueTask<global::System.Text.Json.JsonElement> ")
                .Append("__Invoke_").Append(operation.Index).Append("(global::Sunder.Sdk.Rpc.SunderRpcInvocationContext context, global::System.Text.Json.JsonElement request, ")
                .Append(operation.Stream ? "[global::System.Runtime.CompilerServices.EnumeratorCancellation] " : "").AppendLine("global::System.Threading.CancellationToken token) {")
                .AppendLine("    token.ThrowIfCancellationRequested();");
            if (plan.HasResources) output.AppendLine("    await using var __marshal = global::Sunder.Sdk.Rpc.SunderRpcMarshallingContext.ForInvocation(context, _receiver, __bindings);")
                .AppendLine("    __Configure(__marshal);")
                .AppendLine("    using var __participants = global::System.Threading.CancellationTokenSource.CreateLinkedTokenSource(token, __marshal.CancellationToken);")
                .AppendLine("    token = __participants.Token;");
            SunderRpcCSharpServiceApiEmitter.EmitValueValidation(output, plan.SchemaNamespace, operation.Request, "request");
            if (plan.HasResources) output.Append("    var input = await ").Append(plan.Deserialize(operation.Request, "request")).AppendLine(".ConfigureAwait(false);");
            else output.Append("    var input = global::System.Text.Json.JsonSerializer.Deserialize<").Append(plan.Layouts[operation.Request].TypeName).AppendLine(">(request, JsonOptions)!;");
            output.AppendLine("    token.ThrowIfCancellationRequested();");
            if (operation.Stream)
                output.Append("    await foreach (var result in ").Append(operation.Invoke("input")).AppendLine(".WithCancellation(token).ConfigureAwait(false)) {");
            else output.Append(operation.Unit ? "    await " : "    var result = await ").Append(operation.Invoke("input")).AppendLine(".ConfigureAwait(false);");
            if (operation.CompleteOnResponseContentRetirement)
                output.Append("    var __item = await ").Append(plan.Serialize(operation.Output, "result", responseStream: true)).AppendLine(".ConfigureAwait(false);")
                    .AppendLine("    if (!__item.HasValue) yield break;")
                    .AppendLine("    var encoded = __item.Value;");
            else if (plan.HasResources) output.Append("    var encoded = await ").Append(operation.Unit
                    ? "__marshal.SerializeAsync(new global::System.Collections.Generic.Dictionary<string, object>(), cancellationToken: token)"
                    : plan.Serialize(operation.Output, "result")).AppendLine(".ConfigureAwait(false);");
            else output.Append("    var encoded = global::System.Text.Json.JsonSerializer.SerializeToElement(")
                .Append(operation.Unit ? "new global::System.Collections.Generic.Dictionary<string, object>()" : "result").AppendLine(", JsonOptions);");
            SunderRpcCSharpServiceApiEmitter.EmitValueValidation(output, plan.SchemaNamespace, operation.Output, "encoded");
            output.AppendLine(operation.Stream ? "    yield return encoded;\n    }" : "    return encoded;").AppendLine("}");
        }
    }
    private static string Quoted(string value) => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true);
}
