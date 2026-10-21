// @ts-check
import { defineService } from "@sunder/sdk";
import { startProbe, cancelled } from "./probe.js";

/** @type {import("@sunder/sdk").WorkerContext} */
let worker;
/** @param {import("@sunder/sdk").WorkerContext} context */
export function configure(context) { worker = context; return startProbe(context); }

export const probe = defineService({
  /** @param {string} value @returns {string} */
  echo(value) { return "echo:" + value; },
  /** @param {string} value @returns {AsyncIterable<string>} */
  async *finite(value) { yield value + ":1"; yield value + ":2"; },
  /** @param {string} value @param {import("@sunder/sdk").ServiceContext} context @returns {AsyncIterable<string>} */
  async *wait(value, context) {
    try { yield value + ":started"; await cancelled(context.signal); }
    finally {
      if (context.signal.aborted) await worker.state.set("compatibility.provider-cancelled", value);
    }
  },
}, { contractId: "compatibility.probe", providerId: "compatibility.probe.provider", runtimeUse: {} });
