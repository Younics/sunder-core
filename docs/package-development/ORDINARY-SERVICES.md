# Ordinary C# services

One `SunderPackageProject=true` project owns its services, values and optional App.
Author each business record once and use it directly in ordinary methods. For a
package whose canonical ID is `example.greeting`:

```csharp
using Sunder.Sdk.Services;
using Sunder.Generated.Packages.Example_Greeting_Services;

public sealed record GreetingResult(string Message);

[SunderService]
internal sealed partial class GreetingService
{
    public ValueTask<GreetingResult> GreetAsync(string name, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new GreetingResult($"Hello, {name}!"));
    }
}

internal sealed class GreetingViewModel(IGreetingService greeting)
{
    public ValueTask<GreetingResult> GreetAsync(string name, CancellationToken token = default)
        => greeting.GreetAsync(name, token);
}
```

The existing semantic compiler, editor projection and role generator produce
`IGreetingService`, local Worker bindings and an associated-App proxy. Declared
public async instance methods are included without a method annotation. Constructors,
accessors, object methods, private helpers and recognized local lifecycle hooks are
excluded. Unsupported selected signatures, overloads and ambiguous names fail with
source diagnostics.

Services are package-private by default. Public methods on the class do not publish
an external API. Sunder generates the interfaces, request envelopes, codecs,
dispatch, clients and role composition; authors maintain the business implementation.

## Values and optional semantics

`Task`, `ValueTask`, their generic forms and `IAsyncEnumerable<T>` are supported.
Supported scalar, nullable, list, string-keyed map and structured values work at
method roots using the existing value profile. Cancellation stays out of band.
Include a server-side `SunderRpcInvocationContext` only when business policy needs
its authenticated `Caller`, such as validating an Agent run grant. Generated callers
omit it; ordinary content and service references need no context parameter.
An ordinary method may place its single invocation context before or
after wire arguments, before its cancellation token. Generated calls preserve the
declared argument order; context is never serialized or manufactured. Duplicate
contexts and wire arguments after cancellation remain invalid. Empty input and
no-result completion use a generated closed `Unit`;
the ordinary interface does not expose a dummy argument or result.

Existing named nonnullable structured roots retain their definitions. Other roots
receive descriptor definitions named `<Service><Method>Request`,
`<Service><Method>Result`, or `<Service><Method>Event`. These are wire schemas, not
developer-maintained wrapper classes.

### Preserving public model names

Wire definition names and exported model names are separate identities. Before
renaming an implementation type, pin both names to their existing public spellings:

```csharp
[SunderValue("WireGreeting", PublicName = "GreetingResult")]
public sealed record InternalGreeting(string Message);
```

The constructor still names the `$defs` wire definition; optional `PublicName`
selects the exact exported name in the package's Models namespace. Omitting it
(or setting it to `null`) retains source-derived public naming. An unchanged value
shape with both names pinned retains its descriptor/ABI bytes and `modelProjection`
when `InternalGreeting` is renamed. Owning roles continue using the real source
type, including its constructors and ordinary methods.

Explicit public names must match `[A-Za-z_][A-Za-z0-9_]{0,127}` and must not be
reserved C#/TypeScript keywords. Spelling is exact: it is not Pascal-cased or
escaped. Distinct source models cannot share a name, including ASCII case-only
differences, across the source compilation. Invalid names and collisions produce
source diagnostics before export. Changing a public name is an API projection
change even when the wire definition is unchanged.

### Constructor-bound source values

Positional records and ordinary source classes/structs may use a uniquely selected
constructor. Each parameter must bind one same-typed property by its CLR name
(case-insensitive); `JsonPropertyName` changes the wire name, not this binding.
The constructor may widen the property's outer reference nullability: a nonnullable
`IReadOnlyList<T>` property can bind a nullable `IReadOnlyList<T>?` parameter that
locally normalizes null to an empty list. The serialized property's type still
defines the wire contract, so the array is required and cannot be null on the wire.
Nested type arguments/nullability must match exactly; narrowing nullability,
variance, numeric conversions and user-defined conversions are not inferred.
A single accessible `JsonConstructor` may select among overloads. Unannotated
parameterless DTOs retain their explicit `required`/`SunderRpcOptional<T>` policy;
an unrelated convenience overload does not infer presence for a POCO.

