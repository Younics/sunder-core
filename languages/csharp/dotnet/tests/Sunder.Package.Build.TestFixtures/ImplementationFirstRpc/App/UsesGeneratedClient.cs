namespace Example.Generated.App;

internal static class UsesGeneratedClient
{
    public static Type ClientType => typeof(Sunder.Generated.Rpc.Greeting.GreetingClient);

    public static async Task<string> InvokeAsync(Sunder.Sdk.Rpc.ISunderRpcClient rpc, string name, CancellationToken cancellationToken)
    {
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: cancellationToken);
        var api = new Sunder.Generated.Rpc.PackageApi(rpc);
        var provider = await api.Greeting.ResolveSelectedPackageAsync(cancellationToken);
        var client = api.Greeting.CreateClient(scope, provider).SGreeting;
        var response = await client.GreetAsync(new Sunder.Generated.Rpc.Greeting.GreetingRequest { Name = name }, cancellationToken);
        return response.Message;
    }
}
