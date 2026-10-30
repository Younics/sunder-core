import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import test from "node:test";
import { canonicalRpcDescriptorBytes, parseRpcContractDescriptor } from "@sunder/sdk";
import { consumerRequirements, consumerRequirementsOuter, readConsumerRequirements, requiredDefinitions, type RequiredMethod } from "../src/rpc-consumer-requirements";
import { satisfies } from "../src/rpc-evolution";
import { outerBytes } from "../src/rpc-baseline-json";

interface Operation { path: string; remove?: boolean; value?: unknown }
interface Recipe { id: string; satisfies: boolean; code?: string; source?: Operation[]; provider?: Operation[]; methods?: RequiredMethod[]; views?: Record<string, string[]>; exactOutputs?: string[] }
const fixture = JSON.parse(readFileSync(resolve(__dirname, "../../../../../../../specs/rpc/v1/authoring/consumer-requirements/cases.json"), "utf8")) as {
  baseline: Record<string, unknown>; methods: RequiredMethod[]; cases: Recipe[] };
function apply(value: Record<string, unknown>, operations: readonly Operation[] = []): void {
  for (const operation of operations) {
    const tokens = operation.path.slice(1).split("/").map(token => token.replaceAll("~1", "/").replaceAll("~0", "~"));
    let parent = value; for (const token of tokens.slice(0, -1)) parent = parent[token] as Record<string, unknown>;
    if (operation.remove) delete parent[tokens.at(-1)!]; else parent[tokens.at(-1)!] = structuredClone(operation.value);
  }
}
for (const recipe of fixture.cases) test(`consumer requirements: ${recipe.id}`, () => {
  const sourceJson = structuredClone(fixture.baseline); apply(sourceJson, recipe.source);
  const providerJson = structuredClone(sourceJson); apply(providerJson, recipe.provider);
  const source = parseRpcContractDescriptor(JSON.stringify(sourceJson)), provider = parseRpcContractDescriptor(JSON.stringify(providerJson));
  const methods = recipe.methods ?? fixture.methods;
  const requirements = consumerRequirements(source, methods, recipe.views, recipe.exactOutputs);
  const sourceBytes = canonicalRpcDescriptorBytes(source), providerBytes = canonicalRpcDescriptorBytes(provider);
  const evidence = outerBytes(consumerRequirementsOuter(requirements));
  const result = satisfies(requirements, provider);
  assert.equal(result.kind !== "Major", recipe.satisfies, JSON.stringify(result));
  if (recipe.code) assert.ok(result.changes.some(change => change.code === recipe.code), JSON.stringify(result));
  assert.deepEqual(canonicalRpcDescriptorBytes(source), sourceBytes); assert.deepEqual(canonicalRpcDescriptorBytes(provider), providerBytes);
  assert.deepEqual(outerBytes(consumerRequirementsOuter(readConsumerRequirements(consumerRequirementsOuter(requirements), source))), evidence);
  assert.deepEqual(outerBytes(consumerRequirementsOuter(consumerRequirements(source, [...methods].reverse(), recipe.views, recipe.exactOutputs))), evidence);
  assert.ok(!requiredDefinitions(requirements).includes("Unused"));
});
test("consumer requirements retain closure independently of the complete authenticated inventory", () => {
  const source = parseRpcContractDescriptor(JSON.stringify(fixture.baseline));
  const selected = [...fixture.methods]; const snapshot = consumerRequirements(source, selected); selected.length = 0;
  assert.equal(snapshot.methods.length, 1);
  assert.deepEqual(requiredDefinitions(consumerRequirements(source, fixture.methods)), ["Detail", "Error", "Input", "Other", "Output"]);
  assert.deepEqual(requiredDefinitions(consumerRequirements(source, fixture.methods, { Detail: ["keep"] })), ["Detail", "Error", "Input", "Output"]);
  assert.equal(Object.keys(source.$defs).length, 6);
});
test("consumer requirements match the shared canonical selector bytes", () => {
  const source = parseRpcContractDescriptor(JSON.stringify(fixture.baseline));
  const value = consumerRequirements(source, [{ serviceId: "main", methodId: "watch" }, ...fixture.methods], { Detail: ["keep"] });
  const bytes = readFileSync(resolve(__dirname, "../../../../../../../specs/rpc/v1/authoring/consumer-requirements/requirements.json"));
  assert.deepEqual(outerBytes(consumerRequirementsOuter(value)), bytes);
  assert.deepEqual(outerBytes(consumerRequirementsOuter(readConsumerRequirements(JSON.parse(bytes.toString()), source))), bytes);
});
test("consumer selectors reject unknown, duplicate, orphan and exact-schema views", () => {
  const source = parseRpcContractDescriptor(JSON.stringify(fixture.baseline));
  assert.throws(() => consumerRequirements(source, [{ serviceId: "main", methodId: "absent" }]));
  assert.throws(() => consumerRequirements(source, [...fixture.methods, ...fixture.methods]));
  const invalidViews: Record<string, string[]>[] = [{ Missing: [] }, { Unused: [] }, { Detail: ["missing"] }, { Output: ["name"], Detail: ["keep"] }];
  for (const views of invalidViews)
    assert.throws(() => consumerRequirements(source, fixture.methods, views));
  assert.throws(() => consumerRequirements(source, fixture.methods, { Detail: ["keep"] }, ["Detail"]));
  for (const member of ["contractId", "contractVersion", "descriptorSha256", "abiSha256"]) {
    const value = { ...consumerRequirementsOuter(consumerRequirements(source, fixture.methods)), [member]: "untrusted" };
    assert.throws(() => readConsumerRequirements(value, source));
  }
  const valid = consumerRequirementsOuter(consumerRequirements(source, [...fixture.methods, { serviceId: "main", methodId: "watch" }]));
  for (const changed of [{ ...valid, unsupported: true }, { ...valid, schemaVersion: "1" }, { ...valid, outputMembers: null },
    { ...valid, methods: [...valid.methods as unknown[]].reverse() }, { ...valid, methods: [fixture.methods[0], fixture.methods[0]] },
    { ...valid, outputMembers: { Detail: Array.from({ length: 257 }, () => "keep") } }])
    assert.throws(() => readConsumerRequirements(changed as import("../src/rpc-baseline-json").OuterObject, source));
});
