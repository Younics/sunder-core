import { rpc, RpcDomainError, type RpcHandlers, type WorkerContext } from "@sunder/sdk";

const s = rpc.schema;
const messages = rpc.contract({
  contractId: "example.messages",
  exposure: "public",
  errors: {
    "example.rejected": {
      description: "The conformance request was rejected.",
      details: s.ref("ErrorDetails"),
    },
  },
  definitions: {
    ErrorDetails: s.object({
      reason: s.required(s.string({ minLength: 1, maxLength: 128 })),
      retryable: s.required(s.boolean()),
    }),
    Request: s.object({ message: s.required(s.string({ minLength: 1, maxLength: 256 })) }),
    Response: s.object({ message: s.required(s.string({ minLength: 1, maxLength: 1024 })) }),
  },
  services: {
    messages: {
      send: rpc.unary({ request: s.ref("Request"), response: s.ref("Response"), idempotency: "idempotent" }),
      data: rpc.unary({ request: s.ref("Request"), response: s.ref("Response"), idempotency: "idempotent" }),
      fault: rpc.unary({ request: s.ref("Request"), response: s.ref("Response"), errors: ["example.rejected"] }),
      watch: rpc.serverStream({ request: s.ref("Request"), event: s.ref("Response") }),
    },
  },
});

let workerState: WorkerContext["state"] | undefined;

export function initializePackageState(context: WorkerContext): void {
  workerState = context.state;
}

const handlers: RpcHandlers<typeof messages> = {
  messages: {
    async send(_request, context) {
      context.signal.throwIfAborted();
      return { message: "runtime-backed Node unary" };
    },
    async data(_request, context) {
      context.signal.throwIfAborted();
      if (workerState === undefined) throw new Error("Worker package state was not initialized.");
      const key = "worker-v1.conformance.value";
      const expected = "shared Worker V1 package data";
      await workerState.set(key, expected, { signal: context.signal });
      const value = await workerState.get(key, { signal: context.signal });
      await workerState.delete(key, { signal: context.signal });
      if (value !== expected) throw new Error("Runtime-backed Node package data round trip changed.");
      return { message: value };
    },
    async fault(_request, context) {
      context.signal.throwIfAborted();
      throw new RpcDomainError("example.rejected", "rejected", {
        reason: "conformance rejection", retryable: false,
      });
    },
    async *watch(_request, context) {
      context.signal.throwIfAborted();
      yield { message: "runtime-backed Node stream 1" };
      yield { message: "runtime-backed Node stream 2" };
    },
  },
};

export const packageRpc = rpc.router({
  contracts: { messages },
  providers: {
    messages: rpc.provider({ contract: "messages", id: "sunder.node.runtimebacked.provider", handlers }),
  },
});