Constructor-bound concrete properties are **required wire members**, even when a
constructor parameter has a default. A nullable property is required-but-nullable.
Public auto `get; init;` properties on an otherwise qualified constructor-bound
record are also required, including properties with local initializers. Other
unbound properties need explicit `required` or `SunderRpcOptional<T>` intent.
Ordinary method defaults likewise remain caller-side conveniences: generated calls
send the selected value and never omit a required wire argument.

Generated typed codecs call the selected constructor and populate qualified source
properties directly. Explicit optional properties retain absent versus present-null
or present-default semantics; an accessible setter/init accessor restores decoded
presence after constructor/initializer execution. Constructor-bound inference is
bounded to 256 parameters/properties and rejects ambiguous/unmatched bindings,
inaccessible accessors, public fields, inherited instance properties and unsupported
JSON representation overrides. It does not add a reflection fallback. Already
qualified explicitly required DTOs retain their existing codec path.

Service transfer and ordinary JSON/persistence can have different member intent.
`[JsonIgnore]` members stay out of the service value unless a **public instance
property** also declares `[SunderServiceMember]`; generated caller/provider codecs
then transfer that property while normal JSON still ignores it. This is useful for
per-call typed service references and transient authority, not local callbacks or
private/internal state. Unmarked ignored constructor properties need an explicit
representable parameter default; their authored default/initializer is preserved.
`[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]` is supported on
`SunderRpcOptional<T>` properties without another marker: absence stays absent,
and present-null or present-default stays present. Other conditional omission,
custom converters and number-handling overrides do not acquire permission through
the marker. Ordinary transferable properties need no new annotation.

### Ordinary timestamps and enums

`DateTimeOffset` maps to the existing `string` / `format: "date-time"` profile, using
the offset-date-time validator and exact tick conversion. Source codecs preserve
both the instant and the actual offset; they do not convert to UTC. Output uses `Z`
for a zero offset, otherwise `±HH:mm`, with up to seven fractional digits and no
unnecessary trailing fractional zeros. Input accepts the existing profile's up-to-
nine-digit fractions only when exactly representable in .NET ticks (extra digits
must be zero). Nonrepresentable precision or an invalid date/offset fails instead
of rounding. Nullable timestamps and timestamps in lists, maps, records, method
roots and stream values use the same codec. The SDK's existing UTC metadata
converter is unchanged.

An ordinary service's unannotated source enum uses the existing closed safe-integer
enum profile. Flags, aliases and out-of-range integers are rejected. Explicit
`SunderNumericEnum` semantics and complete `JsonStringEnumMemberName` mappings keep
their existing wire representation; partially annotated string enums are rejected.

Ordinary service/value metadata lives in the single `Sunder.Sdk.Services` namespace.
Optional metadata:

- `SunderService(SunderServiceExposure.Public)` opts into explicit public evolution.
  `Key`, `ContractId`, `ServiceId`, `IncludeSchemaDialect` and `AdditionalDefinitions`
  preserve reviewed identities and retained value definitions.
