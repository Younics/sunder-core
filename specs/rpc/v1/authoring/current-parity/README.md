# Current authoring checks

This directory contains independently reviewed current authoring inputs. Tests read
these files directly: no historical source translation, diagnostic-span remapping,
or removal of current receipt/lock fields is permitted.

`csharp/expectations.json` indexes the C# sources. Valid cases pin the current
canonical descriptor and ABI identities, provider audience and generated namespaces.
The wire semantics of these cases are still V1; the existing reviewed wire hashes
remain valid. Invalid cases identify current diagnostic spans by source line and
literal token, including related collision locations. A diagnostic without a source
location has an empty `spans` array and an explicit message.

`metadata-expectations.json` reviews current conformance documents by path and records
their complete lock/projection joins and role intent. Paths are relative to this
directory. The referenced conformance documents remain unchanged. Their synthetic
artifact identities are fixture data, not publication or candidate pins.

`import-receipt-records.json` contains explicit C# receipt record expectations:
`common` plus exactly one of `consumer` or `provider` covers every record section.
Neither the compiler nor a historical receipt generates these expectations. The
selected artifact includes a platform contract that must stay out of package API
materialization. `service-name-vectors.json` pins twelve C# transport symbol spellings
for punctuation, digit prefixes and escaped-looking inputs. Node owners can reuse
the metadata identities and role intent while separately reviewing Node symbols.

`csharp/build/greeting.rpc.json` is the current `7.6.5` private package fixture's
wire expectation. The build test reads it directly, without rewriting a historical
version or using its generated descriptor as the expected value.

Language owners may add separate current source indexes here. The protected sibling
`parity/` corpus is inert history and is not an input to these checks or a regeneration
source. Current wire conformance and canonical-byte goldens remain independently
owned by the descriptor/format suites.
