# ADR: Implementation-First RPC Authoring

> **Status:** Accepted V1 design with C#/Node receipt-integrated authoring,
> baseline lifecycle/Pack, exact local/Registry imports, source-derived catalog export,
> generated Stack transport and Registry/Host admission implementations. The
> 2026-10-03 amendment makes the explicitly selected Registry the publication authority;
> the 2026-10-04 correction permits unpublished local authoring without any Registry.
> Canonical first-party migration, deployed publication and final release acceptance
> remain open.
>
> **Decision date:** 2026-09-05
> **Compatible Installation decision:** Explicitly selected by the user on 2026-09-11.
>
> **Applies to:** Sunder SDK `1.1.x`, package manifest V1, RPC descriptor V1,
> Worker protocol V1, TypeScript 6.0.3, and ES2023.

`GenerateSunderRpcBindingsTask` now integrates the descriptor-only C# import
prepass for package authority into initial semantic analysis and final generated-
member compilation. The passes must agree on registration output and the complete
receipt. `PackageContractBuilder` consumes that receipt with its expected SHA-256,
lock hash, and package identity, then projects import groups and canonical reviewed-
range dependencies with explicit presence. The blanket nonempty-import generation
gate is removed; direct platform imports require original catalog evidence matching
their exact local or Registry selection.

C# source analysis uses the project's evaluated `AssemblyName`, including the
generated-member verification pass, lifecycle commands, import source checks and
Pack revalidation. Ordinary Roslyn accessibility rules therefore apply to domain
dependencies that use `InternalsVisibleTo`. The assembly name is included in the
source input fingerprint; changing it invalidates prior receipt/export evidence
without changing the contract's wire identity. The package build targets supply
this compiler input automatically.

Project exports preserve the producer lock hash without exporting external
contracts or use grants. Canonical exact-RID C# Worker leaves must retain identical
import evidence; aggregation permits only the associated owned-only App projection,
not arbitrary role-aware import merging. Source, owned/imported descriptor snapshots,
and applicable receipt/export outputs publish as one atomic marker-owned set.

**Generating an artifact is not publishing, installing, or activating it.**
Registry publication and Host admission validate import groups, exact contract
ownership and optional branches against the selected Registry's accepted inventory
and lifecycle policy. Both language toolchains derive those groups from explicit
lock selections. Local build success does not grant Registry or Runtime authority.

The internal `SunderRpcContractCompatibility` classifier is implemented in
`Sunder.Package.Format` and reused by C# build tooling and Registry's preflight
and locked-family version checks. Local Check/Accept/Freeze targets retain lossless
baseline history and guard source-based packing. Registry authority and explicit
publication-evidence import have their own validation; baseline evidence alone
does not establish publication. Unsupported prerelease allocation remains guarded.
The [evolution rules and recipes](../../specs/rpc/v1/authoring/evolution/README.md)
define the supported proofs and conservative failure boundaries.

Shared platform-catalog creation and content validation live in `Sunder.Package.Format`.
Node package-tool independently validates the same canonical catalog and locked pins.
The selected Registry establishes publication authority without catalog signatures or
key configuration. The existing non-public `Sunder.ExecutionRuntime.Build` tool owns
Core platform Check/Accept/Freeze/Export and publication-evidence import, and a separate
Stack migration-descriptor oracle check. Ordinary Stack source reproduces all 48
definitions, six methods, and the complete shipped `2.0.0` canonical descriptor/ABI
tuple with unchanged hashes. SDK source builds now generate an SDK-only handler and
required partial factory through a short-lived build-only tool process; missing
generation fails compilation. The thin Stack wrapper retains only routing-error
semantics, not handwritten public-DTO JSON conversion. The embedded descriptor/props
distribution and public DTO/client bridge remain migration paths pending retirement.
Exported local content alone does not establish publication or admission.
The [platform-catalog implementation boundary](../../specs/rpc/v1/authoring/platform-catalog/README.md)
records exact bytes, pins, dependencies, and remaining integration.

Node now implements the bounded owned TS/JS DSL vertical, lossless package-owned
baseline reader/writer, conservative production classifier, closed
`contracts check|accept|freeze`, and source-verified V3 release Pack, including
prebuilt aggregation. The real generate/dev/build pipeline reproduces seven frozen
owned tuples independently from TS and JS; its classifier executes all 199 shared
proof/direction/minimum/history recipes without changing C# oracles or frozen
expectations. The SDK remains zero-dependency.

The Node baseline retains deterministic canonical bytes, complete immutable
history/state and release/publication evidence within the frozen 268,435,456-byte,
depth-16 ceiling and bounded structural/collection budgets. It accepts package
authority only and never invents published state or Registry evidence. Contracts
default to `1.0.0` independently of package version. A genuinely new selected key
with no current/history may use a next-only stable initial override; existing
current/history overrides require the exact key/current/next pair, plus explicit
acknowledgement for breaking, unclassified, exposure, or removal changes. Check
checks the complete set; Freeze takes the package version from config.

First Accept requires fresh source, a valid exact read-only lock, an absent
baseline and no prior receipt/export, before first generation. Package and platform
selection may populate the lock first in an initial dependency-configuration phase.
It validates inventory/hash/generated-name and source/lock stability while permitting
not-yet-selected provider interfaces in complete source. First Accept must compile
and validate the complete graph; accepted projects retain full source checks during
selection. Prior receipt/export/marker evidence forbids missing-baseline bootstrap.
Local candidates and Core catalogs need no Registry; selected provider surfaces must
be bound before Accept. The earlier
baseline-free development route cannot adopt an existing receipt into a release;
baseline deletion or V3-to-V2/non-RPC fallback cannot prove freeze. Accepted versions
are injected before descriptor, contextual type, binding, registration, and manifest
generation. Accept recompiles at proposed versions while retaining the original
physical baseline/absence fingerprint and revalidating source/config/producer/
compiler/runtime-reference and resolution inputs. Baseline-only lease transactions
perform no generated-output recovery/orphan cleanup; no-ops preserve mtimes.

Pack requires every current contract frozen/published and exact complete owned
projection. Prebuilt Pack also repeats ordinary-source analysis and verifies fresh
production Worker bytes. Post-archive/evidence revalidation holds the baseline lease.
Own-property membership across maps and null-prototype source maps prevent inherited
names from hiding removals, including `constructor` errors. Shared handlers are
checked against every generated context, independent of provider order, without
type widening. Actual packed omit-dev TS/JS execution includes next-only first
contract `2.0.0`, independent package `7.6.5`, lifecycle/Pack and mutation guards.

