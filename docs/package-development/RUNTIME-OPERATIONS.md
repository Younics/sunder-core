# Runtime Operations

> **Applies to:** Sunder SDK `1.1.x`, package manifest V1, RPC descriptor V1, .NET 10, and Runtime protocol revision 1.

> **RPC authoring transition:** The descriptor, Protocol project, direct manifest
> item, and handwritten registration steps below are legacy reference material.
> Use [Implementation-First RPC Authoring](IMPLEMENTATION-FIRST-RPC.md) for current
> source authoring and [single-project authoring](SINGLE-PROJECT-AUTHORING.md) for the
> ongoing canonical project cutover.

## Owner RPC

Owner RPC is the package-scoped App-to-Runtime boundary. Declare a package-exposed RPC V1 contract, provide it from the Runtime role with `Audience="owner"`, and import it from the App role. Use a unary method for one request/response and a server-stream method for ordered events over time.

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

Generated declarations are public by default. Set `Accessibility="Internal"` on `SunderRpcBinding` when the complete generated binding stays in this assembly behind handwritten public contracts. The setting is uniform across generated DTOs, provider interfaces, clients, and handlers; keep `Public` when another assembly directly implements or consumes those types.

The retained Quickstart [descriptor fixture](../../languages/csharp/dotnet/tests/Sunder.Package.Build.Tests/Fixtures/Quickstart.Greeting.before-source.json) demonstrates the earlier unary wire shape. Current Quickstart source derives that contract from its service implementation. Keep legacy Protocol code free of Avalonia and Host implementation types during migration.

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

Implement the generated provider interface and pass its generated adapter to Worker V1:

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

RPC V1 validates requests, responses, events, and declared errors against the bundled closed schema. Large bytes stay off the JSON control plane: register bounded request content through the call scope and open Host-validated response content through that same scope. Do not send local filesystem paths.

At the SDK boundary, expect `ArgumentException` for invalid local arguments, `OperationCanceledException` for cancellation, and `SunderRpcException` for declared domain or infrastructure failures. Retry only errors whose kind and method idempotency make retry safe. Host deadlines are upper bounds; callers should pass earlier deadlines through `SunderRpcCallOptions` when appropriate.

## Worker Recovery

Runtime supervises each Worker V1 activation independently. When one activation faults, Runtime retires only that package's RPC authority and waits for its calls, process, and storage transaction to drain before creating a fresh activation. Healthy siblings keep their processes and endpoint identities, but in-flight calls from the retired session generation terminate and callers must reconnect. Calls to the recovered package must discover its replacement endpoints; every endpoint from the failed activation remains stale and is never retargeted.

Recovery is automatic and uses delays of 250 ms, 500 ms, 1 second, 2 seconds, and 4 seconds in a rolling one-minute failure window. A sixth failure suspends recovery. Missing dependencies pause recovery without consuming another attempt. Disable, uninstall, update, session replacement, or Runtime shutdown fences stale recovery work. App notifications for restart, success, and suspension are host-owned; packages should not add their own worker restart loop.

Runtime refuses to overlap a replacement with an activation whose cleanup or package-owned calls have not drained. Storage snapshots cover startup through publication and roll back only after worker and RPC authority are proven inactive. An unrecoverable rollback failure fences Runtime and requests supervised process restart rather than continuing with uncertain storage.

## Design Guidance

- Model domain commands and queries, not remote SQL or a generic repository.
- Include explicit paging, cursors, and idempotency keys where retries can occur.
- Evolve descriptor semantics compatibly or publish a new exact contract version.
- Dispose call scopes and async stream enumeration when a view closes.
- Do not discover or invoke providers during App service/contribution configuration; the candidate generation is not published yet.

## Execution Runtime Management

Runtime launches every Worker through an exact signed execution-runtime binding. The identity contains runtime id, semantic version, packaging revision, current RID, and module ABI; the installed package record also pins manifest and artifact hashes, Registry origin, signing key, trust generation, and last-known lifecycle policy. The absolute launcher path, size, and SHA-256 identity resolve from the installed runtime receipt and immutable store root. The Worker environment contains no `PATH` or language-runtime selector, so package code cannot locate or substitute a system-wide `.NET`, Node, or other ambient runtime.

