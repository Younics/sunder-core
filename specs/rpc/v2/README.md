# Sunder RPC V2

Status: normative and deployable. Package manifest V3 binds deployable descriptor version 2 contracts. Explicit descriptor V1 parsing and generation remain available only as non-manifest compatibility utilities.

The key words MUST, MUST NOT, REQUIRED, SHALL, SHALL NOT, SHOULD, SHOULD NOT, and MAY are to be interpreted as described by RFC 2119 and RFC 8174 when they appear in uppercase.

## Scope

RPC V2 defines:

- a language-neutral contract descriptor with `descriptorVersion` equal to `2`;
- the restricted JSON Schema profile used for request, response, and event values;
- exact descriptor and ABI identities;
- method error, idempotency, and server-stream semantics; and
- the RPC fields that a Manifest V3 package must bind for contract exposure, uses, and providers.

Transport framing, package archive format, registry resolution, user-consent UI, and language-specific generated APIs are outside this slice. A transport claiming RPC V2 conformance MUST preserve the semantics in this document.

## V2 Delta Boundary

V2 retains V1's descriptor limits and unary/server-stream shapes while selecting the normative `sunder.rpc.values.v1` value profile. The following rules are V2-only:

- one descriptor representation: keyed services, keyed methods, keyed errors, and closed object references;
- root error declarations and method `errors` and `idempotency` members;
- asserted string and number formats, exact string numerics, bounded JSON escape hatches, base64 bytes, and the exact `content-reference` logical type;
- finite binary32/binary64 runtime `number` semantics and safe runtime `integer` semantics;
- controlled productive local recursion;
- the annotation-stripped, domain-separated ABI hash in addition to the full descriptor hash;
- exact role-scoped use locks, `package`/`public` exposure, `owner`/`public` audience, and ongoing required/optional readiness; and
- the precise ASCII-only case-collision fold used by V2.

Descriptor V1 retains its implemented parser meaning. A V1 reader MUST reject, not reinterpret, V2-only fields; canonical Manifest V3 packages bind descriptor V2 only.

## Descriptor

A descriptor is one strict UTF-8 JSON object. It MUST be no larger than 4 MiB, MUST NOT begin with a byte-order mark, and MUST have no duplicate or ASCII-case-colliding object member names. ASCII case folding means replacing `A` through `Z` with `a` through `z`; non-ASCII scalar values are not folded. JSON strings MUST contain Unicode scalar values and MUST NOT contain unpaired UTF-16 surrogates.

The root is closed and has these members:

| Member | Required | Rule |
| --- | --- | --- |
| `$schema` | No | If present, exactly `https://json-schema.org/draft/2020-12/schema`. |
| `descriptorVersion` | Yes | Integer `2`. |
| `schemaProfile` | Yes | Exactly `sunder.rpc.values.v1`. |
| `contractId` | Yes | Lowercase dot-separated ASCII identifier, 1 through 128 characters. Every segment is non-empty and contains only `a-z` and `0-9`. |
| `version` | Yes | Strict SemVer 2.0, at most 256 characters. Build metadata is part of exact version identity. |
| `errors` | Yes | Object containing zero through 256 domain-error declarations keyed by error code. |
| `services` | Yes | Object containing one through 64 services keyed by service ID. |
| `$defs` | Yes | One through 256 named schemas. Every definition is validated, including unreachable definitions. |

Unknown root members are invalid. Definition names are 1 through 128 ASCII letters, digits, `.`, `_`, or `-`. Service IDs, method IDs, and error codes are 1 through 128 lowercase ASCII characters, begin and end with a letter or digit, and otherwise contain only letters, digits, `.`, `_`, or `-`. IDs are case-sensitive and stable once published.

`services` MUST be keyed by `serviceId`, and each service's required `methods` object MUST be keyed by `methodId`. A service value contains only `methods`. A method value does not repeat its key. List forms and repeated `serviceId` or `methodId` members are invalid. Each methods object is non-empty, and a descriptor has at most 512 methods in total.

