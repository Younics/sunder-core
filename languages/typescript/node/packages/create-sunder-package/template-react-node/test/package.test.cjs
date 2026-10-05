const assert = require("node:assert/strict");
const test = require("node:test");
const descriptor = require("../.sunder/generated/rpc/contracts/messages.rpc.json");
const config = require("../sunder.package.json");

test("package identity, web view, and RPC V1 metadata are configured", () => {
  assert.equal(config.schemaVersion, 3);
  assert.equal(config.id, "sunderpackageidscaffoldtoken");
  assert.deepEqual(config.workerCapabilities, []);
  assert.equal(config.app.views[0].viewId, "sunderpackageidscaffoldtoken.main");
  assert.equal(config.app.views[0].route, "/");
  assert.deepEqual(config.rpc, { module: "src/rpc.ts", export: "packageRpc" });
  const receipt = require("../.sunder/generated/rpc/app-authoring-receipt.v1.json");
  assert.deepEqual(receipt.providers, []);
  assert.deepEqual(receipt.uses, [{ role: "app", required: true, actions: ["discover", "invoke", "subscribe"], contractKey: "messages" }]);

  assert.equal(descriptor.descriptorVersion, 1);
  assert.equal(descriptor.contractId, `${config.id}.messages`);
  assert.deepEqual(descriptor.errors, {});
  assert.equal(Array.isArray(descriptor.services), false);
  assert.equal(Array.isArray(descriptor.services.messages.methods), false);
  assert.deepEqual(descriptor.services.messages.methods.send, {
    kind: "unary",
    idempotency: "idempotent",
    errors: [],
    requestSchema: { $ref: "#/$defs/Request" },
    responseSchema: { $ref: "#/$defs/Response" },
  });
  assert.deepEqual(descriptor.services.messages.methods.watch, {
    kind: "server-stream",
    idempotency: "none",
    errors: [],
    requestSchema: { $ref: "#/$defs/Request" },
    eventSchema: { $ref: "#/$defs/Response" },
  });
});
