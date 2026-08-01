import { RpcError, runWorker, safeStderr, type JsonValue, type RpcClient, type RpcContentReference, type RpcInvocationContext } from "../src/index";
import { readFile } from "node:fs/promises";

const identity = {
  providerId: "example.provider",
  contractId: "example.rpc",
  contractVersion: "1.0.0",
  contractSha256: "a".repeat(64),
} as const;

async function* stream(context: RpcInvocationContext, request: JsonValue): AsyncIterable<JsonValue> {
  context.signal.throwIfAborted();
  yield { sequence: 1, request };
  await Promise.resolve();
  context.signal.throwIfAborted();
  yield { sequence: 2, request };
}

async function outbound(client: RpcClient): Promise<void> {
  const mode = process.env.SUNDER_TEST_MODE;
  if (mode === "outbound-unary") {
    await client.discover("example.rpc");
    safeStderr("OUTBOUND_OK");
  } else if (mode === "outbound-stream") {
    for await (const _ of client.watch(0, 0)) {
      // The mock Host controls stream termination.
    }
  } else if (mode === "outbound-stream-overflow") {
    client.watch(0, 0);
    safeStderr("OUTBOUND_STARTED");
  }
}

void runWorker({
  providers: [{
    ...identity,
    handler: {
      async invokeUnary(context, _serviceId, methodId, request): Promise<JsonValue> {
        if (methodId === "wait") {
          await new Promise<void>((resolve, reject) => {
            context.signal.addEventListener("abort", () => reject(context.signal.reason), { once: true });
          });
        }
        if (methodId === "domain") {
          throw new RpcError({ kind: "domain", code: "example.rejected", message: "rejected" });
        }
        if (methodId === "content") {
          if (request === null || Array.isArray(request) || typeof request !== "object") {
            throw new TypeError("Content test request must be an object.");
          }
          const requestObject = request as Readonly<Record<string, JsonValue>>;
          const registerPath = requestObject.registerPath;
          const reference = requestObject.reference;
          if (typeof registerPath !== "string" || reference === null || Array.isArray(reference) || typeof reference !== "object") {
            throw new TypeError("Content test request is invalid.");
          }
          const registered = await context.content.registerFile(registerPath, {
            mediaType: "text/plain",
            fileName: "provider.txt",
            length: 16,
          });
          const opened = await context.content.openFile(reference as unknown as RpcContentReference);
          const openedValue = await readFile(opened.filePath, "utf8");
          await opened.discard();
          return { registered: { ...registered }, openedValue };
        }
        return { accepted: true, request };
      },
      invokeServerStream: (context, _serviceId, _methodId, request) => stream(context, request),
    },
  }],
  onActivated: outbound,
  limits: process.env.SUNDER_TEST_MODE === "outbound-stream-overflow"
    ? { maxStreamQueueMessages: 2 }
    : undefined,
}).catch(() => {
  process.exitCode = 70;
});
