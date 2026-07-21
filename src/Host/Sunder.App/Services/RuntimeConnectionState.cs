using Sunder.Host.Client;
using Sunder.Runtime.Client;

namespace Sunder.App.Services;

public sealed class RuntimeConnectionState
{
    private readonly object _syncRoot = new();
    private readonly Func<Uri, RuntimeConnectionInfo?> _loadConnection;
    private Uri _runtimeUrl;
    private RuntimeConnectionInfo? _connectionInfo;

    public RuntimeConnectionState(Uri runtimeUrl)
        : this(runtimeUrl, HostConnectionInfoStore.LoadPreferredFor)
    {
    }

    internal RuntimeConnectionState(
        Uri runtimeUrl,
        Func<Uri, RuntimeConnectionInfo?> loadConnection)
    {
        _loadConnection = loadConnection ?? throw new ArgumentNullException(nameof(loadConnection));
        _runtimeUrl = RuntimeUrlHelper.Normalize(runtimeUrl);
        _connectionInfo = LoadConnection(_runtimeUrl);
    }

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

    public RuntimeConnectionInfo? GetConnectionInfo()
    {
        Uri runtimeUrl;
        RuntimeConnectionInfo? current;
        lock (_syncRoot)
        {
            runtimeUrl = _runtimeUrl;
            current = _connectionInfo;
        }

        var refreshed = LoadConnection(runtimeUrl);
        if (refreshed is null)
        {
            lock (_syncRoot)
            {
                return _runtimeUrl == runtimeUrl ? current : _connectionInfo;
            }
        }

        lock (_syncRoot)
        {
            if (_runtimeUrl == runtimeUrl)
            {
                _connectionInfo = refreshed;
                return refreshed;
            }
            return _connectionInfo;
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

    private RuntimeConnectionInfo? LoadConnection(Uri runtimeUrl)
        => _loadConnection(runtimeUrl);
}
