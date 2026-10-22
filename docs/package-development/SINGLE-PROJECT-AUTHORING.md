# Single-project .NET package authoring

**SDK status: UNFROZEN / UNRELEASED.** This is current-source documentation, not a
supported-release declaration. After the first supported release, compatible fixes
and additions remain allowed while supported source, binary and behavioral contracts
must be preserved. See the [compatibility policy](../SUNDER-SDK-COMPATIBILITY.md).

Status: the template and compiled Quickstart now use one authored project. Ordinary
Build, development composition and direct multi-RID Pack include restored NuGet/project
ownership, compiled XAML/resources and generated App preview routing. The workflow is
IDE-independent; native and production-release qualification remains separate.

## One authored package

Start with `dotnet new sunder-package` from the coordinated template candidate; see
[Getting Started](GETTING-STARTED.md). The default scaffold already contains ordinary
service source. Add `--withAvalonia` for the starter view/view model rather than
creating another project or writing an App module.

A package owns one project, service implementations, optional views/view models and
portable value/domain source. Sunder produces separate App and exact-RID Worker
assemblies and composes one package. The author does not maintain role projects,
generated interfaces, RPC handlers, default role entry points or duplicate dependency
lists. Source folders are organizational and do not determine role ownership.

Services and business models are the normal authoring surface. The template's
`SampleService` produces `ISampleService`; for package ID `my.company.package`, the
starter view model imports `Sunder.Generated.Packages.My_Company_Package_Services`
and injects that interface. Owned roles reuse authored business values; imported
models come from validated package projection metadata. Do not maintain mirrored
models, request wrappers or handwritten clients.

Handwritten App modules, explicit role leaves and Worker bootstrap are advanced
integration boundaries, not an alternative normal scaffold. The generated-role
internals below explain what tooling owns, not files authors must create.

## Intent declarations

- `[SunderService]` marks an ordinary partial service class. A concrete source service
  is a Worker root; declared public async methods form its generated interface without method
  annotations. Optional semantic metadata preserves stable identities, constraints,
  errors and use policies. See [ordinary services](ORDINARY-SERVICES.md) for natural
  value roots, selected package APIs and the retired declaration migration.
- `SunderPackageViewAttribute(viewId, displayName)` marks an App view. `ViewModel`
  identifies its optional DI-created view model; `IconPath` and `DefaultPlacement`
  provide existing view registration metadata. The view must be a concrete Avalonia
  control. Registration never constructs controls or executes package code.
- `SunderPackageSettingsViewAttribute` marks the optional settings control and its
  optional `ViewModel`; at most one settings view is allowed.
- `SunderPackageCodeRoleAttribute(Worker | App | Shared)` is a narrow override for
  ambiguous helper ownership. It is not an exposure or authorization grant. A
  role-exclusive declaration cannot be reachable from the other role. Shared source
  compiles independently; it does not share mutable state across processes.

Service interfaces that declare extension points retain their source-authority purpose.
For an implementation class `N.GreetingService` in package `example.greeting`, the
generated typed API is `Sunder.Generated.Packages.Example_Greeting_Services.IGreetingService`,
with the same accessibility and generated service-call signatures (server invocation
context is omitted). Reserved-name collisions fail; generated symbols do not rename
themselves with unstable suffixes. Concrete implementation references from UI code are rejected;
UI code depends on the generated API. Declared interfaces are not duplicated.

`[SunderService]` defaults to package-private exposure. A public C# member does not make a public
package contract. Private App use defaults to required applicable actions; concrete
ordinary public services default to optional App readiness with applicable actions.
Explicit App policy overrides those defaults; public declaration-only interfaces
retain opt-in App use. Runtime broker use and provider imports remain explicit.
Worker-local typed API dependencies resolve to
the local implementation/adapter; App typed APIs resolve through generated exact-
provider RPC clients. Default generated service lifetime is package-activation
singleton; custom DI/lifecycle registration must remain explicitly analyzable before
it can replace the default. No convention silently changes an existing lifetime.

### Default constructor composition

