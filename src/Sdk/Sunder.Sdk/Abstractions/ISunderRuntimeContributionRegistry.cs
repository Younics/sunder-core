using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Runtime;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Settings;

namespace Sunder.Sdk.Abstractions;

/// <summary>Collects Runtime contributions during one package activation.</summary>
/// <remarks>Every registration is attributed to the currently activating package. The Runtime retains registrations and their service instances until deactivation. Registration is single-threaded and valid only during module activation.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.ContributionsV1)]
public interface ISunderRuntimeContributionRegistry
{
    /// <summary>Registers an activation-scoped service that the host starts and stops.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.BackgroundServicesV1)]
    void RegisterBackgroundService<TService>() where TService : class, IPackageBackgroundService;

    /// <summary>Registers the package's complete host-rendered settings schema.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.SettingsSchemaV1)]
    void RegisterSettingsSchema(PackageSettingsSchema schema)
        => throw new NotSupportedException(
            "This host has not implemented the Sunder 1.1 settings-schema registration adapter.");

    /// <summary>Registers one package-scoped typed operation handler.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.RuntimeOperationsV1)]
    void RegisterRuntimeOperation<TRequest, TResponse>(
        PackageRuntimeOperation<TRequest, TResponse> operation,
        IPackageRuntimeOperationHandler<TRequest, TResponse> handler)
        where TRequest : class
        where TResponse : class;

    /// <summary>Registers one package-scoped typed event-stream handler.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.RuntimeOperationsV1)]
    void RegisterRuntimeStream<TRequest, TEvent>(
        PackageRuntimeStream<TRequest, TEvent> stream,
        IPackageRuntimeStreamHandler<TRequest, TEvent> handler)
        where TRequest : class
        where TEvent : class;

    /// <summary>Registers one manifest-declared schema-first RPC provider for this exact activation.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
    void RegisterRpcProvider(string providerId, ISunderRpcServiceHandler handler)
        => throw new NotSupportedException(
            "This host has not implemented the Sunder schema-first RPC V1 registration adapter.");
}
