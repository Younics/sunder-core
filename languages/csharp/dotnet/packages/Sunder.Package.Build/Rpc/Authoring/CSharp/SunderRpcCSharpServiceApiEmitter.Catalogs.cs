using System.Text;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Build.Rpc.Authoring.CSharp;

internal static partial class SunderRpcCSharpServiceApiEmitter
{
    internal static string CatalogType(SunderRpcCSharpCompiledContract contract) => RemoteType(contract) + "Providers";
    internal static string CatalogApiType(string serviceType) => "global::Sunder.Sdk.Services.IServiceProviders<" + serviceType + ">";
    internal static string ReferenceFactoryApiType(string serviceType)
        => "global::System.Func<global::Sunder.Sdk.Rpc.SunderRpcProviderSnapshot, global::System.Threading.CancellationToken, " + serviceType + ">";

    internal static void EmitCatalogDeclaration(StringBuilder output, SunderRpcContractDescriptor descriptor,
        string apiType, string catalogName, string clientName, bool imported)
    {
        output.Append("internal sealed class ").Append(catalogName).Append(" : ")
            .Append(imported ? "ImportedServiceProviders<" : "GeneratedServiceProviders<").Append(apiType).AppendLine("> {")
            .Append("    public ").Append(catalogName).AppendLine("(global::Sunder.Sdk.Rpc.ISunderRpcClient rpc) : this(rpc, null) { }")
            .Append("    public ").Append(catalogName).AppendLine("(global::Sunder.Sdk.Rpc.ISunderRpcClient rpc, global::System.IServiceProvider? services)")
            .Append("        : base(rpc, ").Append(Literal(descriptor.ContractId)).Append(", ").Append(Literal(descriptor.Version))
            .Append(", ").Append(Literal(descriptor.Sha256)).Append(", ").Append(Literal(descriptor.AbiSha256))
            .Append(", (lifetime, provider, retirement) => new ").Append(clientName).AppendLine("(lifetime, provider, services, retirement), services?.GetService(typeof(global::Sunder.Sdk.Rpc.SunderRpcServiceBindingLifetime)) as global::Sunder.Sdk.Rpc.SunderRpcServiceBindingLifetime) { }")
            .AppendLine("}");
    }

    // Catalogs own a single bounded lifetime. Their ready-to-call proxies borrow
    // it and retain the exact snapshot, not a resolver that can retarget a call.
    // Discovery/watch never allocate a call scope or eagerly instantiate providers.
    private const string CatalogRuntimeSource = """
        internal class GeneratedServiceProviders<TService>(ISunderRpcClient rpc, string contractId, string contractVersion,
            string descriptorSha256, string abiSha256, Func<ServiceClientLifetime, SunderRpcProviderSnapshot, CancellationToken, TService> createService,
            SunderRpcServiceBindingLifetime? bindings)
            : global::Sunder.Sdk.Services.IServiceProviders<TService>, IDisposable, IAsyncDisposable where TService : class
        {
            private readonly ServiceClientLifetime _lifetime = new(rpc, bindings);
            internal TService BindExact(SunderRpcProviderSnapshot provider, CancellationToken retirement)
            {
                Validate(provider); retirement.ThrowIfCancellationRequested();
                if (provider.State != SunderRpcProviderState.Active) throw new SunderRpcException(new(SunderRpcErrorKind.StaleEndpoint, "rpc.stale-endpoint", "The referenced provider is not active."));
                return createService(_lifetime, provider, retirement);
            }

            public ValueTask<global::Sunder.Sdk.Services.ServiceProviderCatalog<TService>> ListAsync(CancellationToken cancellationToken = default)
                => _lifetime.Query(async (client, token) => {
                    var snapshot = await _lifetime.Bindings.DiscoverAsync(contractId, token).ConfigureAwait(false);
                    var providers = new List<global::Sunder.Sdk.Services.ServiceProvider<TService>>();
                    foreach (var provider in snapshot.Providers)
                        if (Matches(provider)) providers.Add(Wrap(provider));
                    return new global::Sunder.Sdk.Services.ServiceProviderCatalog<TService>(snapshot.Revision, snapshot.Sequence, providers, snapshot.ResetRequired);
                }, cancellationToken);

            public IAsyncEnumerable<global::Sunder.Sdk.Services.ServiceProviderCatalogEvent<TService>> WatchAsync(
                long afterRevision, long afterSequence, CancellationToken cancellationToken = default)
                => _lifetime.Observe((client, token) => Events(client, afterRevision, afterSequence, token), cancellationToken);

            private async IAsyncEnumerable<global::Sunder.Sdk.Services.ServiceProviderCatalogEvent<TService>> Events(
                ISunderRpcClient client, long afterRevision, long afterSequence,
                [global::System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
            {
                await foreach (var item in client.WatchAsync(afterRevision, afterSequence, token).WithCancellation(token).ConfigureAwait(false))
                {
                    if (!Enum.IsDefined(item.Kind)) throw InvalidProvider();
                    if (item.Kind == SunderRpcCatalogEventKind.ResetRequired)
                        yield return new(item.Revision, item.Sequence, global::Sunder.Sdk.Services.ServiceProviderChangeKind.ResetRequired, null);
                    else if (item.Provider is { } provider && Matches(provider))
                        yield return new(item.Revision, item.Sequence, (global::Sunder.Sdk.Services.ServiceProviderChangeKind)(int)item.Kind, Wrap(provider));
                }
            }

            private bool Matches(SunderRpcProviderSnapshot provider)
                => provider.ContractId == contractId && provider.ContractVersion == contractVersion
                    && provider.DescriptorSha256 == descriptorSha256 && provider.AbiSha256 == abiSha256;

            private global::Sunder.Sdk.Services.ServiceProvider<TService> Wrap(SunderRpcProviderSnapshot provider)
            {
                Validate(provider);
                return _lifetime.Bindings.CreateProvider(provider, (_, snapshot, retirement) => createService(_lifetime, snapshot, retirement));
            }
            private void Validate(SunderRpcProviderSnapshot provider)
            {
                if (!Matches(provider) || !global::Sunder.Sdk.Packaging.PackageId.TryParse(provider.PackageId, out _)
                    || !global::Sunder.Sdk.Packaging.SemanticVersion.TryParse(provider.PackageVersion, out _)
                    || !global::Sunder.Sdk.Packaging.PackageId.TryParse(provider.ProviderId, out _) || provider.Endpoint is null
                    || provider.ActivationId == Guid.Empty || provider.ActivationEpoch < 0 || provider.SessionGeneration < 0
                    || provider.CatalogRevision < 0 || !Enum.IsDefined(provider.State)) throw InvalidProvider();
            }

            private static SunderRpcException InvalidProvider()
                => new(new(SunderRpcErrorKind.Protocol, "rpc.binding.invalid-provider", "The broker returned invalid provider metadata."));
            public void Dispose() => _lifetime.Dispose();
            public ValueTask DisposeAsync() => _lifetime.DisposeAsync();
        }
        """;
}