Default composition follows the constructors of ordinary service implementations,
registered views/view models and source implementations of `ISunderWorkerLifecycleService`.
It does not execute constructors during analysis. Each constructed source type must
have one accessible instance constructor, or select one with the standard
`Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute`.
The selected constructor must be accessible to generated code; multiple selections
or an inaccessible selection fail rather than falling back to another overload.
The attribute is recognized by its referenced DI assembly identity, not its name alone.
Concrete source dependencies are resolved
recursively; an ordinary source interface can bind to one unambiguous concrete
source implementation. Optional parameters with no inferred binding retain their
authored default. Generated calls explicitly supply those defaults and preserve each
parameter's declared type, so overload resolution cannot replace the selected constructor.
Missing/ambiguous bindings, cycles, inaccessible constructors and
singleton dependencies on transient views/view models produce located diagnostics.

`Lazy<T>` can defer construction of an otherwise supported dependency until its first
use. The dependency still participates in role, cycle and lifetime validation and
resolves to the same DI-owned singleton. This allows a lifecycle service to keep
database/startup dependencies dormant through candidate preparation and generation
commit. It does not permit constructor cycles or arbitrary factory/service-location
bindings.

Generated factories register one owned concrete instance per singleton helper and
supply that instance to its constructor-interface bindings. They do not create
multiple disposable aliases for the same object. Such inferred helper interfaces
are constructor bindings, not an open-ended service-locator registration convention.
Generated service APIs have their own explicit DI registrations. Views/view models
are transient; their ordinary helper dependencies are role-local singletons.
When a view requests its declared `ViewModel` in its constructor, the same instance
is assigned to `DataContext`. Omitting `ViewModel` leaves data-context/lifetime choices
to the view's ordinary constructor graph, without generating a second model.
`IPackageContext` and `ISunderRpcClient` are supplied by the role host/composition.
App constructors can also request the host's `IPackageNotificationService`,
`IBackgroundTaskQueue`, `IPackageSettingsNavigationService` and
`IPackageShellViewService`; Workers cannot.
Sunder itself uses `IBackgroundTaskQueue`; packages receive activation-scoped
adapters. The public `BackgroundTask*` types have no `Package` prefix. Queued work is
not an OS process, durable job or Worker cleanup receipt.
Generated App modules register package-owned services only, leaving reserved host
capabilities to the App's constrained service-provider factory.

Worker-local generated APIs forward context-free methods directly to their owning
implementation, preserving ordinary C# results, exceptions, cancellation and stream
ownership. Methods declaring `SunderRpcInvocationContext` use generated RPC under
the service's explicit Runtime-use policy. Generated code never manufactures or
borrows another invocation's caller/content authority. Injecting a context-requiring
API without Runtime-use intent fails analysis. Mixed APIs keep context-free methods
local and route only context-requiring methods through RPC.

Source lifecycle implementations are Worker roots. Default lifecycle registration
uses deterministic dependency-first order, with ordinal type-name order for
independent roots, and delegates candidate/commit/activation/completion/shutdown to
the existing Worker composition. Singleton disposal has one owner. Context-sensitive
Runtime RPC still obeys the existing activation/readiness rules. Dynamic factories,
arbitrary service-location, custom lifetimes and custom composition remain separate
qualification work rather than being guessed by default constructor analysis.

Packages without lifecycle or callback services use generated shutdown ownership for their DI
provider. Concurrent or repeated shutdown calls await the same disposal completion,
including its failure. Ordinary disposable helpers therefore do not add
`background-services.v1` or `runtime-generations.v1` requirements merely for DI
ownership; those APIs are emitted for lifecycle and host-routed callback composition.

For activation-only work, implement `ISunderWorkerActivationService.ActivateAsync`.
Its source implementation is a Worker constructor root. The generated callback runs
after generation commit, before publication readiness, in dependency-first order.
It does not run during candidate preparation. Use ordinary `IDisposable` or
`IAsyncDisposable` ownership for cleanup, including candidates never activated.
When mixed with full lifecycle services, activation follows the same combined
dependency order. A class implementing both interfaces is activated once through
`ISunderWorkerLifecycleService`. Activation-only work requires the Worker protocol,
without implying background-service supervision or generation-commit hooks.

A package can declare one `ISunderWorkerSettingsSchemaProvider`. It is Worker-owned
and constructed by the generated service provider; its `SettingsSchema` is advertised
during composition. The getter and its construction must be deterministic without
Host I/O or package-data mutation. Multiple schema providers are rejected. The Worker
SDK supplies the DI implementation used by generated composition.

