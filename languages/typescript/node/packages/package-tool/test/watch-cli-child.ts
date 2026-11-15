// Windows-only test launcher. Production has no IPC shutdown hook and the CLI's
// actual stop handler must already be installed before the test can dispatch it.
const [watchCli, ...watchArguments] = process.argv.slice(2);
if (watchCli === undefined || process.send === undefined) throw new Error("Watch fixture requires a CLI path and IPC.");
process.argv = [process.execPath, watchCli, ...watchArguments];
process.once("message", message => {
  if (message !== "SIGINT" || process.listenerCount("SIGINT") === 0) throw new Error("The real watch shutdown handler is not ready.");
  process.disconnect();
  process.emit("SIGINT");
});
require(watchCli);
