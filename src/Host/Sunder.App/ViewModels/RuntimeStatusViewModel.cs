using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Sunder.App.Services;
using Sunder.Protocol;
using Sunder.Sdk.Theming;

namespace Sunder.App.ViewModels;

public sealed partial class RuntimeStatusViewModel : ViewModelBase
{
    private static IBrush? RuntimeReadyBrush => ResolveThemeBrush(SunderThemeKeys.SuccessBrush);
    private static IBrush? RuntimeWarningBrush => ResolveThemeBrush(SunderThemeKeys.WarningBrush);
    private static IBrush? RuntimeErrorBrush => ResolveThemeBrush(SunderThemeKeys.DangerBrush);
    private static IBrush? RuntimeUnavailableBrush => ResolveThemeBrush(SunderThemeKeys.ForegroundMutedBrush);
    private static IBrush? RuntimeBusyBrush => ResolveThemeBrush(SunderThemeKeys.AccentBrush);

    private readonly RuntimeConnectionState _runtimeConnectionState;
    private readonly IRuntimeApiClientFactory _runtimeApiClientFactory;
    private readonly RuntimeHostProcessManager _runtimeHostProcessManager;
    private readonly Action<Uri> _persistPreferredRuntimeUrl;
    private readonly Func<Uri, CancellationToken, Task> _startRuntimeAsync;
    private readonly SemaphoreSlim _operationSemaphore = new(1, 1);

    public RuntimeStatusViewModel(
        RuntimeConnectionState runtimeConnectionState,
        IRuntimeApiClientFactory runtimeApiClientFactory,
        RuntimeHostProcessManager runtimeHostProcessManager,
        string initialSystemStatusText,
        SystemStatusResponse? initialSystemStatus,
        IReadOnlyList<string> startupErrors,
        Action<Uri> persistPreferredRuntimeUrl,
        Func<Uri, CancellationToken, Task>? startRuntimeAsync = null)
    {
        _runtimeConnectionState = runtimeConnectionState;
        _runtimeApiClientFactory = runtimeApiClientFactory;
        _runtimeHostProcessManager = runtimeHostProcessManager;
        _persistPreferredRuntimeUrl = persistPreferredRuntimeUrl;
        _startRuntimeAsync = startRuntimeAsync ?? _runtimeHostProcessManager.EnsureStartedAsync;
        SystemStatusText = initialSystemStatusText;
        RuntimeAddressText = _runtimeConnectionState.RuntimeUrl.AbsoluteUri;
        ApplyInitialRuntimeState(initialSystemStatus, startupErrors);
    }

    public bool CanManageRuntime => !IsRuntimeBusy;

    public bool ShowRuntimeAddressEditor => !IsRuntimeRunning;

    public bool ShowApplyRuntimeButton => !IsRuntimeRunning;

    public bool ShowStartRuntimeButton => !IsRuntimeRunning;

    public bool ShowStopRuntimeButton => IsRuntimeRunning;

    public bool ShowRuntimeError => !string.IsNullOrWhiteSpace(RuntimeLastError);

    [ObservableProperty]
    private string _systemStatusText = "System Ready";

    [ObservableProperty]
    private string _runtimeAddressText = string.Empty;

    [ObservableProperty]
    private string _runtimeName = "Sunder Server";

    [ObservableProperty]
    private string _runtimeVersion = "Unknown";

    [ObservableProperty]
    private string _runtimeStatusText = "Runtime unavailable";

    [ObservableProperty]
    private string _runtimeLastError = string.Empty;

    [ObservableProperty]
    private bool _isRuntimeRunning;

    [ObservableProperty]
    private bool _isRuntimeReady;

    [ObservableProperty]
    private bool _isRuntimeBusy;

    [ObservableProperty]
    private IBrush? _runtimeStatusBrush = RuntimeUnavailableBrush;

    partial void OnIsRuntimeBusyChanged(bool value) => OnPropertyChanged(nameof(CanManageRuntime));

