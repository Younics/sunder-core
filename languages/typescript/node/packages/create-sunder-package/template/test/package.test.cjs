const assert = require("node:assert/strict");
const test = require("node:test");
const { pathToFileURL } = require("node:url");
const { resolve } = require("node:path");
const config = require("../sunder.package.json");

test("messages greet, stream, and respect cancellation", async () => {
  const { messages } = await import(pathToFileURL(resolve(__dirname, "..", config.services.module)).href);
  const controller = new AbortController();
  const context = { signal: controller.signal };
  assert.equal(await messages.methods.send("Sunder", context), "Hello from __SUNDER_PACKAGE_NAME_JSON__: Sunder");
  const events = [];
  for await (const value of messages.methods.watch("Sunder", context)) events.push(value);
  assert.deepEqual(events, ["Watching Sunder"]);
  controller.abort();
  await assert.rejects(messages.methods.send("Sunder", context), { name: "AbortError" });
});
