import assert from "node:assert/strict";
import test from "node:test";
import { bindApiContext, createApiContext, createApiToken, createDeferredApi, type ApiIdentity, type ApiTransport } from "../src/api";
import { createBrowserContext, getSunder, waitForSunder, type SunderBrowserApi } from "../src/browser";
import {liveWatch,provider} from "./service-binding-fixture";

const identity: ApiIdentity = { packageId: "example.provider", contractId: "example.service", contractVersion: "1.0.0", descriptorSha256: "a".repeat(64), abiSha256: "b".repeat(64) };
function transport() {
  const calls = { discover: 0, invoke: 0, subscribe: 0, open: 0, close: 0, iteratorClosed: 0 };
  const value: ApiTransport = {
    async discover() { calls.discover++; return {revision:8,sequence:19, resetRequired: false, providers: [{ ...provider,...identity, packageVersion: "1.0.0", state: "active", endpointReference: "exact-activation" }] }; },
    watch:liveWatch,
    async invoke(_target, _service, _method, request) { calls.invoke++; return request as never; },
    subscribe(_target, _service, _method, request) { return { async *[Symbol.asyncIterator]() { calls.subscribe++; try { yield request as never; } finally { calls.iteratorClosed++; } } }; },
    async createCallScope() { calls.open++; return { ...value, async close() { calls.close++; } }; },
  };
  return { value, calls };
}
const Token = createApiToken("example.owner", context => ({
  call: (signal?: AbortSignal) => context.unary(identity, (transport, target, options) => transport.invoke(target, "service", "call", "ok", options), signal ? { signal } : undefined),
  watch: () => context.stream(identity, (transport, target, options) => transport.subscribe(target, "service", "watch", "event", options)),
}));
test("managed APIs acquire lazily, scope each call, and close streams on early disposal", async () => {
  const { value, calls } = transport(); const owner = createApiContext("example.owner", value); const api = owner.api(Token);
  const uniterated = api.watch(); assert.ok(uniterated); assert.equal(calls.open, 0); assert.equal(calls.discover, 0);
  assert.equal(await api.call(), "ok"); assert.equal(calls.open, 1); assert.equal(calls.close, 1);
  for await (const event of api.watch()) { assert.equal(event, "event"); break; }
  assert.equal(calls.open, 2); assert.equal(calls.close, 2); assert.equal(calls.iteratorClosed, 1);
  owner.dispose(); await assert.rejects(api.call()); assert.equal(calls.open, 2);
});
test("preaborted calls and retired deferred component owners make no RPC and cannot cancel siblings", async () => {
  const { value, calls } = transport(); const root = createApiContext("example.owner", value); let child = root.fork();
  const component = createDeferredApi(Token, () => child); const sibling = root.fork().api(Token);
  const aborted = new AbortController(); aborted.abort(); await assert.rejects(component.call(aborted.signal)); assert.equal(calls.open, 0);
  const unstarted=component.watch().withCancellation(aborted.signal);assert.equal(calls.open,0);
  await assert.rejects(unstarted[Symbol.asyncIterator]().next());assert.equal(calls.open,0);
  await assert.rejects(component.watch()[Symbol.asyncIterator](aborted.signal).next());assert.equal(calls.open,0);
  child.dispose(); await assert.rejects(component.call());
  child = root.fork(); assert.equal(await component.call(), "ok"); assert.equal(await sibling.call(), "ok");
  root.dispose(); await assert.rejects(component.call()); await assert.rejects(sibling.call());
});
test("selection ambiguity/hash mismatch rejects without invoking or retrying and closes the scope", async () => {
  for (const duplicate of [false, true]) {
    const { value, calls } = transport(); const original = value.discover;
    value.discover = async (...args) => { const snapshot = await original(...args); return { ...snapshot, providers: duplicate ? [...snapshot.providers, ...snapshot.providers.map(provider=>({...provider,providerId:"example.other",endpointReference:"other"}))] : snapshot.providers.map(provider => ({ ...provider, abiSha256: "c".repeat(64) })) }; };
    const owner = createApiContext("example.owner", value); await assert.rejects(owner.api(Token).call(), /ambiguous|No active/);
    assert.equal(calls.invoke, 0); assert.equal(calls.discover, 3); assert.equal(calls.close, 1); owner.dispose();
    assert.throws(() => createApiContext("another.owner", value).api(Token), /different package/);
  }
});
test("generated context binding rejects stale tokens even under the same package identity", () => {
  const { value, calls } = transport(); const root = createApiContext("example.owner", value);
  const current = createApiToken("example.owner", () => ({}), "current"); const stale = createApiToken("example.owner", () => ({}), "stale");
  bindApiContext(root, current); assert.ok(root.api(current));
  assert.throws(() => root.api(stale), /stale/); assert.throws(() => root.fork().api(stale), /stale/);
  assert.equal(calls.open, 0); root.dispose();
});
test("cleanup preserves the operation error and never advertises control-call retry safety after dispatch", async () => {
  const { value } = transport();
  const controlFailure = Object.assign(new Error("control failed"), { error: { dispatchState: "not-dispatched", retrySafety: "safe" } });
  value.createCallScope = async () => ({ ...value, close: async () => { throw controlFailure; } });
  const owner = createApiContext("example.owner", value);
  await assert.rejects(owner.api(Token).call(), error => error instanceof Error && error !== controlFailure && error.cause === controlFailure && !("error" in error));
  const original = new Error("domain failure"); value.invoke = async () => { throw original; };
  await assert.rejects(owner.api(Token).call(), error => error === original); owner.dispose();
});
test("document readiness is event-driven, abortable, immutable, and isolated across roots", async () => {
  const host = new EventTarget() as unknown as Window; const lifetime = new AbortController();
  const abort = new AbortController(); const pending = waitForSunder(host, abort.signal); abort.abort(); await assert.rejects(pending);
  const { value, calls } = transport();
  const first = createBrowserContext(Token, host); const second = createBrowserContext(Token, host);
  const waiting = first.api(Token).call(); assert.equal(calls.discover, 0);
  const bridge = Object.freeze({ protocol: "sunder.bridge.v1", version: 1, signal: lifetime.signal, rpc: value }) as unknown as SunderBrowserApi;
  Object.defineProperty(host, "sunder", { value: bridge, configurable: false, writable: false }); host.dispatchEvent(new CustomEvent("sunder:ready", {detail:bridge}));
  assert.equal(await waiting, "ok"); first.dispose(); const secondApi = second.api(Token); assert.equal(await secondApi.call(), "ok");
  lifetime.abort(); await assert.rejects(secondApi.call()); second.dispose();
});
test("managed browser discovery and invocation retain the existing seven-digit deadline representation", async () => {
  const { value } = transport(); const observed: string[] = [];
  const original = value.discover;
  value.discover = async (id, options) => { if(options?.deadline)observed.push(options.deadline.toISOString()); return original(id, options); };
  value.invoke = async (_target, _service, _method, request, options) => { observed.push(options!.deadline!.toISOString()); return request as never; };
  const token = createApiToken("example.owner", context => ({ call: () => context.unary(identity, (transport, target, options) => transport.invoke(target, "service", "call", "ok", options), { deadline: new Date("2030-01-02T03:04:05.123Z") }) }));
  const host = new EventTarget() as unknown as Window;
  Object.defineProperty(host, "sunder", { value: Object.freeze({ protocol: "sunder.bridge.v1", version: 1, signal: new AbortController().signal, rpc: value }), writable: false, configurable: false });
  const owner = createBrowserContext(token, host);
  assert.equal(await owner.api(token).call(), "ok");
  assert.deepEqual(observed, ["2030-01-02T03:04:05.1230000Z", "2030-01-02T03:04:05.1230000Z"]);
  owner.dispose();
});
test("content-bearing methods never close authority merely because unary returned a reference", async () => {
  const { value, calls } = transport();
  const contentIdentity = {...identity,callerOwnedContent:true};
  const token = createApiToken("example.owner", context => ({call:()=>context.unary(contentIdentity,(transport,target,options)=>transport.invoke(target,"service","content",{},options))}));
  const ordinary = createApiContext("example.owner",value);
  await assert.rejects(ordinary.api(token).call(),/caller-owned content scope/); assert.equal(calls.open,0); assert.equal(calls.invoke,0);
  let closed=false; const scope={discover:value.discover,invoke:value.invoke,subscribe:value.subscribe,content:{async register(){throw new Error("Unexpected registration");},async open(){throw new Error("Unexpected content open");}},async close(){closed=true;}};
  const owner=createApiContext("example.owner",{...scope,watch:liveWatch}); await owner.api(token).call(); assert.equal(closed,false);
  owner.dispose();assert.equal(closed,false);await scope.close();assert.equal(closed,true);ordinary.dispose();
});
test("ready events bind only their exact installed candidate and adapted APIs retain the native signal",async()=>{
  const host=new EventTarget() as unknown as Window;const {value}=transport();const signal=new AbortController().signal;
  const bridge=Object.freeze({protocol:"sunder.bridge.v1",version:1,signal,rpc:value}) as unknown as SunderBrowserApi;
  let settled=false;const ready=waitForSunder(host).then(value=>{settled=true;return value;});
  Object.defineProperty(host,"sunder",{value:bridge,writable:false,configurable:false});
  host.dispatchEvent(new CustomEvent("sunder:ready",{detail:Object.freeze({...bridge})}));await Promise.resolve();assert.equal(settled,false);
  host.dispatchEvent(new CustomEvent("sunder:ready",{detail:bridge}));assert.equal(await ready,bridge);
  const original=Object.getOwnPropertyDescriptor(globalThis,"sunder");
  try{Object.defineProperty(globalThis,"sunder",{value:bridge,configurable:true});assert.equal(getSunder().signal,signal);}
  finally{if(original)Object.defineProperty(globalThis,"sunder",original);else Reflect.deleteProperty(globalThis,"sunder");}
});
