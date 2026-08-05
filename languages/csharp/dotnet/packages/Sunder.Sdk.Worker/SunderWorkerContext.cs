using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.Worker;

/// <summary>Provides Host-mediated services for one V2 worker activation.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
[SunderSdkCapability(SunderWorkerCapabilities.ProtocolV2)]
public sealed class SunderWorkerContext
{
    internal SunderWorkerContext(Internal.WorkerRpcClient client)
    {
        Rpc = client;
        Settings = new Internal.WorkerPackageSettings(client);
        State = new Internal.WorkerPackageKeyValueStore(client);
        Files = new Internal.WorkerPackageFileStore(client);
        Secrets = new Internal.WorkerPackageSecrets(client);
        Logging = new Internal.WorkerPackageLogging(client);
    }

    /// <summary>Gets the activation-scoped schema-first RPC client.</summary>
    /// <remarks>The client admits calls only after the Host activates the committed worker generation.</remarks>
    public ISunderRpcClient Rpc { get; }

    /// <summary>Gets validated access to this package's schema-declared non-secret settings.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.SettingsV1)]
    public IPackageSettings Settings { get; }

    /// <summary>Gets the package-owned persistent key/value state store.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.StorageV1)]
    public IPackageKeyValueStore State { get; }

    /// <summary>Gets the package-owned persistent file store.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.StorageV1)]
    public IPackageFileStore Files { get; }

    /// <summary>Gets Host-protected package secrets without exposing their persistence path.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.SecretsV1)]
    public IPackageSecrets Secrets { get; }

    /// <summary>Gets package-scoped conventional and structured logging.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.LoggingV1)]
    public IPackageLogging Logging { get; }
}
