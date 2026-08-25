namespace Sunder.Runtime.Client;

public sealed partial class RuntimeManagementClient
{
    public RuntimeManagementClient(Uri runtimeUrl)
        : this(() => RuntimeConnectionInfoStore.LoadFor(runtimeUrl))
    {
    }

    public RuntimeManagementClient(
        Func<RuntimeConnectionInfo?> getConnectionInfo,
        HttpMessageHandler? innerHandler = null,
        RuntimeClientPolicyOptions? policy = null)
        : this(new RuntimeClientTransport(getConnectionInfo, innerHandler, policy), ownsTransport: true)
    {
    }

    public RuntimeManagementClient(RuntimeClientTransport transport)
        : this(transport, ownsTransport: false)
    {
    }

    private RuntimeManagementClient(RuntimeClientTransport transport, bool ownsTransport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _ownsTransport = ownsTransport;
        _responses = transport.Responses;
    }

    public RuntimeClientResourceProfile? CurrentResourceProfile => _transport.CurrentResourceProfile;

    public RuntimeClientResourceLimits ResourceLimits => _transport.ResourceLimits;

    public void Dispose()
    {
        if (_ownsTransport)
        {
            _transport.Dispose();
        }
    }
}
