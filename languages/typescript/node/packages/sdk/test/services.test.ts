import assert from "node:assert/strict";
import test from "node:test";
import {defineService,defineServiceError,assertServiceOptions,createServiceContract,type ServiceContext,type ServiceDefinition,type ServiceOptions,type ServiceUse} from "../src/services";
import {createServiceToken} from "../src/service-reference";

interface Payload {
  count: bigint;
  bytes: Uint8Array;
  label?: string | null;
  kind: "a" | "b";
}
interface Echo { run(request: Payload, context: ServiceContext): Payload }
const imported = createServiceContract<Echo>("echo");
const echo = defineService({ run(request, context) {
  const count: bigint = request.count;
  const bytes: Uint8Array = request.bytes;
  const label: string | null | undefined = request.label;
  const kind: "a" | "b" = request.kind;
  // @ts-expect-error Imported service context cannot erase logical request types to any.
  const wrong: string = request.count;
  void [count, bytes, label, kind, wrong];
  context.signal.throwIfAborted();
  return request;
} }, { implements: imported });
const declaration = defineService<Echo>(null, { declarationOnly: true });
const local = defineService({ run(request, context) { context.signal.throwIfAborted(); return request; } }, { implements: declaration });
const independent = defineService({ ping(request: string): string { return request; } });
const mixed = { echo, local, independent };
const retained: ServiceDefinition<Echo> = mixed.echo;
// @ts-expect-error Required source properties cannot be omitted.
const missing: Payload = { count: 1n };
// @ts-expect-error An imported contract cannot widen the implementation result.
defineService({ run() { return { count: "wrong", bytes: new Uint8Array(), kind: "a" }; } }, { implements: imported });
// @ts-expect-error A local declaration also checks the result when other services coexist.
defineService({ run() { return 42; } }, { implements: declaration });
// @ts-expect-error Implementations must supply every declared method.
defineService({}, { implements: imported });
// @ts-expect-error A local-name string is not a selected service declaration.
defineService({ run() { return 42; } }, { implements: "absent" });
void [retained, missing];

test("ordinary service definitions retain exact imported/local method types without registering providers", () => {
  assert.equal(echo.options.implements, imported);
  assert.equal(local.options.implements, declaration);
  assert.equal(declaration.methods, null);
  assert.deepEqual(Object.keys(echo), ["methods", "options"]);
  assert.equal(Object.hasOwn(echo, "providerRegistrations"), false);
  assert.equal(Object.hasOwn(echo, "bind"), false);
  assert.equal(independent.methods!.ping("ordinary"), "ordinary");
});

test("ordinary methods and copied semantic options remain immutable after definition", () => {
  const facets = { minLength: 1, maxLength: 5 };
  const actions: Array<NonNullable<ServiceUse["actions"]>[number]> = ["discover"];
  const errors = ["example.invalid"];
  const options: ServiceOptions = {
    exposure: "public", constraints: { "run.request": facets }, runtimeUse: { actions },
    methods: { run: { idempotency: "idempotent", errors } },
    values: { Payload: { name: "Payload", publicName: "Payload" } },
    errors: { "example.invalid": defineServiceError<{ reason: string }>({ description: "Invalid payload" }) },
  };
  const methods = { run(request: Payload): Payload { return request; } };
  const service = defineService(methods, options);
  assert.notEqual(service.options, options);
  assert.notEqual(service.options.constraints, options.constraints);
  assert.notEqual(service.options.constraints!["run.request"], facets);
  assert.deepEqual(service.options.constraints!["run.request"], { minLength: 1, maxLength: 5 });
  actions.push("invoke"); errors.push("example.other");
  assert.deepEqual(service.options.runtimeUse!.actions, ["discover"]);
  assert.deepEqual(service.options.methods!.run!.errors, ["example.invalid"]);
  for (const [value, changes] of [
    [facets, { maxLength: 10 }], [service.options.constraints!["run.request"], { maxLength: 10 }],
    [methods, { other() {} }], [service.methods, { run: () => 42 }],
    [service, { providerId: "other.provider" }], [service.options, { exposure: "package" }],
    [service.options.values!.Payload!, { publicName: "Changed" }],
    [service.options.errors!["example.invalid"], { description: "Changed" }],
  ] as const) assert.throws(() => Object.assign(value!, changes), TypeError);
  assert.throws(() => (service.options.runtimeUse!.actions as string[]).push("invoke"), TypeError);
  assertServiceOptions(service, { ...options, runtimeUse: { actions: ["discover"] }, methods: { run: { idempotency: "idempotent", errors: ["example.invalid"] } } });
});

test("typed Runtime semantic options retain canonical token identity and immutable policy snapshots",()=>{
  const target={contractId:"example.target",contractVersion:"1.0.0",descriptorSha256:"a".repeat(64),abiSha256:"b".repeat(64),serviceId:"target"};
  const service=createServiceToken(target,()=>({run:async()=>true}));const actions:Array<NonNullable<ServiceUse["actions"]>[number]>=["discover"];
  const provider=defineService({run(){return true;}},{implements:createServiceContract<{run():boolean}>("provider"),runtimeUses:[{service,actions}]});
  actions.push("invoke");assert.equal(provider.options.runtimeUses![0]!.service,service);assert.deepEqual(provider.options.runtimeUses![0]!.actions,["discover"]);
  assertServiceOptions(provider,{runtimeUses:[{service:target,actions:["discover"]}]});
  for(const expected of [{runtimeUses:[{service:target,actions:["discover","invoke"]}]},{runtimeUses:[{service:{...target,descriptorSha256:"c".repeat(64)},actions:["discover"]}]},{}])
    assert.throws(()=>assertServiceOptions(provider,expected),/metadata differs/);
});
test("typed Runtime option capture rejects counterfeit tokens and accessor/unknown policy fields",()=>{
  const token=createServiceToken({contractId:"example.target",contractVersion:"1.0.0",descriptorSha256:"a".repeat(64),abiSha256:"b".repeat(64),serviceId:"target"},()=>({}));
  for(const runtimeUses of [[],[{service:{contractId:"example.target",serviceId:"target"}}],[{get service(){return token;}}],[{service:token,extra:true}]])
    assert.throws(()=>defineService({run(){}},{runtimeUses:runtimeUses as never}));
});
