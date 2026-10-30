# Consumer requirements

This is a separate question from whole-provider version classification:
**does one current provider descriptor satisfy this consumer's selected surface?**
`cases.json` executes against the actual C# and Node proof implementations.
`requirements.json` pins their common deterministic selector bytes.

Requirements retain the exact source descriptor identity and select methods.
Their schema closure includes request, response/event and declared error details,
including nested records, arrays, maps and named references. Unrelated services,
methods, error declarations and definitions are not requirements. A full response
model requires all declared members, even optional ones. Explicit output-member
selectors can narrow ordinary records; they cannot narrow exact service-reference
schemas or records with explicit cardinality bounds. Generated source analysis
currently records full model requirements, not inferred property reads.

Requests must remain accepted by the provider. Known response/event/error members
must remain declared, required members guaranteed, and their value constraints
compatible. Additional ordinary record members are ignored by this proof and dropped
by the consumer output reader after full provider-side validation. Method kind,
idempotency and error-set checks remain;
unsupported changed numeric, tagged-union and recursive forms fail closed. The
existing traversal/reason limits still apply. This does not prove business behavior.

Typed C# and Node Worker/App reference collectors emit separate build-receipt
evidence while retaining complete restore inventory. The manifest carries selectors
on each consuming role's `usesContracts` entry and requires
`rpc.consumer-requirements.v1` on its targets. Registry/local resolution, discovery,
readiness and method admission check these selectors against one current provider
schema. Requests validate against both caller and provider schemas; unary results,
stream events and domain-error details validate against the provider before being
read into the caller's shape. Missing selectors retain exact matching, including
an older role in a mixed-role package. Imported provider implementations stay exact.

Raw/dynamic calls declare method IDs through C# `SunderRpcDynamicUse` or Node
`declareRpcUse`. Their output definitions stay exact unless explicitly narrowed by
`SunderRpcConsumerView` / `declareRpcView`; views never narrow full typed models or
service-reference protocol values. See the SDK guides for authoring syntax.

Authority, integrity, package version ranges and permissions remain independent
checks; a requirement selector is never an authorization grant.
The descriptor proof alone also cannot establish service-reference targets:
reachable source/provider projection targets are checked separately before binding.
Referenced services contribute their target method requirements too. Exact locator
schemas do not substitute for that cross-contract check. Once issued, a binding
retains its actual exact schema and activation identity; compatibility never
retargets it to a replacement activation.