The authenticated Runtime API and `sunder runtime` commands expose list, details and package references, bounded Registry ensure, trusted local import, deep verify, repair, and garbage collection. Registry ensure is an explicit HTTPS operation. Local import uploads bytes through an opaque one-shot handle and performs no hidden Registry request. A fresh local import remains policy-required until an authoritative lifecycle policy permits package binding.

Warm launch validates the immutable receipt, launcher metadata, current signing trust, lifecycle evidence, and stored health marker without rehashing the complete root. Deep verification is explicit. If repair finds modified content, Runtime marks the root unhealthy to block new launches, faults every active package bound to that exact root, proves Worker cleanup and durable process-lease drain, quarantines the old bytes, rebuilds from the content-addressed cached archive, and reloads installed packages. It never renames a root beneath a live process.

Execution-runtime garbage collection derives reachability from installed and development bindings, pending transactions, active process leases, and explicit operation retention. Package uninstall and update do not synchronously delete runtime roots. Missing, policy-required, retired, revoked, and unhealthy runtimes fail closed with actionable Runtime errors; startup and activation never download or repair them implicitly.

### Lifecycle And Offline Behavior

| State | New install or rebind | Existing activation |
| --- | --- | --- |
| `staged` | Blocked and absent from public resolution. | Blocked. |
| `supported` | Allowed. | Allowed. |
| `deprecated` | Allowed with a warning until `InstallBlockedAtUtc`, when present. | Allowed with a warning until `ActivationBlockedAtUtc`, when present. |
| `retired` | Blocked. | Allowed only while authoritative cached policy has not reached `ActivationBlockedAtUtc`; otherwise blocked. |
| `revoked` | Blocked. | Blocked immediately. |

Runtime persists the last authoritative lifecycle and trust evidence with each exact package binding. A previously resolved `supported` or permitted `deprecated` binding can therefore activate while Registry is offline. Offline operation never erases a known retirement, revocation, activation deadline, trust denial, or unhealthy root. A fresh local import authenticates and caches the archive but remains `policy-required` until Registry has authorized that exact identity; missing runtime or policy failures perform no hidden network request.

### Download Size And Exact Reuse

Install and update plans report package projection bytes separately from shared execution-runtime bytes. Runtime artifacts are deduplicated by exact identity and digest across every package in the plan. An already installed artifact contributes zero incremental download bytes only when runtime id, version, packaging revision, RID, module ABI, manifest hash, artifact hash, and ready health all match. The UI may also show the matching installed artifact size as reused shared bytes; that is existing disk use, not another download.

A runtime-only rebind may change an exact package binding without downloading or changing package bytes. Package archives and projections never contain language-runtime bytes, and Runtime never copies an execution-runtime root into a package projection or session.

### Recovery Runbook

Start with `sunder runtime list` and exact `sunder runtime info` output so package references, lifecycle, health, and identity are recorded. Use `sunder runtime ensure` for an explicit online acquisition or `sunder runtime import --file` for an authenticated offline artifact. Use `sunder runtime verify` when deep integrity evidence is needed; normal warm activation deliberately does not rehash the complete root.

Run `sunder runtime repair ... --yes` only for the exact unhealthy identity reported by Runtime. Repair fences dependent package launches, drains affected Workers and leases, quarantines modified bytes, rebuilds from the immutable cached archive, and reloads packages. If no trusted cached archive exists, acquire or import the exact artifact first. Run `sunder runtime gc` only through the API-backed command; never delete roots, receipts, leases, quarantine, or binding records by hand.

`sunder runtime reset --yes` is a last-resort Host operation that drains Runtime and deletes validated Runtime V1 state. Use it only for a disposable profile or an explicitly approved recovery where reinstalling package/runtime state is acceptable. A partial reset retains schema/key evidence and is safe to retry. No recovery procedure requires editing installed package state, execution-runtime receipts, or bindings.
