// @ts-check
// Copy this authored helper into each Node probe's src/ before its first build.
// Retain that exact authored tree and its original dependency lock afterwards.
import { RpcError } from "@sunder/sdk";

/** @param {AbortSignal} signal @returns {Promise<void>} */
export function cancelled(signal) {
  return new Promise((resolve) => {
    if (signal.aborted) resolve();
    else signal.addEventListener("abort", () => resolve(), { once: true });
  });
}
/** @param {number} ms @returns {Promise<void>} */
const delay = (ms) => new Promise(resolve => setTimeout(resolve, ms));

/** @param {import("@sunder/sdk").WorkerContext} context */
export function startProbe(context) {
  const lifetime = new AbortController();
  /** @type {Promise<void> | undefined} */
  let running;
  return {
    async onActivated() {
      running = execute(context, lifetime.signal).catch(async error => {
        if (!lifetime.signal.aborted) await context.state.set("compatibility.failure", String(error));
      });
    },
    async onShutdown() { lifetime.abort(); await running; },
  };
}

/** @param {import("@sunder/sdk").WorkerContext} context @param {AbortSignal} lifetime */
async function execute(context, lifetime) {
  let nonce;
  while (!lifetime.aborted) {
    nonce = await context.state.get("compatibility.challenge");
    if (nonce) break;
    await delay(25);
  }
  if (!nonce || lifetime.aborted) return;
  const catalog = await context.rpc.discover("compatibility.probe", { signal: lifetime });
  const provider = catalog.providers.find(p => p.packageId === context.packageId);
  if (!provider) throw Error("Probe provider missing");
  const unary = await context.rpc.invoke(provider.endpointReference, "probe", "echo", { value: nonce }, { signal: lifetime });
  const stream = [];
  for await (const value of context.rpc.subscribe(provider.endpointReference, "probe", "finite", { value: nonce }, { signal: lifetime })) stream.push(value);
  const abort = new AbortController();
  let clientCancelled = false;
  let started = false;
  try {
    for await (const value of context.rpc.subscribe(provider.endpointReference, "probe", "wait", { value: nonce }, { signal: abort.signal })) {
      if (value !== nonce + ":started" || started) throw Error("Unexpected cancellation stream event");
      started = true;
      // Cancellation happens only after a real provider event traversed the Host.
      abort.abort();
    }
    clientCancelled = abort.signal.aborted;
  } catch (error) {
    if (!abort.signal.aborted || !(error instanceof RpcError && error.error.kind === "cancelled"
      || error instanceof Error && error.name === "AbortError")) throw error;
    clientCancelled = true;
  }
  const deadline = Date.now() + 10_000;
  let providerCancelled;
  while (Date.now() < deadline && !lifetime.aborted) {
    providerCancelled = await context.state.get("compatibility.provider-cancelled");
    if (providerCancelled === nonce) break;
    await delay(25);
  }
  if (providerCancelled !== nonce || !started || !clientCancelled) throw Error("Cancellation did not drain the provider");
  const afterCancel = await context.rpc.invoke(provider.endpointReference, "probe", "echo", { value: "after:" + nonce }, { signal: lifetime });
  await context.state.set("compatibility.report", JSON.stringify({ protocol: "sunder.compatibility.probe.v1", nonce,
    unary, stream, started, clientCancelled, providerCancelled, afterCancel }));
}
