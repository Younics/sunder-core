import { runWorker, type JsonValue, type RpcInvocationContext } from "@sunder/sdk";

async function* events(context: RpcInvocationContext): AsyncIterable<JsonValue> {
  context.signal.throwIfAborted();
  yield { message: "native SEA stream" };
}

void runWorker({
  providers: [
    {
      providerId: "sunder.sea.smoke.provider",
      contractId: "example.messages",
      contractVersion: "1.0.0",
      contractSha256: "12cfba1e7a29bd7c0d9288e463ea9ae29d4b24afa09b6633f4f026d673537f06",
      handler: {
        invokeUnary: () => ({ message: "native SEA unary" }),
        invokeServerStream: (context) => events(context),
      },
    },
  ],
}).catch(() => {
  process.exitCode = 70;
});
