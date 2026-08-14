# Owner RPC

> **Applies to:** Sunder SDK `1.1.x`, package manifest V3, RPC descriptor V2, .NET 10, and Runtime protocol revision 8.

Owner RPC is the package-scoped App-to-Runtime boundary. Declare a package-exposed RPC V2 contract, provide it from the Runtime role with `Audience="owner"`, and import it from the App role. Use a unary method for one request/response and a server-stream method for ordered events over time.

## Define The Protocol

Keep the descriptor in a non-packable package-local Protocol project:

```xml
<PropertyGroup>
  <SunderPackageProject>false</SunderPackageProject>
</PropertyGroup>
<ItemGroup>
  <SunderRpcBinding Include="Contracts/greeting.rpc.json"
                    Namespace="Example.Greeting.Protocol.Generated"
                    OutputFile="GreetingRpc.g.cs" />
</ItemGroup>
```

The descriptor defines closed request and response schemas, method kind, idempotency, and declared domain errors. `Sunder.Package.Build` validates and canonicalizes it, derives descriptor and ABI hashes, and generates deterministic DTOs, a typed client, a provider interface, and an `ISunderRpcServiceHandler` adapter.

The compiled Quickstart's [descriptor](../../languages/csharp/dotnet/samples/Sunder.Package.Quickstart/Sunder.Package.Quickstart.Protocol/Contracts/greeting.rpc.json) demonstrates the complete unary shape. Keep Protocol code free of Avalonia and Host implementation types.

## Declare Owner Access

The Worker leaf bundles and provides the contract:

```xml
<SunderContractBundle Include="../Example.Greeting.Protocol/Contracts/greeting.rpc.json"
                      ContractId="example.greeting"
                      Version="2.0.0"
                      DescriptorPath="contracts/greeting.rpc.json"
                      Exposure="package" />
<SunderRpcProvider Include="example.greeting"
                   Role="runtime"
                   ContractId="example.greeting"
                   ContractVersion="2.0.0"
                   Audience="owner" />
```

The App leaf bundles the same immutable descriptor and declares only the actions it needs:

```xml
<SunderUsesContract Include="example.greeting"
                    Role="app"
                    ContractVersion="2.0.0"
                    Required="true"
                    Actions="discover;invoke" />
```

Package exposure permits only owner audience. Owner providers are visible only to the App and Runtime activations of the same package identity. The Host stamps caller authority; knowing a provider id or endpoint string does not grant access.

## Implement The Worker Provider

Implement the generated provider interface and pass its generated adapter to Worker V3:

```csharp
internal sealed class GreetingProvider(SunderWorkerContext worker) : IGreetingProvider
{
    public async ValueTask<GreetResponse> CreateAsync(
        SunderRpcInvocationContext invocation,
        GreetRequest request,
        CancellationToken cancellationToken = default)
    {
        var stored = await worker.State.GetValueAsync("greeting.count", cancellationToken);
        var count = long.TryParse(stored, out var previous) ? checked(previous + 1) : 1;
        await worker.State.SetValueAsync("greeting.count", count.ToString(), cancellationToken);
        return new GreetResponse { Message = $"Hello, {request.Name}!", InvocationCount = count };
    }
}
```

```csharp
await SunderWorker.RunAsync(context =>
{
    var descriptor = QuickstartProtocol.Greeting;
    return new SunderWorkerOptions(
    [
        new SunderWorkerProviderRegistration(
            "example.greeting",
            descriptor.ContractId,
            descriptor.Version,
            descriptor.Sha256,
            descriptor.AbiSha256!,
            new ExampleGreetingRpcHandler(new GreetingProvider(context))),
    ]);
});
```

Provider handlers may run concurrently. Protect mutable state, observe cancellation promptly, and throw only domain errors declared by the descriptor. Unexpected exceptions become bounded generic provider faults; never include credentials or private request data in exception messages.

## Invoke From App

Inject `ISunderRpcClient`, discover the owner provider, create a caller-owned scope, and use the generated client:

```csharp
var descriptor = QuickstartProtocol.Greeting;
var catalog = await rpc.DiscoverAsync(descriptor.ContractId, cancellationToken);
var provider = catalog.Providers.Single(candidate =>
    candidate.ProviderId == "example.greeting"
    && candidate.State == SunderRpcProviderState.Active
    && candidate.AbiSha256 == descriptor.AbiSha256);

await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: cancellationToken);
var client = new GreetingClient(scope, provider.Endpoint);
var response = await client.CreateAsync(
    new GreetRequest { Name = "Sunder" },
    cancellationToken);
```

Discovery returns Host-stamped exact activation endpoints. Never persist an endpoint or silently retarget it after provider replacement; discover again when the catalog changes. Dispose the call scope to cancel its work and revoke any remaining content references.

## Streams, Content, And Errors

Declare a `server-stream` method for ordered events and consume the generated client with `await foreach`. Each subscription is independent and owns an exact provider-generation lease until completion or cancellation. Resume tokens, replay, and ordering across subscriptions remain package-defined.

RPC V2 validates requests, responses, events, and declared errors against the bundled closed schema. Large bytes stay off the JSON control plane: register bounded request content through the call scope and open Host-validated response content through that same scope. Do not send local filesystem paths.

At the SDK boundary, expect `ArgumentException` for invalid local arguments, `OperationCanceledException` for cancellation, and `SunderRpcException` for declared domain or infrastructure failures. Retry only errors whose kind and method idempotency make retry safe. Host deadlines are upper bounds; callers should pass earlier deadlines through `SunderRpcCallOptions` when appropriate.

## Design Guidance

- Model domain commands and queries, not remote SQL or a generic repository.
- Include explicit paging, cursors, and idempotency keys where retries can occur.
- Evolve descriptor semantics compatibly or publish a new exact contract version.
- Dispose call scopes and async stream enumeration when a view closes.
- Do not discover or invoke providers during App service/contribution configuration; the candidate generation is not published yet.
