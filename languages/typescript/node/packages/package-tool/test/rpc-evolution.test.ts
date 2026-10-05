import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import test from "node:test";
import { canonicalRpcAbiProjectionBytes, canonicalRpcDescriptorBytes, parseRpcContractDescriptor, RpcDescriptorError } from "@sunder/sdk";
import { classify, minimumVersion, minimumOwnedVersion, ordinal, validateVersion, type ChangeKind } from "../src/rpc-evolution";

const root = resolve(__dirname, "../../../../../../../specs/rpc/v1/authoring/evolution");
interface Operation { path: string; remove?: boolean; copy?: string; schema?: string; value?: unknown }
interface Expected { kind: ChangeKind; changes: [string, string][] }
const proof = JSON.parse(readFileSync(resolve(root, "proof-cases.json"), "utf8"));
const history = JSON.parse(readFileSync(resolve(root, "history-cases.json"), "utf8"));
function apply(value: Record<string, unknown>, operations: Operation[] = []): void {
  const tokens = (path: string): string[] => path.slice(1).split("/").map((key) => key.replaceAll("~1", "/").replaceAll("~0", "~"));
  for (const operation of operations) {
    const path = tokens(operation.path); const key = path.pop()!;
    const parent = path.reduce((obj, part) => obj[part] as Record<string, unknown>, value);
    if (operation.remove) delete parent[key];
    else Object.defineProperty(parent, key, { configurable: true, enumerable: true, writable: true,
      value: structuredClone(operation.copy ? tokens(operation.copy).reduce((obj, part) => obj[part] as Record<string, unknown>, value)
        : operation.schema ? proof.schemas[operation.schema] : operation.value) });
  }
}
function assertResult(expected: Expected, actual: ReturnType<typeof classify>): void {
  assert.equal(actual.kind, expected.kind);
  assert.deepEqual(actual.changes.map(({ path, code }) => [path, code]), [...expected.changes].sort((a, b) => ordinal(a[0], b[0]) || ordinal(a[1], b[1])));
}
for (const recipe of proof.cases) for (const inverse of recipe.inverse ? [false, true] : [false]) {
  test(`Format proof recipe ${recipe.id}${inverse ? " inverse" : ""}`, () => {
    const old = structuredClone(proof.baseline); apply(old, recipe.setup); apply(old, recipe.previous);
    const next = structuredClone(old); apply(next, recipe.candidate);
    let previous = parseRpcContractDescriptor(JSON.stringify(old)); let candidate = parseRpcContractDescriptor(JSON.stringify(next));
    if (inverse) [previous, candidate] = [candidate, previous];
    const before = canonicalRpcDescriptorBytes(previous); const after = canonicalRpcDescriptorBytes(candidate);
    const expected = inverse ? recipe.inverse : recipe;
    assertResult(expected, classify(previous, candidate)); assertResult(expected, classify(previous, candidate));
    assert.deepEqual(canonicalRpcDescriptorBytes(previous), before); assert.deepEqual(canonicalRpcDescriptorBytes(candidate), after);
  });
}
for (const recipe of proof.fourStateCases) for (const direction of ["request", "output"]) {
  test(`Format four-state ${recipe.from} -> ${recipe.to} ${direction}`, () => {
    const slot = direction === "request" ? "Input" : "Output"; const old = structuredClone(proof.baseline);
    old.$defs[slot] = structuredClone(proof.schemas[recipe.from]); const next = structuredClone(old); next.$defs[slot] = structuredClone(proof.schemas[recipe.to]);
    const changes: [string, string][] = recipe[direction + "Codes"].map((code: string) => [`/$defs/${slot}/properties/value`, code]);
    if (recipe[direction] === "Minor") changes.push([`/$defs/${slot}`, "schema-compatible"]);
    assertResult({ kind: recipe[direction], changes }, classify(parseRpcContractDescriptor(JSON.stringify(old)), parseRpcContractDescriptor(JSON.stringify(next))));
  });
}
for (const [previous, kind, expected] of history.minimumCases) test(`Format minimum ${previous} ${kind}`, () => assert.equal(minimumVersion(previous, kind), expected));
for (const recipe of history.cases) for (const reversed of [false, true]) {
  test(`Format history ${recipe.id}${reversed ? " reversed" : ""}`, () => {
    const descriptor = (recipe: { variant: string; version: string }) => {
      const value = structuredClone(proof.baseline); apply(value, history.variants[recipe.variant]); value.version = recipe.version;
      return parseRpcContractDescriptor(JSON.stringify(value));
    };
    const previous = recipe.history.map(descriptor); if (reversed) previous.reverse();
    const diagnostics = validateVersion(descriptor(recipe.candidate), previous);
    assert.equal(diagnostics.length, recipe.diagnostics.length ? 1 : 0);
    for (const text of recipe.diagnostics) assert.ok(diagnostics[0]!.includes(text), diagnostics[0]);
    if (diagnostics.length) { assert.ok(diagnostics[0]!.length <= 1024); assert.doesNotMatch(diagnostics[0]!, /Reviewed annotation|\$defs/); }
  });
}
test("proof/history fixtures are complete and contain unique frozen recipe IDs", () => {
  for (const fixture of [proof, history]) { assert.equal(fixture.formatVersion, 1); assert.equal(new Set(fixture.cases.map((row: { id: string }) => row.id)).size, fixture.cases.length); }
  assert.equal(proof.fourStateCases.length * 2, 32);
});
test("strict SemVer syntax, unknown classification, prerelease and arbitrary-digit overflow bounds", () => {
  for (const value of ["", "1.2", "01.2.3", "1.2.3 ", "1.2.3-01", "1.0.0+" + "a".repeat(251)]) assert.throws(() => minimumVersion(value, "Patch"));
  assert.throws(() => minimumVersion("1.0.0", "Unknown" as ChangeKind));
  for (const kind of ["Patch", "Minor", "Major"] as const) {
    assert.throws(() => minimumVersion("1.2.3-alpha+reviewed", kind), /prerelease-policy-required/);
    const make = (count: number) => kind === "Patch" ? `1.0.${"9".repeat(count)}` : kind === "Minor" ? `1.${"9".repeat(count)}.0` : `${"9".repeat(count)}.0.0`;
    assert.equal(minimumVersion(make(251), kind).length, 256);
    assert.throws(() => minimumVersion(make(252), kind), /version-limit-exceeded/);
    if (kind !== "Major") assert.ok(minimumOwnedVersion(make(252), kind).length <= 256);
    else assert.throws(() => minimumOwnedVersion(make(252), kind), /version-limit-exceeded/);
  }
  for (const minor of [false, true]) {
    const old = structuredClone(proof.baseline); old.version = minor ? `1.${"9".repeat(252)}.0` : `1.0.${"9".repeat(252)}`;
    const next = structuredClone(old); next.version = minor ? "2.0.0" : "1.1.0"; if (minor) next.$defs.Input.maxLength = 20;
    assert.deepEqual(validateVersion(parseRpcContractDescriptor(JSON.stringify(next)), [parseRpcContractDescriptor(JSON.stringify(old))]), []);
  }
});
test("reason truncation cannot hide Major; bounded proofs never print literals", () => {
  const old = structuredClone(proof.baseline); const next = structuredClone(old);
  for (let i = 0; i < 80; i++) next.services.main.methods[`added${String(i).padStart(3, "0")}`] = structuredClone(next.services.main.methods.call);
  delete next.errors["example.failed"].detailsSchema;
  const result = classify(parseRpcContractDescriptor(JSON.stringify(old)), parseRpcContractDescriptor(JSON.stringify(next)));
  assert.equal(result.kind, "Major"); assert.equal(result.changes.length, 64); assert.ok(result.changes.some((item) => item.code === "changes-truncated"));
  old.$defs.Input = { type: "string", const: "before" }; next.$defs.Input = { type: "string", const: "DO-NOT-PRINT", title: "DO-NOT-PRINT" };
  assert.doesNotMatch(JSON.stringify(classify(parseRpcContractDescriptor(JSON.stringify(old)), parseRpcContractDescriptor(JSON.stringify(next)))), /DO-NOT-PRINT/);
});
const record = (properties: Record<string, unknown>) => ({ type: "object", properties, required: [], additionalProperties: false });
test("65,536-step proof budget fails closed on otherwise compatible literal sets", () => {
  const old = structuredClone(proof.baseline); const next = structuredClone(old); const before: Record<string, unknown> = {}; const after: Record<string, unknown> = {};
  const values = Array.from({ length: 255 }, (_, i) => `v${i}`);
  for (let i = 0; i < 256; i++) { const key = `p${String(i).padStart(3, "0")}`; before[key] = { type: "string", enum: values }; after[key] = { type: "string", enum: [...values, "z"] }; }
  old.$defs.Input = record(before); next.$defs.Input = record(after);
  const result = classify(parseRpcContractDescriptor(JSON.stringify(old)), parseRpcContractDescriptor(JSON.stringify(next)));
  assert.equal(result.kind, "Major"); assert.ok(result.changes.some((item) => item.code === "analysis-limit-exceeded"));
});
test("1024 UTF-16 pointer bound escapes tokens and never truncates them", () => {
  for (const key of ["a".repeat(128), "😀".repeat(64), "~/".repeat(64)]) {
    const old = structuredClone(proof.baseline); const next = structuredClone(old);
    for (let i = 0; i < 10; i++) { old.$defs.Input = record({ [key]: old.$defs.Input }); next.$defs.Input = record({ [key]: i === 0 ? { type: "string", maxLength: 5 } : next.$defs.Input }); }
    const result = classify(parseRpcContractDescriptor(JSON.stringify(old)), parseRpcContractDescriptor(JSON.stringify(next)));
    assert.equal(result.kind, "Major"); assert.ok(result.changes.some((item) => item.code === "analysis-limit-exceeded")); assert.ok(result.changes.every((item) => item.path.length <= 1024));
  }
});
test("unused aliases and shared request/response slots require complete directional coverage", () => {
  for (const alias of [true, false]) {
    const old = structuredClone(proof.baseline);
    if (alias) old.$defs.Alias = { $ref: "#/$defs/Input" }; else old.services.main.methods.call.responseSchema = { $ref: "#/$defs/Input" };
    const next = structuredClone(old); next.$defs.Input.maxLength = 20;
    assert.equal(classify(parseRpcContractDescriptor(JSON.stringify(old)), parseRpcContractDescriptor(JSON.stringify(next))).kind, "Major");
  }
});

