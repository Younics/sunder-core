import { runWorker } from "@sunder/sdk";
import { packageRpc } from "./rpc.js";

void runWorker(() => ({
  providers: packageRpc.providerRegistrations,
})).catch(() => {
  process.exitCode = 70;
});
