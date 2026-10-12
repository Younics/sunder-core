import assert from "node:assert/strict";
import test from "node:test";
import { JSDOM } from "jsdom";
import { act, createElement, StrictMode, useEffect } from "react";
import type { Root } from "react-dom/client";
import { ApiProvider, useApi } from "../src/react";
import { bindApiContext, createApiContext, createApiToken, type ApiTransport } from "../src/api";
import {liveWatch,provider} from "./service-binding-fixture";

test("React StrictMode replays component ownership without leaking scopes or retiring another root", async () => {
  const dom = new JSDOM('<!doctype html><div id="first"></div><div id="second"></div>');
  const globals = new Map(["window", "document", "IS_REACT_ACT_ENVIRONMENT"].map(key => [key, Object.getOwnPropertyDescriptor(globalThis, key)]));
  Object.defineProperty(globalThis, "window", { value: dom.window, configurable: true });
  Object.defineProperty(globalThis, "document", { value: dom.window.document, configurable: true });
  Object.defineProperty(globalThis, "IS_REACT_ACT_ENVIRONMENT", { value: true, configurable: true });
  const { createRoot } = await import("react-dom/client");
  const identity = { packageId: "example.provider", contractId: "example.greeting", contractVersion: "1.0.0", descriptorSha256: "a".repeat(64), abiSha256: "b".repeat(64) };
  let opened = 0; let invoked = 0; const active = new Set<number>();
  const transport: ApiTransport = {
    async discover() { return {revision:8,sequence:19, resetRequired: false, providers: [{ ...provider,...identity, packageVersion: "1.0.0", state: "active", endpointReference: "exact" }] }; },
    watch:liveWatch,
    async invoke(_target, _service, _method, request, options) { options?.signal?.throwIfAborted(); invoked++; return request as never; },
    async *subscribe() {},
    async createCallScope() {
      const id = ++opened; active.add(id);
      return { ...transport, async close() { assert.equal(active.delete(id), true, "A component scope closed more than once."); } };
    },
  };
  const Token = createApiToken("example.owner", context => ({ call: () => context.unary(identity, (transport, target, options) => transport.invoke(target, "greeting", "greet", "hello", options)) }), "current");
  const first = createApiContext("example.owner", transport); const second = createApiContext("example.owner", transport);
  bindApiContext(first, Token); bindApiContext(second, Token);
  const apis: Record<string, { call(): Promise<unknown> }> = {};
  const pending: Promise<unknown>[] = [];
  function Consumer({ name }: { name: string }) {
    const api = useApi(Token); apis[name] = api;
    useEffect(() => { pending.push(api.call().catch(() => undefined)); }, [api]);
    return null;
  }
  let a: Root | undefined; let b: Root | undefined;
  try {
    await act(async () => {
      a = createRoot(dom.window.document.getElementById("first")!);
      b = createRoot(dom.window.document.getElementById("second")!);
      a.render(createElement(StrictMode, null, createElement(ApiProvider, { context: first }, createElement(Consumer, { name: "first" }))));
      b.render(createElement(StrictMode, null, createElement(ApiProvider, { context: second }, createElement(Consumer, { name: "second" }))));
    });
    await Promise.all(pending);
    assert.equal(pending.length,4);assert.equal(opened,2,"Retired replay owners are cancelled before acquiring a scope.");assert.equal(invoked, 2); assert.equal(active.size, 0);
    await act(async () => { a!.unmount(); a = undefined; });
    await assert.rejects(apis.first!.call(), /mounted/);
    assert.equal(first.signal.aborted, false, "A component must not dispose its externally provided root.");
    assert.equal(await apis.second!.call(), "hello");
    assert.equal(active.size, 0);
    second.dispose(); await assert.rejects(apis.second!.call());
  } finally {
    await act(async () => { a?.unmount(); b?.unmount(); }); first.dispose(); second.dispose(); dom.window.close();
    for (const [key, descriptor] of globals) if (descriptor) Object.defineProperty(globalThis, key, descriptor); else Reflect.deleteProperty(globalThis, key);
  }
});
