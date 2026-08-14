import { runWorker } from "@sunder/sdk";
import {
  createExampleMessagesRpcProviderRegistration,
  type IMessagesProvider,
} from "../.sunder/generated/rpc/example-messages.g";

const packageName = "__SUNDER_PACKAGE_NAME_JSON__";

const messagesService: IMessagesProvider = {
  async Send(request, context) {
    context.signal.throwIfAborted();
    return { message: `Hello from ${packageName}: ${request.message}` };
  },
  async *Watch(request, context) {
    context.signal.throwIfAborted();
    yield { message: `Watching ${request.message}` };
  },
};

void runWorker(() => ({
  providers: [
    createExampleMessagesRpcProviderRegistration(
      "__SUNDER_PROVIDER_ID_JSON__",
      messagesService,
    ),
  ],
})).catch(() => {
  process.exitCode = 70;
});
