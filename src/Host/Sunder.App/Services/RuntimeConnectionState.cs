using Sunder.Runtime.Client;

namespace Sunder.App.Services;

public sealed class RuntimeConnectionState(Uri runtimeUrl)
{
    private readonly object _syncRoot = new();
    private Uri _runtimeUrl = RuntimeUrlHelper.Normalize(runtimeUrl);
    private RuntimeConnectionInfo? _connectionInfo = LoadConnection(runtimeUrl);

    public Uri RuntimeUrl
    {
        get
        {
            lock (_syncRoot)
            {
                return _runtimeUrl;
            }
        }
        set
        {
            lock (_syncRoot)
            {
                _runtimeUrl = RuntimeUrlHelper.Normalize(value);
                _connectionInfo = LoadConnection(_runtimeUrl);
            }
        }
    }

    public RuntimeConnectionInfo? ConnectionInfo
    {
        get
        {
            lock (_syncRoot)
            {
                return _connectionInfo;
            }
        }
    }

    public void SetConnection(RuntimeConnectionInfo connectionInfo)
    {
        ArgumentNullException.ThrowIfNull(connectionInfo);
        lock (_syncRoot)
        {
            _runtimeUrl = RuntimeUrlHelper.Normalize(connectionInfo.RuntimeUrl);
            _connectionInfo = connectionInfo;
        }
    }

    private static RuntimeConnectionInfo? LoadConnection(Uri runtimeUrl)
    {
        var connectionInfo = RuntimeConnectionInfoStore.Load();
        return connectionInfo is not null && connectionInfo.Matches(runtimeUrl) ? connectionInfo : null;
    }
}
