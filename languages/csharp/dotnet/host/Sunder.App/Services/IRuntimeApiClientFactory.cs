namespace Sunder.App.Services;

public interface IRuntimeApiClientFactory
{
    TClient CreateClient<TClient>() where TClient : class, IRuntimeClient;
}
