using Microsoft.Extensions.Logging;
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

    private AppPackageContext(
        string packageId,
        string version,
        string installPath,
        IPackageStorageContext storage,
        IPackageSettings settings,
        IPackageSecrets secrets,
        RuntimePackageDataClient? runtimeClient,
        RuntimePackageOperationClient? runtimeOperationClient,
        RuntimePackageCallbackClient? runtimeCallbackClient)
    {
        PackageId = packageId;
        Version = version;
        InstallPath = installPath;
        Storage = storage;
        Settings = settings;
        Secrets = secrets;
        _runtimeClient = runtimeClient;
        _runtimeOperationClient = runtimeOperationClient;
        _runtimeCallbackClient = runtimeCallbackClient;
        Runtime = runtimeOperationClient is null
            ? NullPackageRuntimeClient.Instance
            : new AppPackageRuntimeClient(packageId, runtimeOperationClient);
        Callbacks = runtimeCallbackClient is null
            ? NullPackageCallbackClient.Instance
            : new AppPackageCallbackClient(packageId, runtimeCallbackClient, new ExternalBrowserService());
        Logging = new AppPackageLogging(PackageId);
    }

    public static Task<AppPackageContext> CreateAsync(
        string packageId,
        string version,
        string installPath,
        bool isPreflight,
        Func<RuntimeConnectionInfo?>? getRuntimeConnectionInfo,
        CancellationToken cancellationToken)
    {
        if (isPreflight)
        {
            return Task.FromResult(new AppPackageContext(
                packageId,
                version,
                installPath,
                AppPreflightPackageStorageContext.Instance,
                AppPreflightPackageSettings.Instance,
                AppPreflightPackageSecrets.Instance,
                runtimeClient: null,
                runtimeOperationClient: null,
                runtimeCallbackClient: null));
        }

        var runtimeClient = new RuntimePackageDataClient(getRuntimeConnectionInfo ?? (static () => null));
        var runtimeOperationClient = new RuntimePackageOperationClient(getRuntimeConnectionInfo ?? (static () => null));
        var runtimeCallbackClient = new RuntimePackageCallbackClient(getRuntimeConnectionInfo ?? (static () => null));
        try
        {
            IPackageRoleLocalWorkspace workspace = new AppPackageRoleLocalWorkspace(packageId);

            return Task.FromResult(new AppPackageContext(
                packageId,
                version,
                installPath,
                new AppRuntimePackageStorageContext(packageId, runtimeClient, workspace),
                new AppRuntimePackageSettings(packageId, runtimeClient),
                new AppRuntimePackageSecrets(packageId, runtimeClient),
                runtimeClient,
                runtimeOperationClient,
                runtimeCallbackClient));
        }
        catch
        {
            runtimeClient.Dispose();
            runtimeOperationClient.Dispose();
            runtimeCallbackClient.Dispose();
            throw;
        }
    }

    public string PackageId { get; }

    public string Version { get; }

    public string InstallPath { get; }

    public IPackageStorageContext Storage { get; }

    public IPackageSettings Settings { get; }

    public IPackageSecrets Secrets { get; }

    public IPackageCallbackClient Callbacks { get; }

    internal IPackageRuntimeClient Runtime { get; }

    public ILoggerFactory LoggerFactory => Logging.LoggerFactory;

    public IPackageLogging Logging { get; }

    public ValueTask DisposeAsync()
    {
        _runtimeClient?.Dispose();
        _runtimeOperationClient?.Dispose();
        _runtimeCallbackClient?.Dispose();
        if (Logging is IDisposable disposableLogging)
        {
            disposableLogging.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
