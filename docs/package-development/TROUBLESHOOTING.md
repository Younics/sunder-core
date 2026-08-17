# Troubleshooting

> **Applies to:** Sunder SDK `1.1.x`, package manifest V3, .NET 10, and Runtime protocol revision 8.

Start with the first failing boundary: build, archive validation, Runtime activation, App activation, or a leased call. Keep the correlation id from CLI/App errors and inspect Runtime package logs or the App session log without copying secrets into diagnostics.

## Build And Packaging

### No `sunder-dev` Folder Or `.sunderpkg`

- Verify the package references `Sunder.Package.Build` with `PrivateAssets="all"`.
- Check that build/publish completed successfully and was not a design-time build.
- Look under the actual configuration/target-framework output: `bin/Debug/net10.0/sunder-dev`, the canonical pack output under `bin/Release/net10.0`, or the publish directory only when the pipeline explicitly used `dotnet publish`.
- Do not add a source `sunder-package.json`; metadata must come from `[assembly: SunderPackage(...)]`.

### Package Metadata, App Module, Or Worker Target Fails

- Ensure the entry assembly has exactly one `SunderPackageAttribute` with a lowercase dot-separated package id.
- Set `Version` to strict SemVer 2.0.
- Declare at most one public, non-abstract App module with a public parameterless constructor.
- Give each Worker leaf exactly one explicit exact-RID `worker` target and set `OutputType=Exe`, `SelfContained=true`, `UseAppHost=true`, and `PublishSingleFile=false`.
- Remove stale output and rebuild rather than editing generated manifest/role data.
- Keep App-only Avalonia types out of Runtime-only dependency paths.

### SDK Compatibility Or Capability Inference Fails

- Keep every Sunder developer package in `[1.1.0,1.2.0)` and restore the intended lock file.
- Remove a stale `SunderSdkPackageVersion` override; it must match the resolved SDK.
- Read the exact unresolved dynamic-access diagnostic. Add only the required `SunderSdkCapability` and exact `SunderSdkDynamicAccess` acknowledgement.
- Do not mix 1.0 metadata or contract assemblies with the 1.1 baseline.

### Generated Output Path Is Rejected

`SunderDevOutputPath` is intentionally constrained to the direct `TargetDir/sunder-dev` child, and an existing output must carry the build marker. Remove the override instead of pointing generated deletion at a shared or source directory.

## Development Activation

### Runtime Does Not Load The Development Package

- Pass the generated `sunder-dev` directory, not the project, `bin` root, publish folder, or `.sunderpkg`.
- Rebuild after changing metadata or dependencies.
- Include a dependency and all dependent development packages in the same App invocation so Runtime receives one complete desired set.
- Close an older App invocation that owns the same development package before starting another.
- Check `sunder runtime status` and the Runtime connection configuration when the Host/Runtime is unavailable.

### Watch Rebuild Does Not Activate

The Runtime validates and prepares a complete candidate before replacing the active generation. A bad intermediate build leaves the prior generation active. Wait for a successful build, then inspect activation diagnostics. Avoid tools that expose partially written output; `Sunder.Package.Build` recreates the generated directory as one marked output.

### Reload Times Out Or Old Code Remains Active

An RPC call/stream, callback, Stack call, lifecycle callback, timer, watcher, or child task is not observing cancellation. Runtime cancels generation leases and allows a bounded drain; it refuses replacement rather than terminating code beneath running work.

Propagate cancellation, dispose RPC scopes and async enumerators, stop child work in `OnShutdown`, and unsubscribe events. Do not retry reload in a tight loop.

### Dependency Is Missing Or Incompatible

Confirm the installed package id and version satisfy `[assembly: SunderPackageDependency(...)]`. A NuGet contracts reference alone does not install its host package. Runtime activates dependencies first and prevents dependents from loading after missing, incompatible, cyclic, or failed dependencies.

## App And Avalonia

### View Does Not Appear

- Confirm the assembly declares an App role and calls `RegisterPackageView<TView>` from `RegisterAppContributions`.
- Use a stable globally unique, package-prefixed view id; collisions are case-insensitive.
- Ensure constructor dependencies are registered in App DI or are supported host services.
- Check the App session log for a client-local package presentation fault.
- Remember that `showInHotbarByDefault` applies only before user customization; use shell navigation to open a known registered view.

### View Is Quarantined After Opening

Sunder automatically quarantines a view after an attributable recoverable construction, XAML, warmup, navigation, attachment, or rendering failure. Sibling views remain active when the host knows the exact failed view. A package-attributed dispatcher failure without a safe view identity quarantines only that package's App UI activation; installed state and its Runtime role are unchanged. The host shows an error notification with a correlation reference, writes exception details to the App session log, and makes one fresh App activation attempt after cleanup and the exact Runtime package content are authoritative. If cleanup is uncertain, recovery times out, or the replacement faults, the fallback remains and App does not retry that content again during the process session. Fix the first correlated exception and rebuild.

An App package runs in-process, so a UI-thread deadlock, infinite loop, native crash, `StackOverflowException`, severe memory exhaustion, or `FailFast` can still terminate or freeze the App. Those failures require a direct code fix or a future out-of-process UI target.

### Runtime Package Is Restarting