    partial void OnIsRuntimeRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowRuntimeAddressEditor));
        OnPropertyChanged(nameof(ShowApplyRuntimeButton));
        OnPropertyChanged(nameof(ShowStartRuntimeButton));
        OnPropertyChanged(nameof(ShowStopRuntimeButton));
    }

    partial void OnRuntimeLastErrorChanged(string value) => OnPropertyChanged(nameof(ShowRuntimeError));

    public async Task RefreshRuntimeAsync(CancellationToken cancellationToken = default)
    {
        await _operationSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (!TrySyncRuntimeAddressFromText(persistPreference: false, out _))
            {
                return;
            }

            await RefreshRuntimeStateAsync(cancellationToken);
        }
        finally
        {
            _operationSemaphore.Release();
        }
    }

    public async Task ApplyRuntimeAddressAsync(CancellationToken cancellationToken = default)
    {
        await _operationSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (!TrySyncRuntimeAddressFromText(persistPreference: true, out _))
            {
                return;
            }

            await RefreshRuntimeStateAsync(cancellationToken);
        }
        finally
        {
            _operationSemaphore.Release();
        }
    }

    public async Task StartRuntimeAsync(CancellationToken cancellationToken = default)
    {
        await _operationSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (!TrySyncRuntimeAddressFromText(persistPreference: true, out var runtimeUrl) || runtimeUrl is null)
            {
                return;
            }

            SetBusyState("Starting runtime...");
            try
            {
                await _startRuntimeAsync(runtimeUrl, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                SetRuntimeState(
                    isRunning: false,
                    isReady: false,
                    name: "Sunder Server",
                    version: "Unknown",
                    statusText: "Runtime unavailable",
                    lastError: ex.Message);
                return;
            }
            finally
            {
                IsRuntimeBusy = false;
            }

            await RefreshRuntimeStateAsync(cancellationToken);
        }
        finally
        {
            _operationSemaphore.Release();
        }
    }

    public async Task StopRuntimeAsync(CancellationToken cancellationToken = default)
    {
        await _operationSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (!TrySyncRuntimeAddressFromText(persistPreference: true, out _))
            {
                return;
            }

            SetBusyState("Stopping runtime...");
            try
            {
                using var runtimeApiClient = _runtimeApiClientFactory.CreateClient();
                await runtimeApiClient.ShutdownAsync(cancellationToken);
                await Task.Delay(250, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                await Task.Delay(250, cancellationToken);
            }
            finally
            {
                IsRuntimeBusy = false;
            }

            await RefreshRuntimeStateAsync(cancellationToken);
        }
        finally
        {
            _operationSemaphore.Release();
        }
    }

    private void ApplyInitialRuntimeState(SystemStatusResponse? initialSystemStatus, IReadOnlyList<string> startupErrors)
    {
        if (initialSystemStatus is not null)
        {
            SetRuntimeState(
                isRunning: true,
                isReady: initialSystemStatus.IsReady,
                name: initialSystemStatus.Name,
                version: initialSystemStatus.Version,
                statusText: initialSystemStatus.IsReady ? "Runtime ready" : "Runtime running",
                lastError: string.Empty);
            return;
        }

        SetRuntimeState(
            isRunning: false,
            isReady: false,
            name: "Sunder Server",
            version: "Unknown",
            statusText: "Runtime unavailable",
            lastError: startupErrors.FirstOrDefault() ?? string.Empty);
    }

    private async Task RefreshRuntimeStateAsync(CancellationToken cancellationToken)
    {
        SetBusyState("Checking runtime...");
        try
        {
            using var runtimeApiClient = _runtimeApiClientFactory.CreateClient();
            Exception? statusException = null;
            try
            {
                var systemStatus = await runtimeApiClient.GetSystemStatusAsync(cancellationToken);
                if (systemStatus is not null)
                {
                    SetRuntimeState(
                        isRunning: true,
                        isReady: systemStatus.IsReady,
                        name: systemStatus.Name,
                        version: systemStatus.Version,
                        statusText: systemStatus.IsReady ? "Runtime ready" : "Runtime running",
                        lastError: string.Empty);
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                statusException = ex;
            }

            var isRuntimeHealthy = false;
            try
            {
                isRuntimeHealthy = await runtimeApiClient.IsRuntimeHealthyAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                statusException ??= ex;
            }

            SetRuntimeState(
                isRunning: isRuntimeHealthy,
                isReady: false,
                name: "Sunder Server",
                version: "Unknown",
                statusText: statusException is not null
                    ? isRuntimeHealthy ? "Runtime API error" : "Runtime unavailable"
                    : isRuntimeHealthy ? "Runtime running" : "Runtime unavailable",
                lastError: statusException?.Message ?? string.Empty);
        }
        finally
        {
            IsRuntimeBusy = false;
        }
    }

    private bool TrySyncRuntimeAddressFromText(bool persistPreference, out Uri? runtimeUrl)
    {
        if (!RuntimeUrlHelper.TryParse(RuntimeAddressText, out runtimeUrl) || runtimeUrl is null)
        {
            RuntimeLastError = $"'{RuntimeAddressText}' is not a valid HTTP runtime URL.";
            SystemStatusText = "Runtime address invalid";
            RuntimeStatusBrush = RuntimeErrorBrush;
            return false;
        }

        _runtimeConnectionState.RuntimeUrl = runtimeUrl;
        RuntimeAddressText = runtimeUrl.AbsoluteUri;

        if (persistPreference)
        {
            _persistPreferredRuntimeUrl(runtimeUrl);
        }

        return true;
    }

    private void SetBusyState(string statusText)
    {
        IsRuntimeBusy = true;
        RuntimeLastError = string.Empty;
        RuntimeStatusText = statusText;
        SystemStatusText = statusText;
        RuntimeStatusBrush = RuntimeBusyBrush;
    }

    private void SetRuntimeState(bool isRunning, bool isReady, string name, string version, string statusText, string lastError)
    {
        IsRuntimeRunning = isRunning;
        IsRuntimeReady = isReady;
        RuntimeName = name;
        RuntimeVersion = version;
        RuntimeStatusText = statusText;
        RuntimeLastError = lastError;
        SystemStatusText = statusText;
        RuntimeStatusBrush = ResolveRuntimeStatusBrush(isRunning, isReady, lastError);
        IsRuntimeBusy = false;
    }

    private static IBrush? ResolveRuntimeStatusBrush(bool isRunning, bool isReady, string lastError)
    {
        if (!string.IsNullOrWhiteSpace(lastError))
        {
            return isRunning ? RuntimeWarningBrush : RuntimeErrorBrush;
        }

        if (isReady)
        {
            return RuntimeReadyBrush;
        }

        return isRunning ? RuntimeWarningBrush : RuntimeUnavailableBrush;
    }

    private static IBrush? ResolveThemeBrush(string resourceKey)
    {
        var application = Application.Current;
        if (application?.Resources.TryGetResource(resourceKey, application.ActualThemeVariant, out var resource) == true
            && resource is IBrush brush)
        {
            return brush;
        }

        return null;
    }
}