`errors` MUST be keyed by error code. Each value is a closed object with optional `description` and `detailsSchema`; `{}` is valid. `description` is an annotation of at most 2048 Unicode scalar values. `detailsSchema`, when present, is an exact closed local definition reference. It declares the optional machine-readable detail value carried by that domain error and is ABI material. Infrastructure-error detail semantics are outside the descriptor.

A method is a closed object with:

| Member | Rule |
| --- | --- |
| `kind` | Exactly `unary` or `server-stream`. |
| `idempotency` | Exactly `none` or `idempotent`. |
| `errors` | Required list of distinct codes declared by the descriptor; it may be empty. |
| `requestSchema` | Required local definition reference. |
| `responseSchema` | Required only for `unary`. |
| `eventSchema` | Required only for `server-stream`. |

A method schema reference MUST be the closed object `{ "$ref": "#/$defs/<name>" }`, with no sibling members. A schema reference inside `$defs` uses the same exact form. String references and `$ref` siblings are invalid. The target MUST exist.

## Restricted Schema Profile

Every V2 root selects `sunder.rpc.values.v1`. Schemas use Draft 2020-12 vocabulary only through the closed forms below. This is not unrestricted JSON Schema. Unknown keywords, remote or dynamic references, open record forms, ambiguous unions, and unsupported formats are invalid.

Every schema object MUST be exactly one form. Non-reference forms MAY carry `title` and `description` annotations of at most 2048 Unicode scalar values; a reference has no siblings.

| Form | Required members | Other allowed members |
| --- | --- | --- |
| Reference | Only `$ref` to an existing `#/$defs/<name>` | None |
| Union | `oneOf` with 2 through 16 schemas | Annotations |
| JSON | No structural member: `{}` after annotations are removed | Annotations |
| JSONObject | `type: "object"` and no record/map members | `maxProperties`, annotations |
| Closed record | `type: "object"`, `properties`, `required`, `additionalProperties: false` | `minProperties`, `maxProperties`, annotations |
| Map | `type: "object"`, schema-valued `additionalProperties`, ordinary-string `propertyNames` | Empty `properties`, empty `required`, `minProperties`, `maxProperties`, annotations |
| Array | `type: "array"`, `items` | `minItems`, `maxItems`, annotations |
| Ordinary string | `type: "string"` | `minLength`, `maxLength`, `enum` or `const`, annotations |
| Formatted string | `type: "string"`, one supported `format` | Optional format-specific limits and exact-value bounds, annotations |
| Base64 bytes | `type: "string"`, `contentEncoding: "base64"` | `maxDecodedBytes`, annotations |
| Safe integer | `type: "integer"` | Numeric bounds, `enum` or `const`, annotations |
| Floating point | `type: "number"`, `format: "float32"` or `format: "float64"` | Numeric bounds, `enum` or `const`, annotations |
| Boolean or null | `type` | `enum` or `const`, annotations |
| Content reference | `type: "object"`, `logicalType: "content-reference"` | Annotations |

`enum` and `const` are mutually exclusive. An enum contains 1 through 256 semantically unique primitive values compatible with its declared type. Inclusive and exclusive bounds on the same side are mutually exclusive. All present lower bounds MUST be no greater than corresponding upper bounds. Length, collection, exact-digit, precision, scale, decoded-byte, and dynamic-property limits are optional safe integers from 0 through 9007199254740991, except that a supplied `maxDigits` or `maxPrecision` is at least 1. An omitted minimum is zero and an omitted maximum is unbounded. A closed record has at most 256 declared properties independently of its instance property-count facets; closedness still prevents undeclared instance members. The descriptor has a maximum non-repeating resolved schema depth of 64 and at most 4096 physical schema nodes.

