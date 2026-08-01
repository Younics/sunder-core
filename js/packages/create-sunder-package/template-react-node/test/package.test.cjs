const assert = require("node:assert/strict");
const test = require("node:test");
const config = require("../sunder.package.json");

test("package identity and web view are configured", () => {
  assert.equal(config.id, "SUNDER_PACKAGE_ID");
  assert.equal(config.providers[0].providerId, "SUNDER_PACKAGE_ID.provider");
  assert.equal(config.app.views[0].viewId, "SUNDER_PACKAGE_ID.main");
  assert.equal(config.app.views[0].route, "/");
});
