using Sunder.Runtime.LocalState;
using Sunder.Host.Contracts;

namespace Sunder.Host.Client;

public static class HostConnectionInfoStore
{
    public static string GetDefaultRootPath()
        => HostStatePaths.GetDefaultRootPath();

    public static string GetDefaultPath()
        => Path.Combine(GetDefaultRootPath(), "connection", "host.json");

    public static bool IsLifecycleLockAvailable()
    {
        var lockPath = Path.Combine(GetDefaultRootPath(), "runtime", "v1", "lifecycle.lock");
        if (!File.Exists(lockPath))
        {
            return true;
        }

        try
        {
            using var instanceLock = new FileStream(
                lockPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static RuntimeConnectionInfo? Load()
        => RuntimeConnectionInfoStore.Load(GetDefaultPath());

    public static RuntimeConnectionInfo? LoadPreferredFor(Uri runtimeUrl)
        => LoadPreferredFor(runtimeUrl, GetDefaultPath(), RuntimeConnectionInfoStore.GetDefaultPath());

    public static RuntimeConnectionInfo? LoadPreferredFor(
        Uri runtimeUrl,
        string hostConnectionPath,
        string runtimeConnectionPath)
    {
        ArgumentNullException.ThrowIfNull(runtimeUrl);
        var hostConnection = RuntimeConnectionInfoStore.Load(hostConnectionPath);
        if (hostConnection is not null && hostConnection.Matches(runtimeUrl))
        {
            return hostConnection;
        }
        var runtimeConnection = RuntimeConnectionInfoStore.Load(runtimeConnectionPath);
        return runtimeConnection is not null && runtimeConnection.Matches(runtimeUrl)
            ? runtimeConnection
            : null;
    }

}
