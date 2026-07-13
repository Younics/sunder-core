namespace Sunder.App.Services;

public sealed class RuntimeApiClientFactory(RuntimeConnectionState runtimeConnectionState) : IRuntimeApiClientFactory
{
    private readonly RuntimeConnectionState _runtimeConnectionState = runtimeConnectionState;

    public TClient CreateClient<TClient>() where TClient : class, IRuntimeClient
    {
        var client = new RuntimeApiClient(_runtimeConnectionState);
        if (client is TClient capability)
        {
            return capability;
        }

        client.Dispose();
        throw new InvalidOperationException($"Runtime client does not provide {typeof(TClient).Name}.");
    }
}
