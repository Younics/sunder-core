using System.Text;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal static partial class SunderRpcCSharpImportPrepass
{
    private static void EmitNaturalMarshalledClientMethod(StringBuilder output, SunderRpcCSharpImportedContract contract,
        SunderRpcCSharpGeneratorModel model, SunderRpcCSharpServiceModel service, Generation.SunderRpcCSharpMethodModel method,
        SunderRpcCSharpMarshallingPlan plan)
    {
        var shape = NaturalMethod(contract, model, service, method);
        var descriptor = contract.Descriptor.Services.Single(value => value.ServiceId == service.ServiceId).Methods.Single(value => value.MethodId == method.MethodId);
        var requestKey = descriptor.RequestSchemaReference[8..]; var outputKey = descriptor.OutputSchemaReference[8..];
        output.Append("    public ").Append(shape.Stream ? "" : "async ").Append(shape.Signature).AppendLine(" {");
        if (!shape.Stream) output.AppendLine("        var __operationCancellation = __providerRetirement;").AppendLine("        try {");
        if (shape.Stream)
            output.AppendLine("        return __lifetime.SubscribeResource(Call, cancellationToken);")
                .Append("        async global::System.Collections.Generic.IAsyncEnumerable<").Append(shape.OutputType)
                .AppendLine("> Call(global::Sunder.Sdk.Rpc.ISunderRpcClient rpc, global::System.Threading.CancellationToken enumerationCancellation, global::System.Action<global::System.Threading.CancellationToken> trackCancellation, [global::System.Runtime.CompilerServices.EnumeratorCancellation] global::System.Threading.CancellationToken token) {");
        else output.Append(shape.Completion ? "        await " : "        return await ").AppendLine("__lifetime.Query(async (rpc, token) => {");
        output.AppendLine("            cancellationToken.ThrowIfCancellationRequested();")
            .AppendLine("            global::Sunder.Sdk.Rpc.SunderRpcServiceCall.ThrowIfRetired(__providerRetirement);");
        EmitNaturalProviderSelection(output, contract, "rpc");
        output.AppendLine("            await ImportedServiceClientLifetime.EnsureCurrent(rpc, provider, token).ConfigureAwait(false);")
            .AppendLine("            var selectedRetirement = __Retirement(provider);")
            .AppendLine(shape.Stream ? "            trackCancellation(selectedRetirement);" : "            __operationCancellation = selectedRetirement;")
            .Append("            await using var __marshal = await __lifetime.CreateCallAsync(provider, cancellationToken")
            .Append(shape.Stream ? ", enumerationCancellation" : ", default").Append(", selectedRetirement, ")
            .Append(MethodTimeout(contract, service.ServiceId, method.MethodId)).AppendLine(").ConfigureAwait(false);")
            .AppendLine("            __Configure(__marshal);")
            .AppendLine("            using var __participants = global::System.Threading.CancellationTokenSource.CreateLinkedTokenSource(token, __marshal.CancellationToken);")
            .AppendLine("            token = __participants.Token;")
            .AppendLine(shape.Stream ? "            trackCancellation(token);" : "            __operationCancellation = token;")
            .Append("            var input = await ").Append(plan.Serialize(requestKey, shape.Empty ? "new " + shape.RequestType + "()" : "request")).AppendLine(".ConfigureAwait(false);");
        SunderRpcCSharpServiceApiEmitter.EmitValueValidation(output, contract.GeneratedLogicalName, requestKey, "input");
        output.AppendLine("            await global::Sunder.Sdk.Rpc.SunderRpcServiceCall.EnsureCurrentAsync((global::Sunder.Sdk.Rpc.ISunderRpcCallScope)__marshal.Client!, provider, token).ConfigureAwait(false);");
        output.Append(shape.Stream ? "            await foreach (var value in " : "            var value = await ")
            .Append(shape.Stream ? "__marshal.Client!.SubscribeAsync(" : "__marshal.Client!.InvokeAsync(")
            .Append("provider.Endpoint, ").Append(Literal(service.ServiceId)).Append(", ").Append(Literal(method.MethodId))
            .AppendLine(shape.Stream ? ", input, cancellationToken: token).ConfigureAwait(false)) {" : ", input, cancellationToken: token).ConfigureAwait(false);")
            .AppendLine("            await ImportedServiceClientLifetime.EnsureCurrent(__marshal.Client!, provider, token).ConfigureAwait(false);");
        SunderRpcCSharpServiceApiEmitter.EmitValueValidation(output, contract.GeneratedLogicalName, outputKey, "value");
        if (shape.Completion) output.AppendLine("            return true;");
        else output.Append(shape.Stream ? "            yield return await " : "            return await ").Append(plan.Deserialize(outputKey, "value")).AppendLine(".ConfigureAwait(false);");
        if (shape.Stream) output.AppendLine("            }").AppendLine("            await ImportedServiceClientLifetime.EnsureCurrent(__marshal.Client!, provider, token).ConfigureAwait(false);").AppendLine("        }");
        else output.AppendLine("        }, cancellationToken).ConfigureAwait(false);")
            .AppendLine("        } catch (global::System.Exception error) {")
            .AppendLine("            var translated = global::Sunder.Sdk.Rpc.SunderRpcServiceCall.TranslateCancellation(error, cancellationToken, default, __operationCancellation);")
            .AppendLine("            if (global::System.Object.ReferenceEquals(translated, error)) throw;")
            .AppendLine("            throw translated;").AppendLine("        }");
        output.AppendLine("    }");
    }
}