Record property names are non-empty, at most 128 scalar values, and MUST NOT duplicate or ASCII-case-collide in the descriptor. `required` contains unique declared names. A map MUST NOT mix named properties with map entries. A closed empty record is the ordinary unit value and validates only `{}`; JSONObject is a distinct dynamic object form.

JSON validates any JSON value and JSONObject validates any JSON object. Neither form, nor any omitted quantitative facet, imposes an implicit string-length, collection-size, node-count, or nesting-depth ceiling. Every number reached through JSON or JSONObject MUST still decode to a finite binary64 value, every string and object member name MUST contain Unicode scalar values, and duplicate object member names are invalid. Transport and Host resource policy MAY reject or externalize a value independently of schema validity, but those limits are not semantics of `sunder.rpc.values.v1`.

A `oneOf` is valid only when it is one of these forms:

- A nullable union has exactly two variants, exactly one of which resolves to `{ "type": "null" }`, and exactly one branch accepts null.
- A tagged union has only variants that resolve to closed records and exactly one common required property whose inline schema has `type: "string"` and a distinct string `const` in every resolved variant. That property is the discriminator.

References MUST be exact local references resolving through `$defs`. Recursion is permitted only when every reference cycle crosses at least one productive boundary: an optional closed-record property, an array whose omitted or explicit `minItems` is zero, or a map whose omitted or explicit `minProperties` is zero. Removing productive edges from the definition-reference graph MUST leave it acyclic. Thus a required self-property, a direct alias cycle, an array with a positive minimum containing itself, and a map with a positive minimum containing itself are invalid. Validation, resolution for union discrimination, and hashing MUST terminate for every valid recursive descriptor. Instance validation MUST NOT impose the descriptor's schema-depth or node-count limits on runtime values.

Keywords not listed for a form, including `pattern`, `default`, `anyOf`, `allOf`, `not`, `if`, `then`, `else`, `unevaluatedProperties`, and tuple-array keywords, are outside the descriptor value profile. [`descriptor.schema.json`](descriptor.schema.json) checks the machine-readable structural subset. Checks requiring raw tokens, format algorithms, exact arithmetic, or whole-document graph analysis remain normative prose.

## Asserted Formats

`format` is an assertion, not an annotation. It is allowed only on the exact string and number forms described here.

- `uuid` is lowercase RFC 9562 hyphenated text `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`; braces, uppercase, compact forms, and whitespace are invalid.
- `date` is a valid proleptic-Gregorian date from year 0001 through 9999 in `YYYY-MM-DD` form.
- `date-time` has `YYYY-MM-DDTHH:mm:ss[.fraction](Z|+HH:MM|-HH:MM)` form and DateTimeOffset semantics. `T` and `Z` are uppercase, the offset is no greater than 14:00, seconds are `00` through `59`, leap seconds are invalid, and a present fraction has 1 through 9 digits. An offset is mandatory.
- `duration` is canonical non-negative ISO 8601 `P[nY][nM][nD][T[nH][nM][n[.fraction]S]]` text with at least one component. Components have no leading zero unless zero, and a fractional second has no trailing zero. Week, sign, and fractional non-second forms are invalid.
- `uri` is an absolute RFC 3986-style URI with an ASCII scheme, no controls, spaces, DEL, or reverse solidus, and only complete hexadecimal percent escapes.
- `big-integer` is a canonical exact integer string and MAY carry `maxDigits`.
- `decimal` is a canonical exact decimal string and MAY carry `maxPrecision` and `maxScale` independently.
- `float32` and `float64` apply only to `type: "number"` and select IEEE-754 binary32 or binary64 decoding respectively. Bare `type: "number"` is invalid.

A canonical big integer is `0` or `-?[1-9][0-9]*`. Its digit count excludes the sign. A canonical decimal is an optional `-`, an integer part that is `0` or has no leading zero, and an optional fraction whose last digit is nonzero. `-0`, exponent notation, a plus sign, leading integer zeroes, a bare decimal point, and trailing fractional zeroes are invalid. Decimal precision is the count of coefficient digits after leading zeroes are removed, with zero having precision 1; scale is the fraction length.

