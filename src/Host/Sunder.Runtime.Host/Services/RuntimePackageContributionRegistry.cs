using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Hosting;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Runtime;
using Sunder.Sdk.Settings;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimePackageContributionRegistry(
    IServiceProvider serviceProvider,
    RuntimePackageExtensionCatalog extensionCatalog,
    string packageId,
    PackageExtensionOwnerActivation? extensionOwner = null) : ISunderRuntimeContributionRegistry
{
    private readonly List<IPackageBackgroundService> _backgroundServices = [];
    private readonly Dictionary<string, RuntimePackageOperationRegistration> _runtimeOperations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RuntimePackageStreamRegistration> _runtimeStreams = new(StringComparer.Ordinal);

    public bool HasRegisteredExtensions { get; private set; }

    public bool HasRegisteredBackgroundServices { get; private set; }

    public IReadOnlyList<IPackageBackgroundService> BackgroundServices => _backgroundServices;

    public PackageSettingsSchema? SettingsSchema { get; private set; }

    public IReadOnlyDictionary<string, RuntimePackageOperationRegistration> RuntimeOperations => _runtimeOperations;

    public IReadOnlyDictionary<string, RuntimePackageStreamRegistration> RuntimeStreams => _runtimeStreams;

    public void RegisterBackgroundService<TService>() where TService : class, IPackageBackgroundService
    {
        HasRegisteredBackgroundServices = true;
        _backgroundServices.Add(serviceProvider.GetRequiredService<TService>());
    }

    public void RegisterExtension<TContract>(PackageExtensionPoint<TContract> extensionPoint, TContract contribution)
    {
        HasRegisteredExtensions = true;
        if (extensionOwner is null)
        {
            extensionCatalog.Add(packageId, extensionPoint, contribution);
        }
        else
        {
            extensionCatalog.Add(extensionOwner, extensionPoint, contribution);
        }
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

}