An exact Worker V3 activation fault disables that activation, retires its endpoints, and schedules a bounded package-only restart. Runtime waits 250 ms, 500 ms, 1 second, 2 seconds, and 4 seconds across failures in a rolling one-minute window; the sixth failure suspends recovery. Healthy package workers and endpoint identities remain in place, but calls from the retired session generation terminate and stream clients must reconnect. Old endpoints stay stale, so clients must rediscover the recovered provider rather than retrying an old endpoint. Runtime and App publish restart, success, and suspension notifications automatically.

If worker cleanup, authority drain, or storage rollback cannot be proven safe, Runtime does not overlap another worker with the quarantined activation. A storage-integrity rollback failure fences Runtime lifecycle operations and requests a supervised Runtime restart.

### Theme Resource Is Missing

Reference `Sunder.Sdk.Avalonia`, use a key from `SunderThemeKeys`, and resolve shell-sensitive values with `{DynamicResource ...}`. Do not merge a private copy of Sunder theme dictionaries. Build diagnostics may require an explicit `theming.v1` declaration only for genuinely dynamic XAML that inference cannot classify.

### UI Updates Fail After `await`

View construction, navigation, warmup, and presentation enter on the App dispatcher, but code that opts out of the captured context must marshal subsequent control access back to the Avalonia dispatcher. Runtime/callback/Stack/extension callbacks have no UI-thread guarantee.

## Data And Services

### App Data Or Owner RPC Is Unavailable

Runtime-backed state, files, settings, secrets, owner RPC, and callbacks require an active Runtime worker for the same package. They are also unavailable for mutation before the candidate App generation is published. Move work from module composition to navigation, a user action, an App background process, or a Worker lifecycle callback. Handle RPC unavailability and check callback availability where applicable.

### Storage Write Is Rejected

Check the portable key/path/value/file limits in [Data And Logging](DATA-AND-LOGGING.md). Paths must use relative `/`-separated segments and cannot traverse observed links/reparse points. `ContentRootPath` is read-only. Use `Storage.Files` or `RoleLocalWorkspace` for mutable data.

### Settings Write Is Rejected

Assign the complete settings schema to `SunderWorkerOptions.SettingsSchema` before `worker.ready`. Writes must target a declared non-secret field and satisfy its type/options/required rules. Secret fields use `IPackageSecrets`, not `IPackageSettings`. `GetValueAsync` includes schema defaults; `GetStoredValueAsync` does not.

### DI Activation Fails

In App code, do not build a nested provider, register reserved host services, or assume the module itself comes from DI. Register owned services in `ConfigureAppServices`; resolve contribution instances from the provider passed to `RegisterAppContributions`. Worker code owns its local composition and receives Host capabilities through `SunderWorkerContext`.

## RPC And Callbacks

### Owner Provider Or Method Is Not Found

Confirm the provider id and contract version match the manifest, the Worker registered the exact descriptor and ABI hashes, the endpoint is active, and the App declared `discover` plus the required call action. Owner providers are visible only to the same package. Duplicate provider ids fail activation rather than choosing one handler.

### Request, Response, Or Event Is Rejected

Validate the value against the bundled RPC V2 descriptor, including closed record members, required/null presence, bounded strings/collections/numbers, and safe integers. Page collections and use Host-mediated content references for large bytes. Every response/event must match its schema, and every server stream must terminate normally or with an authenticated error.

### Stream Ends With `rpc.worker.stream-queue-limit`

The stream producer outran that call's nonblocking consumer queue. The bounded message suffix identifies whether the count limit, encoded-byte limit, or both rejected the incoming event and includes only sanitized local operation/provider/service/method identity. Runtime cancels the exact stream with `ResourceExhausted`, `PossiblyDispatched`, and `Unsafe`; sibling calls remain active, and no request data or error `Details` are added. Drain events promptly, reduce or page event payloads, and reconnect only when the contract defines safe resume/replay semantics. Do not blindly retry the stream.

### Callback Does Not Complete

- Use the host-provided callback URI/session id and echo the exact package/session identity from handler results.
- Do not start a package-owned listener; loopback port/listener ownership belongs to Runtime.
- Validate provider state/nonce/PKCE and callback values before mutation.
- Poll only the session created by the current App activation and treat expiry, conflict, unload, and cancellation as terminal.
- Ensure start/completion returns before its two-minute handler deadline and cancellation returns promptly.

## Stacks

### Import Plan Is Stale Or Already Used

Plans are generation-bound, expire after 15 minutes, and are single use even when apply fails. Preview again after a package reload, contributor change, archive change, selected-fragment change, expiry, or failed import attempt. The import may select any subset of actions known to that plan, but unknown action ids are rejected.

### Import Is Blocked

Resolve required inputs, missing/incompatible package requirements, unsupported fragment schemas, and error-severity conflicts. Preview must not mutate state. If apply partially committed work, return `Partial` with every committed item; Runtime cannot roll back another contributor.

## Still Unresolved

Capture the command, package version/id, App/Runtime versions, first error, correlation id, and a minimal reproduction. Redact credentials and private data. Re-run local archive validation and compare the behavior with the [compiled quickstart](../../languages/csharp/dotnet/samples/Sunder.Package.Quickstart/) before reporting a host issue.
