using System.Text;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal static partial class SunderRpcCSharpImportPrepass
{
    private static SunderRpcCSharpImportedContract PrepareNaturalContract(SunderRpcCSharpImportedContract contract,
        string authorityId, IDictionary<string, string> names)
    {
        var apiNamespace = Editor.GeneratedPackageNames.Services(authorityId);
        var internalNamespace = Editor.GeneratedPackageNames.Internal(authorityId);
        var services = contract.Descriptor.Services.OrderBy(service => service.ServiceId, StringComparer.Ordinal).Select(service =>
        {
            var stem = SunderRpcCSharpSyntax.Identifier(service.ServiceId).TrimStart('@');
            var api = apiNamespace + "." + NaturalInterfaceName(service.ServiceId);
            var origin = contract.ContractKey + "/" + service.ServiceId;
            if (names.TryGetValue(api, out var previous))
                throw new InvalidDataException($"rpc-authoring.generated-name-collision: '{previous}' and '{origin}' both generate '{api}'. Select distinct stable service identities.");
            names.Add(api, origin);
            var client = internalNamespace + "." + ContractName(contract.ContractKey) + "_" + stem + "ServiceClient";
            return new SunderRpcCSharpNaturalService(service.ServiceId, api, client, client + "Providers", "__SunderNatural" + stem + "ProviderAdapter") { ContractId = contract.Descriptor.ContractId };
        }).ToArray();
        var prepared = contract with { AuthorityPackageId = authorityId, NaturalServices = services };
        if (services.Length > 1)
        {
            var aggregate = NaturalProviderInterface(prepared);
            if (names.TryGetValue(aggregate, out var previous))
                throw new InvalidDataException($"rpc-authoring.generated-name-collision: '{previous}' conflicts with aggregate '{aggregate}'.");
            names.Add(aggregate, contract.ContractKey);
        }
        return prepared;
    }

    internal static string NaturalProviderInterface(SunderRpcCSharpImportedContract contract)
        => contract.NaturalServices.Count == 1 ? contract.NaturalServices[0].InterfaceMetadataName
            : Editor.GeneratedPackageNames.Services(contract.AuthorityPackageId!) + ".I" + ContractName(contract.ContractKey)[1..] + "Services";

    internal static string NaturalProviderImportInterface(string packageId, string key)
        => Editor.GeneratedPackageNames.Services(packageId) + ".I" + UpperInitial(key) + "Service";

    private static string NaturalInterfaceName(string serviceId)
        => Editor.GeneratedPackageNames.NaturalInterfaceName(serviceId);

    // Ordinary APIs occupy one flat authority namespace. Two distinct contracts
    // cannot silently change a service's public name by adding contract scopes.
    private static void ValidateNaturalServiceNames(IEnumerable<SunderRpcContractDescriptor> descriptors,
        IReadOnlyDictionary<string, SunderRpcLockedArtifactContract> authorities)
    {
        var collision = descriptors.Where(descriptor => authorities.TryGetValue(descriptor.ContractId, out var authority) && authority.AuthorityKind == "package")
            .SelectMany(descriptor => descriptor.Services.Select(service => (Key: descriptor.ContractId + "/" + service.ServiceId,
                Name: Editor.GeneratedPackageNames.Services(authorities[descriptor.ContractId].AuthorityId) + "." + NaturalInterfaceName(service.ServiceId))))
            .OrderBy(value => value.Key, StringComparer.Ordinal)
            .GroupBy(value => value.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Select(value => value.Key).Distinct(StringComparer.Ordinal).Count() > 1);
        if (collision is not null)
            throw new InvalidDataException($"rpc-authoring.generated-name-collision: Services '{string.Join("', '", collision.Select(value => value.Key))}' collide at '{collision.Key}'.");
    }

    internal static bool HasExpectedNaturalProjection(SunderRpcCSharpImportedContract contract, SunderRpcLockedArtifactContract evidence,
        IReadOnlyList<SunderRpcCSharpImportedContract> allContracts, IReadOnlyDictionary<string, SunderRpcLockedArtifactContract> authorities)
    {
        if (evidence.AuthorityKind != "package") return contract.AuthorityPackageId is null && contract.NaturalServices.Count == 0;
        ValidateNaturalServiceNames(allContracts.Select(value => value.Descriptor), authorities);
        var expected = PrepareNaturalContract(contract, evidence.AuthorityId, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        return contract.AuthorityPackageId == evidence.AuthorityId && contract.NaturalServices.SequenceEqual(expected.NaturalServices);
    }

    internal static string EmitNaturalServices(SunderRpcCSharpImportedContract contract, bool consumers, string accessibility = "public",
        bool declarations = true, string? selectedService = null)
    {
        var output = new StringBuilder("// <auto-generated />\n#nullable enable\nusing global::System.Threading.Tasks;\n");
        var model = GeneratorModel(contract);
        foreach (var service in model.Services)
        {
            if (selectedService is not null && service.ServiceId != selectedService) continue;
            var surface = contract.NaturalServices.Single(value => value.ServiceId == service.ServiceId);
            var apiName = surface.InterfaceMetadataName[(surface.InterfaceMetadataName.LastIndexOf('.') + 1)..];
            if (declarations)
            {
                output.Append("namespace ").Append(surface.InterfaceMetadataName[..surface.InterfaceMetadataName.LastIndexOf('.')]).AppendLine(" {")
                    .Append(accessibility).Append(" interface ").Append(apiName).AppendLine(" {");
                foreach (var method in service.Methods) output.Append("    ").Append(NaturalMethod(contract, model, service, method).Signature).AppendLine(";");
                output.AppendLine("}").AppendLine("}");
            }
            if (!consumers) continue;
            var clientName = surface.ClientMetadataName[(surface.ClientMetadataName.LastIndexOf('.') + 1)..];
            output.Append("namespace ").Append(Editor.GeneratedPackageNames.Internal(contract.AuthorityPackageId!)).AppendLine(" {")
                .Append("internal sealed class ").Append(clientName).Append(" : global::").Append(surface.InterfaceMetadataName)
                .AppendLine(", global::System.IDisposable, global::System.IAsyncDisposable {")
                .AppendLine("    private readonly ImportedServiceClientLifetime __lifetime;")
                .AppendLine("    private readonly global::Sunder.Sdk.Rpc.SunderRpcProviderSnapshot? __provider;")
                .AppendLine("    private readonly global::System.IServiceProvider? __services;")
                .AppendLine("    private readonly global::System.Threading.CancellationToken __providerRetirement;")
                .AppendLine("    private readonly string __packageId = \"\"; private readonly string __range = \"\"; private readonly bool __prerelease; private readonly bool __ownsLifetime;")
                .Append("    internal ").Append(clientName).AppendLine("(ImportedServiceClientLifetime lifetime, global::Sunder.Sdk.Rpc.SunderRpcProviderSnapshot provider, global::System.IServiceProvider? services, global::System.Threading.CancellationToken retirement) { __lifetime = lifetime; __provider = provider; __services = services; __providerRetirement = retirement; }")
                .Append("    internal ").Append(clientName).AppendLine("(global::Sunder.Sdk.Rpc.ISunderRpcClient rpc, string packageId, string range, bool includePrerelease, global::System.IServiceProvider? services = null) { __lifetime = new(rpc, services?.GetService(typeof(global::Sunder.Sdk.Rpc.SunderRpcServiceBindingLifetime)) as global::Sunder.Sdk.Rpc.SunderRpcServiceBindingLifetime); __packageId = packageId; __range = range; __prerelease = includePrerelease; __ownsLifetime = true; __services = services; }")
                .Append("    private global::System.Threading.CancellationToken __Retirement(global::Sunder.Sdk.Rpc.SunderRpcProviderSnapshot provider) => __provider is not null ? __providerRetirement : __lifetime.Bindings.CreateProvider<global::")
                .Append(surface.InterfaceMetadataName).Append(">(provider, (_, snapshot, retirement) => new ").Append(clientName).AppendLine("(__lifetime, snapshot, __services, retirement)).RetirementToken;");
            var marshalling = SunderRpcCSharpMarshallingPlan.Imported(contract);
            marshalling.EmitConfiguration(output, sourceCodec: false);
            foreach (var method in service.Methods)
                EmitNaturalMarshalledClientMethod(output, contract, model, service, method, marshalling);
            output.AppendLine("    void global::System.IDisposable.Dispose() { if (__ownsLifetime) __lifetime.Dispose(); }")
                .AppendLine("    global::System.Threading.Tasks.ValueTask global::System.IAsyncDisposable.DisposeAsync() => __ownsLifetime ? __lifetime.DisposeAsync() : global::System.Threading.Tasks.ValueTask.CompletedTask;")
                .AppendLine("}");
            SunderRpcCSharpServiceApiEmitter.EmitCatalogDeclaration(output, contract.Descriptor, "global::" + surface.InterfaceMetadataName,
                clientName + "Providers", clientName, imported: true);
            output.AppendLine("}");
        }
        if (declarations && contract.NaturalServices.Count > 1)
        {
            var metadataName = NaturalProviderInterface(contract);
            var dot = metadataName.LastIndexOf('.');
            output.Append("namespace ").Append(metadataName[..dot]).AppendLine(" {")
                .Append(accessibility).Append(" interface ").Append(metadataName[(dot + 1)..]).Append(" : ")
                .AppendJoin(", ", contract.NaturalServices.Select(service => "global::" + service.InterfaceMetadataName)).AppendLine(" { }").AppendLine("}");
        }
        return output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static (string Signature, string RequestType, string OutputType, bool Empty, bool Completion, bool Stream) NaturalMethod(
        SunderRpcCSharpImportedContract contract, SunderRpcCSharpGeneratorModel model, SunderRpcCSharpServiceModel service, Sunder.Sdk.Rpc.SunderRpcCSharpMethodModel method)
    {
        var descriptor = contract.Descriptor.Services.Single(value => value.ServiceId == service.ServiceId).Methods.Single(value => value.MethodId == method.MethodId);
        var empty = IsEmptyRoot(contract.Descriptor, descriptor.RequestSchemaReference);
        var completion = method.Kind == SunderRpcMethodKind.Unary && IsEmptyRoot(contract.Descriptor, descriptor.OutputSchemaReference);
        var stream = method.Kind == SunderRpcMethodKind.ServerStream;
        var request = Qualify(method.RequestTypeName, contract, model);
        var result = Qualify(method.OutputTypeName, contract, model);
        var returns = stream ? "global::System.Collections.Generic.IAsyncEnumerable<" + result + ">"
            : completion ? "global::System.Threading.Tasks.ValueTask" : "global::System.Threading.Tasks.ValueTask<" + result + ">";
        return (returns + " " + method.MethodName + "(" + (empty ? "" : request + " request, ")
            + "global::System.Threading.CancellationToken cancellationToken = default)", request, result, empty, completion, stream);
    }

    private static void EmitNaturalClientMethod(StringBuilder output, SunderRpcCSharpImportedContract contract,
        SunderRpcCSharpGeneratorModel model, SunderRpcCSharpServiceModel service, Sunder.Sdk.Rpc.SunderRpcCSharpMethodModel method)
    {
        var shape = NaturalMethod(contract, model, service, method);
        output.Append("    public ").Append(shape.Stream ? "" : "async ").Append(shape.Signature).AppendLine(" {");
        if (shape.Stream)
            output.AppendLine("        return __lifetime.Subscribe(Call, cancellationToken);")
                .Append("        async global::System.Collections.Generic.IAsyncEnumerable<").Append(shape.OutputType).AppendLine("> Call(global::Sunder.Sdk.Rpc.ISunderRpcCallScope scope, [global::System.Runtime.CompilerServices.EnumeratorCancellation] global::System.Threading.CancellationToken token) {");
        else output.Append(shape.Completion ? "        await " : "        return await ").AppendLine("__lifetime.Invoke(async (scope, token) => {");
        EmitNaturalProviderSelection(output, contract, "scope");
        output.AppendLine("            await ImportedServiceClientLifetime.EnsureCurrent(scope, provider, token).ConfigureAwait(false);")
            .Append("            var client = new global::").Append(contract.GeneratedLogicalName).Append('.').Append(service.ClientTypeName).AppendLine("(scope, provider.Endpoint);");
        var call = "client." + method.MethodName + "(" + (shape.Empty ? "new " + shape.RequestType + "()" : "request") + ", token)";
        if (shape.Stream)
            output.Append("            await foreach (var item in ").Append(call).AppendLine(".WithCancellation(token).ConfigureAwait(false)) {")
                .AppendLine("                await ImportedServiceClientLifetime.EnsureCurrent(scope, provider, token).ConfigureAwait(false);")
                .AppendLine("                yield return item;").AppendLine("            }")
                .AppendLine("            await ImportedServiceClientLifetime.EnsureCurrent(scope, provider, token).ConfigureAwait(false);")
                .AppendLine("        }");
        else output.Append("            var value = await ").Append(call).AppendLine(".ConfigureAwait(false);")
                .AppendLine("            await ImportedServiceClientLifetime.EnsureCurrent(scope, provider, token).ConfigureAwait(false);")
                .Append("            return ").Append(shape.Completion ? "true" : "value").AppendLine(";")
                .AppendLine("        }, cancellationToken).ConfigureAwait(false);");
        output.AppendLine("    }");
    }

    private static void EmitNaturalProviderSelection(StringBuilder output, SunderRpcCSharpImportedContract contract, string client)
    {
        output.AppendLine("            var provider = __provider;")
            .AppendLine("            if (provider is null) {")
            .Append("                provider = (await new global::").Append(ContractApiName(contract)).Append('(').Append(client)
            .AppendLine(", __packageId, __range, __prerelease, __lifetime.Bindings).ResolveSelectedPackageAsync(token).ConfigureAwait(false)).Snapshot;");
        if (contract.OwningPackageVersion is { } version)
            output.Append("                if (provider.PackageVersion != ").Append(Literal(version))
                .AppendLine(") throw new global::Sunder.Sdk.Rpc.SunderRpcException(new(global::Sunder.Sdk.Rpc.SunderRpcErrorKind.StaleEndpoint, \"rpc.stale-endpoint\", \"The owning package version changed.\"));");
        output.AppendLine("            }")
            .AppendLine("            else { await __lifetime.Bindings.DiscoverAsync(provider.ContractId, token).ConfigureAwait(false); global::Sunder.Sdk.Rpc.SunderRpcServiceCall.ThrowIfRetired(__providerRetirement); }");
    }

}
