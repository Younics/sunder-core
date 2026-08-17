import { RpcError, runWorker, type JsonValue, type WorkerContext } from "@sunder/sdk";
import {
  createExampleRejectedRpcError,
  createExampleMessagesRpcProviderRegistration,
  tryGetExampleRejectedRpcErrorDetails,
  type ErrorDetails,
  type IMessagesProvider,
} from "../.sunder/generated/rpc/example-messages.g";

const NODE_CALLER_PACKAGE_ID = "sunder.interop.node.caller";
const DOTNET_PROVIDER_PACKAGE_ID = "test.worker.interop";
const NODE_TO_DOTNET_STATE_KEY = "interop.node-to-dotnet";
const PACKAGE_DATA_KEY = "worker-v3.conformance.value";
const PACKAGE_DATA_VALUE = "shared Worker V3 package data";

void runWorker((workerContext) => {
  const messagesService: IMessagesProvider = {
    async Send(_request, context) {
      context.signal.throwIfAborted();
      return { message: "native SEA unary" };
    },
    async Data(_request, context) {
      context.signal.throwIfAborted();
      await workerContext.state.set(PACKAGE_DATA_KEY, PACKAGE_DATA_VALUE, { signal: context.signal });
      const value = await workerContext.state.get(PACKAGE_DATA_KEY, { signal: context.signal });
      await workerContext.state.delete(PACKAGE_DATA_KEY, { signal: context.signal });
      if (value !== PACKAGE_DATA_VALUE) throw new Error("Native SEA package data round trip changed.");
      return { message: value };
    },
    async Fault(_request, context) {
      context.signal.throwIfAborted();
      throw createExampleRejectedRpcError(
        { reason: "conformance rejection", retryable: false },
        "rejected",
      );
    },
    async *Watch(_request, context) {
      context.signal.throwIfAborted();
      yield { message: "native SEA stream 1" };
      yield { message: "native SEA stream 2" };
    },
  };
  return {
    providers: [
      createExampleMessagesRpcProviderRegistration(
        "sunder.sea.smoke.provider",
        messagesService,
      ),
    ],
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
  const catalog = await context.rpc.discover("example.messages", { signal });
  const provider = catalog.providers.find((candidate) => candidate.packageId === DOTNET_PROVIDER_PACKAGE_ID);
  if (provider === undefined) throw new Error("The native .NET interoperability provider was not visible.");
  const request = { message: "Node to .NET" };
  const unary = await context.rpc.invoke<typeof request, { readonly message: string }>(
    provider.endpointReference,
    "messages",
    "send",
    request,
    { signal },
  );
  const stream: string[] = [];
  for await (const event of context.rpc.subscribe<typeof request, { readonly message: string }>(
    provider.endpointReference,
    "messages",
    "watch",
    request,
    { signal },
  )) {
    stream.push(event.message);
  }
  const data = await context.rpc.invoke<typeof request, { readonly message: string }>(
    provider.endpointReference,
    "messages",
    "data",
    request,
    { signal },
  );
  let faultCode: string | null = null;
  let faultDetails: ErrorDetails | null = null;
  try {
    await context.rpc.invoke<typeof request, JsonValue>(
      provider.endpointReference,
      "messages",
      "fault",
      request,
      { signal },
    );
  } catch (error) {
    if (!(error instanceof RpcError) || error.error.kind !== "domain") throw error;
    faultCode = error.error.code;
    faultDetails = tryGetExampleRejectedRpcErrorDetails(error) ?? null;
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
