using System.Text;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;
using Sunder.Package.Format;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal sealed record SunderRpcCSharpImportedContract(
    string ContractKey,
    string GeneratedLogicalName,
    SunderRpcContractDescriptor Descriptor)
{
    internal string? AuthorityPackageId { get; init; }
    internal string? OwningPackageVersion { get; init; }
    internal SunderRpcModelProjection ModelProjection { get; init; } = null!;
    internal SunderRpcMethodPolicies MethodPolicies { get; init; } = SunderRpcMethodPolicies.Empty;
    internal SunderRpcCSharpGeneratorModel? ProjectedModel { get; init; }
    internal IReadOnlyDictionary<string, SunderRpcCSharpResourceTarget> ResourceTargets { get; init; } = new Dictionary<string, SunderRpcCSharpResourceTarget>(StringComparer.Ordinal);
    internal IReadOnlyDictionary<string, SunderRpcCSharpTypeBinding> TypeBindings { get; init; }
        = new Dictionary<string, SunderRpcCSharpTypeBinding>(StringComparer.Ordinal);
    internal IReadOnlyList<SunderRpcCSharpNaturalService> NaturalServices { get; init; } = [];
    internal IReadOnlyList<SunderRpcCSharpImportedContract> ProjectionSet { get; init; } = [];
}

internal sealed record SunderRpcCSharpNaturalService(string ServiceId, string InterfaceMetadataName,
    string ClientMetadataName, string CatalogMetadataName, string AdapterTypeName)
{
    internal string ContractId { get; init; } = string.Empty;
}

internal sealed record SunderRpcCSharpImportedProviderSurface(
    string Key,
    SunderRpcCSharpImportedContract Contract,
    string InterfaceMetadataName)
{
    internal bool IsPlatform { get; init; }
    internal bool IsNatural => !IsPlatform && Contract.NaturalServices.Count != 0;
}

internal sealed record SunderRpcCSharpImportPrepassResult(
    IReadOnlyList<SunderRpcCSharpImportedContract> Contracts,
    IReadOnlyList<SunderRpcCSharpImportedProviderSurface> ProviderSurfaces,
    IReadOnlyList<SunderRpcCSharpSourceFile> Sources)
{
    internal IReadOnlyList<SunderRpcCSharpPackageApiSurface> PackageApis { get; init; } = [];
    internal IReadOnlyDictionary<string, SunderVerifiedPlatformContractCatalog> PlatformCatalogs { get; init; }
        = new Dictionary<string, SunderVerifiedPlatformContractCatalog>(StringComparer.Ordinal);
}

internal static partial class SunderRpcCSharpImportPrepass
{
    private const string GeneratedNamespace = "Sunder.Generated.Rpc";