Big-integer and decimal schemas MAY carry at most one of `valueMinimum` and `valueExclusiveMinimum`, and at most one of `valueMaximum` and `valueExclusiveMaximum`. Every bound is a canonical string fitting every digit, precision, or scale limit that is present; an omitted limit is unbounded. Values and bounds are ordered as mathematical integers or decimals using exact arithmetic, never binary floating point or locale rules.

Base64 values use the standard RFC 4648 alphabet, required canonical padding, no whitespace, and zero unused trailing bits. When present, `maxDecodedBytes` bounds decoded length; encoded length is not the resource measure.

`logicalType` is reserved in this profile except for the exact content-reference schema:

```json
{ "type": "object", "logicalType": "content-reference" }
```

It does not also declare properties, bounds, `enum`, `const`, `format`, or another logical type. A `content-reference` has this exact JSON value shape:

```json
{
  "id": "host-issued opaque value",
  "length": 0,
  "sha256": "64 lowercase hexadecimal characters",
  "mediaType": "application/octet-stream",
  "fileName": "display-name.bin",
  "expiresAtUtc": "2030-01-02T03:04:05Z",
  "repeatability": "single-use"
}
```

All seven members are required and no others are allowed. `id` is non-empty and at most 256 scalar values. `length` is a safe integer from 0 through 9007199254740991. `mediaType` is non-empty and at most 256 ASCII characters. `fileName` is display-only, has no path semantics, and is at most 1024 scalar values. `expiresAtUtc` retains canonical UTC `YYYY-MM-DDTHH:mm:ss[.fraction]Z`, where a present 1 through 9 digit nonzero fraction does not end in zero. `repeatability` is `single-use` or `repeatable`.

A content reference is Host-issued capability metadata, not self-authenticating JSON. The Host MUST bind it to its owner, recipient audience, exact provider activation, invocation, Runtime generation, expiry, digest, and remaining-use policy. A receiver MUST NOT gain authority by constructing or modifying the object. Host policy MAY impose tighter byte, lifetime, and use limits.

## Values And Presence

Every descriptor JSON number MUST decode to a finite IEEE-754 binary64 value. Fractions and exponents are allowed where a number-valued profile keyword permits them. Integer-valued keywords, safe-integer bounds, and integer `enum` or `const` values MUST remain mathematical integers in the inclusive range -9007199254740991 through 9007199254740991. Infinity is invalid even when produced only by conversion of a syntactically valid JSON token.

Runtime values have these cross-language semantics:

- `integer` accepts only mathematical integers in the safe-integer range. It is not an arbitrary 64-bit integer type.
- `number` requires `float32` or `float64`. Incoming decimal JSON is decoded to the selected IEEE-754 format before bounds, `enum`, and `const` comparison. The decoded value MUST be finite.
- Negative zero compares equal to zero for bounds, `enum`, and `const`. Encoders SHOULD emit it as `0`.
- Implementations MUST NOT silently use decimal, binary64-for-binary32, or arbitrary-precision behavior that changes selected-format validation or equality.

Canonical descriptor restrictions do not require incoming request, response, or event JSON to use canonical number text.

For a closed record, membership in `required` controls presence only:

- Required and non-nullable means the member must be present with a non-null value.
- Optional and non-nullable means the member may be absent but, when present, cannot be null.
- Required and nullable means the member must be present and may contain null.
- Optional and nullable has three distinct states: absent, present null, and present non-null.

Nullability MUST use the two-branch nullable `oneOf`; `type` arrays are invalid. Generators and adapters MUST preserve all three optional-nullable states and MUST NOT treat missing as null. V2 defines no implicit defaults.

## Calls, Errors, And Idempotency

Every request is validated before provider dispatch. Every unary response and every stream event is validated before delivery. Validation failure is never a domain result.

