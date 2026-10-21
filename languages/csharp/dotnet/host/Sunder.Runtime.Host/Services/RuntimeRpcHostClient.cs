using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeRpcHostCallerActivation : IRuntimeRpcCallerActivation
{
    public const string StackPrincipalId = "sunder.host.stack";

    private readonly object _gate = new();
    private readonly CancellationToken _retirementToken;
    private int _activeCalls;
    private int _activeObservations;

    public RuntimeRpcHostCallerActivation(
        SunderRpcContractDescriptor descriptor,
        CancellationToken retirementToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _retirementToken = retirementToken;
        RpcContracts = new Dictionary<string, SunderRpcContractDescriptor>(StringComparer.Ordinal)
        {
            [PackageSessionPreparer.ContractKey(descriptor.ContractId, descriptor.Version)] = descriptor,
        };
        RpcContractUses =
        [
            new SunderPackageContractUseManifest
            {
                Role = SunderPackageFormat.RuntimeHostRole,
                ContractId = descriptor.ContractId,
                ContractVersion = descriptor.Version,
                AbiSha256 = descriptor.AbiSha256,
                Required = true,
                Actions = [SunderRpcProtocol.DiscoverAction, SunderRpcProtocol.InvokeAction],
            },
        ];
        ManifestSha256 = descriptor.Sha256;
    }

    public string PackageId => StackPrincipalId;
    public string PackageVersion => "1.0.0";
    public Guid? ActivationId => null;
    public string Role => SunderPackageFormat.RuntimeHostRole;
    public string ManifestSha256 { get; }
    public PackageSourceKind? SourceKind => null;
    public IReadOnlyDictionary<string, SunderRpcContractDescriptor> RpcContracts { get; }
    public IReadOnlyList<SunderPackageContractUseManifest> RpcContractUses { get; }
    public CancellationToken RetirementToken => _retirementToken;

    public bool IsCurrent(PackageSessionLease sessionLease)
        => !_retirementToken.IsCancellationRequested;

    public bool TryAcquireCaller(int maximumCalls, out RuntimeRpcActivationLease? lease, bool observation = false)
    {
        lock (_gate)
        {
            ref var active = ref (observation ? ref _activeObservations : ref _activeCalls);
            if (_retirementToken.IsCancellationRequested || active >= maximumCalls)
            {
                lease = null;
                return false;
            }
            active++;
            lease = new RuntimeRpcActivationLease(_retirementToken, () => Release(observation));
            return true;
        }
    }

    private void Release(bool observation)
    {
        lock (_gate)
        {
            ref var active = ref (observation ? ref _activeObservations : ref _activeCalls);
            if (active > 0) active--;
        }
    }
}

internal sealed class RuntimeRpcHostClient(
    RuntimeRpcBroker broker,
    RuntimeRpcHostCallerActivation caller) : ISunderRpcClient
{
    public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
        => broker.CreateHostCallScopeAsync(caller, options, cancellationToken);

    public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
        SunderRpcEndpointReference endpoint,
        CancellationToken cancellationToken = default)
        => broker.GetProviderHostAsync(caller, endpoint, cancellationToken);

    public ValueTask<bool> TryReportInvariantViolationAsync(
        SunderRpcEndpointReference endpoint,
        Exception exception,
        CancellationToken cancellationToken = default)
        => broker.TryReportInvariantViolationHostAsync(caller, endpoint, exception, cancellationToken);

    public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
        string contractId,
        CancellationToken cancellationToken = default)
        => broker.DiscoverHostAsync(caller, contractId, cancellationToken);

    public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
        long afterRevision,
        long afterSequence,
        CancellationToken cancellationToken = default)
        => Unsupported<SunderRpcCatalogEvent>(cancellationToken);

    public ValueTask<JsonElement> InvokeAsync(
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
        => broker.InvokeHostAsync(caller, endpoint, serviceId, methodId, request, options, cancellationToken);

    public IAsyncEnumerable<JsonElement> SubscribeAsync(
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
        => Unsupported<JsonElement>(cancellationToken);

    private static async IAsyncEnumerable<T> Unsupported<T>(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        throw new NotSupportedException("The internal Stack Host RPC principal supports discovery and unary calls only.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}