    // This prepass never publishes files. Its sources belong to the caller's complete output transaction.
    public static SunderRpcCSharpImportPrepassResult Prepare(SunderRpcContractLock contractLock, string cacheRoot)
    {
        ArgumentNullException.ThrowIfNull(contractLock);
        SunderRpcPlatformCatalogCache.ValidateToolRange(contractLock);
        var catalogs = SunderRpcPlatformCatalogCache.Resolve(contractLock, cacheRoot);

        var cache = SunderRpcContractCache.Resolve(contractLock, cacheRoot);
        var contracts = new Dictionary<string, SunderRpcCSharpImportedContract>(StringComparer.Ordinal);
        var sources = new List<SunderRpcCSharpSourceFile>();
        var naturalNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var authorities = contractLock.PackageArtifacts.Values.SelectMany(artifact => artifact.Contracts.Values)
            .Where(value => value.AuthorityKind == "package").GroupBy(value => value.ContractId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        ValidateNaturalServiceNames(cache.EntriesByDescriptorSha256.Values.Select(entry => entry.Descriptor), authorities);
        foreach (var entry in cache.EntriesByDescriptorSha256.Values.OrderBy(static entry => entry.Descriptor.ContractId, StringComparer.Ordinal))
        {
            var descriptor = entry.Descriptor;
            if (contracts.ContainsKey(descriptor.ContractId))
                throw new InvalidDataException($"rpc-authoring.identity-collision: Imported contract '{descriptor.ContractId}' selects more than one version.");
            var contract = new SunderRpcCSharpImportedContract(
                descriptor.ContractId,
                authorities.TryGetValue(descriptor.ContractId, out var selectedAuthority)
                    ? Editor.GeneratedPackageNames.Transport(selectedAuthority.AuthorityId, descriptor.ContractId)
                    : GeneratedNamespace + ".Imports." + ContractName(descriptor.ContractId),
                descriptor)
            {
                ModelProjection = cache.ModelProjectionsByContractId.TryGetValue(descriptor.ContractId, out var projection)
                    ? projection : throw new InvalidDataException("rpc-authoring.model-projection-invalid: Selected artifact model projection is missing."),
                MethodPolicies = cache.MethodPoliciesByContractId.TryGetValue(descriptor.ContractId, out var policies)
                    ? policies : throw new InvalidDataException("rpc-authoring.evidence-mismatch: Selected contract method-policy evidence is missing."),
            };
            if (authorities.TryGetValue(descriptor.ContractId, out var authority))
                contract = PrepareNaturalContract(contract, authority.AuthorityId, naturalNames);
            if (!contracts.TryAdd(contract.ContractKey, contract))
            {
                throw new InvalidDataException(
                    $"rpc-authoring.identity-collision: Imported contract '{contract.ContractKey}' selects more than one version.");
            }
            if (contract.AuthorityPackageId is null)
                sources.Add(new($"bindings/imports/{contract.ContractKey}.g.cs", SunderRpcCSharpGenerator.Generate(descriptor, contract.GeneratedLogicalName)));
        }

        // Project once over all selected artifacts, including reexports. Grouping
        // by the local selection would duplicate ultimate-authority models.
        sources.AddRange(ProjectNaturalContracts(contracts).Sources);

        var imports = new StringBuilder("// <auto-generated />\n#nullable enable\nnamespace Sunder.Generated.Rpc {\n\n");
        var surfaces = new List<SunderRpcCSharpImportedProviderSurface>();
        var rootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var import in contractLock.ProviderImports.Values.Select(import => (import.Key, import.ContractId, import.PackageId, IsPlatform: false))
            .Concat(contractLock.PlatformImports.Values.Select(import => (import.Key, import.ContractId, PackageId: "", IsPlatform: true)))
            .OrderBy(static import => import.Key, StringComparer.Ordinal))
        {
            var contract = contracts[import.ContractId];
            if (!import.IsPlatform)
            {
                var interfaceName = NaturalProviderImportInterface(import.PackageId, import.Key);
                var canonical = NaturalProviderInterface(contract);
                if (interfaceName != canonical)
                {
                    if (!naturalNames.TryAdd(interfaceName, "provider import " + import.Key))
                        throw new InvalidDataException($"rpc-authoring.generated-name-collision: Imported provider '{import.Key}' collides with '{interfaceName}'. Select a distinct provider key.");
                    var dot = interfaceName.LastIndexOf('.');
                    sources.Add(new("provider-apis/" + import.Key + ".g.cs", "// <auto-generated />\n#nullable enable\nnamespace "
                        + interfaceName[..dot] + ";\npublic interface " + interfaceName[(dot + 1)..] + " : global::" + canonical + " { }\n"));
                }
                surfaces.Add(new(import.Key, contract, interfaceName));
                continue;
            }
            var name = "I" + UpperInitial(import.Key) + "Service";
            ReserveRootName(rootNames, name);
            var model = SunderRpcCSharpGeneratorModelBuilder.Build(contract.Descriptor, "public");
            imports.Append("public interface ").Append(name).Append(" : ")
                .AppendJoin(", ", model.Services.Select(service => "global::" + contract.GeneratedLogicalName + "." + service.ProviderTypeName))
                .AppendLine(" { }").AppendLine();
            surfaces.Add(new(import.Key, contract, GeneratedNamespace + "." + name) { IsPlatform = import.IsPlatform });
        }

        imports.AppendLine("}");
        var consumerContracts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var import in contractLock.PackageImports.Values.OrderBy(static import => import.PackageId, StringComparer.Ordinal))
        {
            var artifact = contractLock.PackageArtifacts[import.PackageId];
            var name = UpperInitial(import.LocalName) + "PackageApi";
            ReserveRootName(rootNames, name);
            imports.Append("namespace ").Append(Editor.GeneratedPackageNames.Internal(import.PackageId)).AppendLine(" {")
                .Append("internal sealed class ").AppendLine(name).AppendLine("{")
                .Append("    public ").Append(name).AppendLine("(global::Sunder.Sdk.Rpc.ISunderRpcClient rpc)")
                .AppendLine("    {")
                .AppendLine("        global::System.ArgumentNullException.ThrowIfNull(rpc);");
            var selected = artifact.Contracts.Values.Where(static contract => contract.AuthorityKind == "package")
                .OrderBy(static contract => contract.ContractId, StringComparer.Ordinal).ToArray();
            foreach (var contract in selected)
            {
                consumerContracts.Add(contract.ContractId);
                imports.Append("        ").Append(ContractName(contract.ContractId)).Append(" = new(rpc, ")
                    .Append(ReviewedPackageSelection(import.PackageId, artifact)).AppendLine(");");
            }
            imports.AppendLine("    }");
            foreach (var contract in selected)
            {
                var contractName = ContractName(contract.ContractId);
                imports.Append("    public global::").Append(ContractApiName(contracts[contract.ContractId]))
                    .Append(' ').Append(contractName).AppendLine(" { get; }");
            }
            imports.AppendLine("}").AppendLine("}").AppendLine();
        }
        foreach (var contractId in consumerContracts.Order(StringComparer.Ordinal))
        {
            sources.Add(new($"apis/{contractId}.g.cs", EmitContractApi(contracts[contractId])));
        }
        foreach (var contract in contracts.Values.Where(value => value.NaturalServices.Count != 0).OrderBy(value => value.ContractKey, StringComparer.Ordinal))
            sources.Add(new("natural-services/" + contract.ContractKey + ".g.cs", EmitNaturalServices(contract, consumerContracts.Contains(contract.ContractKey))));
        foreach (var authority in consumerContracts.Select(id => contracts[id].AuthorityPackageId!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            sources.Add(new("injected-apis/" + authority + ".lifetime.g.cs", SunderRpcCSharpServiceApiEmitter.EmitImportedLifetime(authority)));
        sources.Add(new("imports.g.cs", imports.ToString().Replace("\r\n", "\n", StringComparison.Ordinal)));
        var packageApis = EmitInjectedPackageApis(contractLock, contracts, sources);
        return new(
            Array.AsReadOnly(contracts.Values.OrderBy(static contract => contract.ContractKey, StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(surfaces.ToArray()),
            Array.AsReadOnly(sources.OrderBy(static source => source.Path, StringComparer.Ordinal).ToArray())) { PlatformCatalogs = catalogs, PackageApis = packageApis };
    }

    internal static string ContractName(string contractId)
    {
        if (!PackageId.TryParse(contractId, out _))
        {
            throw new ArgumentException("Imported binding names require a canonical contract ID.", nameof(contractId));
        }
        return Editor.GeneratedPackageNames.ContractSymbol(contractId);
    }

    internal static string ContractApiName(SunderRpcCSharpImportedContract contract) => contract.GeneratedLogicalName + ".Api.Contract";

    internal static SunderRpcCSharpGeneratorModel GeneratorModel(SunderRpcCSharpImportedContract contract)
        => contract.ProjectedModel ?? SunderRpcCSharpGeneratorModelBuilder.Build(contract.Descriptor, "internal");

    internal static int MethodTimeout(SunderRpcCSharpImportedContract contract, string serviceId, string methodId)
        => contract.MethodPolicies.Services.TryGetValue(serviceId, out var methods) && methods.TryGetValue(methodId, out var policy) ? policy.TimeoutSeconds : 0;

    // Restore pins descriptor provenance, not runtime package selection. Both
    // ordinary injection and the advanced facade use this reviewed policy and
    // the same Contract resolver's exact tuple/identity filters.
    private static string ReviewedPackageSelection(string packageId, SunderRpcLockedPackageArtifact artifact)
        => Literal(packageId) + ", " + Literal(artifact.RequestedVersionRange) + ", " + (artifact.IncludePrerelease ? "true" : "false");

    // Source roles already have authored value symbols, source-shaped service
    // interfaces and their internal transport. Only the resolver facade is needed.
    internal static IReadOnlyList<SunderRpcCSharpSourceFile> PrepareOwnedSourcePackageApi(string packageId,
        IReadOnlyList<SunderRpcPackageProjectExportContract> contracts, string packageVersion)
        => EmitOwnedPackageFacade(packageId, contracts.Select(contract => (contract.ContractKey,
            Contract: new SunderRpcCSharpImportedContract(contract.Descriptor.ContractId, contract.GeneratedLogicalName, contract.Descriptor)
            { ModelProjection = contract.ModelProjection, MethodPolicies = contract.MethodPolicies })).ToArray(), packageVersion);

    private static IReadOnlyList<SunderRpcCSharpSourceFile> EmitOwnedPackageFacade(string packageId,
        IReadOnlyList<(string ContractKey, SunderRpcCSharpImportedContract Contract)> contracts, string packageVersion)
    {
        var files = new List<SunderRpcCSharpSourceFile>();
        var output = new StringBuilder("// <auto-generated />\n#nullable enable\nnamespace " + Editor.GeneratedPackageNames.Internal(packageId) + ";\n\ninternal sealed class PackageApi\n{\n");
        var members = new StringBuilder();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PackageApi" };
        output.AppendLine("    public PackageApi(global::Sunder.Sdk.Rpc.ISunderRpcClient rpc)\n    {\n        global::System.ArgumentNullException.ThrowIfNull(rpc);");
        var range = PackageVersionRange.Parse("=" + packageVersion).ToString();
        foreach (var (contractKey, contract) in contracts.OrderBy(contract => contract.ContractKey, StringComparer.Ordinal))
        {
            if (!SunderRpcCSharpNaming.TryCreate(contractKey, null, out var keyName))
                throw new InvalidDataException($"rpc-authoring.generated-name-collision: Owned contract key '{contractKey}' cannot produce a portable API member.");
            var name = keyName!.Pascal;
            ReserveRootName(names, name);
            var type = "global::" + contract.GeneratedLogicalName + ".Api.Contract";
            output.Append("        ").Append(name).Append(" = new(rpc, ").Append(Literal(packageId)).Append(", ")
                .Append(Literal(range)).AppendLine(", true);");
            members.Append("    public ").Append(type).Append(' ').Append(name).AppendLine(" { get; }");
            files.Add(new("apis/owned/" + contractKey + ".g.cs", EmitContractApi(contract)));
        }
        output.AppendLine("    }").Append(members).AppendLine("}");
        files.Add(new("package-api.g.cs", output.ToString()));
        return files;
    }

    private static string UpperInitial(string value) => char.ToUpperInvariant(value[0]) + value[1..];

    private static string Literal(string value) => "\"" + SunderRpcCSharpSyntax.EscapeStringLiteral(value) + "\"";

    private static void ReserveRootName(HashSet<string> names, string name)
    {
        if (!names.Add(name))
        {
            throw new InvalidDataException($"rpc-authoring.generated-name-collision: Imported surface '{name}' is duplicated.");
        }
    }

    private static string EmitContractApi(SunderRpcCSharpImportedContract contract)
    {
        const string accessibility = "internal";
        var descriptor = contract.Descriptor;
        var model = GeneratorModel(contract);
        var clientMembers = new StringBuilder();
        var clientInitialization = new StringBuilder();
        foreach (var service in model.Services)
        {
            var name = "S" + UpperInitial(service.ServiceId)
                .Replace("_", "_5f", StringComparison.Ordinal)
                .Replace(".", "_2e", StringComparison.Ordinal)
                .Replace("-", "_2d", StringComparison.Ordinal);
            clientMembers.Append("    public global::").Append(contract.GeneratedLogicalName).Append('.')
                .Append(service.ClientTypeName).Append(' ').Append(name).AppendLine(" { get; }");
            clientInitialization.Append("        ").Append(name).AppendLine(" = new(scope, provider.Snapshot.Endpoint);");
        }
        return ($$"""
            // <auto-generated />
            #nullable enable
            using global::System;
            using global::System.Collections.Generic;
            using global::System.Linq;
            using global::System.Threading;
            using global::System.Threading.Tasks;
            using global::Sunder.Sdk.Packaging;
            using global::Sunder.Sdk.Rpc;

            namespace {{contract.GeneratedLogicalName}}.Api;

            {{accessibility}} sealed class Provider
            {
                internal Provider(SunderRpcProviderSnapshot snapshot) => Snapshot = snapshot;
                internal SunderRpcProviderSnapshot Snapshot { get; }
                public string PackageId => Snapshot.PackageId;
                public string PackageVersion => Snapshot.PackageVersion;
                public string ProviderId => Snapshot.ProviderId;
                public string ContractId => Snapshot.ContractId;
                public string ContractVersion => Snapshot.ContractVersion;
                public string DescriptorSha256 => Snapshot.DescriptorSha256;
                public string AbiSha256 => Snapshot.AbiSha256;
                public Guid ActivationId => Snapshot.ActivationId;
                public long ActivationEpoch => Snapshot.ActivationEpoch;
                public long SessionGeneration => Snapshot.SessionGeneration;
                public long CatalogRevision => Snapshot.CatalogRevision;
                public SunderRpcProviderState State => Snapshot.State;
                public string? FaultCode => Snapshot.FaultCode;
            }

            {{accessibility}} sealed class CatalogSnapshot
            {
                internal CatalogSnapshot(SunderRpcCatalogSnapshot snapshot, Provider[] providers)
                {
                    Revision = snapshot.Revision;
                    Sequence = snapshot.Sequence;
                    ResetRequired = snapshot.ResetRequired;
                    Providers = Array.AsReadOnly(providers);
                }
                public long Revision { get; }
                public long Sequence { get; }
                public bool ResetRequired { get; }
                public IReadOnlyList<Provider> Providers { get; }
            }

            {{accessibility}} sealed class CatalogEvent
            {
                internal CatalogEvent(SunderRpcCatalogEvent value, Provider? provider)
                {
                    Revision = value.Revision;
                    Sequence = value.Sequence;
                    Kind = value.Kind;
                    Provider = provider;
                }
                public long Revision { get; }
                public long Sequence { get; }
                public SunderRpcCatalogEventKind Kind { get; }
                public Provider? Provider { get; }
            }

            {{accessibility}} sealed class Client
            {
                private readonly ISunderRpcCallScope _scope;
                private readonly SunderRpcEndpointReference _endpoint;
                internal Client(ISunderRpcCallScope scope, Provider provider)
                {
                    _scope = scope;
                    _endpoint = provider.Snapshot.Endpoint;
            {{clientInitialization.ToString().TrimEnd()}}
                }
                public ValueTask<SunderRpcContentReference> RegisterContentAsync(
                    global::System.IO.Stream source, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default)
                    => _scope.RegisterContentAsync(_endpoint, source, options, cancellationToken);
            {{clientMembers.ToString().TrimEnd()}}
            }

            {{accessibility}} sealed class Contract
            {
                public const string ContractId = {{Literal(descriptor.ContractId)}};
                private readonly ISunderRpcClient _rpc;
                private readonly string _packageId;
                private readonly PackageVersionRange _range;
                private readonly bool _includePrerelease;
                private readonly SunderRpcServiceBindingLifetime? _bindings;

                internal Contract(ISunderRpcClient rpc, string packageId, string range, bool includePrerelease, SunderRpcServiceBindingLifetime? bindings = null)
                {
                    _rpc = rpc;
                    _packageId = packageId;
                    _range = PackageVersionRange.Parse(range);
                    _includePrerelease = includePrerelease;
                    _bindings = bindings;
                }

                public async ValueTask<CatalogSnapshot> ListAsync(CancellationToken cancellationToken = default)
                {
                    var snapshot = await (_bindings is null ? _rpc.DiscoverAsync({{Literal(descriptor.ContractId)}}, cancellationToken)
                        : _bindings.DiscoverAsync({{Literal(descriptor.ContractId)}}, cancellationToken)).ConfigureAwait(false);
                    var providers = snapshot.Providers.Where(Matches).Select(Wrap).ToArray();
                    return new(snapshot, providers);
                }

                public async ValueTask<Provider?> GetAsync(Provider provider, CancellationToken cancellationToken = default)
                {
                    ArgumentNullException.ThrowIfNull(provider);
                    var previous = Wrap(provider.Snapshot).Snapshot;
                    var current = await _rpc.GetProviderAsync(previous.Endpoint, cancellationToken).ConfigureAwait(false);
                    if (current is null || !Matches(current)
                        || current.Endpoint != previous.Endpoint
                        || current.PackageId != previous.PackageId || current.PackageVersion != previous.PackageVersion
                        || current.ProviderId != previous.ProviderId || current.ActivationId != previous.ActivationId
                        || current.ActivationEpoch != previous.ActivationEpoch || current.SessionGeneration < previous.SessionGeneration)
                    {
                        return null;
                    }
                    return Wrap(current);
                }

                public async IAsyncEnumerable<CatalogEvent> WatchAsync(
                    long afterRevision, long afterSequence,
                    [global::System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
                {
                    await foreach (var item in _rpc.WatchAsync(afterRevision, afterSequence, cancellationToken)
                        .WithCancellation(cancellationToken).ConfigureAwait(false))
                    {
                        if (item.Kind == SunderRpcCatalogEventKind.ResetRequired)
                        {
                            yield return new(item, null);
                        }
                        else if (item.Provider is { } provider && Matches(provider))
                        {
                            yield return new(item, Wrap(provider));
                        }
                    }
                }

                public Client CreateClient(ISunderRpcCallScope scope, Provider provider)
                {
                    ArgumentNullException.ThrowIfNull(scope);
                    ArgumentNullException.ThrowIfNull(provider);
                    provider = Wrap(provider.Snapshot);
                    if (provider.State != SunderRpcProviderState.Active)
                    {
                        throw new SunderRpcException(new(SunderRpcErrorKind.StaleEndpoint, "rpc.stale-endpoint", "The provider activation is not active."));
                    }
                    return new(scope, provider);
                }

                public async ValueTask<Provider> ResolveSelectedPackageAsync(CancellationToken cancellationToken = default)
                {
                    var catalog = await ListAsync(cancellationToken).ConfigureAwait(false);
                    Provider? selected = null;
                    foreach (var provider in catalog.Providers)
                    {
                        if (!string.Equals(provider.PackageId, _packageId, StringComparison.Ordinal)
                            || provider.State != SunderRpcProviderState.Active
                            || !SemanticVersion.TryParse(provider.PackageVersion, out var version)
                            || !_range.IsSatisfiedBy(version) || (!_includePrerelease && version.Prerelease is not null)) continue;
                        if (selected is not null)
                        {
                            throw new SunderRpcException(new(SunderRpcErrorKind.NotFound, "rpc.provider-ambiguous", "More than one selected-package provider matches the locked RPC contract."));
                        }
                        selected = provider;
                    }
                    return selected ?? throw new SunderRpcException(new(SunderRpcErrorKind.NotFound, "rpc.provider-not-found", "No selected-package provider matches the locked RPC contract."));
                }

                private static bool Matches(SunderRpcProviderSnapshot provider)
                    => string.Equals(provider.ContractId, {{Literal(descriptor.ContractId)}}, StringComparison.Ordinal)
                       && string.Equals(provider.ContractVersion, {{Literal(descriptor.Version)}}, StringComparison.Ordinal)
                       && string.Equals(provider.DescriptorSha256, {{Literal(descriptor.Sha256)}}, StringComparison.Ordinal)
                       && string.Equals(provider.AbiSha256, {{Literal(descriptor.AbiSha256)}}, StringComparison.Ordinal);

                private static Provider Wrap(SunderRpcProviderSnapshot provider)
                {
                    if (!Matches(provider) || !PackageId.TryParse(provider.PackageId, out _) || !SemanticVersion.TryParse(provider.PackageVersion, out _)
                        || !PackageId.TryParse(provider.ProviderId, out _) || provider.Endpoint is null
                        || provider.ActivationId == Guid.Empty || provider.ActivationEpoch < 0 || provider.SessionGeneration < 0
                        || provider.CatalogRevision < 0 || !Enum.IsDefined(provider.State))
                    {
                        throw new SunderRpcException(new(SunderRpcErrorKind.Protocol, "rpc.binding.invalid-provider", "The broker returned invalid provider metadata."));
                    }
                    return new(provider);
                }
            }
            """ + "\n").Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