An RPC error envelope contains `kind`, `code`, sanitized `message`, `dispatchState`, and `retrySafety`. `code` follows the error-code grammar above. `message` is 1 through 512 scalar values after controls and line breaks are removed or replaced. V2 error kinds are:

`domain`, `permission-denied`, `not-found`, `stale-endpoint`, `validation`, `deadline-exceeded`, `cancelled`, `resource-exhausted`, `unavailable`, `provider-faulted`, and `protocol`.

A provider may emit only `domain`, and only with a code listed by that method. A declared domain error MAY carry a detail value validated against its declaration's `detailsSchema`; without that member it carries no detail value. All other kinds are Host-authenticated infrastructure failures, and infrastructure detail semantics remain outside the descriptor. A forged infrastructure kind, undeclared domain code, missing required validation, or mismatched detail value is a protocol violation. Messages are diagnostic, are not stable ABI, and MUST NOT contain secrets or private exception details.

The Host stamps `dispatchState` as `not-dispatched` only when the provider handler was not entered, and as `possibly-dispatched` once handler entry may have occurred. It stamps `retrySafety` as `safe` for a non-dispatched failure and, after possible dispatch, only for an `idempotent` unary method. A dispatched server stream is always `unsafe` because establishment idempotency does not make delivered events replayable. These fields describe duplicate-effect safety, not whether another attempt is likely to succeed.

`idempotency: "none"` means repeating a dispatched call may repeat effects. `idempotency: "idempotent"` means that, for the same authenticated caller, exact provider, method, and semantically equal request, any number of completed invocations has the same externally visible effect as one invocation. It does not require byte-identical responses.

The Host MUST NOT transparently retry a call after possible provider dispatch. A caller may retry only when method idempotency and the observed failure make that safe. A contract requiring deduplication across crashes or changing responses MUST carry an explicit idempotency key in its request and define key scope and retention as domain semantics. RPC V2 adds no hidden idempotency key; `format: "uuid"` is the recommended key representation.

## Server Streams

`unary` produces exactly one response or one terminal error. `server-stream` produces zero or more ordered events followed by exactly one terminal completion, or it terminates with one error. Completion has no response payload. No event may follow a terminal outcome.

Each subscription is independent. Cancellation, deadline expiry, provider retirement, permission revocation, or content-authority expiry terminates the subscription. EOF without an authenticated terminal outcome, a malformed event, and a trailing partial transport record are protocol failures. Hosts MUST use bounded queues and backpressure and MAY enforce documented event, duration, and byte limits.

RPC V2 provides no implicit reconnect, replay, cursor, or resume behavior. Method idempotency applies only to establishment-side effects and does not make event delivery replayable. A resumable stream MUST model its cursor and replay rules in request and event schemas. Invocation-scoped content authority remains valid through the terminal outcome and is then revoked.

## Canonical Bytes And Hashes

Source whitespace and object-member order are not significant. Source array order is significant to the complete descriptor artifact. After normative validation, canonical descriptor bytes are produced as follows:

1. Serialize UTF-8 with no BOM, indentation, trailing newline, or insignificant whitespace.
2. Sort object member names by lexicographic UTF-16 code-unit order as required by RFC 8785. Preserve array order.
3. Serialize strings with RFC 8785 JSON escaping: escape quotation mark, reverse solidus, and controls; use the short escapes for backspace, tab, newline, form feed, and carriage return; use lowercase `\u00xx` for other controls; and emit other scalar values as UTF-8.
4. Serialize descriptor numbers with the RFC 8785 finite binary64 number algorithm: shortest round-tripping decimal, lowercase `e`, no redundant exponent zeroes, and `0` for negative zero. Serialize literals as `true`, `false`, or `null`.

`descriptorSha256` identifies the complete descriptor artifact:

```text
descriptorSha256 = lowercase-hex(SHA-256(canonicalDescriptorBytes))
```

It includes `$schema` when present, contract `version`, annotations, all definitions, all ABI material, and every source array in source order. Formatting and object-member reordering do not change it, but reordering any source array changes it.

