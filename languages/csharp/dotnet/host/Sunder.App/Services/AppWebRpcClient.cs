using System.Security.Cryptography;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;

namespace Sunder.App.Services;

internal interface IAppWebRpcClient : IAsyncDisposable
{
    ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
        string contractId,
        CancellationToken cancellationToken);

    IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
        long afterRevision,
        long afterSequence,
        CancellationToken cancellationToken);

    ValueTask<System.Text.Json.JsonElement> InvokeAsync(
        string endpointReference,
        string serviceId,
        string methodId,
        System.Text.Json.JsonElement request,
        DateTimeOffset? deadlineUtc,
        CancellationToken cancellationToken);

    IAsyncEnumerable<System.Text.Json.JsonElement> SubscribeAsync(
        string endpointReference,
        string serviceId,
        string methodId,
        System.Text.Json.JsonElement request,
        DateTimeOffset? deadlineUtc,
        CancellationToken cancellationToken);
}

internal interface IAppWebRpcClientFactory
{
    Task<IAppWebRpcClient> CreateAsync(
        ActivePackageWebStamp package,
        CancellationToken cancellationToken);
}

internal sealed record ActivePackageWebStamp(
    string PackageId,
    string PackageVersion,
    string ManifestSha256,
    Sunder.Runtime.Contracts.PackageTargetDescriptor Target,
    long SessionGeneration,
    Guid AppGenerationId);

internal sealed class UnavailableAppWebRpcClientFactory : IAppWebRpcClientFactory
{
    public static UnavailableAppWebRpcClientFactory Instance { get; } = new();

    private UnavailableAppWebRpcClientFactory()
    {
    }

    public Task<IAppWebRpcClient> CreateAsync(
        ActivePackageWebStamp package,
        CancellationToken cancellationToken)
        => Task.FromResult<IAppWebRpcClient>(UnavailableAppWebRpcClient.Instance);
}

internal sealed class UnavailableAppWebRpcClient : IAppWebRpcClient
{
    public static UnavailableAppWebRpcClient Instance { get; } = new();

    private UnavailableAppWebRpcClient()
    {
    }

    public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
        string contractId,
        CancellationToken cancellationToken)
        => ValueTask.FromException<SunderRpcCatalogSnapshot>(Unavailable());

    public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
        long afterRevision,
        long afterSequence,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return await Task.FromException<SunderRpcCatalogEvent>(Unavailable());
    }

    public ValueTask<System.Text.Json.JsonElement> InvokeAsync(
        string endpointReference,
        string serviceId,
        string methodId,
        System.Text.Json.JsonElement request,
        DateTimeOffset? deadlineUtc,
        CancellationToken cancellationToken)
        => ValueTask.FromException<System.Text.Json.JsonElement>(Unavailable());

    public async IAsyncEnumerable<System.Text.Json.JsonElement> SubscribeAsync(
        string endpointReference,
        string serviceId,
        string methodId,
        System.Text.Json.JsonElement request,
        DateTimeOffset? deadlineUtc,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return await Task.FromException<System.Text.Json.JsonElement>(Unavailable());
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static SunderRpcException Unavailable()
        => new(new SunderRpcError(
            SunderRpcErrorKind.Unavailable,
            "rpc.app.runtime-unavailable",
            "The local Runtime RPC bridge is unavailable."));
}

internal sealed class AppWebRpcContractCatalog
{
    private readonly IReadOnlyDictionary<string, SunderRpcContractDescriptor> _contracts;

    private AppWebRpcContractCatalog(IReadOnlyDictionary<string, SunderRpcContractDescriptor> contracts)
    {
        _contracts = contracts;
    }

    public static AppWebRpcContractCatalog Load(string contentRoot, SunderPackageManifest manifest)
    {
        var contracts = new Dictionary<string, SunderRpcContractDescriptor>(StringComparer.Ordinal);
        foreach (var bundle in manifest.ContractBundles ?? [])
        {
            if (bundle is null
                || !ArchiveRelativePath.TryParse(
                    bundle.DescriptorPath,
                    SunderPackageFormat.MaxLogicalPathLength,
                    SunderPackageFormat.MaxLogicalPathDepth,
                    out var descriptorPath,
                    out _))
            {
                throw new InvalidDataException("A web package RPC contract descriptor path is invalid.");
            }
            var path = descriptorPath.ToPlatformPath(contentRoot);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Web package RPC contract descriptor '{bundle.DescriptorPath}' is missing or linked.");
            }
            var bytes = File.ReadAllBytes(path);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(hash, bundle.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Web package RPC contract descriptor '{bundle.DescriptorPath}' failed its manifest hash fence.");
            }
            var descriptor = SunderRpcContractDescriptor.Parse(bytes);
            if (!string.Equals(descriptor.ContractId, bundle.ContractId, StringComparison.Ordinal)
                || !string.Equals(descriptor.Version, bundle.Version, StringComparison.Ordinal)
                || !string.Equals(descriptor.Sha256, bundle.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Web package RPC contract descriptor '{bundle.DescriptorPath}' identity does not match its manifest.");
            }
            contracts.Add(Key(descriptor.ContractId, descriptor.Version, descriptor.Sha256), descriptor);
        }
        return new AppWebRpcContractCatalog(contracts);
    }

    public SunderRpcMethodDescriptor GetMethod(
        SunderRpcProviderSnapshot provider,
        string serviceId,
        string methodId,
        SunderRpcMethodKind kind)
    {
        if (!_contracts.TryGetValue(
                Key(provider.ContractId, provider.ContractVersion, provider.ContractSha256),
                out var contract))
        {
            throw Validation("rpc.bridge.contract-missing", "The provider contract is not bundled by the caller package.");
        }
        var method = contract.FindService(serviceId)?.FindMethod(methodId)
                     ?? throw Validation("rpc.bridge.method-not-found", "The RPC service or method is not bundled by the caller package.");
        if (method.Kind != kind)
        {
            throw Validation("rpc.bridge.method-kind", "The RPC method invocation shape is invalid.");
        }
        return method;
    }

    public void ValidateRequest(
        SunderRpcProviderSnapshot provider,
        SunderRpcMethodDescriptor method,
        System.Text.Json.JsonElement value)
        => Validate(provider, method.RequestSchemaReference, value, "request");

    public void ValidateOutput(
        SunderRpcProviderSnapshot provider,
        SunderRpcMethodDescriptor method,
        System.Text.Json.JsonElement value)
        => Validate(provider, method.OutputSchemaReference, value, "output");

    private void Validate(
        SunderRpcProviderSnapshot provider,
        string schemaReference,
        System.Text.Json.JsonElement value,
        string label)
    {
        var contract = _contracts[Key(provider.ContractId, provider.ContractVersion, provider.ContractSha256)];
        if (!contract.IsValid(schemaReference, value, out _))
        {
            throw Validation(
                $"rpc.bridge.{label}-schema",
                $"The RPC {label} does not match the bundled contract schema.");
        }
    }

    private static string Key(string contractId, string version, string hash)
        => $"{contractId}\0{version}\0{hash}";

    private static SunderRpcException Validation(string code, string message)
        => new(new SunderRpcError(SunderRpcErrorKind.Validation, code, message));
}
