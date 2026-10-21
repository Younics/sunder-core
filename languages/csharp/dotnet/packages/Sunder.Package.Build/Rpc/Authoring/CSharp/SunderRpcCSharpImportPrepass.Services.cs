using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal sealed record SunderRpcCSharpPackageApiSurface(string PackageId, string InterfaceMetadataName,
    string ImplementationMetadataName, IReadOnlyList<string> ConsumerRoles)
{
    internal IReadOnlyList<SunderRpcCSharpNaturalService> Services { get; init; } = [];
    internal IReadOnlyList<SunderRpcCSharpImportedContract> Contracts { get; init; } = [];
}

internal static partial class SunderRpcCSharpImportPrepass
{
    internal static IReadOnlyList<SunderRpcCSharpSourceFile> PrepareTypedUseClients(SunderRpcCSharpCompilationResult compilation)
    {
        var sources = new List<SunderRpcCSharpSourceFile>();
        var consumed = compilation.ImportedPackageApis.SelectMany(api => api.Contracts).Select(contract => contract.ContractKey).ToHashSet(StringComparer.Ordinal);
        foreach (var group in compilation.ImportedUses.Where(use => !consumed.Contains(use.Contract.ContractKey)).GroupBy(use => use.Contract.ContractKey, StringComparer.Ordinal))
        {
            var contract = group.First().Contract;
            sources.Add(new("typed-uses/" + contract.ContractKey + ".api.g.cs", EmitContractApi(contract)));
            foreach (var use in group)
                sources.Add(new("typed-uses/" + contract.ContractKey + "." + use.Service.ServiceId + ".client.g.cs",
                    EmitNaturalServices(contract, consumers: true, declarations: false, selectedService: use.Service.ServiceId)));
        }
        var lifetimes = compilation.ImportedPackageApis.SelectMany(api => api.Contracts).Select(contract => contract.AuthorityPackageId).ToHashSet(StringComparer.Ordinal);
        foreach (var authority in compilation.ImportedUses.Select(use => use.Contract.AuthorityPackageId!).Distinct(StringComparer.Ordinal).Where(authority => !lifetimes.Contains(authority)))
            sources.Add(new("typed-uses/" + authority + ".lifetime.g.cs", SunderRpcCSharpServiceApiEmitter.EmitImportedLifetime(authority)));
        return sources;
    }

    private static IReadOnlyList<SunderRpcCSharpPackageApiSurface> EmitInjectedPackageApis(SunderRpcContractLock contractLock,
        IReadOnlyDictionary<string, SunderRpcCSharpImportedContract> contracts, List<SunderRpcCSharpSourceFile> sources)
    {
        var result = new List<SunderRpcCSharpPackageApiSurface>();
        foreach (var import in contractLock.PackageImports.Values.OrderBy(value => value.PackageId, StringComparer.Ordinal))
        {
            var artifact = contractLock.PackageArtifacts[import.PackageId];
            var selected = artifact.Contracts.Values.Where(value => value.AuthorityKind == "package")
                .OrderBy(value => value.ContractId, StringComparer.Ordinal).Select(value => contracts[value.ContractId]).ToArray();
            result.Add(EmitInjectedPackageApi(import.PackageId, ReviewedPackageSelection(import.PackageId, artifact), selected, sources, import.ConsumerRoles));
        }
        return result;
    }

