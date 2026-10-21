# Services, Recovery And Runtime Operations

## Package Services

Use `[SunderService]` and ordinary async methods. Sunder generates typed interfaces,
dispatch, clients, codecs and role composition. App view models inject their owning
service API; external consumers select an API and inject its generated `IPackageApi`.
For business selection among providers, use `IServiceProviders<T>` and call the
selected entry's `Service`.

See [ordinary services](ORDINARY-SERVICES.md), [single-project composition](SINGLE-PROJECT-AUTHORING.md)
and [reviewed imports](CONTRACT-RESTORE-CACHE.md) for authoring. Low-level
`ISunderRpcClient` and call scopes remain advanced SDK facilities; normal services
do not need authored descriptors, Protocol projects or manual handler registration.

## Ownership, Streams And Errors

- Provider endpoints/references identify one exact activation. Never persist or
  silently retarget an endpoint after provider replacement.
- Generated DI owns borrowed provider entries and their binding observation. Dispose
  caller-owned streams and `SunderContent`; do not transfer local filesystem paths.
- Streaming methods use `IAsyncEnumerable<T>`. Ordering/replay/resume semantics belong
  to the service. A provider may explicitly allow clean completion when response
  content authority retires; caller cancellation and other failures still propagate.
- Observe cancellation promptly and protect concurrent mutable state. Declare domain
  errors; unexpected exceptions become bounded provider faults. Keep secrets out of
  messages. Retry only when dispatch classification and method idempotency permit it.
- Use invocation context only for genuine authenticated-caller policy. Host/parent
  deadlines bound declared method timeouts; a reference or imported type is not a grant.
- Do not invoke services while configuring an unpublished candidate generation.

## Worker Recovery

Runtime withdraws a faulted package's authority and waits for its calls, process and
storage transaction to drain before creating a fresh activation. Healthy siblings
retain their processes/endpoints; retired-generation calls terminate and callers
rediscover replacement providers. Endpoints never silently retarget.

Recovery delays are 250 ms, 500 ms, 1 s, 2 s and 4 s in a rolling one-minute window;
a sixth failure suspends recovery. Missing dependencies pause attempts. Disable,
uninstall, update, session replacement and shutdown fence stale recovery work.
Notifications and restart policy are Host-owned.

Replacement cannot overlap uncertain cleanup. Storage rollback waits until Worker
and RPC authority are inactive; unrecoverable rollback fences Runtime and requests
supervised restart. Package lifecycle code must allow cancellation/draining before
disposing services used by in-flight work.

## Execution Runtime Management

Host installs supported official .NET 10 Runtime and Node 24 LTS archives into private
roots. Exact identities include source, runtime ID, vendor version, RID, module ABI
and vendor checksum. Launch uses the absolute verified launcher and a controlled
environment; it performs no implicit acquisition or ambient runtime search.

Install/update review includes required runtime downloads. Existing SDK queue jobs
acquire runtime bytes before one atomic package transaction. Healthy reuse needs no
runtime download. Runtime-only updates retain package versions/content/provenance,
prepare side by side and wait for stopped packages or explicit restart approval.

Use [Package Runtimes](../PACKAGE-RUNTIMES.md) or the [CLI](../SUNDER-CLI.md) for
installation, integrity checks, repair, uninstall and garbage collection. Repair
drains dependent uses before quarantining a root; GC derives reachability from
bindings, pending work, process leases and retention. Do not delete store files by hand.

Vendor support and local health are distinct from package/platform security policy.
Disabling binary updates does not disable that policy. Verified runtime launch can
be offline; missing bytes require explicit acquisition. Technical source/transport/
store rules are in [official runtime management](../OFFICIAL-RUNTIME-HANDOFF.md).

Runtime reset is an explicit authenticated, bound-root Host operation. It removes
reviewed installed/derived state while preserving package data, credentials and
independent RPC decisions. Use the CLI's confirmation flow rather than a directory
delete; source folders and unclassified paths are not reset targets.
