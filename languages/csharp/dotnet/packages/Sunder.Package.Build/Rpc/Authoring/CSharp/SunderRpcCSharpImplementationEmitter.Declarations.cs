using System.Text;
using Microsoft.CodeAnalysis;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal static partial class SunderRpcCSharpImplementationEmitter
{
    private static void EmitProviderDeclaration(StringBuilder output, SunderRpcContractDescriptor descriptor, SunderRpcCSharpProviderModel provider, bool hidesRegistration)
    {
        var declaration = "__SunderGeneratedRpcDeclaration" + provider.BindingSuffix;
        output.Append("    public ").Append(hidesRegistration ? "new " : string.Empty)
            .Append("static global::Sunder.Sdk.Worker.SunderWorkerProviderRegistration ").Append(provider.DeclarationFactory).AppendLine("(global::Sunder.Sdk.Rpc.ISunderRpcServiceHandler handler)")
            .AppendLine("        => new(")
            .Append("            \"").Append(EscapeString(provider.ProviderId)).AppendLine("\",")
            .Append("            \"").Append(EscapeString(descriptor.ContractId)).AppendLine("\",")
            .Append("            \"").Append(EscapeString(descriptor.Version)).AppendLine("\",")
            .Append("            \"").Append(descriptor.Sha256).AppendLine("\",")
            .Append("            \"").Append(descriptor.AbiSha256).AppendLine("\",")
            .Append("            new ").Append(declaration).AppendLine("(handler));")
            .Append("    private sealed class ").Append(declaration).AppendLine(" : global::Sunder.Sdk.Rpc.ISunderRpcServiceHandler, global::Sunder.Sdk.Rpc.ISunderRpcDeclaredErrorMetadata")
            .AppendLine("    {")
            .AppendLine("        private readonly global::Sunder.Sdk.Rpc.ISunderRpcServiceHandler _handler;")
            .Append("        internal ").Append(declaration).AppendLine("(global::Sunder.Sdk.Rpc.ISunderRpcServiceHandler handler)")
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

    private static void EmitRegistrationAggregate(StringBuilder output, INamedTypeSymbol type, IReadOnlyList<SunderRpcCSharpProviderModel> bindings, bool hidesRegistration)
    {
        var ns = type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString();
        if (ns is not null) output.Append("namespace ").Append(EscapeNamespace(ns)).AppendLine(" {");
        output.Append("partial class ").Append(EscapeIdentifier(type.Name)).AppendLine(" {");
        foreach (var services in new[] { false, true })
        {
            output.Append(services ? "    internal " : "    public ").Append(hidesRegistration ? "new " : "")
                .Append("global::System.Collections.Generic.IReadOnlyList<global::Sunder.Sdk.Worker.SunderWorkerProviderRegistration> CreateSunderRpcProviderRegistrations")
                .AppendLine(services ? "ForServices(global::Sunder.Sdk.Rpc.ISunderRpcClient receiver, global::System.IServiceProvider services)" : "()")
                .AppendLine("        => global::System.Array.AsReadOnly(new global::Sunder.Sdk.Worker.SunderWorkerProviderRegistration[] {");
            foreach (var binding in bindings)
                output.Append("            ").Append(services ? binding.ServicesFactory : binding.RegistrationFactory)
                    .AppendLine(services ? "(receiver, services)," : "(),");
            output.AppendLine("        });");
        }
        output.AppendLine("}");
        if (ns is not null) output.AppendLine("}");
    }
}
