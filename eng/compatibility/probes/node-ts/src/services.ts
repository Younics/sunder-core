import { defineService, type ServiceContext, type WorkerContext } from "@sunder/sdk";
import { startProbe, cancelled } from "./probe.js";

let worker: WorkerContext;
export function configure(context: WorkerContext) {
  worker = context;
  return startProbe(context);
}

export const probe = defineService({
  echo(value: string): string { return "echo:" + value; },
  async *finite(value: string): AsyncIterable<string> { yield value + ":1"; yield value + ":2"; },
  async *wait(value: string, context: ServiceContext): AsyncIterable<string> {
    try { yield value + ":started"; await cancelled(context.signal); }
    finally {
      if (context.signal.aborted) await worker.state.set("compatibility.provider-cancelled", value);
    }
  },
}, { contractId: "compatibility.probe", providerId: "compatibility.probe.provider", runtimeUse: {} });
