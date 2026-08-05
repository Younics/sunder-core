# Callbacks And Auth

> **Applies to:** Sunder SDK `1.1.x`, package manifest V1, .NET 10, and Runtime protocol revision 5.

Callbacks let an App-started package flow open a browser and return values to a Runtime handler without giving the package ownership of a network listener. Auth is the standard authorization projection built on the same callback machinery.

## Generic Callbacks Versus Auth

| Contract | Use it for |
| --- | --- |
| `IPackageCallbackHandler` | Any browser/local callback flow with package-defined parameters and completion values. |
| `IPackageAuthHandler` | Authorization status, browser authorization, callback completion, credential removal, and host auth UI. It extends `IPackageCallbackHandler` with reserved handler id `auth`. |
| `IPackageCallbackClient` | App-side start, bounded completion wait, cancellation, status lookup, and launch-URI opening for the current package. |

Runtime and preflight contexts expose `Callbacks.IsAvailable == false`. App exposes callbacks only after its generation is published and only when Runtime is connected.

## Register A Generic Handler

```csharp
services.AddSingleton<DeviceLinkHandler>();
services.AddSingleton<IPackageCallbackHandler>(provider =>
    provider.GetRequiredService<DeviceLinkHandler>());
```

The handler supplies a stable package-local `CallbackHandlerId` and implements start/completion. Handler ids are ASCII letters, digits, `.`, `-`, or `_`, at most 128 characters; routing and duplicate detection are case-insensitive.

Runtime supplies a host-created opaque session id and absolute callback URI:

```csharp
public sealed class DeviceLinkHandler(IPackageContext packageContext)
    : IPackageCallbackHandler
{
    public string CallbackHandlerId => "device-link";

    public Task<PackageCallbackStartResult?> StartCallbackAsync(
        PackageCallbackStartContext context,
        CancellationToken cancellationToken = default)
    {
        var launchUri = BuildProviderUri(context.CallbackUri, context.CallbackSessionId);
        return Task.FromResult<PackageCallbackStartResult?>(new(
            packageContext.PackageId,
            context.CallbackSessionId,
            PackageCallbackFlowKind.Browser,
            launchUri.AbsoluteUri,
            "Complete linking in your browser."));
    }

    public Task<PackageCallbackCompletionResult> CompleteCallbackAsync(
        PackageCallbackCompletionContext context,
        CancellationToken cancellationToken = default)
    {
        ValidateAndApply(context.QueryValues);
        return Task.FromResult(new PackageCallbackCompletionResult(
            packageContext.PackageId,
            context.CallbackSessionId,
            PackageCallbackCompletionState.Completed,
            "Linking completed."));
    }
}
```

The result must echo the activating package id and exact session id. `LaunchUrl` must be absolute and at most 4,096 characters. Runtime bounds the user-facing message to 2,048 characters.

`CompleteCallbackAsync` must validate provider state and all callback values before applying them. Return `Completed`, `Failed`, or `Cancelled` and echo the same package/session identity. Implement `CancelCallbackAsync` when start leaves provider work, delegates, or waiters in flight; cancellation is best effort and must be idempotent.

## Start From App

```csharp
var status = await context.Callbacks.StartAsync(
    "device-link",
    new Dictionary<string, string> { ["account"] = accountId },
    cancellationToken);

if (status.LaunchUri is not null)
{
    await context.Callbacks.OpenLaunchUriAsync(status.LaunchUri, cancellationToken);
}

status = await context.Callbacks.WaitForCompletionAsync(
    status.CallbackSessionId,
    TimeSpan.FromMinutes(5),
    cancellationToken);
```

`StartAsync` reuses an existing starting/pending session for the same package generation, handler id, and ordinal parameter snapshot. `WaitForCompletionAsync` performs bounded status polling and throws `TimeoutException` when its required positive timeout elapses; it does not implicitly cancel the Runtime session. Call `CancelAsync` when the user abandons a pending flow. Status and cancellation access remain scoped to the App package activation that created the session.

## Callback Limits

### App Start Parameters

| Limit | Value |
| --- | ---: |
| Entries | 16 |
| Key length | 64 characters |
| Value length | 2,048 characters |
| Combined key/value length | 8,192 characters |

Keys must be nonblank and unique by ordinal comparison. Values may be empty but not `null`. Parameters are copied into an immutable snapshot.

### Runtime Sessions And Handler Calls

| Limit/deadline | V1 default |
| --- | ---: |
| Active callback sessions across Runtime | 32 |
| Pending session lifetime | 5 minutes |
| Terminal status retention | 1 minute |
| Handler start deadline | 2 minutes |
| Handler completion deadline | 2 minutes |
| Handler cancellation deadline | 5 seconds |
| Callback shutdown drain | 15 seconds |

Starting more than 32 sessions returns a Runtime conflict. Expired, unloaded, or shutdown sessions receive `CancelCallbackAsync` with `Expired`, `PackageUnloaded`, or `HostShutdown`. A completion endpoint can be claimed only once; duplicate or stale callbacks are rejected.

### Host Listener

The listener uses loopback port `1455` with fallback `1457`, at most 16 concurrent requests, a 4,096-byte request-line limit, 64 headers, 8,192 bytes per header line, and 32,768 header bytes total. Callback query data is limited to 32 values, 128 characters per key, 4,096 per value, and 16,384 combined characters.

These are host transport constraints, not permission to place sensitive data in URLs. Prefer short opaque state/code values and exchange them server-side.

## Implement Auth

`IPackageAuthHandler` uses the reserved callback id `PackageCallbackHandlerIds.Authentication` (`auth`). Register the implementation as `IPackageAuthHandler`; because that contract extends `IPackageCallbackHandler`, Runtime also exposes it to callback routing:

```csharp
services.AddSingleton<MyOAuthHandler>();
services.AddSingleton<IPackageAuthHandler>(provider =>
    provider.GetRequiredService<MyOAuthHandler>());
```

Implement:

- `GetStatusAsync` without starting user interaction;
- `StartAuthorizationAsync` to return a browser launch URL or `null` when unavailable;
- `CompleteAuthorizationAsync` to validate callback values and persist credentials;
- `DisconnectAsync` to remove package credentials and return the resulting status.

Persist refresh/access credentials only through `IPackageSecrets`. `PackageAuthStatus` tells host UI whether to offer authorize/disconnect actions. Auth status values are `Unavailable`, `NotConnected`, `Connected`, and `Failed`.

## Security And Lifecycle

The Runtime owns listener startup, callback path, session identity, generation leases, expiry, one-time completion, bounded transport parsing, package unload cancellation, and shutdown. Packages must not start `HttpListener`, `TcpListener`, Kestrel, or another callback server.

The package still owns provider-specific security:

- generate and validate state/nonce/PKCE values;
- bind callback values to the expected provider account and session;
- reject missing, duplicated, malformed, or unexpected query values;
- allow-list provider launch origins and never reflect arbitrary launch URLs;
- avoid credentials in URLs, messages, exceptions, or logs; and
- honor linked cancellation in every handler call.

Handlers are activation-scoped and may be called concurrently. A generation lease remains active across start/completion work. App publication, Runtime retirement, request cancellation, and host shutdown can all make the flow unavailable. Treat not-found, conflict, stale-session, timeout, and cancellation results as expected terminal UX rather than retrying indefinitely.
