import { runWorker, type JsonValue, type RpcInvocationContext } from "@sunder/sdk";
import { contractIdentity } from "sunder:contracts";

async function* events(context: RpcInvocationContext): AsyncIterable<JsonValue> {
  context.signal.throwIfAborted();
  yield { message: "native SEA stream" };
}

void runWorker({
  providers: [
    {
      providerId: "sunder.sea.smoke.provider",
      ...contractIdentity("contracts/example.rpc.json"),
      handler: {
        invokeUnary: () => ({ message: "native SEA unary" }),
        invokeServerStream: (context) => events(context),
      },
    },
  ],
}).catch(() => {
  process.exitCode = 70;
});
