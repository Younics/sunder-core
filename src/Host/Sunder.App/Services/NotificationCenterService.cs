using System.Text.Json;
using Sunder.App.Models;
using Sunder.Sdk.Notifications;

namespace Sunder.App.Services;

public sealed class NotificationCenterService
{
    private const int MaxPersistedNotifications = 200;
    private static readonly TimeSpan MaxPersistedNotificationAge = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _stateFilePath;
    private readonly object _syncRoot = new();
    private List<AppNotificationRecord> _notifications = [];
    private DateTimeOffset _lastReadAtUtc = DateTimeOffset.MinValue;

    public NotificationCenterService(string? stateFilePath = null)
    {
        _stateFilePath = stateFilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sunder",
            "notifications.json");
        LoadState();
    }

    public event Action? NotificationsChanged;

    public event Action<AppToastNotification>? ToastQueued;

    public DateTimeOffset LastReadAtUtc
    {
        get
        {
            lock (_syncRoot)
            {
                return _lastReadAtUtc;
            }
        }
    }

    public IReadOnlyList<AppNotificationRecord> ListNotifications()
    {
        lock (_syncRoot)
        {
            return _notifications
                .OrderByDescending(notification => notification.CreatedAtUtc)
                .ThenBy(notification => notification.NotificationId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public bool HasUnreadTrayNotifications()
    {
        lock (_syncRoot)
        {
            return _notifications.Any(notification => notification.CreatedAtUtc > _lastReadAtUtc);
        }
    }

    public ValueTask PublishAsync(
        string sourcePackageId,
        string sourceDisplayName,
        PackageNotificationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var createdAtUtc = DateTimeOffset.UtcNow;
        var notification = CreateNotification(sourcePackageId, sourceDisplayName, request, createdAtUtc);

        if (request.DisplayMode is PackageNotificationDisplayMode.TrayOnly or PackageNotificationDisplayMode.ToastAndTray)
        {
            lock (_syncRoot)
            {
                _notifications.Add(notification);
                TrimNotificationsCore(DateTimeOffset.UtcNow);
                SaveStateCore();
            }

            NotificationsChanged?.Invoke();
        }

        if (request.DisplayMode is PackageNotificationDisplayMode.ToastOnly or PackageNotificationDisplayMode.ToastAndTray)
        {
            ToastQueued?.Invoke(new AppToastNotification(
                notification.NotificationId,
                notification.SourcePackageId,
                notification.SourceDisplayName,
                notification.Title,
                notification.Message,
                notification.Severity,
                notification.CreatedAtUtc));
        }

        return ValueTask.CompletedTask;
    }

    public void MarkAllRead(DateTimeOffset? readAtUtc = null)
    {
        lock (_syncRoot)
        {
            _lastReadAtUtc = readAtUtc ?? DateTimeOffset.UtcNow;
            SaveStateCore();
        }

        NotificationsChanged?.Invoke();
    }

    public void ClearAll(DateTimeOffset? clearedAtUtc = null)
    {
        lock (_syncRoot)
        {
            _notifications.Clear();
            _lastReadAtUtc = clearedAtUtc ?? DateTimeOffset.UtcNow;
            SaveStateCore();
        }

        NotificationsChanged?.Invoke();
    }

    private static AppNotificationRecord CreateNotification(
        string sourcePackageId,
        string sourceDisplayName,
        PackageNotificationRequest request,
        DateTimeOffset createdAtUtc)
        => new(
            Guid.NewGuid().ToString("N"),
            Normalize(sourcePackageId, "unknown-package"),
            Normalize(sourceDisplayName, sourcePackageId),
            Normalize(request.Title, "Notification"),
            request.Message?.Trim() ?? string.Empty,
            request.Severity,
            createdAtUtc);

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private void LoadState()
    {
        lock (_syncRoot)
        {
            if (!File.Exists(_stateFilePath))
            {
                return;
            }

            try
            {
                var state = JsonSerializer.Deserialize<PersistedNotificationState>(File.ReadAllText(_stateFilePath), JsonOptions);
                _lastReadAtUtc = state?.LastReadAtUtc ?? DateTimeOffset.MinValue;
                _notifications = state?.Notifications?.Where(notification => notification is not null).ToList() ?? [];
                TrimNotificationsCore(DateTimeOffset.UtcNow);
                SaveStateCore();
            }
            catch
            {
                QuarantineCorruptState();
                _lastReadAtUtc = DateTimeOffset.MinValue;
                _notifications = [];
            }
        }
    }

    private void TrimNotificationsCore(DateTimeOffset nowUtc)
    {
        var cutoffUtc = nowUtc - MaxPersistedNotificationAge;
        _notifications = _notifications
            .Where(notification => notification.CreatedAtUtc >= cutoffUtc)
            .OrderByDescending(notification => notification.CreatedAtUtc)
            .ThenBy(notification => notification.NotificationId, StringComparer.Ordinal)
            .Take(MaxPersistedNotifications)
            .ToList();
    }

    private void SaveStateCore()
    {
        var stateDirectory = Path.GetDirectoryName(_stateFilePath);
        if (!string.IsNullOrWhiteSpace(stateDirectory))
        {
            Directory.CreateDirectory(stateDirectory);
        }

        File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(new PersistedNotificationState
        {
            LastReadAtUtc = _lastReadAtUtc,
            Notifications = _notifications,
        }, JsonOptions));
    }

    private void QuarantineCorruptState()
    {
        try
        {
            if (File.Exists(_stateFilePath))
            {
                File.Move(_stateFilePath, $"{_stateFilePath}.corrupt.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}");
            }
        }
        catch
        {
            // Corrupt notification state should not prevent app startup.
        }
    }

    private sealed class PersistedNotificationState
    {
        public DateTimeOffset LastReadAtUtc { get; set; } = DateTimeOffset.MinValue;

        public List<AppNotificationRecord> Notifications { get; set; } = [];
    }
}
