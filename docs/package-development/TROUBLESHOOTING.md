# Troubleshooting

> **Applies to:** Sunder SDK `1.1.x`, package manifest V1, .NET 10, and Runtime protocol revision 3.

Start with the first failing boundary: build, archive validation, Runtime activation, App activation, or a leased call. Keep the correlation id from CLI/App errors and inspect Runtime package logs or the App session log without copying secrets into diagnostics.

## Build And Packaging

### No `sunder-dev` Folder Or `.sunderpkg`

- Verify the package references `Sunder.Package.Build` with `PrivateAssets="all"`.
- Check that build/publish completed successfully and was not a design-time build.
- Look under the actual configuration/target-framework output, such as `bin/Debug/net10.0/sunder-dev` or `bin/Release/net10.0/publish`.
- Do not add a source `sunder-package.json`; metadata must come from `[assembly: SunderPackage(...)]`.

### Package Metadata Or Module Discovery Fails

- Ensure the entry assembly has exactly one `SunderPackageAttribute` with a lowercase dot-separated package id.
- Set `Version` to strict SemVer 2.0.
- Declare at most one public, non-abstract implementation with a public parameterless constructor for each App/Runtime role.
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
- Check `sunder system status` and the Runtime connection configuration when the Host/Runtime is unavailable.

### Watch Rebuild Does Not Activate

The Runtime validates and prepares a complete candidate before replacing the active generation. A bad intermediate build leaves the prior generation active. Wait for a successful build, then inspect activation diagnostics. Avoid tools that expose partially written output; `Sunder.Package.Build` recreates the generated directory as one marked output.

### Reload Times Out Or Old Code Remains Active

An operation, stream, callback, Stack call, background service, timer, watcher, or child task is not observing cancellation. Runtime cancels generation leases and allows a 10-second drain; it refuses replacement rather than unloading code beneath running work.

Propagate cancellation, dispose async enumerators, stop child work in `StopAsync`/disposal, unsubscribe events, and remove static references to package types. Do not retry reload in a tight loop.

### Dependency Is Missing Or Incompatible

Confirm the installed package id and version satisfy `[assembly: SunderPackageDependency(...)]`. A NuGet contracts reference alone does not install its host package. Runtime activates dependencies first and prevents dependents from loading after missing, incompatible, cyclic, or failed dependencies.

## App And Avalonia

### View Does Not Appear

- Confirm the assembly declares an App role and calls `RegisterPackageView<TView>` from `RegisterAppContributions`.
- Use a stable globally unique, package-prefixed view id; collisions are case-insensitive.
- Ensure constructor dependencies are registered in App DI or are supported host services.
- Check the App session log for a client-local package presentation fault.
- Remember that `showInHotbarByDefault` applies only before user customization; use shell navigation to open a known registered view.

### View Is Disabled After Opening

Unhandled construction, XAML, warmup, navigation, or rendering failures disable that package for the current App generation. Fix the first App-session exception and rebuild. Do not look for a Runtime package fault unless the failing action crossed into Runtime.

### Theme Resource Is Missing

Reference `Sunder.Sdk.Avalonia`, use a key from `SunderThemeKeys`, and resolve shell-sensitive values with `{DynamicResource ...}`. Do not merge a private copy of Sunder theme dictionaries. Build diagnostics may require an explicit `theming.v1` declaration only for genuinely dynamic XAML that inference cannot classify.

### UI Updates Fail After `await`

View construction, navigation, warmup, and presentation enter on the App dispatcher, but code that opts out of the captured context must marshal subsequent control access back to the Avalonia dispatcher. Runtime/callback/Stack/extension callbacks have no UI-thread guarantee.

## Data And Services

### App Data Or Runtime Client Is Unavailable

Runtime-backed state, files, settings, secrets, operations, and callbacks require an active Runtime role for the same package. They are also unavailable for mutation before the candidate App generation is published. Move work from module composition to navigation, a user action, an App background process, or a Runtime background service, and check `IsAvailable` where applicable.

### Storage Write Is Rejected

Check the portable key/path/value/file limits in [Data And Logging](DATA-AND-LOGGING.md). Paths must use relative `/`-separated segments and cannot traverse observed links/reparse points. `ContentRootPath` is read-only. Use `Storage.Files` or `RoleLocalWorkspace` for mutable data.

### Settings Write Is Rejected

Register the complete Runtime settings schema before publication. Writes must target a declared non-secret field and satisfy its type/options/required rules. Secret fields use `IPackageSecrets`, not `IPackageSettings`. `GetValueAsync` includes schema defaults; `GetStoredValueAsync` does not.

### DI Activation Fails

Do not build a nested provider, register reserved host services, resolve App services in Runtime, or assume the module itself comes from DI. Register owned services in `Configure*Services`; resolve contribution instances from the provider passed to `Register*Contributions`.

## Operations And Callbacks

### Operation Or Stream Is Not Found

Confirm the lowercase package-scoped id matches exactly, the handler is registered in the current Runtime generation, and the App client belongs to the same package. A package cannot invoke another package's operation. Duplicate ids fail activation rather than choosing one handler.

### Request, Response, Or Event Is Rejected

Measure serialized UTF-8 JSON, not in-memory object size. Requests are limited to 1 MiB, responses to 4 MiB, and events to 1 MiB. Page collections and keep DTOs concrete/non-polymorphic. Every handler response/event must be non-null, and every stream must terminate normally or with an error frame.

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

Capture the command, package version/id, App/Runtime versions, first error, correlation id, and a minimal reproduction. Redact credentials and private data. Re-run local archive validation and compare the behavior with the [compiled quickstart](../samples/Sunder.Package.Quickstart/) before reporting a host issue.