- `SunderMethod` supplies stable method/root names, `SunderServiceIdempotency` and
  declared `Errors`; idempotency never enables automatic retries. `TimeoutSeconds`
  requests a caller deadline of 1–2100 seconds; zero uses the Host default.
  Put `[SunderMethod(TimeoutSeconds = 2100)]` on the declaring method when that
  business operation needs the longer budget. Host limits and parent deadlines
  still apply; implementations do not construct call options or scopes.
  A separate provider-local option, `CompleteOnResponseContentRetirement`, is
  described under [content and transient authority](#content-and-transient-authority).
- `SunderServiceError` declares stable errors and optional structured detail types.
- `SunderValue`, `SunderValueConstraints`, `SunderTransparentValue`,
  `SunderNumericEnum`, `SunderUnion`, `SunderDiscriminator` and `SunderVariant`
  express value semantics not inferable from CLR source.
- `SunderAppUse` and `SunderRuntimeUse` preserve explicit action/readiness policy.
  Private App use defaults to required applicable actions. A concrete ordinary public
  service defaults to optional App readiness with all applicable actions, so its own
  package UI can inject the generated interface without another annotation. Explicit
  App policy overrides this default, including narrowed actions. A public service
  can declare `[SunderAppUse(Enabled = false)]` to expose no owning-App use; it cannot
  combine that opt-out with required readiness or actions. Declaration-only
  public interfaces retain opt-in behavior.
  Runtime broker consumption remains explicit and independent. These normalized
  policies are recorded outside descriptor and ABI identity.
- Shared extension points may declare a `[SunderService]` interface and bind
  `[SunderServiceProvider(typeof(ExtensionService), ProviderId = "...")]` classes.
  Multiple providers require distinct stable IDs. `SunderServiceAudience` controls
  discovery; declaration-only interfaces remain valid without a local provider.
  One real class can implement several contracts: repeat `SunderServiceProvider`
  for each interface and provider ID. Use explicit C# interface implementations
  when contracts have colliding method names; no forwarding class is needed.
- `SunderPlatformImport` identifies the local semantic adapter for a selected platform
  extension interface. It grants no platform ownership or consumer permission.

## Selected package services

The current first-party Agent package line is `2.0.0`. Its 18 public
`younics.ai.agent.*` contract families start at `1.0.0`; package-private contracts
follow package version `2.0.0` (13 current private APIs). Package Maker is
`younics.sunder.package.maker`. Agent's independently reviewed
`compatibility/agent-current-inventory.json` defines the current 15 project/package
pairs and 64 providers; the workspace-path migrator API is absent. Current byte
expectations are separately reviewed in `compatibility/agent-current-contracts.json`,
after qualification of fresh artifacts. Archived identity maps and predecessor
descriptors do not define these expectations.

Package API selection stores explicit `consumerRoles`. New Add defaults to both
App and Runtime; Update and Restore preserve existing reviewed intent. Each role
gets its complete selected public package API with independent required-wins policy.

Generated names use the canonical package ID, independent of import aliases and
authored namespaces. Uppercase the first character of each dot-separated segment,
join the segments with `_`, and append the area suffix; a digit-leading stem gets
a leading `_`. For `younics.ai.agent`:

```csharp
using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using Sunder.Generated.Packages.Younics_Ai_Agent_Services;
```

The injectable selected-package root is `IPackageApi`. Imported service interfaces
use contract-symbol subnamespaces: `CYounics_Ai_Agent_Chat_Provider.IChatProviderService`
and `CYounics_Ai_Agent_Runtime_Catalog.IRuntimeCatalogService` under `_Services`.
For example, an injected Agent `IPackageApi` exposes
`agent.Younics_Ai_Agent_Runtime_Catalog.RuntimeCatalog.GetProfileAsync(profileId, token)`.
Contract scopes are root properties; shared models remain flat under `_Models`.

Owned roles reuse authored business records. Importers receive one generated model
per source identity in the authority package's flat `_Models` namespace, shared
across contracts. Generated records have init properties and support `with` updates;
initialize them with object initializers. Do not copy the owner's records or write
wire/domain mirrors. The selected artifact's validated descriptors and model-projection
sidecar preserve model names and full value metadata through export, restore and role
generation. Missing projection metadata requires coordinated regeneration/reselection,
not guessed names from descriptor text.

Imported methods follow the descriptor shape: scalar/list/nullable roots stay natural,
nonempty request objects remain one typed request, and empty requests and unary
completion are hidden. Compiler-created multi-argument envelopes also live in Models;
for example, embedding generation uses
`CYounics_Ai_Agent_Embedding_Provider_EmbeddingProviderGenerateRequest`. Authors do not
maintain these envelopes. Ambiguous public names fail rather than gaining numeric,
hash or contract-namespace suffixes. Transport implementation stays in `_Internal`.

Resolving a root from DI does no discovery or dispatch. Unary calls and lazy stream
enumerations own independent scopes. Cancellation and generation/activation fences
cover discovery, dispatch, results and disposal; disposal retires and drains work.
A selected external provider must match the reviewed lock's package ID, requested
version range and prerelease policy, plus the exact contract version/descriptor/ABI.
The restore-selected package version/content identity establish reviewed artifact
provenance, not runtime routing authority. Same-package clients retain their exact
owning package version and activation/generation binding.
A dispatched call is never replayed automatically. Type availability, injection and
reference possession do not grant access: reviewed role/action uses and Host grants
remain authoritative. Provider-only imports do not infer consumer uses.

For a narrowly selected imported service, declare the Runtime actions the actual
implementation needs, using its canonical generated type:

```csharp
[SunderRuntimeUse(typeof(IExecutionTargetService), Actions = ["discover"])]
```

Place this on the consuming class/interface. The type must resolve in the validated
import set; the attribute neither selects a package nor grants publication authority.
Discovery-only use supports reference availability checks without requesting invocation.
Request `invoke` or `subscribe` only when the implementation needs those actions.
Readiness defaults to optional; type availability alone never supplies a use declaration.

## Multiple providers and typed references

Inject `IServiceProviders<TService>` from `Sunder.Sdk.Services` when business logic
chooses among authorized implementations:

```csharp
// chatProviders is an injected IServiceProviders<IChatProviderService>.
var catalog = await chatProviders.ListAsync(token);
var provider = catalog.Providers.Single(entry =>
    entry.State == ServiceProviderState.Active && entry.ProviderId == "acme.chat");
var models = await provider.Service.ListModelsAsync(token);
ServiceReference<IChatProviderService> reference = provider.Reference;
```

The selection rule belongs to the application. Entries expose package/provider
identity and a ready-to-call `.Service`; `.Reference` transfers a typed locator for
that existing provider. Generated code validates the complete exact locator against
the receiver's visible providers, including activation and session generation. It
never exports a local object or callback and never treats a locator as a grant.

Entries are borrowed values; do not dispose them. `EnsureCurrentAsync` validates an
exact binding, `RetirementToken` can bound a selected service's business lifetime,
and `TryReportInvariantViolationAsync` reports a genuine semantic defect against
that binding. Retirement is permanent; recovery requires a fresh entry. If a UI
needs catalog changes, start `WatchAsync` with both `catalog.Revision` and
`catalog.Sequence`, and obtain a new snapshot on reset. Generated DI owns binding
observation, transport cleanup and shutdown.

### Optional reference availability

References are strict by default, including nullable references when non-null.
When a business operation can continue without a dependency, explicitly annotate a
transferred, direct nullable reference property on its record:

```csharp
[JsonIgnore, SunderServiceMember, SunderOptionalServiceReference]
public ServiceReference<IExecutionTargetService>? ExecutionTarget { get; init; }
```

`SunderServiceMember` opts this JSON-ignored property into service transfer;
`SunderOptionalServiceReference` separately permits safe exact-provider absence to
transfer as `null`. Nullability alone does not select that policy. The marker cannot
be applied to containers, optional wrappers or transparent values. The receiver must
handle null as an explicit business alternative.

A live optional reference remains exact-bound and never retargets. Its retirement
does not cancel the outer operation merely because it was passed as an optional
dependency. Caller/callee cancellation and required-reference participants still
apply. Permission denial, malformed metadata and unsafe/possibly-dispatched failures
are not converted to null; this is no retry or blanket exception-suppression policy.

## Content and transient authority

Use `SunderContent` in ordinary arguments, results and nested records. Create local
content with `FromBytes`, `FromStream` or `FromFile`; open received content with
`OpenReadAsync`. Dispose each read stream and the content when finished:

```csharp
await using var content = SunderContent.FromFile(path, "application/pdf");
await documents.UploadAsync(content, token);
```

Sending does not dispose the author's content. `FromStream` owns its input unless
`leaveOpen: true`; files are never deleted. Received result content may outlive the
call or stream iteration, within its cancellation, deadline, provider and DI lifetime.
Borrowed provider request content remains bounded by the handler. Generated bindings
own registration, forwarding and resource leases; authors create no RPC scopes.

A replayable content-output watch may put
`[SunderMethod(CompleteOnResponseContentRetirement = true)]` on its **provider
implementation method**. It defaults to false and permits normal completion only
for authenticated retirement of the current response-registration authority.
Consumers must resnapshot or resume after completion. Caller cancellation, unsafe
dispatch and business failures remain failures. This local lifecycle choice is
neither a contract-interface annotation nor a wire/deadline policy.

Keep transient authority out of ordinary JSON/persistence. Opt a public property
into service transfer only when the contract needs it:

```csharp
[JsonIgnore, SunderServiceMember]
public ServiceReference<IExecutionTargetService>? ExecutionTarget { get; init; }
```

This fragment uses `System.Text.Json.Serialization`, `Sunder.Sdk.Services` and the
selected Agent Services namespace. An ignored property without `SunderServiceMember`
stays local. Explicit transfer does not bypass caller/run-grant checks or confer
authority on the receiver.

## Generated JSON metadata and completion

Ordinary service codecs use compiled, closed JSON metadata for the supported model
graph, including records and collections. They avoid the framework reflection
accessor cache that can retain collectible package types. Required/nullable/optional
members and constructor normalization retain their declared semantics. Authors add
no reflection resolver or serializer registration; arbitrary persistence calls with
default JSON options are a separate responsibility.

No-argument methods and `Task`/`ValueTask` completion need no authored `Unit` or
request type. Generated metadata handles the empty-object wire form automatically;
compiler-owned request envelopes and imported wire types remain implementation details.

## Explicit consumer roles

Readers reject missing or invalid `consumerRoles`; Build and Restore never infer
or write it. Select roles through normal targeted Add/Update:

```sh
dotnet msbuild Package.csproj -t:UpdateSunderPackageApi \
  -p:SunderPackageId=example.dependency -p:SunderPackageApiConsumerRoles=app%2Cruntime
```

The option values after MSBuild decoding are exactly `app`, `runtime`, and
`app,runtime`. Use `%2C` for the comma in a command-line property. The same property
applies to targeted Add/Update. A global operation cannot apply a per-selection
override. Selection reparses the complete proposal, validates current source and
atomically replaces only the lock. An incomplete lock requires restoration of its
reviewed role intent.

## Private identity and public lifecycle

Private descriptor versions equal the **full effective package version**, including
prerelease and case-sensitive build metadata. A private-only project needs no owned
baseline and no Accept/Freeze. Check and Freeze are no-ops when there are no public
contracts to transition. Normal Build/Check/Pack never rewrite a tracked baseline.

Existing `retiredPrivateContracts` records remain readable and immutable; normal
commands cannot add to, remove or enrich the archive. Active private baselines are
unsupported. The active and archived maps share the 128-entry budget.
Public current/history evidence is classified first, so demotion, renaming, removal
or baseline deletion cannot bypass public review. A later public successor requires
explicit acceptance and can coexist with the unchanged private archive.

Project exports require `packageVersion`; private tuples must match it exactly.
Source and qualified prebuilt Pack still validate private descriptor/ABI bytes,
role policies, lock inputs and payload integrity. For a missing private-only baseline,
Core's qualification frame records explicit absence (`baseline-absent:<relative path>`)
and the empty-byte SHA-256. The source-derived package identity is bound by the native
plan and revalidated source/tool frame; an absent baseline is never public evidence.

## Retired declaration spellings

The earlier RPC-prefixed declaration attributes and authoring exposure/audience
enums are removed. Import `Sunder.Sdk.Services` and use the ordinary declarations;
there is no fallback to an explicit-method-only marker or alternate authoring model.
Retired declaration source produces compiler errors. Frozen source and descriptor
oracles retain their original bytes as history, not current compiler inputs.

Advanced wire, provider/content-scope and runtime RPC APIs remain in `Sunder.Sdk.Rpc`.
For example, `SunderRpcMethodIdempotency` is still the descriptor/runtime enum, while
source metadata uses `SunderServiceIdempotency`. Generated
`CreateSunderRpcProviderRegistration` / `DeclareSunderRpcProvider` factories keep
their established identity and behavior.
