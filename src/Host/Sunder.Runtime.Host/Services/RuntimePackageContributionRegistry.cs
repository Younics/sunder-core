using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Format;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Runtime;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Settings;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimePackageContributionRegistry(
    IServiceProvider serviceProvider,
    string packageId,
    SunderPackageManifest? manifest = null,
    IReadOnlyDictionary<string, SunderRpcContractDescriptor>? rpcContracts = null) : ISunderRuntimeContributionRegistry
{
    private readonly List<IPackageBackgroundService> _backgroundServices = [];
    private readonly Dictionary<string, RuntimePackageOperationRegistration> _runtimeOperations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RuntimePackageStreamRegistration> _runtimeStreams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RuntimeRpcProviderRegistration> _rpcProviders = new(StringComparer.Ordinal);

    public bool HasRegisteredBackgroundServices { get; private set; }

    public IReadOnlyList<IPackageBackgroundService> BackgroundServices => _backgroundServices;

    public PackageSettingsSchema? SettingsSchema { get; private set; }

    public IReadOnlyDictionary<string, RuntimePackageOperationRegistration> RuntimeOperations => _runtimeOperations;

    public IReadOnlyDictionary<string, RuntimePackageStreamRegistration> RuntimeStreams => _runtimeStreams;

    public IReadOnlyDictionary<string, RuntimeRpcProviderRegistration> RpcProviders => _rpcProviders;

    public void RegisterBackgroundService<TService>() where TService : class, IPackageBackgroundService
    {
        HasRegisteredBackgroundServices = true;
        _backgroundServices.Add(serviceProvider.GetRequiredService<TService>());
    }

    public void RegisterSettingsSchema(PackageSettingsSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (SettingsSchema is not null)
        {
            throw new InvalidOperationException(
                $"Package '{packageId}' registered more than one settings schema.");
        }

        SettingsSchema = schema;
    }

    public void RegisterRuntimeOperation<TRequest, TResponse>(
        PackageRuntimeOperation<TRequest, TResponse> operation,
        IPackageRuntimeOperationHandler<TRequest, TResponse> handler)
        where TRequest : class
        where TResponse : class
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(handler);
        if (!_runtimeOperations.TryAdd(
                operation.OperationId,
                new RuntimePackageOperationRegistration<TRequest, TResponse>(operation, handler)))
        {
            throw new InvalidOperationException(
                $"Package '{packageId}' registered duplicate Runtime operation '{operation.OperationId}'.");
        }
    }

    public void RegisterRuntimeStream<TRequest, TEvent>(
        PackageRuntimeStream<TRequest, TEvent> stream,
        IPackageRuntimeStreamHandler<TRequest, TEvent> handler)
        where TRequest : class
        where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(handler);
        if (!_runtimeStreams.TryAdd(
                stream.StreamId,
                new RuntimePackageStreamRegistration<TRequest, TEvent>(stream, handler)))
        {
            throw new InvalidOperationException(
                $"Package '{packageId}' registered duplicate Runtime stream '{stream.StreamId}'.");
        }
    }

    public void RegisterRpcProvider(string providerId, ISunderRpcServiceHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(handler);
        var declarations = (manifest?.Provides ?? [])
            .Where(provider => provider is not null
                               && string.Equals(provider.ProviderId, providerId, StringComparison.Ordinal)
                               && string.Equals(provider.Role, SunderPackageFormat.RuntimeHostRole, StringComparison.Ordinal))
            .Select(static provider => provider!)
            .ToArray();
        if (declarations.Length != 1)
        {
            throw new InvalidOperationException(
                $"Package '{packageId}' registered RPC provider '{providerId}' without one matching Runtime provider manifest declaration.");
        }

        var declaration = declarations[0];
        var key = PackageSessionPreparer.ContractKey(declaration.ContractId!, declaration.ContractVersion!);
        if (rpcContracts is null
            || !rpcContracts.TryGetValue(key, out var contract)
            || !string.Equals(contract.Sha256, declaration.ContractSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Package '{packageId}' RPC provider '{providerId}' does not match a validated local contract descriptor.");
        }
        if (!_rpcProviders.TryAdd(
                providerId,
                new RuntimeRpcProviderRegistration(
                    providerId,
                    declaration.ContractId!,
                    declaration.ContractVersion!,
                    declaration.ContractSha256!,
                    contract,
                    handler,
                    declaration)))
        {
            throw new InvalidOperationException(
                $"Package '{packageId}' registered duplicate RPC provider '{providerId}'.");
        }
    }

    public void ValidateRpcProviders()
    {
        foreach (var declaration in (manifest?.Provides ?? []).Where(provider =>
                     provider is not null
                     && string.Equals(provider.Role, SunderPackageFormat.RuntimeHostRole, StringComparison.Ordinal)))
        {
            if (!_rpcProviders.ContainsKey(declaration!.ProviderId!))
            {
                throw new InvalidOperationException(
                    $"Package '{packageId}' did not register manifest-declared RPC provider '{declaration.ProviderId}'.");
            }
        }
    }

}
