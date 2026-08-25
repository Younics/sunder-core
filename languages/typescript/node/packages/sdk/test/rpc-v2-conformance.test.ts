import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import { resolve } from "node:path";
import test from "node:test";
import {
  RpcDescriptorError,
  canonicalRpcAbiProjectionBytes,
  canonicalRpcDescriptorBytes,
  computeRpcAbiSha256,
  computeRpcDescriptorSha256,
  parseRpcContractDescriptor,
  validateRpcInstance,
  type RpcContractDescriptorV2,
  type RpcDescriptorErrorCode,
} from "../src/index";

interface ConformanceFixture {
  readonly path: string;
  readonly valid: boolean;
  readonly schemaValid: boolean;
  readonly errorCode?: RpcDescriptorErrorCode;
  readonly descriptorSha256?: string;
  readonly abiSha256?: string;
  readonly canonicalDescriptorBytes?: number;
  readonly canonicalAbiProjectionBytes?: number;
}

interface ConformanceExpectations {
  readonly formatVersion: number;
  readonly descriptorVersion: number;
  readonly schemaProfile: string;
  readonly hashes: {
    readonly algorithm: string;
    readonly encoding: string;
    readonly descriptorInput: string;
    readonly abiInput: string;
    readonly abiDomainSeparatorHex: string;
  };
  readonly fixtures: readonly ConformanceFixture[];
}

interface RuntimeInstanceCorpus {
  readonly formatVersion: number;
  readonly descriptor: string;
  readonly cases: ReadonlyArray<{
    readonly name: string;
    readonly schema: string;
    readonly value: unknown;
    readonly valid: boolean;
  }>;
}

const conformanceRoot = resolve(__dirname, "../../../../../../../specs/rpc/v2/conformance");

async function indexedFixturePaths(directory: string, prefix = ""): Promise<string[]> {
  const paths: string[] = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const relative = prefix === "" ? entry.name : `${prefix}/${entry.name}`;
    if (entry.isDirectory()) paths.push(...await indexedFixturePaths(resolve(directory, entry.name), relative));
    else if (entry.isFile() && entry.name.endsWith(".rpc.json")) paths.push(relative);
  }
  return paths.sort();
}

test("TypeScript SDK executes every indexed RPC V2 conformance fixture", async () => {
  const expectationsPath = resolve(conformanceRoot, "expectations.json");
  const expectations = JSON.parse(await readFile(expectationsPath, "utf8")) as ConformanceExpectations;
  assert.equal(expectations.formatVersion, 1);
  assert.equal(expectations.descriptorVersion, 2);
  assert.equal(expectations.schemaProfile, "sunder.rpc.values.v1");
  assert.deepEqual(expectations.hashes, {
    algorithm: "sha256",
    encoding: "lowercase-hex",
    descriptorInput: "canonical-descriptor",
    abiInput: "domain-separator || normalized-canonical-abi-projection",
    abiDomainSeparatorHex: "73756e6465722e7270632e6162692e763200",
  });
  assert.ok(Array.isArray(expectations.fixtures) && expectations.fixtures.length > 0, "RPC V2 expectations must index at least one fixture.");

  const expectedPaths = expectations.fixtures.map((fixture) => fixture.path);
  assert.equal(new Set(expectedPaths).size, expectedPaths.length, "RPC V2 expectations contain duplicate fixture paths.");
  assert.deepEqual(await indexedFixturePaths(conformanceRoot), [...expectedPaths].sort(), "RPC V2 fixtures and expectations must index exactly the same files.");

  for (const fixture of expectations.fixtures) {
    assert.match(fixture.path, /^(?:valid|invalid)\/[a-z0-9-]+\.rpc\.json$/u);
    assert.equal(typeof fixture.valid, "boolean", `${fixture.path} must declare valid.`);
    assert.equal(typeof fixture.schemaValid, "boolean", `${fixture.path} must declare schemaValid.`);
    const source = await readFile(resolve(conformanceRoot, fixture.path));

    if (!fixture.valid) {
      assert.equal(typeof fixture.errorCode, "string", `${fixture.path} must declare errorCode.`);
      assert.throws(
        () => parseRpcContractDescriptor(source),
        (error: unknown) => error instanceof RpcDescriptorError && error.code === fixture.errorCode,
        `${fixture.path} must fail with '${fixture.errorCode}'.`,
      );
      continue;
    }

    assert.match(fixture.descriptorSha256 ?? "", /^[0-9a-f]{64}$/u, `${fixture.path} must declare descriptorSha256.`);
    assert.match(fixture.abiSha256 ?? "", /^[0-9a-f]{64}$/u, `${fixture.path} must declare abiSha256.`);
    assert.equal(Number.isSafeInteger(fixture.canonicalDescriptorBytes), true, `${fixture.path} must declare canonicalDescriptorBytes.`);
    assert.equal(Number.isSafeInteger(fixture.canonicalAbiProjectionBytes), true, `${fixture.path} must declare canonicalAbiProjectionBytes.`);

    const descriptor = parseRpcContractDescriptor(source);
    assert.equal(descriptor.descriptorVersion, 2, `${fixture.path} must remain a V2 descriptor.`);
    const descriptorV2 = descriptor as RpcContractDescriptorV2;
    assert.equal(descriptorV2.schemaProfile, expectations.schemaProfile);
    assert.equal(await computeRpcDescriptorSha256(descriptorV2), fixture.descriptorSha256, fixture.path);
    assert.equal(await computeRpcAbiSha256(descriptorV2), fixture.abiSha256, fixture.path);
    assert.equal(canonicalRpcDescriptorBytes(descriptorV2).byteLength, fixture.canonicalDescriptorBytes, fixture.path);
    assert.equal(canonicalRpcAbiProjectionBytes(descriptorV2).byteLength, fixture.canonicalAbiProjectionBytes, fixture.path);
  }
});

test("TypeScript SDK executes the shared RPC V2 runtime instance corpus", async () => {
  await verifyRuntimeInstanceCorpus(false);
});

test("RPC V2 runtime instance oracle rejects a seeded expectation mutation", async () => {
  await assert.rejects(() => verifyRuntimeInstanceCorpus(true), /uuid-lowercase/u);
});

async function verifyRuntimeInstanceCorpus(mutateFirstExpectation: boolean): Promise<void> {
  const corpus = JSON.parse(await readFile(resolve(conformanceRoot, "instances.json"), "utf8")) as RuntimeInstanceCorpus;
  assert.equal(corpus.formatVersion, 1);
  assert.ok(corpus.cases.length >= 20, "The shared runtime instance corpus lost its boundary coverage.");
  const descriptor = parseRpcContractDescriptor(await readFile(resolve(conformanceRoot, corpus.descriptor)));
  for (const [index, fixture] of corpus.cases.entries()) {
    const expected = mutateFirstExpectation && index === 0 ? !fixture.valid : fixture.valid;
    let valid = true;
    try {
      validateRpcInstance(descriptor, `#/$defs/${fixture.schema}`, fixture.value);
    } catch {
      valid = false;
    }
    assert.equal(valid, expected, fixture.name);
  }
}
