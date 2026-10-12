import { defineService, defineServiceError, RpcDomainError, type ServiceContext, type WorkerContext } from "@sunder/sdk";
export { configure } from "./worker.js";

type Request = { message: string };
type Response = { message: string };
type ErrorDetails = { reason: string; retryable: boolean };
let workerState: WorkerContext["state"] | undefined;
export function initializePackageState(context: WorkerContext): void { workerState = context.state; }

export const messages = defineService({
  async send(request: Request, context: ServiceContext): Promise<Response> {
    context.signal.throwIfAborted();
    return { message: "runtime-backed Node unary" };
  },
  async data(request: Request, context: ServiceContext): Promise<Response> {
    context.signal.throwIfAborted();
    if (!workerState) throw new Error("Worker package state was not initialized.");
    const key = "worker-v1.conformance.value";
    const expected = "shared Worker V1 package data";
    await workerState.set(key, expected, { signal: context.signal });
    const value = await workerState.get(key, { signal: context.signal });
    await workerState.delete(key, { signal: context.signal });
    if (value !== expected) throw new Error("Runtime-backed Node package data round trip changed.");
    return { message: value };
  },
  async fault(request: Request, context: ServiceContext): Promise<Response> {
    context.signal.throwIfAborted();
    throw new RpcDomainError("example.rejected", "rejected", { reason: "conformance rejection", retryable: false });
  },
  async *watch(request: Request, context: ServiceContext): AsyncIterable<Response> {
    context.signal.throwIfAborted();
    yield { message: "runtime-backed Node stream 1" };
    yield { message: "runtime-backed Node stream 2" };
  },
}, {
  exposure: "public", contractId: "example.messages", providerId: "sunder.node.runtimebacked.provider",
  errors: { "example.rejected": defineServiceError<ErrorDetails>({ description: "The conformance request was rejected." }) },
  methods: { send: { idempotency: "idempotent" }, data: { idempotency: "idempotent" }, fault: { errors: ["example.rejected"] } },
  constraints: {
    "Request.message": { minLength: 1, maxLength: 256 },
    "Response.message": { minLength: 1, maxLength: 1024 },
    "ErrorDetails.reason": { minLength: 1, maxLength: 128 },
  },
});
