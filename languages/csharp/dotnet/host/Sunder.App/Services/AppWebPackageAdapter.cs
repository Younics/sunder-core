using System.Security.Cryptography;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class AppWebPackageAdapter(
    AppPackageViewRegistry viewRegistry,
    IAppWebViewFactory webViewFactory,
    IAppWebRpcClientFactory rpcClientFactory,
    ExternalBrowserService externalBrowser)
{
    public async Task ActivateAsync(
        ActivePackageDescriptor package,
        PackageUiSnapshotDescriptor source,
        AppPreparedPackageSource preparedSource,
        SunderPackageTargetManifest target,
        Guid appGenerationId,
        AppPackageActivationState activation,
        CancellationToken cancellationToken)
    {
        var packageInfo = new AppLoadedPackageInfo(
            package,
            preparedSource.Folder,
            preparedSource.Manifest,
            source);
        activation.PackageInfo = packageInfo;
        var entryPoint = ArchiveRelativePath.Parse(
                source.Target.EntryPoint,
                SunderPackageFormat.MaxLogicalPathLength,
                SunderPackageFormat.MaxLogicalPathDepth)
            .ToPlatformPath(preparedSource.Folder);
        if (!File.Exists(entryPoint))
        {
            throw new InvalidOperationException(
                $"Package '{package.PackageId}' web entry point '{source.Target.EntryPoint}' is missing from its selected projection.");
        }

        AppWebContentServer? contentServer = null;
        IAppWebRpcClient? rpcClient = null;
        try
        {
            contentServer = await AppWebContentServer.StartAsync(
                preparedSource.Folder,
                target,
                cancellationToken).ConfigureAwait(false);
            var contracts = AppWebRpcContractCatalog.Load(
                preparedSource.Folder,
                preparedSource.Manifest);
            var manifestPath = Path.Combine(
                preparedSource.Folder,
                SunderPackageFormat.ManifestPath.Replace('/', Path.DirectorySeparatorChar));
            await using var manifestStream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            var manifestHash = Convert.ToHexString(
                    await SHA256.HashDataAsync(manifestStream, cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();
            rpcClient = await rpcClientFactory.CreateAsync(
                new ActivePackageWebStamp(
                    package.PackageId,
                    package.Version,
                    manifestHash,
                    source.Target,
                    source.SessionGeneration,
                    appGenerationId),
                cancellationToken).ConfigureAwait(false);
            var lifetime = new AppWebPackageLifetime(
                package.PackageId,
                preparedSource.Folder,
                contentServer,
                rpcClient,
                contracts,
                viewRegistry,
                webViewFactory,
                externalBrowser);
            activation.TargetLifetime = lifetime;
            contentServer = null;
            rpcClient = null;

            viewRegistry.SetSettingsViewPackage(new PackageSettingsViewDescriptor(
                package.PackageId,
                package.DisplayName,
                $"Configure {package.DisplayName}."));
            foreach (var view in target.Views ?? [])
            {
                if (view is null)
                {
                    continue;
                }
                lifetime.Register(view);
            }
        }
        catch
        {
            if (rpcClient is not null)
            {
                await rpcClient.DisposeAsync().ConfigureAwait(false);
            }
            if (contentServer is not null)
            {
                await contentServer.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }
}

internal sealed class AppWebPackageLifetime : IAsyncDisposable
{
    private readonly string _packageId;
    private readonly string _userDataRoot;
    private readonly AppWebContentServer _contentServer;
    private readonly IAppWebRpcClient _rpcClient;
    private readonly AppWebRpcContractCatalog _contracts;
    private readonly AppPackageViewRegistry _viewRegistry;
    private readonly IAppWebViewFactory _webViewFactory;
    private readonly ExternalBrowserService _externalBrowser;
    private int _disposed;

    public AppWebPackageLifetime(
        string packageId,
        string packageFolder,
        AppWebContentServer contentServer,
        IAppWebRpcClient rpcClient,
        AppWebRpcContractCatalog contracts,
        AppPackageViewRegistry viewRegistry,
        IAppWebViewFactory webViewFactory,
        ExternalBrowserService externalBrowser)
    {
        _packageId = packageId;
        _userDataRoot = Path.Combine(packageFolder, ".sunder-webview-data");
        _contentServer = contentServer;
        _rpcClient = rpcClient;
        _contracts = contracts;
        _viewRegistry = viewRegistry;
        _webViewFactory = webViewFactory;
        _externalBrowser = externalBrowser;
    }

    public void Register(SunderPackageWebViewManifest view)
    {
        var registration = new PackageViewRegistration(
            view.ViewId!,
            view.DisplayName!,
            view.Icon,
            ParsePlacement(view.DefaultPlacement!),
            view.ShowInHotbar!.Value);
        _viewRegistry.RegisterPackageView(
            _packageId,
            registration,
            () => new AppWebPackageViewControl(
                _contentServer.GetRouteUri(view.Route!),
                _userDataRoot,
                _contentServer,
                _webViewFactory,
                new NonDisposingAppWebRpcClient(_rpcClient),
                _contracts,
                _externalBrowser));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await _rpcClient.DisposeAsync().ConfigureAwait(false);
        await _contentServer.DisposeAsync().ConfigureAwait(false);
        DeleteOrQuarantineUserData(_userDataRoot);
    }

    private static PackageViewPlacement ParsePlacement(string value)
        => value switch
        {
            "leftTop" => PackageViewPlacement.LeftTop,
            "middle" => PackageViewPlacement.Middle,
            "rightTop" => PackageViewPlacement.RightTop,
            "leftBottom" => PackageViewPlacement.LeftBottom,
            "rightBottom" => PackageViewPlacement.RightBottom,
            _ => throw new InvalidDataException($"Unknown package web view placement '{value}'."),
        };

    private static void DeleteOrQuarantineUserData(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception)
        {
            try
            {
                var quarantineRoot = AppLocalState.GetPath("webview-quarantine");
                Directory.CreateDirectory(quarantineRoot);
                Directory.Move(path, Path.Combine(
                    quarantineRoot,
                    $"webview-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"));
            }
            catch (Exception quarantineException)
            {
                AppSessionLog.WriteError(
                    "Failed to delete or quarantine package WebView user data.",
                    new AggregateException(exception, quarantineException));
            }
        }
    }
}
