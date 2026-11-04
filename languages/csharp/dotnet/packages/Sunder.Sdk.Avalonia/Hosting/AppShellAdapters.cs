using Sunder.App.Bridge;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Services;

namespace Sunder.Sdk.Avalonia.Hosting;

internal sealed class AppShellAdapter(AppChannel channel) : IPackageShellViewService
{
    public IReadOnlyList<PackageHotbarView> ListHotbarViews() => channel.Get<IReadOnlyList<PackageHotbarView>>("shell.list");
    public bool IsViewInHotbar(string viewId) => channel.Get<bool>("shell.contains", new { viewId });
    public ValueTask<bool> AddViewToDefaultHotbarAsync(string viewId, bool openPanel = false, IReadOnlyDictionary<string, string?>? parameters = null, CancellationToken cancellationToken = default)
        => channel.Call<bool>("shell.add-default", new { viewId, openPanel, parameters }, cancellationToken);
    public ValueTask<bool> AddViewToHotbarAsync(string viewId, PackageViewPlacement placement, int? index = null, bool openPanel = false, IReadOnlyDictionary<string, string?>? parameters = null, CancellationToken cancellationToken = default)
        => channel.Call<bool>("shell.add", new { viewId, placement, index, openPanel, parameters }, cancellationToken);
    public ValueTask<bool> RemoveViewFromHotbarAsync(string viewId, CancellationToken cancellationToken = default)
        => channel.Call<bool>("shell.remove", new { viewId }, cancellationToken);
    public ValueTask<bool> OpenViewPanelAsync(string viewId, IReadOnlyDictionary<string, string?>? parameters = null, CancellationToken cancellationToken = default)
        => channel.Call<bool>("shell.open", new { viewId, parameters }, cancellationToken);
    public ValueTask<bool> CloseViewPanelAsync(string viewId, CancellationToken cancellationToken = default)
        => channel.Call<bool>("shell.close", new { viewId }, cancellationToken);
}

internal sealed class AppSettingsNavigationAdapter(AppChannel channel) : IPackageSettingsNavigationService
{
    public ValueTask<bool> OpenSettingsAsync(IReadOnlyDictionary<string, string?>? parameters = null, CancellationToken cancellationToken = default)
        => channel.Call<bool>("navigation.settings", new { parameters }, cancellationToken);
    public ValueTask<bool> OpenPackageSettingsAsync(string packageId, IReadOnlyDictionary<string, string?>? parameters = null, CancellationToken cancellationToken = default)
        => channel.Call<bool>("navigation.package-settings", new { packageId, parameters }, cancellationToken);
}

internal sealed class AppNotificationsAdapter(AppChannel channel) : IPackageNotificationService
{
    public ValueTask PublishAsync(PackageNotificationRequest request, CancellationToken cancellationToken = default)
        => channel.Call("notifications.publish", request, cancellationToken);
}

internal sealed class AppLogNavigationAdapter(AppChannel channel) : IPackageLogNavigationService
{
    public Task OpenPackageLogsAsync(string packageId, CancellationToken cancellationToken = default)
        => channel.Call("navigation.logs", new { packageId }, cancellationToken).AsTask();
}

internal sealed class AppStateUpdatesAdapter(IAppPackageHostV1 host) : IPackageAppStateUpdates
{
    public Task RunAsync(string key, Func<PackageAppStateUpdateContext, Task> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        return host.RunStateUpdateAsync(key, (settlement, cancellation) => update(new(settlement, cancellation)), cancellationToken);
    }
}

internal sealed class AppWorkerObservationsAdapter(AppChannel channel) : IPackageWorkerExecutionObservations
{
    public Task<PackageWorkerExecutionReference> CaptureAsync<TService>(ServiceProvider<TService> provider, CancellationToken cancellationToken = default) where TService : class
    {
        ArgumentNullException.ThrowIfNull(provider);
        return channel.Call<PackageWorkerExecutionReference>("worker.capture", new {
            provider.PackageId, provider.PackageVersion, provider.ProviderId, provider.ContractId, provider.ContractVersion,
            provider.DescriptorSha256, provider.AbiSha256, provider.ActivationId, provider.ActivationEpoch, provider.SessionGeneration, provider.State
        }, cancellationToken).AsTask();
    }
    public Task<PackageWorkerExecutionObservation> ObserveAsync(PackageWorkerExecutionReference reference, CancellationToken cancellationToken = default)
        => channel.Call<PackageWorkerExecutionObservation>("worker.observe", new { reference }, cancellationToken).AsTask();
}

internal sealed class AppBackgroundTasksAdapter : IBackgroundTaskQueue, IDisposable
{
    private readonly IAppPackageHostV1 _host;
    private readonly AppChannel _channel;
    private readonly IDisposable _subscription;
    internal AppBackgroundTasksAdapter(IAppPackageHostV1 host, AppChannel channel)
    {
        _host = host;
        _channel = channel;
        _subscription = host.SubscribeBackgroundTasks(value => TaskChanged?.Invoke(this, new(AppChannel.Decode<BackgroundTaskSnapshot>(value))));
    }
    public event EventHandler<BackgroundTaskChangedEventArgs>? TaskChanged;
    public BackgroundTaskSnapshot Enqueue(BackgroundTaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AppChannel.Decode<BackgroundTaskSnapshot>(_host.QueueBackgroundTask(AppChannel.Encode(new {
            request.Title, request.GroupKey, request.Indicator, request.ConcurrencyMode, request.CanCancel, request.Metadata
        }), context => request.ExecuteAsync(new(context.CancellationToken, context.ReportStatus, context.ReportProgress, context.ReportIndeterminate))));
    }
    public IReadOnlyList<BackgroundTaskSnapshot> ListTasks(string? groupKey = null) => _channel.Get<IReadOnlyList<BackgroundTaskSnapshot>>("background.list", new { groupKey });
    public bool Cancel(Guid taskId) => _channel.Get<bool>("background.cancel", new { taskId });
    public void Dispose() { _subscription.Dispose(); TaskChanged = null; }
}