`abiSha256` identifies the V2 structural ABI, not the source artifact. Construct the ABI projection from the validated descriptor model:

1. Remove root `$schema` and `version`. Retain `schemaProfile`.
2. Remove `description` from each value in the root `errors` object.
3. Remove `title` and `description` only from recognized non-reference schema objects at every depth. Do not remove record property names that happen to be `title` or `description`.
4. Sort every method `errors` list by error code using lexicographic UTF-16 code-unit order.
5. Sort every closed-record `required` list by property name using lexicographic UTF-16 code-unit order. An optional empty `required` list on a map is unchanged.
6. Sort every primitive `enum` list by the unsigned lexicographic order of each value's canonical JSON UTF-8 bytes.
7. Normalize each valid nullable `oneOf` by placing the variant that resolves to `null` first and the non-null variant second.
8. Normalize each valid tagged `oneOf` by sorting its original variants by the lexicographic UTF-16 code-unit order of their resolved string discriminator value. Keep each projected variant's own reference or inline representation; resolution is only used to obtain the sort key.
9. Preserve every other value and every other array in source order, then canonicalize the projection with the same canonical JSON algorithm.

Only the arrays in steps 4 through 8 are set-like for ABI hashing. No other descriptor array is sorted. Runtime array values, stream event order, and any other order-bearing values are never reordered by descriptor or ABI processing.

Then prepend this exact 18-byte ASCII domain separator, including its final NUL byte:

```text
text: sunder.rpc.abi.v2\0
hex:  73756e6465722e7270632e6162692e763200
```

```text
abiSha256 = lowercase-hex(
  SHA-256(domainSeparatorBytes || canonicalAbiProjectionBytes)
)
```

The contract version is excluded so unchanged ABI can be recognized across releases. Any non-annotation ABI projection change MUST change the contract version, and breaking changes MUST follow SemVer major-version rules. An ABI hash is an exact structural fingerprint, not proof that two different hashes are incompatible. Reordering one of the explicitly set-like arrays changes `descriptorSha256` but not `abiSha256`; reordering any other array changes both.

## Package Binding And Visibility

This section defines the RPC portion of package manifest V3. The current manifest accepts only this canonical binding shape; legacy hash and range aliases are rejected.

A Manifest V3 RPC binding is schematically:

```json
{
  "contractBundles": [
    {
      "contractId": "example.rpc.v2",
      "version": "2.0.0",
      "descriptorPath": "contracts/example.rpc.json",
      "descriptorSha256": "<64 lowercase hex>",
      "abiSha256": "<64 lowercase hex>",
      "exposure": "public"
    }
  ],
  "usesContracts": [
    {
      "role": "runtime",
      "contractId": "example.rpc.v2",
      "contractVersion": "2.0.0",
      "abiSha256": "<64 lowercase hex>",
      "required": true,
      "actions": ["discover", "invoke", "subscribe"]
    }
  ],
  "provides": [
    {
      "providerId": "example.rpc.default",
      "role": "runtime",
      "contractId": "example.rpc.v2",
      "contractVersion": "2.0.0",
      "descriptorSha256": "<64 lowercase hex>",
      "abiSha256": "<64 lowercase hex>",
      "audience": "public"
    }
  ]
}
```

Every used or provided contract MUST have one matching local bundle. A bundle is uniquely keyed by exact `(contractId, version)` and binds both hashes to its descriptor bytes. Hash fields are lowercase 64-character hexadecimal.

`exposure` is contract distribution policy:

- `package` permits only App and Runtime roles owned by the bundle's package to bind the contract. It is not advertised as an importable Registry contract.
- `public` permits other packages to bundle and bind that exact contract version and ABI. It grants no invocation permission by itself.

`audience` is provider visibility policy:

- `owner` permits callers only when the Host-stamped caller package ID equals the provider owner package ID.
- `public` permits other package IDs subject to their exact use declaration and effective grant.

