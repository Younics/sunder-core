import assert from "node:assert/strict";
import test from "node:test";
import { policy, synchronize } from "../toolchains.mjs";

test("build, CI, templates and Host policy retain the same exact development versions", async () => {
  await synchronize(true);
  assert.ok(policy.supportedDotnetRuntimeMajors.includes(policy.dotnetRuntimeMajor));
  assert.ok(policy.supportedNodeRuntimeMajors.includes(policy.nodeRuntimeMajor));
});
