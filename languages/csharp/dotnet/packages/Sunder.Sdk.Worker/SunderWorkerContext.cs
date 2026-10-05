using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.Worker;

/// <summary>Provides Host-mediated services for one V1 worker activation.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
[SunderSdkCapability(SunderWorkerCapabilities.ProtocolV1)]
public sealed class SunderWorkerContext : IPackageContext
{
    internal SunderWorkerContext(Internal.WorkerRpcClient client)
    {
        var environment = client.Environment;
        PackageId = environment.PackageId;
        Version = environment.PackageVersion;
        ContentRootPath = environment.PackageContentPath;
        TemporaryRootPath = environment.PackageDataPath;
        Rpc = client;
        Settings = new Internal.WorkerPackageSettings(client);
        Storage = new Internal.WorkerPackageStorageContext(client);
        Secrets = new Internal.WorkerPackageSecrets(client);
        Logging = new Internal.WorkerPackageLogging(client);
    }

    /// <summary>Gets the immutable canonical package identifier stamped by the Host.</summary>
    public string PackageId { get; }

    /// <summary>Gets the strict semantic package version stamped by the Host.</summary>
    public string Version { get; }

    /// <summary>Gets the read-only package content root for this activation.</summary>
    public string ContentRootPath { get; }

    /// <summary>Gets the activation-owned temporary root removed when this worker exits.</summary>
    public string TemporaryRootPath { get; }

    /// <summary>Gets Host-mediated persistent storage and the local persistent Runtime workspace.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.StorageV1)]
    public IPackageStorageContext Storage { get; }

    /// <summary>Gets the activation-scoped schema-first RPC client.</summary>
    /// <remarks>The client admits calls only after the Host activates the committed worker generation.</remarks>
    public ISunderRpcClient Rpc { get; }

    /// <summary>Gets validated access to this package's schema-declared non-secret settings.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.SettingsV1)]
    public IPackageSettings Settings { get; }

    /// <summary>Gets the package-owned persistent key/value state store.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.StorageV1)]
    public IPackageKeyValueStore State => Storage.State;

    /// <summary>Gets the package-owned persistent file store.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.StorageV1)]
    public IPackageFileStore Files => Storage.Files;

    /// <summary>Gets the persistent role-local workspace bound when this worker enters activation.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.RoleLocalWorkspaceV1)]
    public IPackageRoleLocalWorkspace RoleLocalWorkspace => Storage.RoleLocalWorkspace;

    /// <summary>Gets Host-protected package secrets without exposing their persistence path.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.SecretsV1)]
    public IPackageSecrets Secrets { get; }

    /// <summary>Gets the unavailable App callback client required by <see cref="IPackageContext"/>.</summary>
    public IPackageCallbackClient Callbacks => NullPackageCallbackClient.Instance;

    /// <summary>Gets package-scoped conventional and structured logging.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.LoggingV1)]
    public IPackageLogging Logging { get; }

    /// <summary>Reports a bounded failure for this exact active Runtime generation.</summary>
    /// <remarks>The Host fences the report by activation and generation, withdraws only that generation, and terminates the worker. Messages must not contain secrets or request content.</remarks>
    public ValueTask ReportGenerationFaultAsync(
        string code,
        string message,
        CancellationToken cancellationToken = default)
        => ((Internal.WorkerRpcClient)Rpc).ReportGenerationFaultAsync(code, message, cancellationToken);
}
