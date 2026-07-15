using Sunder.Sdk.Notifications;

namespace Sunder.Package.Build.Tests.Fixtures.ExternalDependency;

public abstract class ExternalBase;

public static class ExternalApi
{
    public static string Normalize(string value) => value.Trim();

    public static Type PackageOwnedSdkContract => typeof(IPackageNotificationService);
}
