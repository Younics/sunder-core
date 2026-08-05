using Sunder.Runtime.Client;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Runtime;

namespace Sunder.App.Services;

internal sealed class AppPackageContext : IPackageContext, IAsyncDisposable
{
    private readonly RuntimePackageDataClient? _runtimeClient;
    private readonly RuntimePackageOperationClient? _runtimeOperationClient;
    private readonly RuntimePackageCallbackClient? _runtimeCallbackClient;
    private readonly RuntimeClientTransport? _ownedTransport;

    private AppPackageContext(
        string packageId,
        string version,
        string contentRootPath,
        IPackageStorageContext storage,
        IPackageSettings settings,
        IPackageSecrets secrets,
        RuntimePackageDataClient? runtimeClient,
        RuntimePackageOperationClient? runtimeOperationClient,
        RuntimePackageCallbackClient? runtimeCallbackClient,
        RuntimeClientTransport? ownedTransport,
        AppPackageGenerationPublication publication)
    {
        PackageId = packageId;
        Version = version;
        ContentRootPath = contentRootPath;
        Storage = storage;
        Settings = settings;
        Secrets = secrets;
        _runtimeClient = runtimeClient;
        _runtimeOperationClient = runtimeOperationClient;
        _runtimeCallbackClient = runtimeCallbackClient;
        _ownedTransport = ownedTransport;
        Runtime = runtimeOperationClient is null
            ? NullPackageRuntimeClient.Instance
            : new AppPackageRuntimeClient(packageId, runtimeOperationClient, publication);
        Callbacks = runtimeCallbackClient is null
            ? NullPackageCallbackClient.Instance
            : new AppPackageCallbackClient(packageId, runtimeCallbackClient, new ExternalBrowserService(), publication);
        Logging = new AppPackageLogging(PackageId);
    }

    public static Task<AppPackageContext> CreateAsync(
        string packageId,
        string version,
        string contentRootPath,
        Func<RuntimeConnectionInfo?>? getRuntimeConnectionInfo,
        AppPackageGenerationPublication publication,
        CancellationToken cancellationToken)
    {
        var sharedTransport = getRuntimeConnectionInfo?.Target as RuntimeClientTransport;
        var ownedTransport = sharedTransport is null
            ? new RuntimeClientTransport(getRuntimeConnectionInfo ?? (static () => null))
            : null;
        var transport = sharedTransport ?? ownedTransport!;
        var runtimeClient = new RuntimePackageDataClient(transport);
        var runtimeOperationClient = new RuntimePackageOperationClient(transport);
        var runtimeCallbackClient = new RuntimePackageCallbackClient(transport);
        try
        {
            IPackageRoleLocalWorkspace workspace = new AppPackageRoleLocalWorkspace(packageId);

            return Task.FromResult(new AppPackageContext(
                packageId,
                version,
                contentRootPath,
                new AppRuntimePackageStorageContext(packageId, runtimeClient, workspace, publication),
                new AppRuntimePackageSettings(packageId, runtimeClient, publication),
                new AppRuntimePackageSecrets(packageId, runtimeClient, publication),
                runtimeClient,
                runtimeOperationClient,
                runtimeCallbackClient,
                ownedTransport,
                publication));
        }
        catch
        {
            runtimeClient.Dispose();
            runtimeOperationClient.Dispose();
            runtimeCallbackClient.Dispose();
            ownedTransport?.Dispose();
            throw;
        }
    }

    public string PackageId { get; }

    public string Version { get; }

    public string ContentRootPath { get; }

    public IPackageStorageContext Storage { get; }

    public IPackageSettings Settings { get; }

    public IPackageSecrets Secrets { get; }

    public IPackageCallbackClient Callbacks { get; }

    internal IPackageRuntimeClient Runtime { get; }

    public IPackageLogging Logging { get; }

    public ValueTask DisposeAsync()
    {
        _runtimeClient?.Dispose();
        _runtimeOperationClient?.Dispose();
        _runtimeCallbackClient?.Dispose();
        _ownedTransport?.Dispose();
        if (Logging is IDisposable disposableLogging)
        {
            disposableLogging.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
