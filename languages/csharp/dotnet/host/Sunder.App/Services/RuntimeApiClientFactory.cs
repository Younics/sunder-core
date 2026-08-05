using Sunder.Runtime.Client;

namespace Sunder.App.Services;

public sealed class RuntimeApiClientFactory(RuntimeClientTransport transport) : IRuntimeApiClientFactory
{
    private readonly RuntimeClientTransport _transport = transport;

    public TClient CreateClient<TClient>() where TClient : class, IRuntimeClient
    {
        var client = new RuntimeApiClient(_transport);
        if (client is TClient capability)
        {
            return capability;
        }

        client.Dispose();
        throw new InvalidOperationException($"Runtime client does not provide {typeof(TClient).Name}.");
    }
}
