using System.Diagnostics;
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
                preparedSource.Manifest,
                preparedSource.ContentIndex);
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
                    appGenerationId) { LoadPermitId = activation.LoadAdmission?.PermitId },
                cancellationToken).ConfigureAwait(false);
            var lifetime = new AppWebPackageLifetime(
                package.PackageId,
                appGenerationId,
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
    private readonly object _gate = new();
    private readonly HashSet<AppWebPackageViewControl> _views = [];
    private readonly List<Exception> _viewRetirementFailures = [];
    private readonly Lazy<Task> _disposal;
    private int _disposed;

    public AppWebPackageLifetime(
        string packageId,
        Guid appGenerationId,
        AppWebContentServer contentServer,
        IAppWebRpcClient rpcClient,
        AppWebRpcContractCatalog contracts,
        AppPackageViewRegistry viewRegistry,
        IAppWebViewFactory webViewFactory,
        ExternalBrowserService externalBrowser)
    {
        _packageId = packageId;
        _userDataRoot = AppWebViewProfile.ForPackage(
            AppLocalState.RootPath, packageId, appGenerationId, OperatingSystem.IsWindows());
        _contentServer = contentServer;
        _rpcClient = rpcClient;
        _contracts = contracts;
        _viewRegistry = viewRegistry;
        _webViewFactory = webViewFactory;
        _externalBrowser = externalBrowser;
        _disposal = new(DisposeCoreAsync);
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
            () => CreateView(view), isWebView: true);
    }

    private AppWebPackageViewControl CreateView(SunderPackageWebViewManifest view)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var control = new AppWebPackageViewControl(
                _contentServer.GetRouteUri(view.Route!), AppWebViewProfile.ForView(_userDataRoot, view.ViewId!), _contentServer,
                _webViewFactory, new NonDisposingAppWebRpcClient(_rpcClient), _contracts, _externalBrowser);
            _views.Add(control);
            _ = ReleaseRetiredViewAsync(control);
            return control;
        }
    }

    public ValueTask DisposeAsync() => new(_disposal.Value);

    private async Task ReleaseRetiredViewAsync(AppWebPackageViewControl view)
    {
        try
        {
            await view.Retired.ConfigureAwait(false);
            await view.DrainNativeRetirementAsync().ConfigureAwait(false);
        }
        catch (Exception error) { lock (_gate) _viewRetirementFailures.Add(error); }
        finally { lock (_gate) _views.Remove(view); }
    }

    private async Task DisposeCoreAsync()
    {
        var started = Stopwatch.GetTimestamp();
        AppWebPackageViewControl[] views;
        lock (_gate)
        {
            _disposed = 1;
            views = _views.ToArray();
            _views.Clear();
        }
        var failures = new List<Exception>();
        try
        {
            // Registry disposal starts synchronous controls' cleanup, but a browser
            // bridge can still be notifying its document. Drain every owned view
            // before releasing its shared transport, server or private profile.
            try
            {
                if (views.Length != 0) await AvaloniaUiDispatcher.Instance.InvokeAsync(async () =>
                {
                    var drains = views.Select(view => view.DisposeAsync().AsTask()).ToArray();
                    await Task.WhenAll(drains);
                }).ConfigureAwait(false);
            }
            catch (Exception error) { failures.Add(error); }
            // A failed document must not leave its activation's idle profile or
            // other native sessions alive. Keep both verdicts if cleanup fails.
            try { await _webViewFactory.RetireActivationAsync(_userDataRoot).ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
            lock (_gate)
                failures.AddRange(_viewRetirementFailures);
        }
        finally
        {
            try { await _rpcClient.DisposeAsync().ConfigureAwait(false); }
            finally { await _contentServer.DisposeAsync().ConfigureAwait(false); }
        }
        if (failures.Count != 0) throw new AggregateException("A package browser document did not retire.", failures);
        if (views.Length != 0)
        {
            var nativeDrain = Task.WhenAll(views.Select(view => view.DrainNativeRetirementAsync()));
            try { await nativeDrain.WaitAsync(RemainingCleanupBudget(started)).ConfigureAwait(false); }
            catch (TimeoutException error)
            {
                AppCleanupQuarantine.Retain(nativeDrain, $"observing native browser exit for '{_packageId}'");
                throw new TimeoutException($"Package '{_packageId}' did not prove native view retirement within its browser cleanup allowance.", error);
            }
        }
        await DeleteOrQuarantineUserDataAsync(_userDataRoot, started).ConfigureAwait(false);
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

    private static TimeSpan RemainingCleanupBudget(long started)
    {
        var remaining = AppShutdownBudgets.BrowserCleanup - Stopwatch.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("Package browser retirement exceeded its browser cleanup allowance.");
        return remaining;
    }

    private static async Task DeleteOrQuarantineUserDataAsync(string path, long started)
    {
        while (Directory.Exists(path))
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Native view retirement was observed before reclamation. An external file
                // owner can still delay deletion, but never restart the budget.
                var remaining = AppShutdownBudgets.BrowserCleanup - Stopwatch.GetElapsedTime(started);
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
                    continue;
                }
                try
                {
                    var quarantineRoot = AppLocalState.GetPath("webview-quarantine");
                    Directory.CreateDirectory(quarantineRoot);
                    Directory.Move(path, Path.Combine(
                        quarantineRoot,
                        $"webview-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"));
                }
                catch (Exception quarantineException) when (quarantineException is IOException or UnauthorizedAccessException)
                {
                    // This is not proven retirement. Let the owning activation
                    // retain its resources/admission rather than acknowledge it.
                    throw new AggregateException("Failed to delete or quarantine package WebView user data.", exception, quarantineException);
                }
                throw new IOException("Package WebView user data was quarantined, not reclaimed.", exception);
            }
        }
    }
}
