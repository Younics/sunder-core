using System.Security.Cryptography;
using Sunder.Package.Format;
using Sunder.Runtime.Client;
using Sunder.Sdk.Rpc;

namespace Sunder.App.Services;

internal interface IAppWebRpcClient : IAsyncDisposable
{
    RuntimeClientResourceLimits ResourceLimits => RuntimeClientResourceLimits.Standard;

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
    Guid AppGenerationId)
{
    internal string? LoadPermitId { get; init; }
}

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

    public static AppWebRpcContractCatalog Load(
        string contentRoot,
        SunderPackageManifest manifest,
        SunderPackageContentIndex contentIndex)
    {
        AppPackageManifestReader.EnsureSupported(manifest);
        if (contentIndex.SchemaVersion != SunderPackageFormat.CurrentContentIndexVersion)
        {
            throw new InvalidDataException("The web package content index version is invalid.");
        }
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
            var indexedDescriptors = (contentIndex.Files ?? [])
                .Where(entry => entry is not null
                                && string.Equals(entry.Path, descriptorPath.ToString(), StringComparison.Ordinal))
                .ToArray();
            if (indexedDescriptors.Length != 1
                || indexedDescriptors[0]!.Size < 0
                || indexedDescriptors[0]!.Size > SunderPackageFormat.MaxContractDescriptorBytes
                || !IsSha256(indexedDescriptors[0]!.Sha256))
            {
                throw new InvalidDataException(
                    $"Web package RPC contract descriptor '{bundle.DescriptorPath}' has invalid content-index metadata.");
            }
            var path = descriptorPath.ToPlatformPath(contentRoot);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Web package RPC contract descriptor '{bundle.DescriptorPath}' is missing or linked.");
            }
            if (new FileInfo(path).Length != indexedDescriptors[0]!.Size)
            {
                throw new InvalidDataException(
                    $"Web package RPC contract descriptor '{bundle.DescriptorPath}' failed its content-index integrity check.");
            }
            var bytes = File.ReadAllBytes(path);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var indexedDescriptor = indexedDescriptors[0]!;
            if (bytes.LongLength != indexedDescriptor.Size
                || !string.Equals(hash, indexedDescriptor.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Web package RPC contract descriptor '{bundle.DescriptorPath}' failed its content-index integrity check.");
            }
            SunderRpcContractDescriptor descriptor;
            try
            {
                descriptor = SunderRpcContractDescriptor.Parse(bytes);
            }
            catch (SunderRpcDescriptorException exception)
            {
                throw new InvalidDataException(
                    $"Web package RPC contract descriptor '{bundle.DescriptorPath}' is invalid: {exception.Message}",
                    exception);
            }
            if (descriptor.DescriptorVersion != SunderRpcProtocol.DescriptorVersion)
            {
                throw new InvalidDataException(
                    $"Web package RPC contract descriptor '{bundle.DescriptorPath}' must use descriptorVersion {SunderRpcProtocol.DescriptorVersion}.");
            }
            if (!string.Equals(descriptor.ContractId, bundle.ContractId, StringComparison.Ordinal)
                || !string.Equals(descriptor.Version, bundle.Version, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Web package RPC contract descriptor '{bundle.DescriptorPath}' failed its exact contract identity fence.");
            }
            if (!string.Equals(descriptor.Sha256, bundle.DescriptorSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Web package RPC contract descriptor '{bundle.DescriptorPath}' failed its descriptorSha256 fence.");
            }
            if (!string.Equals(descriptor.AbiSha256, bundle.AbiSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Web package RPC contract descriptor '{bundle.DescriptorPath}' failed its abiSha256 fence.");
            }
            if (!contracts.TryAdd(Key(descriptor.ContractId, descriptor.Version), descriptor))
            {
                throw new InvalidDataException(
                    $"Web package RPC contract '{descriptor.ContractId}' version '{descriptor.Version}' is bundled more than once.");
            }
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
                Key(provider.ContractId, provider.ContractVersion),
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
        var contract = _contracts[Key(provider.ContractId, provider.ContractVersion)];
        if (!contract.IsValid(schemaReference, value, out _))
        {
            throw Validation(
                $"rpc.bridge.{label}-schema",
                $"The RPC {label} does not match the bundled contract schema.");
        }
    }

    private static string Key(string contractId, string version)
        => $"{contractId}\0{version}";

    private static bool IsSha256(string? value)
        => value is { Length: 64 }
           && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static SunderRpcException Validation(string code, string message)
        => new(new SunderRpcError(SunderRpcErrorKind.Validation, code, message));
}
