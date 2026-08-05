# Runtime Operations

> **Applies to:** Sunder SDK `1.1.x`, package manifest V1, .NET 10, and Runtime protocol revision 5.

Typed Runtime operations are the package-scoped App-to-Runtime boundary. Use an operation for one request/response and a stream for ordered events over time.

## Define A Contract

Keep DTOs host-neutral, coarse-grained, and JSON-serializable:

```csharp
public sealed record GreetRequest(string Name);
public sealed record GreetResponse(string Message, int InvocationCount);

public static class QuickstartOperations
{
    public static PackageRuntimeOperation<GreetRequest, GreetResponse> Greet { get; }
        = new("greeting.create");
}
```

The [compiled operation contract](../../languages/csharp/dotnet/samples/Sunder.Package.Quickstart/QuickstartOperations.cs), [handler](../../languages/csharp/dotnet/samples/Sunder.Package.Quickstart/GreetHandler.cs), and [App caller](../../languages/csharp/dotnet/samples/Sunder.Package.Quickstart/QuickstartView.axaml.cs) are built with `Sunder.Core.slnx`.

Operation and stream ids are package-scoped lowercase ASCII tokens of at most 128 characters. They may contain letters, digits, `.`, `-`, and `_`. Invalid ids throw `ArgumentException` at contract construction; duplicate registration fails Runtime activation.

## Implement And Register

```csharp
public sealed class GreetHandler(IPackageContext context)
    : IPackageRuntimeOperationHandler<GreetRequest, GreetResponse>
{
    public async ValueTask<GreetResponse> HandleAsync(
        GreetRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = int.Parse(
            await context.Storage.State.GetValueAsync("greeting.count", cancellationToken)
            ?? "0") + 1;
        await context.Storage.State.SetValueAsync("greeting.count", count.ToString(), cancellationToken);
        return new GreetResponse($"Hello, {request.Name}!", count);
    }
}
```

Register the handler instance from Runtime DI:

```csharp
services.AddSingleton<GreetHandler>();

registry.RegisterRuntimeOperation(
    QuickstartOperations.Greet,
    services.GetRequiredService<GreetHandler>());
```

Handlers may run concurrently. Protect shared mutable state, use atomic storage operations appropriately, and promptly observe cancellation.

## Invoke From App

Inject `IPackageRuntimeClient` into an App service or view:

```csharp
if (!runtime.IsAvailable)
{
    return;
}

var response = await runtime.InvokeAsync(
    QuickstartOperations.Greet,
    new GreetRequest("Sunder"),
    cancellationToken);
```

The client is scoped to the calling package and cannot invoke another package's handler, even if it knows the other operation id. It is unavailable in Runtime/preflight contexts and before the App generation is published.

## Serialization

Sunder serializes request, response, and event DTOs as UTF-8 JSON with `System.Text.Json` and `JsonSerializerDefaults.Web` on both sides. This uses web defaults such as camel-case output and case-insensitive property matching. Packages cannot supply custom serializer options for this channel.

Use concrete records/classes composed of stable JSON primitives, arrays/lists, dictionaries, nullable values, and nested DTOs. Avoid polymorphic object graphs, interface-typed payloads, delegates, streams, exceptions, host types, and filesystem paths. Add fields compatibly and treat missing optional fields as defaults. A request body must deserialize to non-null; handlers must return non-null responses/events.

## Limits And Deadlines

| Boundary | V1 default |
| --- | ---: |
| Serialized request | 1 MiB |
| Serialized operation response | 4 MiB |
| Serialized stream event | 1 MiB |
| One newline-framed stream record | 1 MiB + 16 KiB |
| Stream terminal error message | 4,096 characters |
| App operation request deadline | 10 minutes |
| App stream lifetime deadline | 30 minutes |
| Runtime generation drain | 10 seconds |

Limits apply to encoded bytes, not object count. Page large collections and use package file storage or a purpose-built transfer flow for larger content. A stream lifetime is not an idle timeout; the App client cancels the complete subscription after the deadline.

There is no additional package-handler execution timeout inside Runtime. The caller deadline/disconnect, Runtime shutdown, or generation retirement cancels the linked handler token. Ignoring cancellation can prevent a generation from draining and causes reload to fail while the old generation remains active.

## Streams

Define and register a stream similarly:

```csharp
public static PackageRuntimeStream<WatchRequest, ItemChanged> Changes { get; }
    = new("items.changes");
```

Implement `IPackageRuntimeStreamHandler<TRequest,TEvent>` and consume it with `await foreach` over `SubscribeAsync`. Each subscription is independent and owns a generation lease until it completes or is disposed. Reconnect, resume tokens, ordering across subscriptions, and replay are package-defined.

The wire format is bounded newline-delimited JSON. Each frame is exactly one of:

```json
{"type":"event","event":{"itemId":"42"}}
{"type":"completed"}
{"type":"error","error":{"code":"runtime.package-stream.handler-error","message":"..."}}
```

Every successful stream ends with `completed`; a failed stream after response start ends with `error`. EOF without a terminal frame, an empty/malformed/unknown/oversized frame, data after a terminal frame, or a trailing partial frame is a transport failure. Package events must never be `null`.

## Errors

Before a stream response starts, Runtime API failures use RFC Problem Details with an error `code` and `correlationId`. Current mappings relevant to package operations are:

| HTTP | Code | Typical cause |
| ---: | --- | --- |
| 400 | `runtime.v1.validation` | Invalid id or JSON request. |
| 404 | `runtime.v1.not-found` | Package inactive or operation/stream not registered. |
| 413 | `runtime.v1.upload-limit` | Request, response, event, or framed record exceeds its limit. |
| 499 | `runtime.v1.cancellation` | Request was cancelled. |
| 503 | `runtime.v1.unavailable` | Package generation is draining or Runtime is unavailable. |
| 500 | `runtime.v1.internal` | Unhandled handler/serialization failure before response start. |

Problem details include `X-Correlation-ID`; internal failures expose a generic detail while Runtime logs the exception. Once a stream has emitted data, a handler failure uses the terminal `runtime.package-stream.handler-error` frame and truncates its message to 4,096 characters. Do not put credentials or private data in exception messages.

At the SDK boundary, expect `ArgumentException`/`ArgumentNullException` for invalid local arguments, `OperationCanceledException` when the caller cancels, and `InvalidDataException` for malformed or oversized successful transport data. Runtime availability, transport, timeout, Runtime-reported operation failures, and stream terminal failures throw `PackageRuntimeInvocationException`, which exposes a bounded lowercase `Code`, `IsTransient`, optional HTTP `StatusCode`, and optional diagnostic `CorrelationId`. This scalar metadata is the complete package-visible failure contract; host exceptions are logged internally and are never attached as inner exceptions.

```csharp
try
{
    await runtime.InvokeAsync(QuickstartOperations.Greet, request, cancellationToken);
}
catch (PackageRuntimeInvocationException error) when (error.IsTransient)
{
    // Retry according to package policy; retain the correlation id for diagnostics.
}
```

## Design Guidance

- Model domain commands and queries, not remote SQL or a generic repository.
- Include explicit paging/cursors and idempotency keys where retries can occur.
- Keep DTO namespaces free of Avalonia and host implementation types.
- Version semantics additively within the package's compatibility line.
- Dispose async stream enumeration when a view closes.
- Never call operations or subscribe during App service/contribution configuration; the candidate generation is not published yet.
