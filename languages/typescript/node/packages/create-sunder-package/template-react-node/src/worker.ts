import {
  runWorker,
  type JsonValue,
  type RpcInvocationContext,
} from "@sunder/sdk";
import { contractIdentity } from "sunder:contracts";

const packageName = "__SUNDER_PACKAGE_NAME_JSON__";

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
      providerId: "__SUNDER_PROVIDER_ID_JSON__",
      ...contractIdentity("contracts/example.rpc.json"),
      handler: {
        invokeUnary: (_context, _serviceId, _methodId, request) => ({
          message: `Hello from ${packageName}: ${JSON.stringify(request)}`,
        }),
        invokeServerStream: (context, _serviceId, _methodId, request) => watch(context, request),
      },
    },
  ],
}).catch(() => {
  process.exitCode = 70;
});
