using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sunder.App.Bridge;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Rpc;

namespace Sunder.App.Services;

internal sealed class AppPackageBridgeHost(IServiceProvider services, IPackageContext context, ISunderRpcClient rpc)
    : AppBridgeServiceChannel(rpc), IAppPackageHostV1
{
    public string PackageId => context.PackageId;
    public string PackageVersion => context.Version;
    public string ContentRootPath => context.ContentRootPath;
    private T Service<T>() where T : notnull => services.GetRequiredService<T>();

    protected override JsonElement GetCore(string operation, JsonElement a)
    {
        switch (operation)
        {
            case "toolchain": return Encode(context.DevelopmentToolchain);
            case "workspace.root": return Encode(context.Storage.RoleLocalWorkspace.WorkspaceRootPath);
            case "workspace.path": return Encode(context.Storage.RoleLocalWorkspace.GetLocalPath(Arg<string>(a, "relativePath")));
            case "callbacks.available": return Encode(context.Callbacks.IsAvailable);
            case "shell.list": return Encode(Service<IPackageShellViewService>().ListHotbarViews());
            case "shell.contains": return Encode(Service<IPackageShellViewService>().IsViewInHotbar(Arg<string>(a, "viewId")));
            case "background.list": return Encode(Service<IBackgroundTaskQueue>().ListTasks(Arg<string?>(a, "groupKey")));
            case "background.cancel": return Encode(Service<IBackgroundTaskQueue>().Cancel(Arg<Guid>(a, "taskId")));
            case "logging.enabled": return Encode(context.Logging.LoggerFactory.CreateLogger(Arg<string>(a, "category")).IsEnabled(Arg<LogLevel>(a, "level")));
            case "logging.write":
                context.Logging.LoggerFactory.CreateLogger(Arg<string>(a, "category")).Log(Arg<LogLevel>(a, "level"),
                    new EventId(Arg<int>(a, "eventId"), Arg<string?>(a, "eventName")), Arg<string>(a, "message"),
                    ReadException(a), static (message, _) => message);
                return Encode<object?>(null);
            default: return base.GetCore(operation, a);
        }
    }

    protected override async ValueTask<JsonElement> InvokeCoreAsync(string operation, JsonElement a, CancellationToken token)
    {
        switch (operation)
        {
            case "state.get": return Encode(await context.Storage.State.GetValueAsync(Arg<string>(a, "key"), token).ConfigureAwait(false));
            case "state.contains": return Encode(await context.Storage.State.ContainsKeyAsync(Arg<string>(a, "key"), token).ConfigureAwait(false));
            case "state.list": return Encode(await context.Storage.State.ListKeysAsync(Arg<string?>(a, "prefix"), token).ConfigureAwait(false));
            case "state.set": await context.Storage.State.SetValueAsync(Arg<string>(a, "key"), Arg<string>(a, "value"), token).ConfigureAwait(false); break;
            case "state.delete": await context.Storage.State.DeleteValueAsync(Arg<string>(a, "key"), token).ConfigureAwait(false); break;
            case "files.read": return Encode(await context.Storage.Files.ReadAsync(Arg<string>(a, "relativePath"), token).ConfigureAwait(false));
            case "files.delete": await context.Storage.Files.DeleteAsync(Arg<string>(a, "relativePath"), token).ConfigureAwait(false); break;
            case "settings.get": return Encode(await context.Settings.GetValueAsync(Arg<string>(a, "key"), token).ConfigureAwait(false));
            case "settings.stored": return Encode(await context.Settings.GetStoredValueAsync(Arg<string>(a, "key"), token).ConfigureAwait(false));
            case "settings.set": await context.Settings.SetValueAsync(Arg<string>(a, "key"), Arg<string>(a, "value"), token).ConfigureAwait(false); break;
            case "settings.delete": await context.Settings.DeleteValueAsync(Arg<string>(a, "key"), token).ConfigureAwait(false); break;
            case "secrets.get": return Encode(await context.Secrets.GetSecretAsync(Arg<string>(a, "key"), token).ConfigureAwait(false));
            case "secrets.set": await context.Secrets.SetSecretAsync(Arg<string>(a, "key"), Arg<string>(a, "value"), token).ConfigureAwait(false); break;
            case "secrets.delete": await context.Secrets.DeleteSecretAsync(Arg<string>(a, "key"), token).ConfigureAwait(false); break;
            case "callbacks.start": return Encode(await context.Callbacks.StartAsync(Arg<string>(a, "callbackHandlerId"), Arg<IReadOnlyDictionary<string, string>?>(a, "parameters"), token).ConfigureAwait(false));
            case "callbacks.status": return Encode(await context.Callbacks.GetStatusAsync(Arg<string>(a, "callbackSessionId"), token).ConfigureAwait(false));
            case "callbacks.cancel": return Encode(await context.Callbacks.CancelAsync(Arg<string>(a, "callbackSessionId"), token).ConfigureAwait(false));
            case "callbacks.wait": return Encode(await context.Callbacks.WaitForCompletionAsync(Arg<string>(a, "callbackSessionId"), Arg<TimeSpan>(a, "timeout"), token).ConfigureAwait(false));
            case "callbacks.launch": await context.Callbacks.OpenLaunchUriAsync(Arg<Uri>(a, "launchUri"), token).ConfigureAwait(false); break;
            case "shell.add-default": return Encode(await Service<IPackageShellViewService>().AddViewToDefaultHotbarAsync(Arg<string>(a, "viewId"), Arg<bool>(a, "openPanel"), Parameters(a), token).ConfigureAwait(false));
            case "shell.add": return Encode(await Service<IPackageShellViewService>().AddViewToHotbarAsync(Arg<string>(a, "viewId"), Arg<PackageViewPlacement>(a, "placement"), Arg<int?>(a, "index"), Arg<bool>(a, "openPanel"), Parameters(a), token).ConfigureAwait(false));
            case "shell.remove": return Encode(await Service<IPackageShellViewService>().RemoveViewFromHotbarAsync(Arg<string>(a, "viewId"), token).ConfigureAwait(false));
            case "shell.open": return Encode(await Service<IPackageShellViewService>().OpenViewPanelAsync(Arg<string>(a, "viewId"), Parameters(a), token).ConfigureAwait(false));
            case "shell.close": return Encode(await Service<IPackageShellViewService>().CloseViewPanelAsync(Arg<string>(a, "viewId"), token).ConfigureAwait(false));
            case "navigation.settings": return Encode(await Service<IPackageSettingsNavigationService>().OpenSettingsAsync(Parameters(a), token).ConfigureAwait(false));
            case "navigation.package-settings": return Encode(await Service<IPackageSettingsNavigationService>().OpenPackageSettingsAsync(Arg<string>(a, "packageId"), Parameters(a), token).ConfigureAwait(false));
            case "navigation.logs": await Service<IPackageLogNavigationService>().OpenPackageLogsAsync(Arg<string>(a, "packageId"), token).ConfigureAwait(false); break;
            case "notifications.publish": await Service<IPackageNotificationService>().PublishAsync(Decode<PackageNotificationRequest>(a), token).ConfigureAwait(false); break;
            case "logging.event": await context.Logging.Events.WriteAsync(Arg<PackageLogLevel>(a, "level"), Arg<string>(a, "eventName"), Arg<string>(a, "message"), Arg<IReadOnlyDictionary<string, object?>?>(a, "attributes"), ReadException(a), token).ConfigureAwait(false); break;
            case "development.logs": return Encode(await Service<IPackageDevelopmentService>().GetLogsAsync(Arg<string>(a, "packageId"), Arg<PackageDevelopmentLogQuery>(a, "query"), token).ConfigureAwait(false));
            case "worker.capture": return Encode(await CaptureWorkerAsync(a, token).ConfigureAwait(false));
            case "worker.observe": return Encode(await Service<IPackageWorkerExecutionObservations>().ObserveAsync(Arg<PackageWorkerExecutionReference>(a, "reference"), token).ConfigureAwait(false));
            default: return await base.InvokeCoreAsync(operation, a, token).ConfigureAwait(false);
        }
        return Encode<object?>(null);
    }

    protected override async ValueTask<AppBridgeServiceChannel> OpenCoreAsync(string operation, JsonElement a, CancellationToken token)
        => operation == "development.load"
            ? new(development: await Service<IPackageDevelopmentService>().LoadAsync(Arg<string>(a, "developmentFolder"), Arg<bool>(a, "watch"), token).ConfigureAwait(false))
            : await base.OpenCoreAsync(operation, a, token).ConfigureAwait(false);
    protected override ValueTask<Stream?> ReadCoreAsync(string operation, JsonElement a, CancellationToken token)
        => operation == "files.read" ? context.Storage.Files.OpenReadAsync(Arg<string>(a, "relativePath"), token) : base.ReadCoreAsync(operation, a, token);
    protected override async ValueTask<JsonElement> WriteCoreAsync(string operation, JsonElement a, Stream input, CancellationToken token)
    {
        if (operation != "files.write") return await base.WriteCoreAsync(operation, a, input, token).ConfigureAwait(false);
        await context.Storage.Files.WriteAsync(Arg<string>(a, "relativePath"), input, token).ConfigureAwait(false);
        return Encode<object?>(null);
    }

    private async Task<PackageWorkerExecutionReference> CaptureWorkerAsync(JsonElement a, CancellationToken token)
    {
        // Reconstruct only a selector. The existing observer re-discovers and authorizes the exact provider.
        var catalog = await Service<ISunderRpcClient>().DiscoverAsync(Arg<string>(a, "contractId"), token).ConfigureAwait(false);
        var matches = catalog.Providers.Where(provider => provider.PackageId == Arg<string>(a, "packageId")
            && provider.PackageVersion == Arg<string>(a, "packageVersion") && provider.ProviderId == Arg<string>(a, "providerId")
            && provider.ContractVersion == Arg<string>(a, "contractVersion") && provider.DescriptorSha256 == Arg<string>(a, "descriptorSha256")
            && provider.AbiSha256 == Arg<string>(a, "abiSha256") && provider.ActivationId == Arg<Guid>(a, "activationId")
            && provider.ActivationEpoch == Arg<long>(a, "activationEpoch") && provider.SessionGeneration == Arg<long>(a, "sessionGeneration")
            && (int)provider.State == Arg<int>(a, "state")).Take(2).ToArray();
        if (matches.Length != 1) throw new SunderRpcException(new(SunderRpcErrorKind.StaleEndpoint, "rpc.stale-endpoint", "The exact service provider is not visible."));
        return await Service<IPackageWorkerExecutionObservations>().CaptureAsync(new Sunder.Sdk.Services.ServiceProvider<object>(matches[0], new object()), token).ConfigureAwait(false);
    }

    public Task RunStateUpdateAsync(string key, Func<CancellationToken, CancellationToken, Task> update, CancellationToken cancellationToken)
    {
        RequireAlive();
        return Service<IPackageAppStateUpdates>().RunAsync(key, context => update(context.SettlementRequested, context.CancellationToken), cancellationToken);
    }
    public JsonElement QueueBackgroundTask(JsonElement a, Func<AppBackgroundTaskContextV1, Task> execute)
    {
        RequireAlive();
        return Encode(Service<IBackgroundTaskQueue>().Enqueue(new(Arg<string>(a, "title"), Arg<string>(a, "groupKey"),
            Arg<BackgroundTaskIndicator>(a, "indicator"), Arg<BackgroundTaskConcurrencyMode>(a, "concurrencyMode"), Arg<bool>(a, "canCancel"),
            context => execute(new(context.CancellationToken, context.ReportStatus, context.ReportProgress, context.ReportIndeterminate)), Arg<IReadOnlyDictionary<string, string>?>(a, "metadata"))));
    }
    public IDisposable SubscribeBackgroundTasks(Action<JsonElement> changed)
    {
        RequireAlive();
        var queue = Service<IBackgroundTaskQueue>();
        EventHandler<BackgroundTaskChangedEventArgs> handler = (_, args) => changed(Encode(args.Snapshot));
        queue.TaskChanged += handler;
        return OwnSubscription(new Subscription(() => queue.TaskChanged -= handler));
    }
    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;
        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
    private static IReadOnlyDictionary<string, string?>? Parameters(JsonElement a) => Arg<IReadOnlyDictionary<string, string?>?>(a, "parameters");
    private static Exception? ReadException(JsonElement a) => Arg<string?>(a, "exception") is { } text ? new InvalidOperationException(text) : null;
}