A `public` provider MUST use a `public` contract. A `package` contract may be provided only to `owner`. Public contract exposure does not require a public provider, and neither exposure nor audience bypasses role, action-declaration, or endpoint checks. An `owner` provider authorizes an exact same-package declared action without a durable consent grant; a `public` provider requires an effective exact grant.

Each use is scoped to exactly one caller `role`, `app` or `runtime`. It authorizes no other role and is not transitive. `discover` controls catalog visibility, `invoke` controls unary calls, and `subscribe` controls server-stream calls. Actions are non-empty and distinct. Public-provider actions default-deny without an effective exact grant; owner-provider actions use the exact same-package declaration as authorization. Installing a package may establish public-provider grants for its declared actions, but `required` is readiness policy and never a grant.

Runtime binding uses exact `contractVersion` and `abiSha256`; version ranges, tags, and compatibility selection are forbidden. In particular, `versionRange` is invalid in a V2 Runtime manifest. Registry or authoring tools MAY resolve a range before packaging, but the immutable package and every active endpoint MUST contain the resolved exact lock. Runtime compares contract ID, the complete SemVer string including build metadata, and ABI hash for equality. It MUST NOT choose a supposedly compatible version by SemVer range.

For `required: true`, the caller role is ready only while at least one active, visible provider satisfies the exact lock, audience, and all declared actions, including effective exact grants for a public provider. The Host MAY keep the role loaded when that condition is false, but MUST report it as not ready and MUST NOT represent the required use as available. For `required: false`, absence does not block role readiness; discovery may be empty and calls still fail normally if no provider exists. Readiness does not select a provider, pin an endpoint, imply retry, or weaken exact endpoint staleness.

## Conformance Fixtures

The initial corpus is indexed by [`conformance/expectations.json`](conformance/expectations.json). Every fixture file is syntactically valid UTF-8 JSON without a BOM, including fixtures that are normatively invalid descriptors. This lets a harness separate JSON parsing from V2 validation.

[`conformance/expectations.json`](conformance/expectations.json) has `formatVersion: 1` and contains:

| Member | Meaning |
| --- | --- |
| `descriptorVersion` | Descriptor version exercised by the corpus. |
| `schemaProfile` | Exact value-schema profile exercised by the corpus. |
| `hashes` | Hash names, algorithm, and ABI domain-separator bytes. |
| `fixtures[].path` | POSIX path relative to the `conformance` directory. |
| `fixtures[].valid` | Normative V2 validity. |
| `fixtures[].schemaValid` | Expected result from `descriptor.schema.json`; this can be true for a semantic or lexical invalidity the schema cannot express. |
| `fixtures[].descriptorSha256` | Expected full descriptor hash for a valid fixture. |
| `fixtures[].abiSha256` | Expected domain-separated ABI hash for a valid fixture. |
| `fixtures[].canonicalDescriptorBytes` | Byte length of the canonical descriptor. |
| `fixtures[].canonicalAbiProjectionBytes` | Byte length of the canonical ABI projection before the domain separator. |
| `fixtures[].errorCode` | Broad required rejection category for an invalid fixture. Exact diagnostic text is not normative. |

A conforming validator MUST accept every `valid` fixture, reject every invalid fixture for at least its listed category, and reproduce all four hash and length expectations. It MUST perform raw-byte and semantic checks in addition to JSON Schema validation. Fixtures SHOULD contain one primary violation; additional fixtures may extend the manifest without changing `formatVersion` when they use the same shape.

`minimal.rpc.json` and `minimal-reordered.rpc.json` prove that object-member order changes neither hash. `minimal-annotation-variant.rpc.json` proves that an annotation changes `descriptorSha256` but not `abiSha256`. `complete.rpc.json` and `set-order-variant.rpc.json` prove that reordering only the four set-like array categories changes `descriptorSha256` while preserving the normalized ABI bytes and `abiSha256`.
