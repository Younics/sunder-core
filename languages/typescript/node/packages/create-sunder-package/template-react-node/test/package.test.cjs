const assert = require("node:assert/strict");
const test = require("node:test");
const config = require("../sunder.package.json");

test("package identity and web view are configured", () => {
  assert.equal(config.id, "__SUNDER_PACKAGE_ID_JSON__");
  assert.equal(config.providers[0].providerId, "__SUNDER_PROVIDER_ID_JSON__");
  assert.equal(config.app.views[0].viewId, "__SUNDER_VIEW_ID_JSON__");
  assert.equal(config.app.views[0].route, "/");
});
