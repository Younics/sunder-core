using System.ComponentModel;
using Sunder.App.Models;
using Sunder.App.Services;

namespace Sunder.App.ViewModels;

internal sealed class MainWindowSubscriptionScope : IDisposable
{
    private readonly MainWindowViewModel _shell;
    private readonly IWindowLauncher _windowLauncher;
    private readonly RuntimeStatusViewModel _runtimeStatus;
    private readonly NotificationTrayViewModel _notificationTray;
    private readonly AppUpdatePromptViewModel _appUpdatePrompt;
    private readonly PackageViewHostService _packageViewHostService;
    private readonly NotificationCenterService _notificationCenter;
    private readonly AppPackageShellViewService? _shellViewService;
    private readonly BackgroundProcessMonitorViewModel _backgroundProcesses;
    private readonly IDisposable _shellStatePersistence;
    private readonly PropertyChangedEventHandler _runtimeStatusPropertyChanged;
    private readonly PropertyChangedEventHandler _notificationTrayPropertyChanged;
    private readonly PropertyChangedEventHandler _appUpdatePromptPropertyChanged;
    private readonly EventHandler<PackageViewHostFaultEventArgs> _packageFaulted;
    private readonly Action _notificationsChanged;
    private readonly Action<AppToastNotification> _toastQueued;
    private bool _disposed;

    public MainWindowSubscriptionScope(
        MainWindowViewModel shell,
        IWindowLauncher windowLauncher,
        RuntimeStatusViewModel runtimeStatus,
        NotificationTrayViewModel notificationTray,
        AppUpdatePromptViewModel appUpdatePrompt,
        PackageViewHostService packageViewHostService,
        NotificationCenterService notificationCenter,
        AppPackageShellViewService? shellViewService,
        BackgroundProcessMonitorViewModel backgroundProcesses,
        IDisposable shellStatePersistence,
        PropertyChangedEventHandler runtimeStatusPropertyChanged,
        PropertyChangedEventHandler notificationTrayPropertyChanged,
        PropertyChangedEventHandler appUpdatePromptPropertyChanged,
        EventHandler<PackageViewHostFaultEventArgs> packageFaulted,
        Action notificationsChanged,
        Action<AppToastNotification> toastQueued)
    {
        _shell = shell;
        _windowLauncher = windowLauncher;
        _runtimeStatus = runtimeStatus;
        _notificationTray = notificationTray;
        _appUpdatePrompt = appUpdatePrompt;
        _packageViewHostService = packageViewHostService;
        _notificationCenter = notificationCenter;
        _shellViewService = shellViewService;
        _backgroundProcesses = backgroundProcesses;
        _shellStatePersistence = shellStatePersistence;
        _runtimeStatusPropertyChanged = runtimeStatusPropertyChanged;
        _notificationTrayPropertyChanged = notificationTrayPropertyChanged;
        _appUpdatePromptPropertyChanged = appUpdatePromptPropertyChanged;
        _packageFaulted = packageFaulted;
        _notificationsChanged = notificationsChanged;
        _toastQueued = toastQueued;

        _appUpdatePrompt.PropertyChanged += _appUpdatePromptPropertyChanged;
        _runtimeStatus.PropertyChanged += _runtimeStatusPropertyChanged;
        _notificationTray.PropertyChanged += _notificationTrayPropertyChanged;
        _packageViewHostService.PackageFaulted += _packageFaulted;
        _notificationCenter.NotificationsChanged += _notificationsChanged;
        _notificationCenter.ToastQueued += _toastQueued;
        if (_windowLauncher is WindowLauncher concreteWindowLauncher)
        {
            concreteWindowLauncher.AttachShell(_shell);
        }

        _shellViewService?.Attach(_shell);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _appUpdatePrompt.PropertyChanged -= _appUpdatePromptPropertyChanged;
        _runtimeStatus.PropertyChanged -= _runtimeStatusPropertyChanged;
        _notificationTray.PropertyChanged -= _notificationTrayPropertyChanged;
        _notificationTray.Dispose();
        _packageViewHostService.PackageFaulted -= _packageFaulted;
        _notificationCenter.NotificationsChanged -= _notificationsChanged;
        _notificationCenter.ToastQueued -= _toastQueued;
        if (!ReferenceEquals(_backgroundProcesses, BackgroundProcessMonitorViewModel.Empty))
        {
            _backgroundProcesses.Dispose();
        }

        if (_windowLauncher is WindowLauncher concreteWindowLauncher)
        {
            concreteWindowLauncher.DetachShell(_shell);
        }

        _shellViewService?.Detach(_shell);
        _windowLauncher.CloseForShutdown();
        _shellStatePersistence.Dispose();
    }
}
