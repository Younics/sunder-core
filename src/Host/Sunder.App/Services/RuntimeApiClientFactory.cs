namespace Sunder.App.Services;

public sealed class RuntimeApiClientFactory(RuntimeConnectionState runtimeConnectionState) : IRuntimeApiClientFactory
{
    private readonly RuntimeConnectionState _runtimeConnectionState = runtimeConnectionState;

    public IRuntimeApiClient CreateClient() => new RuntimeApiClient(_runtimeConnectionState);
}