const inheritedNames = ["constructor", "toString", "__proto__", "hasOwnProperty", "valueOf", "isPrototypeOf", "propertyIsEnumerable", "toLocaleString", "__defineGetter__", "__lookupGetter__"];
for (const name of inheritedNames) for (const slot of ["errors", "services", "methods", "$defs", "properties"] as const) {
  test(`ABI own-member removal ${slot}/${name} classifies or rejects its wire grammar`, () => {
    const old = structuredClone(proof.baseline);
    const container = slot === "methods" ? old.services.main.methods : slot === "properties" ? {} : old[slot];
    const value = slot === "errors" ? {} : slot === "services" ? structuredClone(old.services.main) : slot === "methods" ? structuredClone(old.services.main.methods.call) : { type: "string", maxLength: 10 };
    Object.defineProperty(container, name, { value, configurable: true, enumerable: true, writable: true });
    if (slot === "properties") old.$defs.Input = record(container);
    const next = structuredClone(old);
    const nextContainer = slot === "methods" ? next.services.main.methods : slot === "properties" ? next.$defs.Input.properties : next[slot];
    delete nextContainer[name];
    // Only constructor satisfies the existing lowercase member-ID grammar.
    // Definition and property names have their existing broader grammars.
    if (name !== "constructor" && !["$defs", "properties"].includes(slot)) {
      assert.throws(() => parseRpcContractDescriptor(JSON.stringify(old)), RpcDescriptorError);
      return;
    }
    const previous = parseRpcContractDescriptor(JSON.stringify(old)); const candidate = parseRpcContractDescriptor(JSON.stringify(next));
    Object.freeze(previous.errors); Object.freeze(candidate.errors);
    const path = slot === "methods" ? `/services/main/methods/${name}` : slot === "properties" ? `/$defs/Input/properties/${name}` : `/${slot}/${name}`;
    const code = slot === "$defs" ? "definition-removed" : slot === "properties" ? "property-not-accepted" : `${slot.slice(0, -1)}-removed`;
    assertResult({ kind: "Major", changes: [[path, code]] }, classify(previous, candidate));
    const inverseCode = slot === "$defs" ? "definition-added" : slot === "properties" ? "schema-compatible" : `${slot.slice(0, -1)}-added`;
    assertResult({ kind: "Minor", changes: [[slot === "properties" ? "/$defs/Input" : path, inverseCode]] }, classify(candidate, previous));
  });
}
for (const name of inheritedNames) test(`ABI named-reference graph uses own definition ${name}`, () => {
  const old = structuredClone(proof.baseline);
  old.$defs = { ...old.$defs, [name]: { type: "string", maxLength: 10 }, Input: { $ref: `#/$defs/${name}` } };
  const next = structuredClone(old); next.$defs[name].maxLength = 20;
  const previous = parseRpcContractDescriptor(JSON.stringify(old)); const candidate = parseRpcContractDescriptor(JSON.stringify(next));
  const expected = [[`/$defs/${name}`, "schema-compatible"], ["/$defs/Input", "schema-compatible"]] as [string, string][];
  assertResult({ kind: "Minor", changes: expected }, classify(previous, candidate));
  assertResult({ kind: "Major", changes: [[`/$defs/${name}/maxLength`, "upper-bound-not-contained"]] }, classify(candidate, previous));
  const missing = structuredClone(old); delete missing.$defs[name];
  assert.throws(() => parseRpcContractDescriptor(JSON.stringify(missing)), RpcDescriptorError);
});
test("canonical ABI maps retain own prototype-sensitive and ordinal numeric keys without prototype writes", () => {
  const old = structuredClone(proof.baseline);
  const keys = [...inheritedNames, "2", "10"];
  old.$defs.Input = record(Object.fromEntries(keys.map((name) => [name, { type: "string" }])));
  const parsed = parseRpcContractDescriptor(JSON.stringify(old));
  const canonical = Buffer.from(canonicalRpcDescriptorBytes(parsed)); const abi = Buffer.from(canonicalRpcAbiProjectionBytes(parsed));
  const properties = parsed.$defs.Input!.properties!;
  assert.ok(typeof properties === "object");
  assert.equal(Object.getPrototypeOf(properties), null);
  assert.equal(Object.hasOwn(properties, "__proto__"), true);
  assert.equal(Object.hasOwn(properties, "constructor"), true);
  for (const bytes of [canonical, abi]) assert.ok(bytes.indexOf('"10"') < bytes.indexOf('"2"'));
  const reordered = structuredClone(old); reordered.$defs.Input.properties = Object.fromEntries(Object.entries(old.$defs.Input.properties).reverse());
  assertResult({ kind: "Unchanged", changes: [] }, classify(parsed, parseRpcContractDescriptor(JSON.stringify(reordered))));
  assert.deepEqual(Buffer.from(canonicalRpcDescriptorBytes(parsed)), canonical);
  assert.deepEqual(Buffer.from(canonicalRpcAbiProjectionBytes(parsed)), abi);
  assert.equal(Object.hasOwn(Object.prototype, "maxLength"), false);
});
