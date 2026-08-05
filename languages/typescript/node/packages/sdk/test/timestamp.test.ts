import assert from "node:assert/strict";
import test from "node:test";
import {
  formatUtcTimestamp,
  getSunder,
  type BrowserCallOptions,
  type BrowserJsonValue,
  type SunderBrowserApi,
} from "../src/browser";

test("UTC formatter emits exactly seven fractional digits", () => {
  assert.equal(formatUtcTimestamp(new Date("2026-08-01T12:34:56.789Z")), "2026-08-01T12:34:56.7890000Z");
  assert.throws(() => formatUtcTimestamp(new Date(Number.NaN)), /valid Date/u);
  assert.throws(() => formatUtcTimestamp(new Date("0000-01-01T00:00:00Z")), /year/u);
});

test("all advertised browser calls use the host round-trip deadline shape", async () => {
  const received = new Map<string, BrowserCallOptions | undefined>();
  const candidate: SunderBrowserApi = {
    version: 1,
    rpc: {
      discover: async (_contractId, options) => {
        received.set("discover", options);
        return { revision: 0, sequence: 0, providers: [], resetRequired: false };
      },
      watch: (_afterRevision, _afterSequence, options) => {
        received.set("watch", options);
        return emptyStream();
      },
      invoke: async <TRequest extends BrowserJsonValue, TResponse extends BrowserJsonValue>(
        _providerHandle: string,
        _serviceId: string,
        _methodId: string,
        _request: TRequest,
        options?: BrowserCallOptions,
      ): Promise<TResponse> => {
        received.set("invoke", options);
        return null as TResponse;
      },
      subscribe: <TRequest extends BrowserJsonValue, TEvent extends BrowserJsonValue>(
        _providerHandle: string,
        _serviceId: string,
        _methodId: string,
        _request: TRequest,
        options?: BrowserCallOptions,
      ) => {
        received.set("subscribe", options);
        return emptyStream<TEvent>();
      },
    },
    openExternal: async (_url, options) => {
      received.set("openExternal", options);
      return { opened: true };
    },
    getNavigationState: async (options) => {
      received.set("getNavigationState", options);
      return { route: "/" };
    },
    navigate: async (_route, options) => {
      received.set("navigate", options);
      return { accepted: true };
    },
  };
  Object.defineProperty(globalThis, "sunder", { configurable: true, value: candidate });
  try {
    const options = { deadline: new Date("2026-08-01T12:34:56.789Z") };
    const api = getSunder();
    await api.rpc.discover("contract", options);
    api.rpc.watch(0, 0, options);
    await api.rpc.invoke("provider", "service", "method", null, options);
    api.rpc.subscribe("provider", "service", "method", null, options);
    await api.openExternal("https://example.test", options);
    await api.getNavigationState(options);
    await api.navigate("/next", options);
    assert.deepEqual([...received.keys()], [
      "discover",
      "watch",
      "invoke",
      "subscribe",
      "openExternal",
      "getNavigationState",
      "navigate",
    ]);
    for (const receivedOptions of received.values()) {
      assert.equal(receivedOptions?.deadline?.toISOString(), "2026-08-01T12:34:56.7890000Z");
    }
  } finally {
    delete (globalThis as typeof globalThis & { sunder?: SunderBrowserApi }).sunder;
  }
});

async function* emptyStream<T>(): AsyncIterable<T> {
  await Promise.resolve();
}
