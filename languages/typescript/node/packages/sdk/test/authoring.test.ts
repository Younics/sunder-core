import assert from "node:assert/strict";
import test from "node:test";
import { rpc } from "../src/authoring";
import type { RpcHandlers, RpcImportedProvider, RpcSchemaValue } from "../src/authoring";

const s = rpc.schema;
const contract = rpc.contract({ exposure: "public", errors: {}, definitions: {
  Payload: s.object({ count: s.required(s.bigInteger()), bytes: s.required(s.bytes()),
    label: s.optional(s.nullable(s.string())), kind: s.required(s.stringEnum(["a", "b"])) }),
}, services: { echo: { run: rpc.unary({ request: s.ref("Payload"), response: s.ref("Payload") }) } } });

const handlers: RpcHandlers<typeof contract> = { echo: { run(request, context) {
  const count: bigint = request.count;
  const bytes: Uint8Array = request.bytes;
  const label: string | null | undefined = request.label;
  const kind: "a" | "b" = request.kind;
  // @ts-expect-error Schema-derived request types are not any.
  const invalid: string = request.count;
  void [count, bytes, label, kind, invalid];
  context.signal.throwIfAborted();
  return request;
} } };
type Payload = RpcSchemaValue<typeof contract.definitions.Payload, typeof contract.definitions>;
// @ts-expect-error Required properties cannot be omitted.
const missing: Payload = { count: 1n };
// @ts-expect-error Wrong output types are rejected by the actual SDK type model.
const wrong: RpcHandlers<typeof contract> = { echo: { run: () => ({ count: "wrong", bytes: new Uint8Array(), kind: "a" }) } };
void [missing, wrong];

// Mixed routers retain exact handler constraints and the supplied provider types.
const imported: RpcImportedProvider = { importKey: "extension", handlers: {} };
const mixed = rpc.router({ contracts: { echo: contract }, providers: {
  extension: imported,
  echo: rpc.provider({ contract: "echo", handlers }),
} });
const retained: RpcHandlers<typeof contract> = mixed.providers.echo.handlers;
// @ts-expect-error Imported bindings do not make unowned contract keys valid.
rpc.router({ contracts: { echo: contract }, providers: { extension: imported, bad: rpc.provider({ contract: "absent", handlers }) } });
// @ts-expect-error Imported bindings cannot widen an owned handler's result.
rpc.router({ contracts: { echo: contract }, providers: { extension: imported, bad: rpc.provider({ contract: "echo", handlers: { echo: { run: () => 42 } } }) } });
void retained;

test("closed DSL stays unbound until the private generated adapter runs", () => {
  const router = rpc.router({ contracts: { echo: contract }, providers: { echo: rpc.provider({ contract: "echo", handlers }) } });
  assert.throws(() => router.providerRegistrations, /has not been bound/);
  assert.equal(Object.hasOwn(rpc, "bind"), false);
  assert.deepEqual(Object.keys(rpc), ["router", "contract", "provider", "unary", "serverStream", "schema"]);
});

test("DSL declarations and copied facets cannot silently mutate after construction", () => {
  const facets = { minLength: 1, maxLength: 5 };
  const schema = s.string(facets) as unknown as { args: readonly unknown[] };
  assert.notEqual(schema.args[0], facets);
  assert.deepEqual(schema.args[0], { minLength: 1, maxLength: 5 });
  assert.throws(() => Object.assign(facets, { maxLength: 10 }), TypeError);
  const input = { contract: "echo" as const, handlers };
  const provider = rpc.provider(input);
  assert.notEqual(provider, input);
  assert.throws(() => Object.assign(input, { id: "other.provider" }), TypeError);
  assert.throws(() => Object.assign(provider, { id: "other.provider" }), TypeError);
  const providers = { echo: provider };
  const router = rpc.router({ contracts: { echo: contract }, providers });
  assert.notEqual(router.providers, providers);
  assert.throws(() => Object.assign(providers, { other: provider }), TypeError);
  assert.throws(() => Object.assign(router.providers, { other: provider }), TypeError);
});
