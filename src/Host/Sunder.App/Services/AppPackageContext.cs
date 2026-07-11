using Microsoft.Extensions.Logging;
using Sunder.Runtime.Client;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;

namespace Sunder.App.Services;

internal sealed class AppPackageContext : IPackageContext, IAsyncDisposable
{
    private readonly RuntimePackageDataClient? _runtimeClient;
    private readonly IPackageLocalWorkspaceLease? _localWorkspace;

    private AppPackageContext(
        string packageId,
        string version,
        string installPath,
        IPackageStorageContext storage,
        IPackageConfiguration configuration,
        IPackageSecrets secrets,
        RuntimePackageDataClient? runtimeClient)
    {
        PackageId = packageId;
        Version = version;
        InstallPath = installPath;
        Storage = storage;
        Configuration = configuration;
        Secrets = secrets;
        _runtimeClient = runtimeClient;
        _localWorkspace = runtimeClient is null ? null : storage.LocalWorkspace;

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
                AppPreflightPackageConfiguration.Instance,
                AppPreflightPackageSecrets.Instance,
                runtimeClient: null));
        }

        var runtimeClient = new RuntimePackageDataClient(getRuntimeConnectionInfo ?? (static () => null));
        try
        {
            IPackageLocalWorkspaceLease workspace = new AppPackageLocalWorkspaceLease(packageId);

            return Task.FromResult(new AppPackageContext(
                packageId,
                version,
                installPath,
                new AppRuntimePackageStorageContext(packageId, runtimeClient, workspace),
                new AppRuntimePackageConfiguration(packageId, runtimeClient),
                new AppRuntimePackageSecrets(packageId, runtimeClient),
                runtimeClient));
        }
        catch
        {
            runtimeClient.Dispose();
            throw;
        }
    }

    public string PackageId { get; }

    public string Version { get; }

    public string InstallPath { get; }

    public IPackageStorageContext Storage { get; }

    public IPackageConfiguration Configuration { get; }

    public IPackageSecrets Secrets { get; }

    public ILoggerFactory LoggerFactory => Logging.LoggerFactory;

    public IPackageLogging Logging { get; }

    public async ValueTask DisposeAsync()
    {
        if (_localWorkspace is not null)
        {
            await _localWorkspace.DisposeAsync().ConfigureAwait(false);
        }

        _runtimeClient?.Dispose();
        if (Logging is IDisposable disposableLogging)
        {
            disposableLogging.Dispose();
        }
    }
}
