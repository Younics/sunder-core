const assert = require("node:assert/strict");
const test = require("node:test");
const config = require("../sunder.package.json");

test("package identity is configured", () => {
  assert.equal(config.id, "SUNDER_PACKAGE_ID");
  assert.equal(config.providers[0].providerId, "SUNDER_PACKAGE_ID.provider");
});
