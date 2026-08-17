using Sunder.App.Services;

namespace Sunder.App.Composition;

public sealed class RuntimeEventSubscriptionServiceFactory(
    IRuntimeApiClientFactory runtimeApiClientFactory,
    DeveloperLogService developerLog,
    RuntimePackageRecoveryNotificationService? recoveryNotifications = null)
{
    public RuntimeEventSubscriptionService Create()
        => new(runtimeApiClientFactory, developerLog, recoveryNotifications);
}
