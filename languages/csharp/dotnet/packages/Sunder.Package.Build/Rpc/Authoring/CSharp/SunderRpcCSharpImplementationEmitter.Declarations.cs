using System.Text;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal static partial class SunderRpcCSharpImplementationEmitter
{
    private static void EmitProviderDeclaration(StringBuilder output, SunderRpcContractDescriptor descriptor, string providerId, bool hidesRegistration)
    {
        output.Append("    public ").Append(hidesRegistration ? "new " : string.Empty)
            .AppendLine("static global::Sunder.Sdk.Worker.SunderWorkerProviderRegistration DeclareSunderRpcProvider(global::Sunder.Sdk.Rpc.ISunderRpcServiceHandler handler)")
            .AppendLine("        => new(")
            .Append("            \"").Append(EscapeString(providerId)).AppendLine("\",")
            .Append("            \"").Append(EscapeString(descriptor.ContractId)).AppendLine("\",")
            .Append("            \"").Append(EscapeString(descriptor.Version)).AppendLine("\",")
            .Append("            \"").Append(descriptor.Sha256).AppendLine("\",")
            .Append("            \"").Append(descriptor.AbiSha256).AppendLine("\",")
            .AppendLine("            new __SunderGeneratedRpcDeclaration(handler));")
            .AppendLine("    private sealed class __SunderGeneratedRpcDeclaration : global::Sunder.Sdk.Rpc.ISunderRpcServiceHandler, global::Sunder.Sdk.Rpc.ISunderRpcDeclaredErrorMetadata")
            .AppendLine("    {")
            .AppendLine("        private readonly global::Sunder.Sdk.Rpc.ISunderRpcServiceHandler _handler;")
            .AppendLine("        internal __SunderGeneratedRpcDeclaration(global::Sunder.Sdk.Rpc.ISunderRpcServiceHandler handler)")
            .AppendLine("            => _handler = handler ?? throw new global::System.ArgumentNullException(nameof(handler));");
        EmitDeclaredErrors(output, descriptor);
        output.AppendLine("        public global::System.Threading.Tasks.ValueTask<global::System.Text.Json.JsonElement> InvokeUnaryAsync(")
            .AppendLine("            global::Sunder.Sdk.Rpc.SunderRpcInvocationContext context, string serviceId, string methodId, global::System.Text.Json.JsonElement request,")
            .AppendLine("            global::System.Threading.CancellationToken cancellationToken = default)")
            .AppendLine("            => _handler.InvokeUnaryAsync(context, serviceId, methodId, request, cancellationToken);")
            .AppendLine("        public global::System.Collections.Generic.IAsyncEnumerable<global::System.Text.Json.JsonElement> InvokeServerStreamAsync(")
            .AppendLine("            global::Sunder.Sdk.Rpc.SunderRpcInvocationContext context, string serviceId, string methodId, global::System.Text.Json.JsonElement request,")
            .AppendLine("            global::System.Threading.CancellationToken cancellationToken = default)")
            .AppendLine("            => _handler.InvokeServerStreamAsync(context, serviceId, methodId, request, cancellationToken);")
            .AppendLine("    }");
    }
}
