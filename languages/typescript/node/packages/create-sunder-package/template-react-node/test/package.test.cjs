const assert = require("node:assert/strict");
const test = require("node:test");
const descriptor = require("../contracts/example.rpc.json");
const config = require("../sunder.package.json");

test("package identity, web view, and RPC V2 metadata are configured", () => {
  assert.equal(config.schemaVersion, 2);
  assert.equal(config.id, "sunderpackageidscaffoldtoken");
  assert.deepEqual(config.workerCapabilities, []);
  assert.equal(config.app.views[0].viewId, "sunderpackageidscaffoldtoken.main");
  assert.equal(config.app.views[0].route, "/");
  assert.deepEqual(config.contracts, [{
    path: "contracts/example.rpc.json",
    exposure: "package",
  }]);
  assert.deepEqual(config.providers, [{
    providerId: "sunderpackageidscaffoldtoken.provider",
    role: "runtime",
    contractId: "example.messages",
    contractVersion: "2.0.0",
    audience: "owner",
  }]);
  assert.deepEqual(config.usesContracts, [{
    role: "app",
    contractId: "example.messages",
    contractVersion: "2.0.0",
    required: true,
    actions: ["discover", "invoke"],
  }]);
  assert.deepEqual(config.rpcBindings, [{
    path: "contracts/example.rpc.json",
    outputFile: "example-messages.g.ts",
  }]);

  assert.equal(descriptor.descriptorVersion, 2);
  assert.equal(descriptor.version, "2.0.0");
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
