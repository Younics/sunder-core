import {
  runWorker,
  type JsonValue,
  type RpcInvocationContext,
} from "@sunder/sdk";

async function* watch(
  context: RpcInvocationContext,
  request: JsonValue,
): AsyncIterable<JsonValue> {
  context.signal.throwIfAborted();
  yield { message: `Watching ${JSON.stringify(request)}` };
}

void runWorker({
  providers: [
    {
      providerId: "SUNDER_PACKAGE_ID.provider",
      contractId: "example.messages",
      contractVersion: "1.0.0",
      contractSha256: "SUNDER_CONTRACT_SHA256",
      handler: {
        invokeUnary: (_context, _serviceId, _methodId, request) => ({
          message: `Hello from SUNDER_PACKAGE_NAME: ${JSON.stringify(request)}`,
        }),
        invokeServerStream: (context, _serviceId, _methodId, request) => watch(context, request),
      },
    },
  ],
}).catch(() => {
  process.exitCode = 70;
});
