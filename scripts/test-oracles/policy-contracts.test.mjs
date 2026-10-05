import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import { createRequire } from "node:module";
import test from "node:test";

const root = new URL("../../", import.meta.url);
const require = createRequire(new URL("languages/typescript/node/package.json", root));
const Ajv2020 = require("ajv/dist/2020.js").default;
const schema = await readJson("specs/policy/v1/contracts.schema.json");
const inventory = await readJson("specs/rpc/v1/authoring/package-public-inventory.schema.json");
const corpus = await readJson("specs/policy/v1/conformance.json");
const fixtureRoot = "languages/csharp/dotnet/tests/Sunder.Sdk.PublicApi.Tests/PolicyContracts/";
// Conditional branches intentionally inherit their object type from the enclosing schema.
const ajv = new Ajv2020({ strict: true, strictTypes: false, strictRequired: false, allErrors: true });
ajv.addSchema(inventory).addSchema(schema);

test("policy corpus covers every reviewed wire golden", async () => {
  assert.equal(corpus.schemaVersion, 1);
  assert.deepEqual(Object.keys(corpus.fixtures).sort(), (await readdir(new URL(fixtureRoot, root))).filter(name => name.endsWith(".json")).sort());
});

for (const [file, definition] of Object.entries(corpus.fixtures)) {
  test(`Draft 2020-12 policy schema validates ${file} and rejects incomplete envelopes`, async () => {
    const validate = ajv.getSchema(`${schema.$id}#/$defs/${definition}`);
    assert.equal(typeof validate, "function", `${file}: missing schema definition`);
    const data = await readJson(fixtureRoot + file);
    assert.equal(validate(data), true, `${file}: ${ajv.errorsText(validate.errors)}`);
    for (const key of Object.keys(data)) {
      const incomplete = structuredClone(data);
      delete incomplete[key];
      assert.equal(validate(incomplete), false, `${file}: missing ${key}`);
    }
    assert.equal(validate({ ...data, unrecognizedAuthority: true }), false);
  });
}

test("policy schema rejects nested inventory laundering and impossible execution permission", async () => {
  const plan = await readJson(fixtureRoot + "resolution-policy.json");
  const validatePlan = ajv.getSchema(`${schema.$id}#/$defs/resolutionPolicy`);
  plan.restoreProvenance[0].artifact.inventory.contracts[0].authorityKind = "unverified";
  assert.equal(validatePlan(plan), false);
  const permit = await readJson(fixtureRoot + "app-permit-result.json");
  permit.lease.clock = null;
  assert.equal(ajv.getSchema(`${schema.$id}#/$defs/appPermitResult`)(permit), false);
  const denied = await readJson(fixtureRoot + "runtime-platform-revoked.json");
  denied.disposition = "Allowed";
  assert.equal(ajv.getSchema(`${schema.$id}#/$defs/runtimePlatformPolicy`)(denied), false);
});

async function readJson(relativePath) {
  return JSON.parse(await readFile(new URL(relativePath, root), "utf8"));
}
