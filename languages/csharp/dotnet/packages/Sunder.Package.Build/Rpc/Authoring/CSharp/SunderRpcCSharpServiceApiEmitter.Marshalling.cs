using System.Text;
using Microsoft.CodeAnalysis;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal static partial class SunderRpcCSharpServiceApiEmitter
{
    private static void EmitMarshalledClientMethod(StringBuilder output, SunderRpcCSharpCompiledContract contract,
        SunderRpcCSharpMethodModel method, string packageId, string packageVersion, SunderRpcCSharpMarshallingPlan plan)
    {
        var used = method.Source.Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
        string Local(string name) { while (!used.Add(name)) name += "_"; return name; }
        var rpc = Local("__rpc"); var token = Local("__token"); var provider = Local("__provider"); var marshal = Local("__marshal");
        var request = Local("__request"); var response = Local("__response"); var core = Local("__call");
        var enumeration = Local("__enumerationCancellation");
        var participants = Local("__participants");
        var operationCancellation = Local("__operationCancellation");
        var providerRetirement = Local("__selectedRetirement");
        var trackCancellation = Local("__trackCancellation");
        var failure = Local("__failure");
        var translated = Local("__translated");
        var cancellation = method.Source.Parameters.FirstOrDefault(IsToken);
        var caller = cancellation is null ? "default" : SunderRpcCSharpSyntax.EscapeIdentifier(cancellation.Name);
        var stream = method.Kind == "server-stream";
        var result = method.ReturnsUnit ? "bool" : ((INamedTypeSymbol)method.Source.ReturnType).TypeArguments[0].ToDisplayString(SignatureFormat);
        output.Append("    public ").Append(stream ? "" : "async ").Append(Signature(method.Source)).AppendLine(" {");
        if (!stream) output.Append("        var ").Append(operationCancellation).AppendLine(" = this.__providerRetirement;")
            .AppendLine("        try {");
        if (stream)
            output.Append("        return this.__lifetime.SubscribeResource(").Append(core).Append(", ").Append(caller).AppendLine(");")
                .Append("        async global::System.Collections.Generic.IAsyncEnumerable<").Append(result).Append("> ").Append(core)
                .Append("(global::Sunder.Sdk.Rpc.ISunderRpcClient ").Append(rpc)
                 .Append(", global::System.Threading.CancellationToken ").Append(enumeration)
                 .Append(", global::System.Action<global::System.Threading.CancellationToken> ").Append(trackCancellation)
                .Append(", [global::System.Runtime.CompilerServices.EnumeratorCancellation] global::System.Threading.CancellationToken ").Append(token).AppendLine(") {");
        else output.Append(method.ReturnsUnit ? "        await " : "        return await ").Append("this.__lifetime.Query(async (").Append(rpc).Append(", ").Append(token).AppendLine(") => {");
        output.Append("            ").Append(caller == "default" ? "global::System.Threading.CancellationToken.None" : caller).AppendLine(".ThrowIfCancellationRequested();")
            .AppendLine("            global::Sunder.Sdk.Rpc.SunderRpcServiceCall.ThrowIfRetired(this.__providerRetirement);")
            .Append("            var ").Append(provider).AppendLine(" = this.__selectedProvider;")
            .Append("            if (").Append(provider).AppendLine(" is null) {")
            .Append("                ").Append(provider).Append(" = (await new global::").Append(contract.GeneratedLogicalName).Append(".Api.Contract(").Append(rpc)
            .Append(", ").Append(Literal(packageId)).Append(", ").Append(Literal("=" + packageVersion)).Append(", true, this.__lifetime.Bindings).ResolveSelectedPackageAsync(").Append(token).AppendLine(").ConfigureAwait(false)).Snapshot;")
            .Append("                if (").Append(provider).Append(".PackageVersion != ").Append(Literal(packageVersion)).AppendLine(") throw new global::Sunder.Sdk.Rpc.SunderRpcException(new(global::Sunder.Sdk.Rpc.SunderRpcErrorKind.StaleEndpoint, \"rpc.stale-endpoint\", \"The owning package version changed.\"));")
            .AppendLine("            }")
            .Append("            else { await this.__lifetime.Bindings.DiscoverAsync(").Append(provider).Append(".ContractId, ").Append(token)
            .AppendLine(").ConfigureAwait(false); global::Sunder.Sdk.Rpc.SunderRpcServiceCall.ThrowIfRetired(this.__providerRetirement); }")
            .Append("            await ServiceClientLifetime.EnsureCurrent(").Append(rpc).Append(", ").Append(provider).Append(", ").Append(token).AppendLine(").ConfigureAwait(false);")
            .Append("            var ").Append(providerRetirement).Append(" = this.__Retirement(").Append(provider).AppendLine(");")
            .Append("            ").Append(stream ? trackCancellation + "(" + providerRetirement + ");" : operationCancellation + " = " + providerRetirement + ";").AppendLine()
            .Append("            await using var ").Append(marshal).Append(" = await this.__lifetime.CreateCallAsync(").Append(provider).Append(", ").Append(caller)
            .Append(", ").Append(stream ? enumeration : "default").Append(", ").Append(providerRetirement).Append(", ")
            .Append(method.TimeoutSeconds).AppendLine(").ConfigureAwait(false);")
            .Append("            __Configure(").Append(marshal).AppendLine(");")
            .Append("            using var ").Append(participants).Append(" = global::System.Threading.CancellationTokenSource.CreateLinkedTokenSource(").Append(token).Append(", ").Append(marshal).AppendLine(".CancellationToken);")
            .Append("            ").Append(token).Append(" = ").Append(participants).AppendLine(".Token;")
            .Append("            ").Append(stream ? trackCancellation + "(" + token + ");" : operationCancellation + " = " + token + ";").AppendLine();
        var parameters = SunderRpcCSharpMarshallingPlan.WireParameters(method.Source);
        var value = parameters.Count == 1 ? SunderRpcCSharpSyntax.EscapeIdentifier(parameters[0].Name)
            : "new " + SunderRpcCSharpMarshallingPlan.Envelope(method.RequestDefinition) + " { "
                + string.Join(", ", parameters.Select((parameter, index) => "P" + index + " = " + SunderRpcCSharpSyntax.EscapeIdentifier(parameter.Name))) + " }";
        output.Append("            var ").Append(request).Append(" = await ").Append(plan.Serialize(method.RequestDefinition, value, marshal, token)).AppendLine(".ConfigureAwait(false);");
        EmitValueValidation(output, contract.GeneratedLogicalName, method.RequestDefinition, request);
        output.Append("            await global::Sunder.Sdk.Rpc.SunderRpcServiceCall.EnsureCurrentAsync((global::Sunder.Sdk.Rpc.ISunderRpcCallScope)")
            .Append(marshal).Append(".Client!, ").Append(provider).Append(", ").Append(token).AppendLine(").ConfigureAwait(false);");
        output.Append(stream ? "            await foreach (var " : "            var ").Append(response).Append(stream ? " in " : " = await ").Append(marshal)
            .Append(stream ? ".Client!.SubscribeAsync(" : ".Client!.InvokeAsync(").Append(provider).Append(".Endpoint, ")
            .Append(Literal(contract.Descriptor.Services.Single().ServiceId)).Append(", ").Append(Literal(method.MethodId)).Append(", ").Append(request)
            .Append(", cancellationToken: ").Append(token).AppendLine(stream ? ").ConfigureAwait(false)) {" : ").ConfigureAwait(false);")
            .Append("            await ServiceClientLifetime.EnsureCurrent(").Append(marshal).Append(".Client!, ").Append(provider).Append(", ").Append(token).AppendLine(").ConfigureAwait(false);");
        EmitValueValidation(output, contract.GeneratedLogicalName, method.OutputDefinition, response);
        if (method.ReturnsUnit) output.AppendLine("            return true;");
        else output.Append(stream ? "            yield return await " : "            return await ").Append(plan.Deserialize(method.OutputDefinition, response, marshal, token)).AppendLine(".ConfigureAwait(false);");
        if (stream) output.AppendLine("            }").Append("            await ServiceClientLifetime.EnsureCurrent(").Append(marshal).Append(".Client!, ").Append(provider).Append(", ").Append(token).AppendLine(").ConfigureAwait(false);").AppendLine("        }");
        else output.Append("        }, ").Append(caller).AppendLine(").ConfigureAwait(false);")
            .Append("        } catch (global::System.Exception ").Append(failure).AppendLine(") {")
            .Append("            var ").Append(translated).Append(" = global::Sunder.Sdk.Rpc.SunderRpcServiceCall.TranslateCancellation(")
            .Append(failure).Append(", ").Append(caller).Append(", default, ").Append(operationCancellation).AppendLine(");")
            .Append("            if (global::System.Object.ReferenceEquals(").Append(translated).Append(", ").Append(failure).AppendLine(")) throw;")
            .Append("            throw ").Append(translated).AppendLine(";").AppendLine("        }");
        output.AppendLine("    }");
    }

    internal static void EmitValueValidation(StringBuilder output, string schemaNamespace, string definition, string expression)
        => output.Append("            if (!global::").Append(schemaNamespace).Append(".__SunderContract.Descriptor.IsValid(").Append(Literal("#/$defs/" + definition))
            .Append(", ").Append(expression).AppendLine(", out _)) throw new global::System.Text.Json.JsonException(\"The service value does not match the selected descriptor.\");");
}