Imports/restores, platform providers/admission, optional dependencies, explicit
publication-evidence import and V3 templates have owning implementations. New
prerelease allocation remains unsupported. Owned source/release verification grants
no publication or Runtime authority. See
the [Node execution boundary](../../languages/typescript/node/packages/package-tool/README.md#owned-v3-source-and-releases).
Descriptor files, Protocol projects, direct MSBuild RPC items, and Node schema-
version-2 RPC config in remaining projects are pre-cutover migration inputs, not
the final V1 authoring contract described here.

The wire contract does not change: canonical RPC descriptor V1 and
`sunder.rpc.values.v1` remain the language-neutral runtime authority. This
decision changes how authors produce that contract.

## Decision

Package authors write ordinary implementation and value-model source. Sunder
derives, validates, versions, and generates the descriptor, ABI projection,
codecs, clients, provider adapters, manifest declarations, and project-role
exports. Authors do not maintain `.rpc.json`, generated source, contract hashes,
provider registration identities, manifest bundle/provider/use records, or a
shared CLR Protocol assembly.

The source-of-truth roots are:

- one annotated C# service declaration or implementation in an ordinary package
  project;
- one configured and statically analyzable `rpc.router(...)` export for a Node
  package; and
- one tool-owned `sunder-contracts.lock.json` for reviewed package ranges, exact
  imported RPC identities, and package/platform restore provenance.

Generated descriptors remain immutable package content and retain exact
contract version, canonical descriptor SHA-256, and ABI SHA-256 identity. The
existing strict descriptor parser is always the final validation step; no source
frontend may bypass its graph, format, size, or canonicalization checks.

## C# Authoring Surface

All declarative authoring types live in `Sunder.Sdk.Rpc`. Existing
`SunderRpcMethodIdempotency`, `SunderRpcOptional<T>`,
`SunderRpcInvocationContext`, and RPC value types are reused. The domain-error
attribute is named `SunderRpcDomainErrorAttribute` because `SunderRpcError` is an
existing runtime value type.

The frozen public names and shapes are:

```csharp
namespace Sunder.Sdk.Rpc;

public enum SunderRpcExposure
{
    Package,
    Public,
}

public enum SunderRpcProviderAudience
{
    Inferred,
    Owner,
    Public,
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface,
    AllowMultiple = false, Inherited = false)]
public sealed class SunderRpcServiceAttribute : Attribute
{
    public SunderRpcServiceAttribute(SunderRpcExposure exposure);
    public SunderRpcExposure Exposure { get; }
    public string? ContractId { get; set; }
    public string? ServiceId { get; set; }
    public bool IncludeSchemaDialect { get; set; }
    public Type[] AdditionalDefinitions { get; set; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface,
    AllowMultiple = false, Inherited = false)]
public sealed class SunderRpcAppUseAttribute : Attribute
{
    public bool Required { get; set; }
    public string[]? Actions { get; set; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface,
    AllowMultiple = false, Inherited = false)]
public sealed class SunderRpcRuntimeUseAttribute : Attribute
{
    public bool Required { get; set; }
    public string[]? Actions { get; set; }
}

[AttributeUsage(AttributeTargets.Class,
    AllowMultiple = false, Inherited = false)]
public sealed class SunderRpcProviderAttribute : Attribute
{
    public SunderRpcProviderAttribute();
    public SunderRpcProviderAttribute(Type serviceType);
    public Type? ServiceType { get; }
    public string? ProviderId { get; set; }
    public SunderRpcProviderAudience Audience { get; set; }
}

[AttributeUsage(AttributeTargets.Method,
    AllowMultiple = false, Inherited = false)]
public sealed class SunderRpcMethodAttribute : Attribute
{
    public string? MethodId { get; set; }
    public string? RequestDefinitionName { get; set; }
    public string? OutputDefinitionName { get; set; }
    public SunderRpcMethodIdempotency Idempotency { get; set; }
    public string[] Errors { get; set; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface,
    AllowMultiple = true, Inherited = false)]
public sealed class SunderRpcDomainErrorAttribute : Attribute
{
    public SunderRpcDomainErrorAttribute(string code);
    public string Code { get; }
    public string? Description { get; set; }
    public Type? DetailsType { get; set; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct |
    AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
public sealed class SunderRpcDefinitionAttribute : Attribute
{
    public SunderRpcDefinitionAttribute(string name);
    public string Name { get; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct,
    AllowMultiple = false, Inherited = false)]
public sealed class SunderRpcTransparentAttribute : Attribute;

[AttributeUsage(AttributeTargets.Enum,
    AllowMultiple = false, Inherited = false)]
public sealed class SunderRpcNumericEnumAttribute : Attribute
{
    public bool IncludeRangeBounds { get; set; } = true;
}

[AttributeUsage(AttributeTargets.Class,
    AllowMultiple = false, Inherited = false)]
public sealed class SunderRpcUnionAttribute : Attribute
{
    public SunderRpcUnionAttribute(params Type[] variants);
    public IReadOnlyList<Type> Variants { get; }
}

[AttributeUsage(AttributeTargets.Class,
    AllowMultiple = false, Inherited = false)]
public sealed class SunderRpcDiscriminatorAttribute : Attribute
{
    public SunderRpcDiscriminatorAttribute(string propertyName);
    public string PropertyName { get; }
}

[AttributeUsage(AttributeTargets.Class,
    AllowMultiple = false, Inherited = false)]
public sealed class SunderRpcVariantAttribute : Attribute
{
    public SunderRpcVariantAttribute(string tag);
    public string Tag { get; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct |
    AttributeTargets.Enum | AttributeTargets.Property |
    AttributeTargets.Parameter | AttributeTargets.ReturnValue,
    AllowMultiple = false, Inherited = false)]
public sealed class SunderRpcFacetAttribute : Attribute
{
    public long MinLength { get; set; }
    public long MaxLength { get; set; }
    public long MinItems { get; set; }
    public long MaxItems { get; set; }
    public long MinProperties { get; set; }
    public long MaxProperties { get; set; }
    public long MaxDecodedBytes { get; set; }
    public long MaxDigits { get; set; }
    public long MaxPrecision { get; set; }
    public long MaxScale { get; set; }
    public string? Minimum { get; set; }
    public string? Maximum { get; set; }
    public string? ExclusiveMinimum { get; set; }
    public string? ExclusiveMaximum { get; set; }
    public string? ExactNumber { get; set; }
    public string? ConstantString { get; set; }
    public bool JsonObjectOnly { get; set; }
    public string[]? AllowedStrings { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
}
```

`SunderRpcFacetAttribute` integral bounds default to `-1`, meaning absent. A
source compiler rejects any other negative value. Numeric strings must already
be canonical invariant RPC integer/decimal text. Mutually exclusive descriptor
facets, reversed bounds, and facets incompatible with the target schema fail at
compile time.

`JsonObjectOnly=true` narrows a raw `JsonElement` value to the exact schema
`{"type":"object"}` without adding property-count bounds or converting it into a
typed dictionary/closed record. It can be combined with explicit property bounds,
and rejects non-raw-JSON targets. The Node equivalent is `rpc.schema.json({ objectOnly:
true })`, with `JsonObject` inference. Omission/false retains arbitrary JSON, including
arrays and scalar values. Normal wire/resource validation still applies.

`AllowedStrings` retains a plain string's closed inline enumeration when the wire
contract uses `{"type":"string","enum":[...]}` directly at that value. It accepts
1..256 distinct, non-null Unicode-scalar strings in declared order, rather than
introducing an extra named enum definition. Null/empty selections, duplicate values,
invalid Unicode and non-plain-string targets fail. Generated codecs and the normal
descriptor validator enforce the values. Use ordinary CLR enums for named enum
definitions; Node's inline `s.stringEnum(...)` already expresses this wire form.

### Service And Provider Forms

`IncludeSchemaDialect` defaults to `false`. Set it to `true` when an existing
descriptor contains the optional `$schema` annotation and source migration must
preserve that descriptor's exact bytes/hash. It emits only the fixed V1 annotation
`https://json-schema.org/draft/2020-12/schema`; it does not select a different value
profile, descriptor version or schema dialect. The ABI projection is unchanged,
but adding/removing this annotation changes the descriptor hash and remains subject
to ordinary baseline checks. The equivalent Node contract option is the literal
boolean `includeSchemaDialect`.

`AdditionalDefinitions` defaults to an empty array. It explicitly retains ordinary
source value types whose named definitions belong to a contract even when no current
method references them. For example, `AdditionalDefinitions = [typeof(CollectionIndex)]`
preserves an existing named alias through source migration. The same closed value
compiler validates these types and their reachable graphs; external CLR types, open
generics, null/duplicate entries and name collisions fail. The array is bounded to
256 entries, and normal complete-descriptor limits still apply. No descriptor JSON
or baseline schema is used as source. Node already expresses such definitions in its
literal `definitions` map.

A concrete partial class with `SunderRpcServiceAttribute` is the single-class
shorthand: it declares one service and one local provider. The provider uses the
default ID and inferred audience unless a parameterless
`SunderRpcProviderAttribute` supplies an exceptional stable provider ID or
audience.

An annotated interface is declaration-only and creates no provider. One or more
partial implementation classes bind it with
`[SunderRpcProvider(typeof(IService))]`. Every binding is compile-time checked
for the complete service shape. If more than one implementation binds the same
contract, every binding must set a distinct `ProviderId`. A class cannot bind two
service declarations, and runtime scanning or reflection dispatch is forbidden.

Package exposure implies owner audience. Public exposure implies public audience
unless explicitly narrowed to owner. Package exposure with public audience is
invalid. Multiple service declarations may share one explicit `ContractId` only
when exposure and contract-level error declarations agree exactly.

An additional concrete provider may also inherit an implemented ordinary source
service and bind it with `[SunderRpcProvider(typeof(BaseService), ProviderId = "...")]`.
This retains one descriptor and separate literal provider identities while reusing
the implemented methods. When both base and derived types have generated registration
factories, the derived factory explicitly hides the base factory; call it on the
concrete provider type when composing the Worker. The generated code remains valid
with warnings treated as errors.

Every concrete provider type is partial. The compiler emits this instance member
with no author-supplied identity arguments:

```csharp
public SunderWorkerProviderRegistration CreateSunderRpcProviderRegistration();
```

The generated registration embeds the accepted contract version, canonical
descriptor hash, ABI hash, exact declared errors, provider ID, audience, and
handler adapter.

For activation-aware composition, concrete providers also expose:

```csharp
public static SunderWorkerProviderRegistration DeclareSunderRpcProvider(
    ISunderRpcServiceHandler handler);
```

This uses generated identity and immutable declared-error metadata while forwarding
calls to the supplied handler. It does not construct a provider instance or inspect
the supplied handler's error metadata. Owned, imported and semantic platform
providers support the same declaration path, including inherited registrations.
Both factory names and their generated helper types are reserved; static declaration
calls are verified again after generated members are available.

A package can supply an activation gate, advertise the declaration before resolving
storage-backed services, and bind the ordinary instance registration after startup
commits. The gate owns waiting, cancellation and retirement. It must verify the
activated registration's provider/contract/version/descriptor/ABI/error tuple before
publishing its handler. Declaration adds no consumer permission or readiness policy.

Ordinary implementation files may contain SDK `GeneratedRegex` partial methods.
The RPC prepass recognizes only the framework attribute/return-type identity and
parameterless static partial shape, and validates literal patterns, options,
timeouts and cultures before deferring the missing-body diagnostic. The normal
.NET compiler still owns generating and compiling those method bodies. Invalid
patterns and source-defined lookalike attributes remain compilation failures;
this does not enable arbitrary generator-produced RPC declarations or models.

### Method Rules

- Only instance methods carrying `SunderRpcMethodAttribute` are RPC methods.
- Unary methods return `Task<T>` or `ValueTask<T>`. Server streams return
  `IAsyncEnumerable<T>`.
- A method accepts zero or more supported wire parameters, followed by at most
  one `SunderRpcInvocationContext` and at most one `CancellationToken`.
- Multiple wire parameters synthesize one closed request definition. Zero wire
  parameters use the canonical `Unit` definition. One explicit DTO remains a
  normal request definition.
- The source method name, after one terminal `Async` is removed, supplies the
  default method ID. `RequestDefinitionName` and `OutputDefinitionName` exist
  only for compatibility or a reported collision.
- Overloads, generic methods, static methods, `ref`/`out`, optional CLR wire
  parameters, synchronous streams, dynamic values, custom awaitables, and
  ambient cancellation are invalid.
- Method kind is inferred from the return type. Idempotency defaults to
  `SunderRpcMethodIdempotency.None`. `Errors` defaults to an empty array and may
  name only contract-level domain errors.

### Value Rules

| C# source | RPC V1 source meaning |
| --- | --- |
| `required T` | Required property; `T?` permits explicit null |
| `SunderRpcOptional<T>` | Optional property; `T?` permits a present null |
| `string`, `bool`, `long`, `float`, `double` | String, boolean, safe integer, float32, float64 |
| `Guid`, `DateOnly` | UUID and date formatted strings |
| `SunderRpcOffsetDateTime`, `SunderRpcDuration`, `SunderRpcUri` | Exact formatted strings |
| `BigInteger`, `SunderRpcDecimal` | Canonical big-integer and decimal strings |
| `byte[]`, `SunderRpcContentReference`, `JsonElement` | Base64 bytes, content reference, bounded JSON |
| `IReadOnlyList<T>` | Array |
| `IReadOnlyDictionary<string, T>` | String-keyed map |
| Closed class or sealed record | Closed record schema |
| `[SunderRpcTransparent]` wrapper with one public `Value` member | Named scalar, array, map, or formatted definition without an extra object layer |
| Enum with `JsonStringEnumMemberName` on every member | String enum in declaration order |
| `[SunderRpcNumericEnum]` enum | Unique safe integers in ascending wire order; flags and aliases are invalid. `IncludeRangeBounds = false` preserves an existing closed enum without redundant minimum/maximum fields; defaults include both bounds. |
| `[SunderRpcUnion(typeof(A), typeof(B))]` abstract record | Closed union of the named variants within the existing SDK schema profile |
| Union plus discriminator and variant attributes | Tagged union with one synthesized constant discriminator member per variant |

Plain non-required properties, fields, indexers, extension-data members, custom
JSON converters, open records, unsupported generic shapes, unknown annotations,
and source or wire-name collisions are rejected. Generated codecs own exact
names, optional omission, enum representation, tagged unions, and unknown-member
rejection.

The source-generated C# provider codecs now preserve required `false`, zero, and
permitted null values; optional absence versus present values, including nullable
values; numeric-enum integer lexical forms; exact string-enum names; transparent
wrappers; and frozen property naming. Optional initializers are cleared before
JSON property population so omission remains absence. Optional-member models with
parameterized JSON constructors fail with `rpc-authoring.value-invalid`; they are
not silently accepted.

Generated union codecs support SDK-profile nullable and tagged record unions,
including inferred authored constant tags, concrete variants used as RPC roots,
and recursive models. The existing SDK validator checks whole values and exactly
one matching variant; generated metadata supplies required synthetic tags, and a
`[ThreadStatic]` depth guard bounds recursive conversion. General scalar/array/map
untagged unions remain unsupported by the SDK profile, not unfinished source-codec
work. No new schema profile is introduced.

Narrow CLR integer codecs (`byte`, `sbyte`, `short`, `ushort`, `int`) read through
the SDK safe-integer reader, then enforce checked CLR ranges. Fraction/exponent
spellings of integral values are accepted when representable by the target type;
this is codec parity, not a descriptor or ABI change.

## TypeScript And JavaScript Surface

`@sunder/sdk` exports one closed `rpc` object. These names are frozen:

```typescript
rpc.router({ contracts, providers })
rpc.contract({ exposure, contractId?, serviceId?, includeSchemaDialect?, errors, definitions, services })
rpc.provider({ contract, id?, audience?, handlers })
rpc.unary({ request, response, idempotency?, errors? })
rpc.serverStream({ request, event, idempotency?, errors? })

rpc.schema.required(schema)
rpc.schema.optional(schema)
rpc.schema.nullable(schema)
rpc.schema.ref(definitionName)
rpc.schema.object(properties, facets?)
rpc.schema.map(values, facets?)
rpc.schema.array(items, facets?)
rpc.schema.string(facets?)
rpc.schema.stringEnum(values, facets?)
rpc.schema.integer(facets?)
rpc.schema.integerEnum(values, facets?)
rpc.schema.float32(facets?)
rpc.schema.float64(facets?)
rpc.schema.boolean(facets?)
rpc.schema.uuid(facets?)
rpc.schema.date(facets?)
rpc.schema.offsetDateTime(facets?)
rpc.schema.duration(facets?)
rpc.schema.uri(facets?)
rpc.schema.bigInteger(facets?)
rpc.schema.decimal(facets?)
rpc.schema.bytes(facets?)
rpc.schema.contentReference(facets?)
rpc.schema.json(facets?)
rpc.schema.literal(value)
rpc.schema.union(variants, facets?)
rpc.schema.taggedUnion(discriminator, variants, facets?)
```

Object properties must use exactly `required(...)` or `optional(...)`.
Nullability composes only through `nullable(...)`, giving the same four
presence/nullability states as C#. Definition-map keys name transparent schemas,
so Node can represent named scalar and collection definitions directly.
`integerEnum` accepts unique RPC safe integers and emits ascending values.

`rpc.router(...)` is the one configured authoring root. A local
`rpc.contract(...)` with no matching provider is the explicit declaration-only
form. Local providers use `rpc.provider(...)`. Imported package/platform
providers use `rpcImports.<localKey>.provider(...)` from the generated
`#sunder/rpc` module and cannot redeclare schema.

TypeScript infers request, result, event, context, and handler types. JavaScript
uses the same runtime constructors and validation semantics; package tooling
emits matching `.d.ts` declarations and checks JSDoc where enabled. Unary
handlers return a value or promise. Stream handlers return `AsyncIterable<T>`.
Both receive the current invocation context and `AbortSignal` through that
context.

### Static Analysis Boundary

Package tooling parses the configured module with the pinned TypeScript compiler
API and resolves exactly one statically exported `rpc.router(...)` binding. It
may follow bounded local `const` references, direct aliased imports of the frozen
RPC constructors, and direct members of the receipt-verified `#sunder/rpc`
module.

Metadata and schemas may contain object/array/primitive literals, bounded local
constants, and allowlisted RPC constructor calls only. Computed keys, spreads,
getters, setters, proxies, class instances, loops, conditionals, identity-data
template interpolation, environment access, dynamic imports, and arbitrary
function calls are rejected. Handler bodies are recorded by source span but are
not traversed for metadata or executed. RPC derivation never imports or executes
package code.

One canonical package-root authoring graph permits at most 4,194,304 bytes of
normalized RPC-significant UTF-8 source, 65,536 visited syntax/type/expression
nodes, traversal/reference depth 64, 4,096 resolved local constant bindings, 128
contracts, and 128 local provider bindings. Each derived descriptor retains its
independent RPC V1 limits of 64 services, 512 methods, 256 definitions, 256
errors, 4,096 schema nodes, and resolved schema depth 64. Contract and provider
IDs remain canonical package-style identities of at most 128 characters. An
over-limit source or derived default fails with
`rpc-authoring.source-limit-exceeded`; no identity is truncated or hash-suffixed.

## Identity And Generated Names

The canonical package ID prefixes every new package-owned contract. C# service
types remove one terminal `Service`; task/stream methods remove one terminal
`Async`. Identifiers are tokenized with this ASCII-only algorithm:

1. `_` and `-` separate tokens. Leading, trailing, or repeated separators fail.
2. Split before uppercase preceded by lowercase/digit and before the final
   uppercase in an uppercase run followed by lowercase.
3. Digits remain with the preceding token until a following uppercase boundary.
4. Reject non-ASCII and every other character; never use culture or Unicode
   normalization.

Lower-kebab tokens form service and method wire keys. The same lowercased tokens
joined with dots form the descriptor-safe contract suffix. Pascal and camel
render generated source symbols. Defaults are:

```text
contract id = <canonical-package-id>.<service-contract-suffix>
service id = <service-key>
method id = <method-key>
provider id = <contract-id>.provider
```

For Node, the contract suffix is the contract object's canonical lower-kebab key
with token separators rendered as dots. Node contract/service/method keys must
already be canonical lower-kebab, definition keys Pascal, and property keys
camel. C# generated symbols prefix `@` for the frozen C# 14 keyword set.
TypeScript/JavaScript symbols prefix `_` for the frozen ES2023 module-binding
restriction set. Escaping never changes a wire identity. No frontend resolves
collisions with source order, namespace order, numeric suffixes, or hashes.

The normative keyword arrays live in
`../../specs/rpc/v1/conformance/identifier-keywords.json`. That fixture has
`schemaVersion: 1`, `csharpLanguageVersion: "14.0"`,
`typescriptVersion: "6.0.3"`, and `ecmascriptTarget: "ES2023"`. Changing a
dialect or array requires a reviewed versioned fixture change.

Accepted identities are persisted in `sunder-contracts.owned.json`. A later
source rename cannot silently rename wire members. The author either supplies an
explicit compatibility override or accepts the classified contract-version
change.

## Package Configuration And Generated Output

Node package config uses only this RPC shape:

```json
{
  "schemaVersion": 3,
  "rpc": {
    "module": "src/rpc.ts",
    "export": "packageRpc"
  }
}
```

Schema-version-2 `contracts`, `rpcBindings`, `providers`, and `usesContracts`
are removed at cutover. Neither Node config nor router source contains an import
selection list.

Generated output is marker-owned and untracked:

| Frontend | Logical output root | Required outputs |
| --- | --- | --- |
| C# | `$(IntermediateOutputPath)sunder-rpc/` | `authoring.g.cs`, `imports.g.cs`, `package-export.v1.json`, `authoring-receipt.v1.json` |
| TypeScript | `.sunder/generated/rpc/` | `imports.ts`, `browser.ts`, `index.d.ts`, `authoring-receipt.v1.json` |
| JavaScript | `.sunder/generated/rpc/` | `imports.js`, `browser.js`, `index.d.ts`, `authoring-receipt.v1.json` |

Generation is transactional. The last complete generation remains authoritative
until source analysis, lock/cache validation, descriptor validation, language
checking, and every output write succeed. Receipts and project exports are
intermediate build data and never enter a package archive.

The integrated C# path stages owned and imported descriptor snapshots together
with generated source and the applicable receipt/export. Its import syntax trees
participate in both the first semantic pass and the final generated-member pass;
registration and complete receipt bytes must reach the same fixed point before
publication. The existing output lock/transaction machinery protects the set and
its consumption, rather than publishing a partial prepass result.

C# source may also consume its own contracts through the generated
`Sunder.Generated.Rpc.PackageApi`, including declaration-only extension points in a
Worker. An internal discovery pass derives tentative owned bindings and facade members
from current ordinary source. The entire source set is then compiled strictly with
those bindings before final provider generation. Descriptor/client source, provider
identity/audience, consumer policies, registration source and the complete receipt must
remain stable across the passes. No provisional output or baseline is published.
This works on a clean bootstrap and when a source change adds a method that the same
source consumes; prior output and old descriptor snapshots are not type-shape authority.
Facade availability adds no grants: owned Worker uses still require explicit
`SunderRpcRuntimeUse` policy. Cross-package imports retain their exact lock boundary.

For C#, one selected package local name `weather` generates the namespace-level
class `Sunder.Generated.Rpc.WeatherPackageApi`; one imported provider intent key
`weatherForecast` generates `IWeatherForecastService`. That aggregate interface
inherits all descriptor service provider interfaces, not just the first service.
These local package/provider intent keys remain stable and are not per-contract
binding aliases.

Every imported receipt `contractKey` is automatically its canonical `contractId`.
There is no per-contract alias. Descriptor bindings live under
`Sunder.Generated.Rpc.Imports.C<encodedContractId>`: uppercase only the first
character of each canonical lowercase dot-separated segment, join with `_`, and
prefix `C`. Examples are `Sunder.Generated.Rpc.Imports.CExample_Rpc_V1_Minimal`
and `Sunder.Generated.Rpc.Imports.CSunder_Stack_Contributor`. The prefix permits
digit-leading IDs, and the encoding is injective on canonical lowercase IDs.
It ignores importing-package local name, version, and hash. Owned contract
keys/names remain unchanged; exact imported tuples coalesce only after the
descriptor and ultimate-provenance agreement checks. `artifactSource` is local
to each selected artifact: an owner may record `owned` and a reexport `imported`
for the same exact descriptor and ultimate provenance. Those values remain in
their respective complete inventories and need not agree across artifacts;
every generated cross-package bundle still has `source=imported`.

Each package facade property `C<encodedContractId>` returns
`Sunder.Generated.Rpc.ImportApis.C<encodedContractId>.Contract`. That namespace
contains these types:

| Type | Surface |
| --- | --- |
| `Provider` | Immutable typed provider metadata; the exact activation endpoint is retained internally, with no public `Endpoint` |
| `CatalogSnapshot` | `Revision`, `Sequence`, `ResetRequired`, `IReadOnlyList<Provider> Providers` |
| `CatalogEvent` | `Revision`, `Sequence`, `Kind`, `Provider? Provider` |
| `Client` | One `S<encodedServiceId>` property per descriptor service, returning the corresponding generated service client |
| `Contract` | Generated `ContractId` constant and the five members below |

Descriptor service IDs use the lowercase member-ID grammar, which permits dots,
hyphens, underscores, digit-leading IDs, and repeated separators, not just source-
authoring lower-kebab keys. A service property is `S` plus
`UpperInitial(serviceId)`: uppercase only the ID's first character, then escape
each original `_` as `_5f`, `.` as `_2e`, and `-` as `_2d`. Escape original
underscores first so inserted escape prefixes are not escaped again. This is
injective on valid descriptor service IDs: `example` becomes `SExample`,
`weather-forecast` becomes `SWeather_2dforecast`, and `1.foo` becomes `S1_2efoo`.
The contract signatures are:

```csharp
ValueTask<CatalogSnapshot> ListAsync(CancellationToken cancellationToken = default);
ValueTask<Provider?> GetAsync(Provider provider, CancellationToken cancellationToken = default);
IAsyncEnumerable<CatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default);
Client CreateClient(ISunderRpcCallScope scope, Provider provider);
ValueTask<Provider> ResolveSelectedPackageAsync(CancellationToken cancellationToken = default);
```

List/get/watch are extension-wide: they allow all authorized providers matching
the exact contract ID/version/`descriptorSha256`/`abiSha256`, without narrowing to
the selected package, package range, or `Active` state. Watch preserves resets,
cursors, and lifecycle events. `GetAsync` revalidates the supplied provider's
exact activation endpoint and never substitutes a replacement. `CreateClient`
revalidates the exact contract identity and uses the supplied scope without
creating or disposing it. A healthy endpoint may advance its session generation
without changing its activation identity; `GetAsync` accepts that advancement
but rejects generation regression or endpoint/activation replacement.

The bound C# `Client` also exposes
`RegisterContentAsync(Stream source, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default)`.
It registers content through the caller-supplied scope for that client's exact
validated provider endpoint. The caller retains ownership of the stream and scope;
registration policy, cancellation and errors pass directly to the SDK. This allows
typed package API consumers to send content without extracting raw endpoint metadata.

Only `ResolveSelectedPackageAsync` additionally requires exact package ID,
package version satisfying `requestedVersionRange` by SemVer precedence,
`includePrerelease` policy, and `Active` state. Prereleases are excluded unless
explicitly included; package build metadata does not affect precedence. There is
NO package content hash filter and no equality check against the restore-selected
package version. Zero matches fail with `rpc.provider-not-found`; multiple matches
fail with `rpc.provider-ambiguous`. There is no ranking, retry, endpoint
replacement, install/restore operation, or Registry fallback.

For Node, `#sunder/rpc` exports `packageApis` and `rpcImports`. The equivalent
members are `list`, `get`, `watch`, `createClient`, and
`resolveSelectedPackage`. Browser code imports generated browser-safe bindings,
not the Worker router module.

## Task Loading Reliability

Mutable source task loading uses a shared `Sunder.Package.Build.targets` request,
not a consumer-owned lease: producer `Build` and complete content-addressed immutable
snapshot publication hold one source lease; consumers stage only from that snapshot.
`IBuildEngine6` enumeration removes consumer globals while retaining explicit
canonical inputs and opt-in `SunderPackageBuildTaskSourceGlobalProperties` owner globals.
Short fresh-identity MSBuild dependency requests avoid long `Yield` waits that can
starve the holder's nested build. Existing staging markers, atomic copy, and validation
remain in use; owner snapshots stay under `obj/sunder-package-build-task-sources`
with no automatic cache GC. See [source task loading](../../languages/csharp/dotnet/packages/Sunder.Package.Build/README.md#source-task-loading)
and [task-shadow regression coverage](../../languages/csharp/dotnet/tests/Sunder.Package.Build.Tests/TaskShadowStagingTests.cs).
The coverage includes `-m:1`/`-m:2`, 1/2/4 bootstrap configurations, real per-consumer
globals, and one owner snapshot per canonical input; this is not clean-checkout/native proof.

## Lock, Cache, Receipt, And Export Contracts

One canonical package root owns one tracked `sunder-contracts.lock.json` and one
tool-owned reviewed `sunder-contracts.owned.json`. An associated App shares the
root files and owns no copy. Build is read-only and offline: it never contacts a
Registry or rewrites either file.

The lock has closed `packageArtifacts`, `packageImports`, `providerImports`, and
`platformImports` maps. It records exact selected artifact/version/content,
canonical Registry origin, complete public inventory count/hash and metadata,
ultimate authority provenance, descriptor byte count/hash, ABI hash, and exact
package presence/required-contract overrides. Every package import stores
`presence: required|optional`; that value controls package dependency installation
and is independent from `requiredContracts`, which controls provider readiness.
Descriptor bodies, credentials, mutable URLs, local paths, and generated source
are forbidden.

Canonical Registry origins are
`https://<lowercase-ascii-dns-host>[:nondefault-port]/`. User info, path beyond
`/`, query, fragment, Unicode/IDNA input, IP literals, default ports,
leading-zero ports, and ports outside `1..65535` are invalid. Explicit
Development mode additionally permits `http://localhost[:nondefault-port]/`.

Restore stores exact canonical descriptor bytes under
`~/.sunder/contracts/v1/sha256/<descriptorSha256>.json` with integrity metadata
in the adjacent `<descriptorSha256>.receipt.json`. Restore validates every
lock field and only populates missing immutable entries. It never selects a
version, changes lock bytes, or publishes generated output. `--no-restore`
reports an actionable cache diagnostic.

The normative closed schemas are owned under `specs/rpc/v1/authoring/`:

```text
contract-lock.schema.json
owned-contracts.schema.json
cache-entry-receipt.schema.json
package-public-inventory.schema.json
package-project-export.schema.json
authoring-receipt.schema.json
platform-catalog.schema.json
```

Each rejects unknown members and fixes UTF-8, depth, byte, count, key-order, and
string bounds. Tracked lock/baseline files use UTF-8 without BOM, LF, one terminal
newline, deterministic two-space formatting, schema member order, and ordinal map
keys. Identity hashes use the exact canonical payload defined by the owning
schema, never arbitrary parser reserialization.

The internal receipt contains validated identities, snapshots/hashes, selected
artifact and complete-inventory evidence, authority provenance, closed manifest
import groups, provider/use records, and generated logical names. It contains no
absolute path, source text, handler, credential, timestamp, or environment value.
Project exports carry owned package contracts and their App/Runtime consumer policies.
Their required `lockSha256`, immediately after `inputFingerprint`,
identifies the producer's exact deterministic lock bytes. Exact-RID builds check
that hash as well as owned descriptors, and associated App receipts preserve it
without inheriting external imported contracts or external use permissions.

The owning service may declare `[SunderRpcAppUse]` in C# or
`appUse: { required: false, actions: ["discover", "invoke"] }` on a TS/JS
`rpc.contract`. With no policy, package-private contracts default to required use
of all applicable actions; public contracts contribute no App use. Explicit policy
defaults `required` to false and omitted actions to all applicable actions. An
explicit action array must be nonempty, distinct, and a subset of `discover`,
`invoke` (unary methods), and `subscribe` (streams), emitted in that order. Null,
unknown or inapplicable actions fail. C# policy belongs on the owning service,
never on a value model or an imported provider. TS/JS metadata remains statically
analyzed and the runtime adapter verifies the resolved policy before binding.

Normalized policy is optional `appUse: { required, actions }` metadata after each
contract record's `abiSha256` in exports and receipts. It affects source/export
evidence, not canonical descriptors, ABI hashes or frozen contract snapshots.
Worker receipts retain App policy without receiving grants from it. App
receipts declare `role: "app"` and grant exactly the resolved policies. Generated
DTO/client availability alone grants no permission and adds no readiness predicate.

An owning Runtime opts into consuming its own contract with
`[SunderRpcRuntimeUse(Required = true, Actions = ["discover", "invoke"])]` on the
service, or `runtimeUse: { required: true, actions: ["discover", "invoke"] }` on a
TS/JS `rpc.contract`. There is no implicit Runtime use for either exposure, including
when a local provider implements the service. Explicit Runtime policy has the same
optional-readiness and applicable-action defaults and validation as explicit App
policy. A declaration-only service can carry consumer policy too.

Worker receipts grant exactly the declared owned Runtime uses alongside locked
package-API uses. Normalized `runtimeUse` follows `appUse` (when present) in owned
export/Worker contract records, and participates in source/export/RID consistency
checks. App receipt generation omits Runtime policy and grants. Imported contracts
cannot carry owned consumer policy; their permissions remain lock-derived.
Changing only consumer policy preserves the frozen wire baseline but requires a
fresh generated projection: Pack rejects missing, extra, broadened or stale uses.
Required readiness still means any authorized exact provider, with no implied
endpoint selection or package-presence change.

Manifest generation reads the receipt using the task output's expected SHA-256,
not merely its path. It checks lock/package identity, exact descriptor snapshots,
complete inventory/group closure, and bundle/use/provider task-item agreement
before output publication. The projected Manifest V1 bundle requires explicit
`source`, `authorityKind`, and `authorityId`; imported bundles additionally require
selected-authority release/content for both package and platform authority.
Owned bundles forbid selected-authority fields. Dependencies require explicit
`presence`; no reader supplies defaults. The four root manifest import groups may
be absent or empty, but explicit `null` is rejected.

Strict import manifest models and validation live in the existing
`Sunder.Package.Format` library. Its public model types are supported for shared
Core/Registry/tooling use; the internally distributed library is not a new public
NuGet package or package-author dependency. No new projects or compatibility
readers are introduced by this integration.

The shared internal `SunderPackagePublicApiInventory` Format parser/hash helper is
implemented with a 1 MiB raw-byte ceiling, depth 16, and a 4,096-value/property-name
preflight budget. The Build lock reader reuses it rather than duplicating inventory
hashing. Registry inventory/envelope DTOs and compiled closed-shape fixtures retain
`inventory` plus `publicInventorySha256` outside the hashed payload. These provide
structural integrity only, not persistence, a read backend, endpoints, or authority
business rules. See the [inventory contract](../../specs/rpc/v1/authoring/README.md#package-public-inventory).

## Package API Commands

The package tools, not `Sunder.Cli`, own these exact target command families.
The local C# and package-owned Node Check/Accept/Freeze subset below is implemented;
Registry-backed operations, publication-evidence import, and Node import/platform
commands remain incomplete:

```text
sunder-package api add <package>
sunder-package api update [package]
sunder-package api remove <package>
sunder-package api list
sunder-package api require <package> <contract>
sunder-package api unrequire <package> <contract>
sunder-package api implement <package> <contract>
sunder-package api unimplement <package> <contract>
sunder-package platform implement <contract>
sunder-package platform update [contract]
sunder-package platform unimplement <contract>
sunder-package platform list
sunder-package contracts restore
sunder-package contracts check
sunder-package contracts accept [contract-key]
sunder-package contracts freeze [contract-key]
sunder-package contracts publication import [contract-key]
```

Package selection options are `--version`, `--registry`, `--candidate`,
`--local-name`, `--presence`, `--include-prerelease`, and `--exclude-prerelease` where the
operation permits them. Local selection uses `--candidate` and, when needed,
`--platform-catalog` without a Registry. New Registry-backed selection requires
`--registry`. Local lock records omit `registryOrigin`; present null/malformed origins
are invalid. A targeted update can explicitly bind a local selection to a Registry,
but cannot override an existing Registry origin. Restore retains exact pins. Global update
rejects version, prerelease, origin, candidate, local-name, and presence overrides.

The matching .NET targets are:

| Operation | MSBuild target |
| --- | --- |
| API add/update/remove/list | `AddSunderPackageApi`, `UpdateSunderPackageApi`, `RemoveSunderPackageApi`, `ListSunderPackageApis` |
| API require/unrequire | `RequireSunderPackageApi`, `UnrequireSunderPackageApi` |
| API implement/unimplement | `ImplementSunderPackageApi`, `UnimplementSunderPackageApi` |
| Platform implement/update/unimplement/list | `ImplementSunderPlatformApi`, `UpdateSunderPlatformApi`, `UnimplementSunderPlatformApi`, `ListSunderPlatformApis` |
| Contract restore/check | `RestoreSunderContracts`, `CheckSunderContracts` |
| Owned accept/freeze/publication import | `AcceptSunderContracts`, `FreezeSunderContracts`, `ImportSunderContractPublication` |

Their exact public properties are `SunderPackageId`, `SunderContractId`,
`SunderContractKey`, `SunderPackageVersionRange`,
`SunderPackageIncludePrerelease`, `SunderRegistryOrigin`,
`SunderPackageCandidatePath`, `SunderPlatformCatalogPath`,
`SunderPackageApiLocalName`, `SunderPackageApiPresence`, `SunderContractCurrentVersion`,
`SunderContractNextVersion`, and `SunderAcknowledgeBreakingChange`. Omitted
nullable properties mean absent; the prerelease property is tri-state on update
and defaults to false only when creating a selection. Presence is the exact
lowercase enum `required|optional`: omission on add writes explicit `required`,
omission on targeted update retains the locked value, and no lock reader or
global update supplies a default. `api require` and `api unrequire` continue to
change only per-contract provider readiness.

Adding a package authenticates its complete public inventory but generates a
consumer facade and optional-by-default uses only for public
`authorityKind=package` members. Platform-authority members remain inventory
evidence and generate no consumer use. Extension and platform implementation
remain contract-scoped. `required=true` is an any-authorized-provider readiness
predicate, not selected-package provider choice.

Package-import presence projects unchanged into `packageApiImports` and into the
selected-artifact and ultimate package-authority `dependsOn` contributors.
Provider imports remain required dependency contributors. Normalized dependencies
have mandatory `presence: required|optional`; `required` wins when compatible
contributors coalesce by package ID, without rewriting the source API presence.
The C# manifest projection now preserves each selected artifact's canonical
reviewed `requestedVersionRange` unchanged, rejects empty range solutions, and
rejects authored non-RPC dependency IDs that duplicate lock-derived IDs.
Every included dependency must satisfy all applicable reviewed requested ranges,
prerelease policies, and exact imported RPC identities: contract ID, exact
contract version, `descriptorSha256`, and `abiSha256`. Overlapping ranges need
not be text-identical. One installed package may satisfy multiple requirements
only when all exact identities required for each contract agree. ABI equality
alone is insufficient if the descriptor differs; schema compatibility is not
permission to substitute a different locked RPC identity.

**Compatible Installation is the user-selected rule.** A newer in-range package
patch with the same exact imported RPC identities may satisfy the dependency.
Selected package release/content and ultimate package-authority release/content
remain exact restore/build/inventory provenance, NOT install or routing hard
pins. Different restore-selected artifact bytes across consumers are not by
themselves installed release conflicts. Restore still verifies the exact selected
artifact, full public inventory, ultimate provenance, descriptor bytes, and all
hashes; it never repairs drift by choosing a compatible artifact. The actual
installed artifact is still validated and recorded exactly. Execution-runtime
`.sunderruntime` exact pins and Registry catalog lifecycle checks remain separate.

A distinct transitive ultimate package authority needs an explicit selection with
a reviewed range and prerelease policy before it can contribute an installable
dependency. If the lock contains only its provenance snapshot, tooling must
fail closed at an unreviewed-range migration gate. Do not guess a broad compatible
range or derive an exact-equality range/content pin from `selectedAuthorityRelease`.
Reviewing a dependency range never implicitly grants consumer uses or actions.

An optional branch may be omitted as a unit when absent, incompatible, or
policy-ineligible. Omission affects that install branch, not RPC visibility or
authorization. The selected-package resolver still matches any otherwise-
authorized active provider with the selected package ID, in-range version, and
exact RPC identity, including a package installed independently of that branch.
Required-contract readiness still accepts any authorized exact matching provider,
including one from another package. No provider means not-found for the resolver
and blocked readiness only for required uses; omission itself implies neither.
Reviewed-range/presence projection, optional-branch installation and runtime admission
have owning C#/Node/Registry/Host implementations and parity coverage. Platform
generation validates original local or Registry catalog evidence against the exact lock before
producing a provider. A locally parsed descriptor cannot create an import selection.
Canonical package lock migration and deployment/release qualification remain separate.

## Stable Diagnostics

Package/lock/cache frontends share these stable IDs:

```text
package-api.package-not-found
package-api.no-compatible-version
package-api.public-api-empty
package-api.consumer-api-empty
package-api.registry-origin-invalid
package-api.public-inventory-invalid
package-api.private-contract-exposed
package-api.dependency-source-duplicate
package-api.authority-release-conflict
package-api.local-name-collision
package-api.provider-contract-not-public
package-api.provider-contract-removed
package-api.remove-still-referenced
package-api.required-contract-removed
package-api.import-not-found
package-api.candidate-authority-unconfirmed
platform-api.import-not-found
platform-api.catalog-envelope-invalid
platform-api.catalog-version-unsupported
platform-api.catalog-origin-invalid
contract-cache.entry-missing
contract-cache.entry-corrupt
contract-version.bump-required
```

Source frontends share these categories, with equivalent source spans and
details in C# and Node fixtures:

```text
rpc-authoring.service-invalid
rpc-authoring.method-invalid
rpc-authoring.value-invalid
rpc-authoring.provider-invalid
rpc-authoring.handler-incomplete
rpc-authoring.identity-collision
rpc-authoring.generated-name-collision
rpc-authoring.unsupported-source
rpc-authoring.source-limit-exceeded
rpc-authoring.baseline-drift
```

The shared version gate additionally distinguishes
`contract-version.prerelease-policy-required` and
`contract-version.version-limit-exceeded`. Registry's temporary
`contract-version.history-unavailable` gate is described below; it must not be
reported as proof that a frozen private or migration version needs renumbering.

Diagnostics may print bounded IDs, local keys, source spans, expected immutable
metadata, and an exact repair command. They never print descriptor bodies,
Registry credentials, headers, signed URLs, untrusted response text, request
payloads, or handler source.

## Contract Evolution

`sunder-contracts.owned.json` stores each current candidate and append-only
frozen/issued history with complete canonical descriptor and ABI bytes, hashes,
wire identities, owning package, and publication evidence. Entry state is
exactly `draft`, `release-frozen`, or `published`. The implemented internal model
retains complete `SnapshotJson` and read-only `History`; deterministic writing
reparses every field and never drops or repairs release/publication evidence.

- New local contracts default to `1.0.0`. New prerelease allocation remains
  fail-closed until an explicit policy exists; package-selection prerelease opt-in
  is not contract allocation policy.
- Identical canonical bytes retain the exact version.
- After freeze, ABI-identical annotation changes require at least patch and proven
  compatible ABI changes require at least minor. Automatic acceptance chooses the
  minimum fitting stable version against immutable history, including compatible
  multi-contract batches; a length-overflowing patch/minor may need a higher bump.
- Removal, rename, method-kind/idempotency change, narrowed request, widened
  response/event/error surface, changed enum/discriminator meaning, and
  unclassified changes require major classification. The supported proof set is
  bounded: changed recursive graphs, numeric constraints, exact formats, tagged
  unions, reference transformations, and uncovered generated value APIs remain
  conservative `Major`, even when a change appears compatible.
- A same-version canonical-byte change is forbidden after release freeze.
- Explicit acceptance may replace an unissued draft at its planned version only
  while every immutable historical floor remains satisfied. Breaking or exposure
  changes still need exact acknowledgement even for an unissued draft.
- Exposure changes are conservatively `Major` in either direction outside the
  descriptor classifier. Breaking, unclassified, exposure, and whole-contract
  removal acceptance requires one exact key, exact old/new versions, and
  `SunderAcknowledgeBreakingChange=true`.

Requests prove `previous subset candidate`; responses, events, and error details
prove `candidate subset previous`. Both directions apply to shared definitions.
See the [evolution specification](../../specs/rpc/v1/authoring/evolution/README.md)
for the precise proof, history, diagnostics, and fixture rules.

### Local Lifecycle Commands

These real MSBuild targets run only on the canonical package source project, not
its App, inner-RID, project-export/import, design-time, or cross-targeting build.
They use ordinary non-`AutoGen` `@(Compile)` source, resolved `@(ReferencePath)`,
defines, nullable/output settings, and the existing offline contract lock/cache.
They do not run Registry restore or generate a new lock.
Targets set the generation task's exact `ContractOperation` to `Check`, `Accept`,
or `Freeze`; generation-mode fallback is not a lifecycle operation.

| Target | Accepted lifecycle input |
| --- | --- |
| `CheckSunderContracts` | Complete owned set only. No `SunderContractKey`, old/new version selection, or breaking acknowledgement. |
| `AcceptSunderContracts` | Omit selection properties to accept a complete new/compatible batch, or supply `SunderContractKey` for one contract. Existing-version overrides require both `SunderContractCurrentVersion` and `SunderContractNextVersion`. Breaking/removal also requires `SunderAcknowledgeBreakingChange=true`. |
| `FreezeSunderContracts` | Optional `SunderContractKey`; no old/new version selection or breaking acknowledgement. Package release identity uses `$(Version)`, not `SunderContractNextVersion`. The complete active source set must match even for a targeted freeze. |

`SunderContractKey` is the exact case-sensitive local baseline/source key, not
`SunderContractId`: 1-128 ASCII characters, alphanumeric at both ends, with only
alphanumerics, `.`, `_`, and `-` inside. Version values are exact strict SemVer,
including build metadata for the expected current identity, never ranges or tags.
Omitted/empty key and version properties mean absent;
`SunderAcknowledgeBreakingChange` defaults to `false`.
Overrides/acknowledgement cannot apply to a batch. A genuinely new selected key
may supply only `SunderContractNextVersion` as a stable initial override; there is
no old version to acknowledge. Targeted acceptance requires all other source keys
and snapshots to remain unchanged. Whole-contract removal checks a reviewed next
major but does not manufacture a descriptor at that version; it retains immutable
history, or removes a never-frozen draft with no history.

The exact path properties are `SunderOwnedContractsPath`, `SunderContractLockPath`,
and `SunderContractCacheRoot`. Defaults are root-local
`sunder-contracts.owned.json`, root-local `sunder-contracts.lock.json`, and
`~/.sunder/contracts/v1/sha256`. The baseline must remain a single regular file
below the canonical root with safe ancestors. `SunderPackageId` is optional when
the assembly's `[SunderPackage]` declaration supplies the identity; supplied and
baseline/source identities must agree. First `AcceptSunderContracts` can bootstrap
a missing baseline using an existing valid lock, without an explicit package ID.
It will not bootstrap over an existing receipt/export or when old-version evidence
is expected. Check and Freeze always require the existing baseline.

From a restored canonical package root, the basic operations are:

```sh
dotnet msbuild Example.Package.csproj -t:CheckSunderContracts
dotnet msbuild Example.Package.csproj -t:AcceptSunderContracts
dotnet msbuild Example.Package.csproj -t:AcceptSunderContracts -p:SunderContractKey=greeting -p:SunderContractCurrentVersion=1.0.0 -p:SunderContractNextVersion=2.0.0 -p:SunderAcknowledgeBreakingChange=true
dotnet msbuild Example.Package.csproj -t:FreezeSunderContracts -p:Version=7.6.5
```

Normal Build and Check are read-only with respect to tracked baseline/lock data.
Drift reports `rpc-authoring.baseline-drift`, classification, minimum next version,
affected paths/codes, and the exact accept command. Accept and Freeze reuse the
generation pipeline's in-memory plan and final generated-member validation, but
publish only the complete baseline. They do not rewrite source, fixtures, lock,
RPC output trees, or packages. Accept recompiles at allocated contract versions
before committing; run Build afterward to regenerate RPC outputs.

The baseline writer takes the existing output-lock lease for its path, verifies
expected baseline/source/reference/lock inputs after waiting, stages and strictly
reparses the complete deterministic proposal, then verifies inputs again immediately
before atomic replacement. Failed or stale plans leave the prior baseline and
RPC tree intact; identical proposals do not rewrite the file. No parallel RPC-tree
publisher or recursively discovered source-tree cleanup is introduced.

### Freeze And Pack

Freeze changes accepted drafts to `release-frozen` and records package ID/version,
never Registry publication evidence. Existing frozen/published API tuples remain
unchanged when reused by later package releases, preserving their original evidence
independently of package version. A second clean build/reproduction remains a
release gate, not proof supplied by the Freeze command itself.

Pack re-derives the current source projection against a frozen/published baseline
and complete lock/cache, including when packing prebuilt leaves. It requires exact
complete bundle/provider/runtime-use/import-group agreement. An associated App
must contribute its exact source-policy projection, including optional and narrowed
uses; a Runtime-only artifact contributes no App uses. An App target cannot hide
missing grants by removing its entire use array. Missing or
extra individually valid records still fail. Baseline and source/reference/lock
inputs are rechecked before archive publication. Deleting the baseline or relying
on authoring-mode detection/fallback cannot prove release freeze. Artifact-only
RPC packing is denied.
`SunderPackageUsePrebuiltLeaves=true` requires `BuildProjectReferences=false` for
source checks; prebuilt packing still requires ordinary source and resolved
references, without rebuilding Worker or App inputs.

The temporary descriptor-first migration path requires explicit authored
`SunderContractBundle`, `SunderUsesContract`, and `SunderRpcProvider` inputs as
applicable, with no source-authoring baseline/lock/receipt/export evidence. Extracted
or generated snapshots are not authored descriptors. The same `PackageContractBuilder`
validates these inputs and their complete projection without publishing descriptor
files; there is no new allow flag or inferred freeze evidence. This path is slated
for retirement with Phase 4 templates/tooling and Phase 5 first-party migration,
not a second final authoring mode.

### Import Verified Publication Evidence

After Registry accepts the retained archive, explicitly import its publication
evidence into the package-owned baseline:

```sh
dotnet msbuild Example.Package.csproj -t:ImportSunderContractPublication -p:SunderPackagePublicationPath=/releases/example.sunderpkg -p:SunderRegistryOrigin=https://registry.example/
sunder-package contracts publication import --project . --package /releases/example.sunderpkg --registry https://registry.example/
```

Both commands inspect the canonical archive and verify exact Registry version,
archive hash/length, package content identity, immutable publication identity and
complete public inventory. Package-private tuples are confirmed against the
accepted artifact's bundle evidence rather than an anonymous private-descriptor
route. A successful initially-Yanked publication is sufficient; visibility is a
separate policy decision. HTTPS is the default; local Development requires the
existing explicit localhost option on that invocation.

Only matching `release-frozen` current/history snapshots become `published`.
Descriptor/ABI bytes, contract versions, exposure, frozen release and immutable
history are preserved. Draft current source stays draft; importing a historical
artifact does not resurrect an old current version. A previously published tuple
reused by a later package retains its original publication evidence. An absent,
changed or conflicting publication fails with
`rpc-authoring.publication-evidence-mismatch` and preserves the prior baseline.

The archive and baseline are fenced again at atomic commit; identical imports
preserve bytes and mtimes. This explicit online operation uploads nothing, changes
no contract lock, and does not Accept, Freeze or rebuild source. Ordinary
Build/Rebuild/dev/Pack stay offline. Release automation records the resulting
evidence in a later evidence-only commit without moving the artifact-source tag.

### Core Platform Lifecycle

Core engineering owns the separate platform-authority baseline path
`eng/platform-contracts/sunder-contracts.owned.json`, using the same immutable
history, evidence-preserving snapshots, and compatibility classifier as package
baselines. Its read/write/Check/Accept/Freeze implementation requires public
snapshots and a current `sunder.stack.contributor`; ordinary package-baseline
readers still reject platform authority.

The existing engineering tool exposes `platform-contracts` actions `check`, `accept`,
`freeze`, `payload`, and `import-publication`, each requiring `--core-root <root>`, with
[closed per-action options](../../eng/platform-contracts/README.md).
Initial bootstrap requires the exact key `stack-contributor-rpc-service-adapter`,
`--next-version 2.0.0`, and source matching the fixed shipped migration hashes.
Freeze records a required catalog release, not publication. Payload generation
requires the complete exact source set to match frozen/published current snapshots
and writes unsigned canonical payload bytes only, without overwrite, into an
existing parent outside Core `languages/`.

Source and SDK-only generated transport must reach a two-pass fixed point before
complete baseline validation. SDK builds supply the actual evaluated `@(Compile)`
set, including `AutoGen` inputs except this generator's own output,
`@(ReferencePathWithRefAssemblies)`, defines, nullable settings, configuration, and
target framework. Explicit lifecycle commands retain bounded recursive ordinary
SDK source and curated references in a fixed RID-neutral `net10.0` Release context.
Neither path extracts the descriptor oracle or loads Runtime Host or compiled Stack
assemblies. Transport uses immutable configuration/content-hash output under `obj`,
marker ownership, leases, and atomic no-overwrite publication. Cancellable leases,
input/directory-membership revalidation, and final byte checks also guard baseline
and staged payload writes; there is no recursive orphan cleanup. Normal Build
generates transport but never accepts/freezes a baseline, emits a platform payload,
or signs a catalog. `CheckSunderPlatformContracts` is an explicit
target on the private tool with `DependsOnTargets="Build"`, not a normal-Build hook.

## Contract Authority, Publication, And Runtime Policy

Contract-family authority, contract-version publication, provider-publication
admission, descriptor exposure, and Runtime use/action authorization are
independent, default-deny checks. Passing one check never satisfies another.
The following authority/lifecycle policy is the target architecture, not a claim
that Registry authority records are finished. The implemented shared version gate
is narrower, as noted under Version Publication And Classification.

### Contract-Family Authority And Identity

Registry authority exists for public contract families only. A package-private
contract remains independently versioned in its package's owned-contract
baseline and has no Registry authority or anonymous descriptor route. Each public
contract ID has exactly one `ContractAuthority` with one of these authority
kinds:

- `package`: `authorityId` and the owning package are the canonical package ID;
  only publication of that authority-owning package may introduce a version;
- `platform`: authority is administrator-managed and comes only from a reviewed,
  signed Sunder Core platform-contract release. V1 fixes this authority to
  `sunder.core` and its only family to `sunder.stack.contributor`.

Bundling, consuming, or implementing an existing exact descriptor never
transfers authority. A package cannot create platform authority, and there is no
generic publisher-claimable platform namespace or contract-version delegate.

The default package-owned contract ID remains
`<canonical-package-id>.<service-contract-suffix>`. For a public contract in this
derived namespace, the authenticated owner of an already claimed package ID, or
the owner of its active package-ID reservation during first publication, may
atomically create and claim an otherwise unreserved authority. An explicit public
ID outside that namespace requires an earlier authenticated reservation naming
the owning package. Neither path permits a provider or consumer package to claim
the family.

Package-ID reservations and public contract-authority reservations have exactly
the states `Reserved`, `Claimed`, `Cancelled`, and `Expired`:

| Reservation | Normal active quota | Unclaimed lease | Claim event |
| --- | ---: | --- | --- |
| Package ID | 16 per issuer-qualified subject | 30 days | First package publication consumes the caller-owned reservation atomically. |
| Explicit contract ID | 64 per issuer-qualified subject | 30 days | First matching owner-package publication stores the immutable descriptor version and claims the authority atomically. |

Only the reservation owner may renew or cancel an unclaimed lease. Renewal grants
another 30 days. Expiry and cancellation are audited and make the ID atomically
reclaimable; they never affect a claim. Claims are permanent: they do not expire,
transfer, return to `Reserved`, or become reusable. Exact administrator-created
first-party bootstrap reservations are the only non-expiring reservations, and
rate limits remain independent of these quotas.

An existing package owner or the owner of its active package-ID reservation may
reserve a public contract ID for that package. A contract reservation bound to an
unclaimed package lease cannot outlive it: package cancellation or expiry
cancels or expires the dependent contract reservations atomically, and contract
renewal requires the package ownership or lease still to be active. Upload order
never creates package or contract ownership, first package publication has no
implicit-reservation fallback, and exact conflicts write nothing.

Configured protected package prefixes and protected contract prefixes are
independent authorization sets. Both protect `sunder.*` plus their configured
prefixes, and both apply to explicit reservations and implicit in-namespace
contract claims. Owning an unrelated package does not authorize a protected
contract claim. Only audited administrator/bootstrap paths may reserve or claim
either protected namespace.

### Registry-Authoritative Platform Catalog

The user's explicitly selected Registry establishes publication authority for Core
platform APIs. The default official origin is `https://hub.sunderapp.io/`; configured
company Registries use the same model. No independent Core catalog signature,
signing key, public trust bundle or trust-generation field is required.

The closed `platform-catalog.schema.json` contains `schemaVersion`, `authorityKind`,
`authorityId`, `releaseVersion`, `contractCount`, and `contracts`, in that order.
Authority is `platform` / `sunder.core`; a stable release contains the complete
ordered descriptor inventory, including the mandatory `sunder.stack.contributor`.
Each record reproduces canonical descriptor text, byte count, identity and both hashes.

C# `Create`/`Verify` and independent Node validation enforce strict canonical UTF-8,
a 64 MiB raw limit, depth 16 and a 262,144-value/property-name preflight budget.
The complete compact catalog has no terminal LF; its SHA-256 content identity is
external. Validated immutable snapshots compare exact release/content/descriptor pins.

Core Check/Accept/Freeze/Export derives catalog content from reviewed source and
immutable history. Registry administrator import records exact catalog content,
authorization and complete inventory atomically. Local selection accepts the exported
canonical catalog with no Registry origin. Published selection records the chosen origin;
restore uses exact pins, and normal generation/Build/Pack remains offline.
Registry revocation and historical recovery retain their existing lifecycle boundaries.
See the [catalog specification](../../specs/rpc/v1/authoring/platform-catalog/README.md)
and [Core commands](../../eng/platform-contracts/README.md).

### Version Publication And Classification

Normal package publication requires authenticated owner or maintainer permission
for the package being published. Introducing a contract version additionally
requires that package to be the package-authority owner. Imported consumer/
provider packages cannot publish or advance that family. Platform versions enter
only through the administrator-imported Registry catalog. Registry does not
trust an archive's owned baseline or claimed change class.

The Contract Evolution rules above are also publication admission rules.
Classification is directional: a new request schema must accept every previously
valid request, and each new response or event must remain valid under the prior
consumer schema. Identical canonical bytes retain the exact version;
ABI-identical annotation changes require at least patch; proven backward-
compatible additions require at least minor; and removal or rename, method-kind
or idempotency change, a newly declared error on an existing method, narrowed
request, widened response/event, changed enum or discriminator meaning, and every
unclassified change require major. An exposure change requires explicit baseline
acceptance and a new version after release freeze; it never relabels frozen
history.

`Sunder.Package.Format` implements the single shared internal
`SunderRpcContractCompatibility` classifier used by C# package tooling and Registry.
Reuse of an existing version requires exact
canonical bytes and hashes. A genuinely new version must have greater SemVer
precedence than the authority's highest version and satisfy the strictest minimum
bump against that version and every published version in the candidate's major.
Each minimum is anchored to its own historical version, not the strictest change
class rebased repeatedly on the highest version. Exact immutable reuse is checked
before new-allocation rules, including old-major and prerelease tuples.
A major version may start a new compatibility line; lower or backfilled versions
are rejected. Failure rejects the complete package or catalog transaction rather
than rewriting an immutable tuple.

Registry now runs this gate in preflight before storage intents and again under
family and descriptor-tuple locks in the publication transaction, validating bounded
stored canonical history and allocations earlier in the same transaction. It does
not derive authority from `FirstPublisherPackageId` or trust archive baselines.
With no public history, an otherwise admissible initial public version other than
exact `1.0.0` currently fails with `contract-version.history-unavailable`. A version
above `1.0.0` may be a legitimate private-to-public promotion or migration; do not
renumber its frozen history. The authoritative-history/publication-evidence adapter,
Registry authority/owner policy, and explicit prerelease allocation policy remain
unfinished. New prerelease allocation stays fail-closed.

### Immutable Lifecycle

Claimed package identities, accepted package versions and artifacts, claimed
contract authorities, descriptor versions, and platform releases have no hard-
delete or cascade-release path. Restrictive references preserve their evidence.
Cancelling or expiring an unclaimed reservation is the only identity-release
path, and neither a block nor a yank releases package or contract authority.

A package identity is `Active` or terminal `SecurityBlocked`; a package version
is `Listed`, `Yanked`, or terminal `SecurityBlocked`. An owner may auditably yank
or unyank a version. `Yanked` excludes ordinary range selection but may remain
available for exact restore/repair and hash-bound dependent-publication
validation where policy allows; yanking alone does not stop an existing exact
binding. Publication is `Listed` by default or explicitly initially `Yanked`; a
publication whose admission graph uses a yanked version must also enter as
`Yanked`, while a `SecurityBlocked` reference always rejects. An administrator
may irreversibly security-block a version or identity. A version block denies
ordinary download, restore, publication reference, and new install/update/repair,
and causes Hosts to stop the known active version, while an active identity may
publish a new higher immutable version. An identity block covers every version
and all future publication under that ID; recovery requires a newly reserved
package ID and explicit user migration. Neither block rewrites or deletes
descriptor evidence.

Registry selection lifecycle uses one monotonic `PackagePolicyRevision` and
fingerprint; package-version publication, dist-tag/deprecation/yank/block,
package-identity block, and platform-catalog import/revocation each advance it
once per transaction. Reservation/claim, initial owner claim, maintainer
membership, and provider-publication policy changes use the separate monotonic
`PublicationAuthorityPolicyRevision` and fingerprint. A first package
publication that claims its reservations advances both once in the same
transaction. Execution-runtime lifecycle remains separately revisioned.

### Separate Admission And Invocation Policies

Public exposure grants anonymous, bounded discovery of immutable descriptor
metadata and canonical bytes through the exact public inventory/content routes.
Package exposure keeps the descriptor within the owning package boundary. Neither
exposure grants family ownership, version-publication permission, provider
admission, or invocation authority.

Each package authority separately sets provider publication to `open`,
`authority-package-only`, or an exact allowlist of at most 256 unique canonical
package IDs in ordinal order. Admission checks authority-package ownership or an
importing provider package's exact lock and provenance, as applicable. Package
policy and allowlist changes are owner-authorized; platform policy is
administrator-authorized. Every change is revisioned and audited, does not mutate
descriptors, retroactively accept a rejected publication, or reclassify an
accepted immutable package version, and applies only to later publication
attempts. Later security action uses package lifecycle policy. The V1 platform
family is pre-seeded with provider publication `open`.

Generated clients, provider bindings, and availability are not ambient Runtime
authority. Associated App source policy and reviewed external locks generate
exact role-scoped uses whose actions are the applicable ordered subset of
`discover`, `invoke`, and `subscribe`. `required=true` adds only an any-authorized-
provider readiness predicate; it does not select an endpoint or broaden actions.
Before discovery or dispatch, Runtime still verifies the authenticated caller;
for an imported public contract, validated lock-derived provenance and applicable
dependency policy; and the exact use/action, provider manifest and audience,
contract version, descriptor and ABI hashes, service/method,
provider and endpoint generation, both current activations, and current package/
platform policy.

### Bootstrap, Readiness, And Catalog Revocation

The fresh V1 Registry baseline seeds only the exact reserved
`sunder.stack.contributor` platform authority under `sunder.core`, with provider
publication `open`. Before any Agent publication, an administrator bootstrap must
atomically reserve all 15 frozen Agent package IDs to the configured issuer-
qualified first-party release owner and all 19 reviewed Agent public contract
IDs and provider policies to owning package `sunder.package.agent`. The four
families `sunder.agent.runtime.catalog`, `sunder.agent.run.control`,
`sunder.agent.workspace.execution.resolver`, and
`sunder.agent.child.run.executor` are `authority-package-only`;
`sunder.agent.tool.execution.observed` is allowlisted only to
`sunder.package.agent.mcp`; the other fourteen are `open`.

Production deployment carries the non-secret hash of that exact inventory. After
schema migration and before the public process rollout, the one-shot
`RegistryAuthorityBootstrapCommand` builds the normal Registry service provider
without Kestrel, synchronizes authorization and execution-runtime trust configuration,
validates/imports the canonical Registry catalog, and
applies the complete package/authority owner/policy inventory. It is idempotent
only for an exact catalog/inventory/owner replay and commits nothing on missing,
extra, or conflicting state. Production has no HTTP or readiness bypass.

Registry startup and public readiness remain separate:

- `startup` health covers configuration, telemetry, database, and storage;
  failure is process-fatal. Authorization and trust rollback/conflict detected by
  the startup synchronizers is also fatal before endpoint service.
- `public-ready` additionally proves the configured catalog/authority inventory
  and ownership hash, current authorization configuration, and external policy-
  checkpoint alignment. `/health/ready` evaluates both tags, and a
  fail-closed route gate denies public browse, resolve, download, and publish
  traffic until both pass.
- liveness and authenticated internal diagnosis/status/recovery remain reachable
  while a process is not public-ready, including before bootstrap, so recovery
  never depends on routing public traffic to an unready pod.

Registry persists each package version's exact platform-release dependency and
inverse lookup. Catalog revocation never rewrites package, descriptor, or catalog
bytes: it excludes the release and every dependent version from new lock
resolution and affected restore/publish/install/update/repair operations,
advances the global package-policy revision/fingerprint once, and remains visible
through public status and Host policy synchronization. Revocation after initial
bootstrap therefore does not make the process unready.

`Sunder.Runtime.Host` durably synchronizes complete active package/platform/
runtime policy every five minutes with at most 30 seconds jitter and a 15-second
request deadline. It accepts only authenticated, nonce-bound, anti-rollback
snapshots; a successful response grants the fixed 24-hour local lease from the
earlier Host request-start tick on a suspend-aware continuous clock, and failure
cannot renew it. Once revocation is known, or that lease expires, Runtime
withdraws affected endpoints/capabilities before further dispatch and stops
supervised Workers and loaded App processes. Offline restart is allowed only
while the last-known catalog release remains supported and the lease is provably
valid. Recovery from a revoked catalog requires a new immutable Core
catalog release and new dependent package versions, not changed old locks or
descriptors.

### Implementation Ownership

This section freezes policy; it does not move implementation into author-facing
SDKs or generated descriptors. Future phases keep these owners:

- Phase 1 in `repos/sunder-core`: `Sunder.Package.Format` owns canonical
  compatibility classification and catalog content validation;
  the normative RPC authoring schemas remain under `specs/rpc/v1/authoring`.
  Existing `tools/Sunder.ExecutionRuntime.Build` owns the migration-input check,
  source-based `eng/platform-contracts` baseline lifecycle/catalog export, and
  Registry publication-evidence import.
  Production release composition remains pending. `Sunder.Registry.Contracts`
  owns dependency-free DTOs and routes.
- Phase 2 in `repos/sunder-registry`: `Sunder.Registry.Infrastructure` owns
  reservation, claim, publication, inventory, admission, lifecycle, policy, and
  revocation persistence; `Sunder.Registry.Server` owns composition, non-HTTP
  bootstrap, health/readiness gates, and routes; Registry Web exposes only the
  corresponding audited management operations.
- Phase 3 in `repos/sunder-core`: `Sunder.Runtime.Host` owns durable policy
  synchronization, anti-rollback/lease enforcement, endpoint withdrawal,
  process supervision, readiness evaluation, and exact use/action authorization.
- Phase 4 in `repos/sunder-core`: `Sunder.Package.Build` and
  `@sunder/package-tool` own baseline commands, exact lock/restore behavior,
  independent catalog verification, and generated provider/use records. They
  consume Registry decisions but never become contract authority.
- Phase 5 in `repos/sunder-agent-package` consumes the pre-established bootstrap
  inventory and migrates first-party packages; it cannot define a second
  authority, admission, exposure, or Runtime policy.

## Rejected Alternatives

- Authored descriptor JSON remains a compatibility oracle, not final authoring.
- A shared Protocol project/package is not retained or renamed.
- Build does not scan call sites, execute Node authoring source, use runtime
  reflection discovery, or infer package imports from language dependencies.
- Generated code never references another package's Worker or generated CLR
  assembly.
- Build never resolves mutable Registry state or silently updates a lock.
- Collisions never receive numeric/hash suffixes.
- Platform contracts are not package-authored consumer imports in V1.

## Delivery Gate

This ADR freezes the design but does not claim implementation completion. The
current Workstream A change set includes the keyword fixture, closed schemas,
equivalent C#/TypeScript/JavaScript source fixtures, exact canonical
descriptor/ABI outputs, generated provider/use inventories, Core and Agent
migration baselines, and the Agent feed audit. Workstream A closes only after
that evidence is reviewed, checked in, and passes its gates. Descriptor-first source remains only until the
coordinated compiler, lock, manifest, templates, and Agent migration cutover is
green.

The integrated C#/Node generation, source-derived baseline/Pack, Registry-backed
selection and exact offline restore, catalog publication/revocation, and Host
import/lifecycle paths have owning implementation and test coverage. The catalog
authority model requires no signing setup. These local checks do not establish a
deployed Registry publication, final canonical first-party migration or a release.

Remaining program work includes actual package-root conversion and obsolete
App/Protocol/helper retirement, final developer/product flows, measured startup/size
acceptance, clean-source/native qualification and durable release evidence. Historical
checkpoints document their original scopes rather than qualifying those final gates.