For storage-key migrations, declare at most one
`ISunderWorkerStorageMigrationProvider`. Its state and secret plans default to empty
and are snapshotted during composition, before `worker.ready`; constructors and plan
getters must be deterministic and free of Host I/O or persistent mutation.
Implement `ISunderWorkerStorageMigrationPreparation` for optional read-only
preparation after generation commit and before any migration or activation callback.
Preparation implementations run in constructor dependency order, with ordinal names
ordering independent roots. A class implementing both contracts is one DI singleton.
Both are Worker roots; migration-only packages use ordinary generated DI disposal
without acquiring background-service or generation-composition requirements.
See [storage migration examples](DATA-AND-LOGGING.md#migrating-legacy-keys).

Concrete source implementations of `IPackageAuthHandler` and `IPackageCallbackHandler`
are also Worker constructor roots. At most one authentication handler is allowed; it
is registered through `SunderWorkerOptions.AuthHandler` and excluded from the generic
callback list. Other handlers populate `CallbackHandlers` in dependency order. The
existing Worker SDK validates duplicate and reserved callback IDs during composition.
Handler construction and metadata getters must not perform Host I/O or start a flow.
The SDK composition owns disposal of these activation-scoped services and their shared
dependencies, including failure while constructing the callback catalog.

## Role plan invariants

The planner consumes the exact augmented Roslyn compilation and ordinary source
trees. It resolves SDK attributes by symbol identity, not name substrings, and follows
type signatures, generic arguments, constructor/member bodies and attribute type
references. Registered view models are App roots even when referenced only by view
metadata. RPC value roots come from the existing validated source compiler model.

Unreachable ordinary source defaults to Worker, so reflection-only entry points must
be declared explicitly when they belong to App. Source required by both roles must
be an explicit shared declaration or a validated portable RPC value; otherwise a
diagnostic asks the author to choose a real boundary rather than silently duplicating
a helper's state. Shared dependency closure cannot reach a role-exclusive type.
Nested declarations move with their containing type. Partial declarations retain all
their files/spans; a source file is not an indivisible role boundary.

Role projection removes namespace/type imports that target only source removed from
that role, including global, alias and static imports. It preserves source positions
and directive balance, retains imports with surviving source or external namespace
members, and validates the projected compilation before using it.

Diagnostics are stable `package-authoring.*` codes with source locations:
`role-invalid`, `role-conflict`, `role-ambiguous`, `view-invalid`,
`view-id-duplicate`, `settings-view-duplicate`, `source-invalid` and
`source-limit-exceeded`. No plan with errors is usable for role compilation.
Constructor composition adds `di-unresolved`, `di-ambiguous`, `di-cycle`,
`di-constructor-invalid`, `di-parameter-invalid`, `di-role-conflict`,
`di-lifetime-conflict`, `di-use-required`, `di-context-required` and
`di-limit-exceeded`. Its bounded graph allows at most 8,192 source types and
128 constructor-dependency levels.

The role planner is not dependency pruning: native files, resources, build assets,
dynamic registrations and external assembly ownership need evaluated MSBuild/restore
evidence. A clean type graph alone does not qualify a publish closure. Subsequent
role builds validate those closures and reject stale plan/input fingerprints before
replacing prior output.

### Restored dependency ownership

For the evaluated TFM/RID, Sunder projects the existing NuGet assets-format-3 or
assets-format-4 graph. Format 4 (used by SDK analysis levels 10.0.300 and later)
pivots targets and framework metadata by the exact evaluated target alias and
records the effective TFM separately. Sunder preserves that format and alias in
each generated role graph, including restore/dependency groups; it never selects
another alias merely because both resolve to the same TFM. Original restore files
are evidence inputs and are not rewritten or downgraded. Unsupported future
formats, inconsistent framework metadata and failed/corrupt restore graphs fail closed.
Bound role-source symbols and complete emitted role assembly references seed library
ownership. Transitive library dependencies follow their owner. An owned library
retains its complete selected compile/runtime/native/resource/build assets; individual
unused members do not justify dropping a native file, analyzer or build dependency.
Method-body-only assembly references count as dependencies.

If a direct dependency has no inferable owner, declare the narrow override on its
`PackageReference` or `ProjectReference`:

```xml
<PackageReference Include="Example.NativeAssets" Version="1.0.0"
                  SunderPackageAssetRole="worker" />
```

Values are `worker`, `app` and `shared`. An override constrains ownership and cannot
contradict a required dependency edge. The Worker SDK is Worker-only; Avalonia and
its App SDK are App-only anchors. `Sunder.Package.Build` is shared tooling. These declarations
do not grant RPC authority or change the authored project/release inventory.

Each role consumes its own projected `project.assets.json` and NuGet-generated
props/targets. Excluded package imports and project references are removed before
dependency resolution. Original restore outputs and dependency locks are unchanged;
ordinary Build performs no restore or version selection. The selected RID must
already exist in the restored graph. Unknown/dynamic NuGet import ownership and
ambiguous/conflicting dependency roles fail with `package-authoring.dependency-*`
diagnostics rather than copying assets into both roles.

The role reader verifies generated restore files and their original input evidence,
including selected package files. Content-addressed role snapshots stabilize imports
that MSBuild reads during evaluation. Build/Clean serialize writes to the shared role
output set; ordinary Clean preserves immutable handoffs that other build requests
may still reference, as it does for project-export snapshots.

### Authored resources and XAML

`AvaloniaXaml` and `AvaloniaResource` inputs belong to App. Their `Link` identities
remain package-relative, including linked files outside the project directory.
MSBuild backslash separators are normalized to portable `/` paths before validation;
absolute and traversing links remain invalid.
Embedded resources can infer ownership from a literal, one-string-argument
`Assembly.GetManifestResourceStream` call in classified source. Otherwise declare
`SunderPackageAssetRole=worker|app|shared`, or name a classified source type with
`SunderPackageResourceOwner`. Copying `Content`/`None` items uses the same explicit
ownership rules. Logical names, target paths and copy metadata are retained.

```xml
<EmbeddedResource Include="data/catalog.bin" LogicalName="Example.Catalog"
                  SunderPackageResourceOwner="Example.CatalogService" />
<None Update="ui/preferences.json" TargetPath="data/preferences.json"
      CopyToOutputDirectory="PreserveNewest" SunderPackageAssetRole="app" />
```

Resource contents and metadata join the hashed role plan. The selected role uses
immutable resource snapshots; unplanned/missing resources or changed role, link and
copy metadata fail before compilation. Public package assets under the configured
`SunderPackageAssetsDirectory` retain their existing shared publication semantics.

The build prepass runs the evaluated Avalonia name generator with its evaluated
options and XAML inputs. It strictly recompiles generated members together with the
typed service APIs and verifies stable generator output. App consumes those generated
members once; Worker receives neither those members nor XAML resources. The normal
Avalonia build still embeds resources and performs its real post-compile XAML/compiled-
binding validation. Sunder does not synthesize a substitute `InitializeComponent`.

The evaluated .NET SDK regex generator also participates in analysis. Sunder resolves
`GeneratedRegex` partial methods before validating source ownership, then runs that
same generator independently against each role's projected source. Generated methods
and helpers stay with their owning role. The generator binary and generated bytes
are hashed into the role handoff, and the inner compile excludes generators whose
output is already included. Invalid regex declarations preserve prior complete output.

The evaluated CommunityToolkit.Mvvm generators participate in the same prepass and
per-role generation. Observable properties, relay commands and their supporting members
are available to constructor/source analysis and real compiled XAML. Only the assembly's
declared source generators are loaded; its IDE code fixes do not add a Workspaces or
editor dependency to builds. `MvvmToolkitEnableINotifyPropertyChangingSupport` is captured
with the evaluated generator settings. Generated source, generator identity and settings
are verified in the role handoff, and invalid Toolkit declarations preserve prior output.
The resource pilot executes generated commands and property-change bindings in a real
headless view and checks that Toolkit dependencies stay out of its Worker.

Static markup type references, including `x:Class` and `x:DataType`, contribute App
roots and dependency ownership. Markup references to Worker implementation types
are rejected. This initial grammar covers ordinary namespace/type references and
literal type/static expressions; generic XAML type arguments, arbitrary dynamic
extensions and richer localization/file-reference resources remain qualification
work. XML DTDs and external `ResXFileRef` inputs are rejected by the current bounded
resource reader.

The current pilot compiles and loads a real headless Avalonia view, its named control,
compiled binding and linked `avares://` resource. Missing control members, invalid
bindings and ambiguous resource roles fail while retaining the prior composed
development package. The fixture creates its bound view through the generated App
module and host-supplied dependencies; it does not
manually construct a substitute view model. Additional resource/generator cases
are qualified when a supported package actually uses them.

### Live editor integration

Sunder is IDE-independent. The shared implementation uses a portable `netstandard2.0`
incremental analyzer and the standard C# 14/Roslyn 5 compiler contracts. No IDE add-on,
installation or editor-created file is required for ordinary CLI builds. Editor-specific
smoke checks can investigate compatibility issues without gating the package workflow.

Owned service interfaces come from the editor's current Roslyn compilation, including
unsaved edits and source-project compilation references. The analyzer shares signature
formatting with the artifact compiler, preserves defaults/async/stream/nullability
shapes and omits Host invocation context. Generated declarations carry original-source
line mappings. Renames/removals refresh the interface; an incomplete method body does
not remove otherwise valid signatures. Invalid declaration shapes and API-name
collisions produce located `SUNDEREDIT001`/`SUNDEREDIT002` diagnostics.

The analyzer is packaged with `Sunder.Package.Build` and included in its fingerprinted
tool snapshots. MSBuild supplies its editor-only compiler setting. Artifact compilations
do not receive editor interfaces. Complete RPC profile, role/constructor and authority
validation remains in the owning build compiler; an editor symbol is not a grant.

Design-time disk generation prepares only imported bindings from the existing offline
lock/cache prepass. Its output is confined to
`obj/<configuration>/<TFM>/sunder-design-time`, with immutable compiler-input snapshots.
Owned transport/composition helpers are not generated from stale saved source for the
editor. Avalonia's evaluated generator and XAML additional inputs remain active.
The combined editor compilation cannot emit a package assembly, role plan, manifest,
receipt or archive. Clean removes mutable editor output while retaining immutable
handoffs; artifact generation rejects the editor output directory.

Current owned symbols require no contract acceptance or preceding package build.
Existing imported selections still require explicit offline lock/cache state. Neither
editor path accepts/freezes contracts or changes authoring files. Build and Pack retain
their normal acceptance and artifact-evidence requirements.

Focused generator tests protect signature parity, source mapping, incomplete edits
and artifact isolation. One workspace integration fixture loads actual projects,
runs both generators together and updates service, XAML and referenced-project
documents in memory. Its source-SDK/task-host and fresh-NuGet variants cover the two
supported tooling distribution paths. The package case also covers locked restore,
Build/Pack, explicit public acceptance and Clean without tooling assemblies in the payload.
Source-workspace compiler tooling may bootstrap independently of the package.

## Advanced: Generated role-build internals

Normal projects let `Sunder.Package.Build` own this machinery. Do not add explicit
App/Worker leaf projects or handwritten bootstrap to reproduce it.

The build package imports its authoring targets after the .NET SDK has evaluated
its standard paths. A package library marked `SunderPackageProject=true`, without
an authored role-project association, uses generated composition. Existing explicit
role projects remain migration inputs until their canonical source is converted.
This transitional detection is not an alternate supported authoring topology.

The outer build re-evaluates the complete source for the selected exact RID and
publishes a role plan through the existing transactional RPC generator. It invokes
the same project for internal App
and Worker compilations, with separate assembly names and role/RID intermediates
and outputs. The outer project emits no mixed implementation assembly. SDK-generated
assembly attributes remain local to each role, and projected source uses original
source paths for diagnostics and portable PDB documents.

Before compilation, the role reader validates the plan hash, project/configuration/
TFM/RID/package version, evaluated ordinary source set and compilation constants, generated source
hashes, managed reference hashes and role-specific contract receipt. The output
lease remains held through compilation and manifest generation. Compile-time
namespace lookup and runtime AssemblyRef closure are tracked separately; an unused
compile-time lookup must not add UI dependencies to a Worker `.deps.json`.

The pilot verifies exact App-use/Worker-provider projection, concurrent unchanged
builds, failure preservation, baseline-free private Pack, Rebuild/Clean, and removal of obsolete App outputs when source becomes
headless. The restored-dependency fixture covers source-SDK and binary-SDK references,
a separate backend project, Avalonia NuGet build imports, and a real current-RID
SQLite operation from Worker output. App excludes the backend/SQLite closure; Worker
excludes Avalonia. The resource pilot adds embedded/copied role resources and compiled
Avalonia markup. Fresh packed-SDK and template checks cover the supported template
options. Qualify additional build assets and resource/localization patterns when
used by a supported package; the complete native runtime matrix remains a release
gate. Source/constant changes caused by different inner evaluation are rejected
rather than silently omitted.

### Framework, RID and preview selection

Worker V1 requires `net10.0`. The current single-project build uses that framework
for both roles and rejects unsupported frameworks before role compilation. A project
with `TargetFrameworks` must explicitly select `TargetFramework` for Pack or preview.
A platform-qualified App framework paired with a separate Worker framework remains
additional qualification work.

Ordinary Build selects `RuntimeIdentifier` when specified, otherwise the SDK's current
host RID. The RID must be declared and already restored. The declared set comes from
`RuntimeIdentifiers`, a single `RuntimeIdentifier`, or `SunderPackageRuntimeIdentifiers`.
Pack selects an explicit `RuntimeIdentifier` first, then an explicitly configured
`SunderPackageComposeRuntimeIdentifiers`, otherwise the declared set. It builds and
checks each selected RID; duplicate, empty and undeclared selections are rejected.
Private services use the full effective package version and need no owned baseline,
Accept or Freeze. Public contract acceptance and Freeze remain explicit prerequisites.
Historical private baseline records are retained through the explicit retirement
migration; ordinary builds never rewrite them.

Native builds also export portable qualified role-input evidence alongside each
exact-RID development leaf. [Qualified prebuilt Pack](PREBUILT-ROLE-INPUTS.md) validates
that evidence against current reviewed source, dependencies, toolset, baseline and
lock, then composes retained Worker/App bytes without compiling or restoring. Native
CI retains and tests the whole bundle and pins its evidence digest for fan-in.

Analysis uses `obj/<configuration>/<TFM>/sunder-analysis/<RID>`. Worker/App outputs
remain under `sunder-roles/<role>/<RID>`, with per-RID development aggregates under
`sunder-targets/<RID>/sunder-dev`. The canonical development package stays under
`bin/<configuration>/<TFM>/sunder-dev` by default. Clean removes mutable analysis and
role/aggregate outputs for all declared RIDs, retaining immutable handoffs. Build,
Pack and Clean serialize mutations of their common output set.

`GetSunderPackageAppPreviewInputs` builds the current native RID and returns the
generated App assembly, `.deps.json`, `.runtimeconfig.json`, evaluated previewer tool,
TFM and RID. `AvaloniaFilePreview` uses those App inputs and `APreviewFile`, with
`APreviewerUrl` defaulting to `http://127.0.0.1:6001`:

```sh
dotnet msbuild Example.csproj -p:TargetFramework=net10.0 -t:AvaloniaFilePreview -p:APreviewFile="Views/ExampleView.axaml"
```

Foreign-RID or headless preview requests fail with `package-authoring.preview-*`
diagnostics. The launch-routing fixture verifies actual `dotnet exec` arguments and
App runtime/dependency inputs. Editor-specific preview checks are optional
compatibility investigations, not package-authoring acceptance gates.

## Generated calls and builds

`dotnet run` delegates to `sunder dev package run` for the canonical development tree.
The Sunder CLI must be on PATH or supplied with `SunderCliPath`; application arguments
such as `--watch` are forwarded. Launch requires current-host-RID output and the normal
Host/runtime prerequisites. `--no-build` uses existing output and does not execute the
managed Worker module directly.

Host output watch is separate from a source producer such as `dotnet watch build`.
Requested watch, producer liveness, build success and applied output are independent.
App development-session status uses the SDK-owned `PackageDevelopmentHostState`,
`PackageDevelopmentRuntimeReadiness` and `PackageDevelopmentAppReadiness` enums,
all with `Unknown`. `HostState`, `RuntimeReadiness` and `AppReadiness` do not collapse
those stages into one readiness string. `RuntimeRevision` identifies sampled validated
Runtime source; `AppliedRevision` additionally requires the matching ready Runtime
and applicable published App activation. Null evidence is unknown, and activation
does not assert that lazy views have opened.

Generated APIs preserve asynchronous completion, cancellation, declared errors,
stream disposal, exact provider identity, deadlines and retirement. Business-specific
semantics (authority, receipts, dispatch acknowledgement, content references) require
their established adapters; they cannot be inferred by generic JSON serialization.

Sunder's build pipeline owns role projections, generated bootstrap/DI and isolated
role/TFM/RID outputs. Design-time symbols and debug/source mapping use the same
semantic model. An additive C# source generator alone cannot remove Worker source
from an App compilation. No mixed authoring assembly is a payload.

Ordinary builds remain offline, using reviewed locks and cache contents. Public
Accept/Freeze, package/provider selection and publication remain explicit operations.
Canonical one-project builds own role/bootstrap/transport output; new packages do
not need authored App/Protocol projects or a historical migration prerequisite.
Native and production-release qualification remains separate.
