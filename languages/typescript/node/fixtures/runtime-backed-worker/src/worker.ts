import { RpcError, runWorker, type WorkerContext } from "@sunder/sdk";
import { Messages } from "#sunder/rpc";
import { initializePackageState, packageRpc } from "./rpc.js";

const NODE_CALLER_PACKAGE_ID = "sunder.interop.node.caller";
const DOTNET_PROVIDER_PACKAGE_ID = "test.worker.interop";
const NODE_TO_DOTNET_STATE_KEY = "interop.node-to-dotnet";
const PACKAGE_DATA_VALUE = "shared Worker V1 package data";

void runWorker((workerContext) => {
  initializePackageState(workerContext);
  return {
    providers: workerContext.packageId === NODE_CALLER_PACKAGE_ID ? [] : packageRpc.providerRegistrations,
    async onActivated(context, signal) {
      if (context.packageId !== NODE_CALLER_PACKAGE_ID) return;
      await runNodeToDotNetInterop(context, signal);
    },
  };
}).catch(() => {
  process.exitCode = 70;
});

async function runNodeToDotNetInterop(context: WorkerContext, signal: AbortSignal): Promise<void> {
  signal.throwIfAborted();
  const catalog = await context.rpc.discover(Messages.CONTRACT_ID, { signal });
  const provider = catalog.providers.find((candidate) => candidate.packageId === DOTNET_PROVIDER_PACKAGE_ID);
  if (provider === undefined) throw new Error("The native .NET interoperability provider was not visible.");
  const request = { message: "Node to .NET" };
  const client = new Messages.MessagesClient(context.rpc, provider.endpointReference);
  const unary = await client.Send(request, { signal });
  const stream: string[] = [];
  for await (const event of client.Watch(request, { signal })) {
    stream.push(event.message);
  }
  const data = await client.Data(request, { signal });
  let faultCode: string | null = null;
  let faultDetails: Messages.ErrorDetails | null = null;
  try {
    await client.Fault(request, { signal });
  } catch (error) {
    if (!(error instanceof RpcError) || error.error.kind !== "domain") throw error;
    faultCode = error.error.code;
    faultDetails = Messages.tryGetExampleRejectedRpcErrorDetails(error) ?? null;
  }
  if (unary.message !== "native .NET unary"
    || stream.join(",") !== "native .NET stream 1,native .NET stream 2"
    || data.message !== PACKAGE_DATA_VALUE
    || faultCode !== "example.rejected"
    || faultDetails?.reason !== "conformance rejection"
    || faultDetails.retryable) {
    throw new Error("Node to .NET Runtime Host RPC conformance failed.");
  }
  await context.state.set(NODE_TO_DOTNET_STATE_KEY, JSON.stringify({
    unary: unary.message,
    stream,
    data: data.message,
    faultCode,
    faultDetails,
  }), { signal });
}