    private static SunderRpcCSharpPackageApiSurface EmitInjectedPackageApi(string packageId, string reviewedSelection,
        IReadOnlyList<SunderRpcCSharpImportedContract> selected, List<SunderRpcCSharpSourceFile> sources,
        IReadOnlyList<string> consumerRoles, string accessibility = "public")
    {
        var servicesNamespace = Editor.GeneratedPackageNames.Services(packageId);
        var implementationNamespace = Editor.GeneratedPackageNames.Internal(packageId);
        var properties = new StringBuilder(); var creation = new StringBuilder();
        var declarations = new StringBuilder(); var implementations = new StringBuilder();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "IPackageApi", "PackageServices", "Dispose", "DisposeAsync" };
        var scopes = new SortedDictionary<string, (StringBuilder Properties, StringBuilder Creation)>(StringComparer.Ordinal);
        foreach (var contract in selected)
        {
            // Keep the canonical dot boundaries. Unlike concatenating Pascal
            // tokens this is injective, including numeric contract segments.
            var scope = ContractName(contract.Descriptor.ContractId)[1..];
            if (char.IsAsciiDigit(scope[0])) scope = "_" + scope;
            scopes.Add(scope, (new(), new()));
            var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var service in contract.NaturalServices)
            {
                var member = SunderRpcCSharpSyntax.Identifier(service.ServiceId);
                if (!members.Add(member)) throw new InvalidDataException("rpc-authoring.generated-name-collision: Imported service member names collide.");
                scopes[scope].Properties.Append("    public global::").Append(service.InterfaceMetadataName)
                    .Append(' ').Append(member).AppendLine(" { get; }");
                scopes[scope].Creation.Append("        ").Append(member).Append(" = Own(new global::")
                    .Append(service.ClientMetadataName).Append("(rpc, ").Append(reviewedSelection).AppendLine(", services), clients);");
            }
        }
        foreach (var (scope, members) in scopes)
        {
            var scopeApi = "I" + scope + "Api"; var scopeClient = scope + "Services";
            if (!names.Add(scopeApi) || !names.Add(scopeClient)) throw new InvalidDataException("rpc-authoring.generated-name-collision: Imported contract scope names collide.");
            properties.Append("    public ").Append(scopeApi).Append(' ').Append(scope).AppendLine(" { get; }");
            creation.Append("        ").Append(scope).Append(" = new ").Append(scopeClient).AppendLine("(rpc, clients, services);");
            declarations.Append(accessibility).Append(" interface ").Append(scopeApi).AppendLine(" {")
                .Append(members.Properties.ToString().Replace("    public ", "    ", StringComparison.Ordinal)).AppendLine("}");
            implementations.Append("internal sealed class ").Append(scopeClient).Append(" : ").Append(scopeApi).AppendLine(" {")
                .Append("    public ").Append(scopeClient).AppendLine("(global::Sunder.Sdk.Rpc.ISunderRpcClient rpc, global::System.Collections.Generic.List<global::System.IAsyncDisposable> clients, global::System.IServiceProvider? services) {")
                .Append(members.Creation).AppendLine("    }").Append(members.Properties).AppendLine("}");
        }
        var output = new StringBuilder("// <auto-generated />\n#nullable enable\nusing global::System.Threading.Tasks;\n");
        output.Append("using global::").Append(servicesNamespace).AppendLine(";")
            .Append("using static global::").Append(implementationNamespace).AppendLine(".PackageServices;")
            .Append("namespace ").Append(servicesNamespace).AppendLine(" {")
            .Append(accessibility).AppendLine(" interface IPackageApi {").Append(properties.ToString().Replace("    public ", "    ", StringComparison.Ordinal))
            .AppendLine("}").Append(declarations).AppendLine("}")
            .Append("namespace ").Append(implementationNamespace).AppendLine(" {")
            .AppendLine("internal sealed class PackageServices : IPackageApi, global::System.IDisposable, global::System.IAsyncDisposable {")
            .AppendLine("    private readonly global::System.Collections.Generic.List<global::System.IAsyncDisposable> __clients = new();")
            .AppendLine("    public PackageServices(global::Sunder.Sdk.Rpc.ISunderRpcClient rpc) : this(rpc, null) { }")
            .AppendLine("    public PackageServices(global::Sunder.Sdk.Rpc.ISunderRpcClient rpc, global::System.IServiceProvider? services) {")
            .AppendLine("        global::System.ArgumentNullException.ThrowIfNull(rpc);")
            .AppendLine("        var clients = __clients;").Append(creation).AppendLine("    }").Append(properties)
            .AppendLine("    internal static T Own<T>(T client, global::System.Collections.Generic.List<global::System.IAsyncDisposable> clients) where T : global::System.IAsyncDisposable { clients.Add(client); return client; }")
            .AppendLine("    public void Dispose() { foreach (var client in __clients) ((global::System.IDisposable)client).Dispose(); }")
            .AppendLine("    public async global::System.Threading.Tasks.ValueTask DisposeAsync() {")
            .AppendLine("        Dispose(); // Signal every client before awaiting any drain.")
            .AppendLine("        await global::System.Threading.Tasks.Task.WhenAll(global::System.Linq.Enumerable.Select(__clients, client => client.DisposeAsync().AsTask())).ConfigureAwait(false);")
            .AppendLine("    }").AppendLine("}").Append(implementations).AppendLine("}");
        sources.Add(new("injected-apis/" + packageId + ".g.cs", output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal)));
        return new(packageId, servicesNamespace + ".IPackageApi", implementationNamespace + ".PackageServices", consumerRoles)
        { Services = selected.SelectMany(contract => contract.NaturalServices).ToArray(), Contracts = selected };
    }

    internal static bool IsEmptyRoot(SunderRpcContractDescriptor descriptor, string reference)
    {
        var schema = descriptor.Definitions[reference["#/$defs/".Length..]]; var count = 0;
        while (schema.TryGetProperty("$ref", out var next))
        {
            if (++count > 64) throw new InvalidDataException("rpc-authoring.source-limit-exceeded: Imported root references exceed their bound.");
            schema = descriptor.Definitions[next.GetString()!["#/$defs/".Length..]];
        }
        return schema.TryGetProperty("type", out var type) && type.GetString() == "object"
            && schema.TryGetProperty("properties", out var properties) && !properties.EnumerateObject().Any()
            && schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == System.Text.Json.JsonValueKind.False;
    }

    private static string Qualify(string type, SunderRpcCSharpImportedContract contract, SunderRpcCSharpGeneratorModel model)
        => contract.ProjectedModel is not null ? type : new ValueTypeQualifier(model.Definitions.Select(value => value.TypeName).ToHashSet(StringComparer.Ordinal), contract.GeneratedLogicalName)
            .Visit(SyntaxFactory.ParseTypeName(type))!.ToFullString();

    private sealed class ValueTypeQualifier(IReadOnlySet<string> names, string ns) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
            => node.Parent is not (QualifiedNameSyntax or AliasQualifiedNameSyntax) && names.Contains(node.Identifier.ValueText)
                ? SyntaxFactory.ParseTypeName("global::" + ns + "." + node.Identifier.Text) : base.VisitIdentifierName(node);
    }
}
