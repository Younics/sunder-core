using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Format;
using Sunder.Package.Hosting;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;

namespace Sunder.App.Services;

internal sealed class AppPackageActivator(
    AppSharedAssemblyRegistry sharedAssemblyRegistry,
    AppPackageServiceProviderFactory serviceProviderFactory,
    AppPackageViewRegistry viewRegistry,
    Func<RuntimeConnectionInfo?>? getRuntimeConnectionInfo = null,
    IAppWebViewFactory? webViewFactory = null,
    IAppWebRpcClientFactory? webRpcClientFactory = null,
    ExternalBrowserService? externalBrowser = null)
{
    public async Task ActivateAsync(
        ActivePackageDescriptor package,
        PackageUiSnapshotDescriptor source,
        AppPreparedPackageSource preparedSource,
        AppPackageActivationState activation,
        Guid appGenerationId,
        Action<string, Assembly> registerPackageAssembly,
        Action<AppPackageLoadContext> trackLoadContext,
        Action<object> trackOwnedDisposable,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((package.HostRoles & PackageHostRoles.App) == 0)
        {
            throw new InvalidOperationException($"Package '{package.PackageId}' does not declare the App host role.");
        }
        if (!string.Equals(source.Target.Role, SunderPackageFormat.AppHostRole, StringComparison.Ordinal)
            || !string.Equals(source.Target.Rid, AppPackageTargetEnvironment.CurrentRid, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Package '{package.PackageId}' snapshot target '{source.Target.Role}/{source.Target.Rid}' does not match the exact current App target 'app/{AppPackageTargetEnvironment.CurrentRid}'.");
        }
        var manifest = preparedSource.Manifest;
        var targetKey = new SunderPackageTargetKey(source.Target.Role, source.Target.Rid);
        if (!SunderPackageTargetResolver.TryResolveTarget(manifest, targetKey, out var target)
            || target is null
            || !TargetMatches(target, source.Target))
        {
            throw new InvalidOperationException(
                $"Package '{package.PackageId}' snapshot target metadata does not match its strict manifest target '{targetKey}'.");
        }

        if (string.Equals(source.Target.Kind, SunderPackageFormat.WebTargetKind, StringComparison.Ordinal))
        {
            var adapter = new AppWebPackageAdapter(
                viewRegistry,
                webViewFactory ?? new AppNativeWebViewFactory(),
                webRpcClientFactory ?? UnavailableAppWebRpcClientFactory.Instance,
                externalBrowser ?? new ExternalBrowserService());
            await adapter.ActivateAsync(
                package,
                source,
                preparedSource,
                target,
                appGenerationId,
                activation,
                cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!string.Equals(source.Target.Kind, SunderPackageFormat.AvaloniaTargetKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Package '{package.PackageId}' App target kind '{source.Target.Kind}' is unsupported; expected '{SunderPackageFormat.AvaloniaTargetKind}' or '{SunderPackageFormat.WebTargetKind}'.");
        }

        activation.PackageInfo = new AppLoadedPackageInfo(package, preparedSource.Folder, manifest, source);
        if (!File.Exists(activation.PackageInfo.EntryAssemblyPath))
        {
            throw new InvalidOperationException(
                $"Package '{package.PackageId}' App target entry point '{source.Target.EntryPoint}' is missing from its selected projection.");
        }

        var loadContext = new AppPackageLoadContext(
            package.PackageId,
            activation.PackageInfo.EntryAssemblyPath,
            source.Target.Rid,
            sharedAssemblyRegistry,
            registerPackageAssembly);
        activation.LoadContext = loadContext;
        trackLoadContext(loadContext);

        var entryAssembly = loadContext.LoadPackageEntryAssembly();
        var module = CreatePackageModule(entryAssembly);
        var packageContext = await AppPackageContext.CreateAsync(
            package.PackageId,
            package.Version,
            activation.PackageInfo.Folder,
            getRuntimeConnectionInfo,
            serviceProviderFactory.Publication,
            cancellationToken).ConfigureAwait(false);
        ISunderRpcClient rpcClient;
        try
        {
            rpcClient = await CreateManagedRpcClientAsync(
                package,
                source,
                preparedSource,
                appGenerationId,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await packageContext.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        activation.TargetLifetime = rpcClient as IAsyncDisposable;
        ServiceProvider serviceProvider;
        try
        {
            serviceProvider = serviceProviderFactory.Create(package, packageContext, module, rpcClient);
        }
        catch
        {
            await packageContext.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        activation.ServiceProvider = serviceProvider;
        trackOwnedDisposable(serviceProvider);

        viewRegistry.SetSettingsViewPackage(new PackageSettingsViewDescriptor(
            package.PackageId,
            package.DisplayName,
            $"Configure {package.DisplayName}."));
        if (module is not null)
        {
            var registry = new AppPackageContributionRegistry(
                serviceProvider,
                viewRegistry,
                package.PackageId);
            module.RegisterAppContributions(registry, serviceProvider);
        }
    }

    private async Task<ISunderRpcClient> CreateManagedRpcClientAsync(
        ActivePackageDescriptor package,
        PackageUiSnapshotDescriptor source,
        AppPreparedPackageSource preparedSource,
        Guid appGenerationId,
        CancellationToken cancellationToken)
    {
        if (getRuntimeConnectionInfo is null)
        {
            return UnavailableManagedAppRpcClient.Instance;
        }
        var manifestPath = Path.Combine(
            preparedSource.Folder,
            SunderPackageFormat.ManifestPath.Replace('/', Path.DirectorySeparatorChar));
        await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var manifestHash = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
        var factory = webRpcClientFactory ?? new RuntimeAppWebRpcClientFactory(getRuntimeConnectionInfo);
        var client = await factory.CreateAsync(
            new ActivePackageWebStamp(
                package.PackageId,
                package.Version,
                manifestHash,
                source.Target,
                source.SessionGeneration,
                appGenerationId),
            cancellationToken).ConfigureAwait(false);
        if (client is ISunderRpcClient rpcClient)
        {
            return rpcClient;
        }
        await client.DisposeAsync().ConfigureAwait(false);
        throw new InvalidOperationException("The managed App RPC factory did not produce a trusted SDK RPC client.");
    }

    private static ISunderAppPackageModule? CreatePackageModule(Assembly entryAssembly)
    {
        var moduleResolution = PackageModuleShapeReader.Read(entryAssembly.Location)
            .Resolve(PackageHostRoleMetadataValue.App);
        if (moduleResolution.Error is not null)
        {
            throw new InvalidOperationException(moduleResolution.Error);
        }
        if (moduleResolution.TypeName is null) return null;

        var moduleType = entryAssembly.GetType(moduleResolution.TypeName, throwOnError: true)!;

        if (Activator.CreateInstance(moduleType) is ISunderAppPackageModule module)
        {
            return module;
        }

        throw new InvalidOperationException($"Package module '{moduleType.FullName}' does not implement ISunderAppPackageModule.");
    }

    private static bool TargetMatches(
        SunderPackageTargetManifest manifestTarget,
        PackageTargetDescriptor descriptor)
        => string.Equals(manifestTarget.Role, descriptor.Role, StringComparison.Ordinal)
           && string.Equals(manifestTarget.Rid, descriptor.Rid, StringComparison.Ordinal)
           && string.Equals(manifestTarget.Kind, descriptor.Kind, StringComparison.Ordinal)
           && string.Equals(manifestTarget.EntryPoint, descriptor.EntryPoint, StringComparison.Ordinal)
           && string.Equals(manifestTarget.TargetFramework, descriptor.TargetFramework, StringComparison.Ordinal)
           && string.Equals(manifestTarget.SdkVersion, descriptor.SdkVersion, StringComparison.Ordinal)
           && (manifestTarget.RequiredHostCapabilities ?? [])
                .Select(static capability => capability!)
                .SequenceEqual(descriptor.RequiredHostCapabilities, StringComparer.Ordinal)
           && (manifestTarget.Views ?? []).Where(static view => view is not null)
               .Select(static view => new PackageWebViewDescriptor(
                   view!.ViewId!,
                   view.DisplayName!,
                   view.Route!,
                   view.Icon,
                   view.DefaultPlacement!,
                   view.ShowInHotbar!.Value))
               .SequenceEqual(descriptor.Views);
}

internal sealed class UnavailableManagedAppRpcClient : ISunderRpcClient
{
    public static UnavailableManagedAppRpcClient Instance { get; } = new();

    private UnavailableManagedAppRpcClient()
    {
    }

    public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromException<ISunderRpcCallScope>(Unavailable());

    public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
        => ValueTask.FromException<SunderRpcProviderSnapshot?>(Unavailable());

    public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default)
        => ValueTask.FromException<bool>(Unavailable());

    public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
        => ValueTask.FromException<SunderRpcCatalogSnapshot>(Unavailable());

    public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default)
        => ThrowAsync<SunderRpcCatalogEvent>(cancellationToken);

    public ValueTask<System.Text.Json.JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, System.Text.Json.JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        => ValueTask.FromException<System.Text.Json.JsonElement>(Unavailable());

    public IAsyncEnumerable<System.Text.Json.JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, System.Text.Json.JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        => ThrowAsync<System.Text.Json.JsonElement>(cancellationToken);

    private static SunderRpcException Unavailable() => new(new SunderRpcError(
        SunderRpcErrorKind.Unavailable,
        "rpc.app.runtime-unavailable",
        "The local Runtime RPC bridge is unavailable."));

    private static async IAsyncEnumerable<T> ThrowAsync<T>(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        throw Unavailable();
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}
