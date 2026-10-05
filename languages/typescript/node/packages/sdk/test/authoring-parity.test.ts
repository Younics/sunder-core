import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFile, readdir } from "node:fs/promises";
import { extname, resolve } from "node:path";
import test from "node:test";
import ts from "typescript";
import {
  canonicalRpcAbiProjectionBytes,
  canonicalRpcDescriptorBytes,
  computeRpcAbiSha256,
  computeRpcDescriptorSha256,
  isVersionInRange,
  parseRpcContractDescriptor,
} from "../src/index";

type JsonRecord = Record<string, any>;

interface SourceSpan {
  readonly text: string;
  readonly startLine: number;
  readonly startColumn: number;
  readonly endLine: number;
  readonly endColumn: number;
}

interface SourceFixture {
  readonly language: "csharp" | "typescript" | "javascript";
  readonly path: string;
  readonly spans: readonly SourceSpan[];
}

interface SourceIndexEntry {
  readonly path: string;
  readonly byteLength: number;
  readonly sha256: string;
}

interface ContractExpectation {
  readonly contractKey: string;
  readonly contractId: string;
  readonly contractVersion: string;
  readonly exposure: "package" | "public";
  readonly canonicalDescriptorBase64: string;
  readonly canonicalDescriptorBytes: number;
  readonly descriptorSha256: string;
  readonly canonicalAbiProjectionBase64: string;
  readonly canonicalAbiProjectionBytes: number;
  readonly abiSha256: string;
}

interface ValidFixture {
  readonly id: string;
  readonly packageId: string;
  readonly packageVersion: string;
  readonly sources: Readonly<Record<"csharp" | "typescript" | "javascript", string>>;
  readonly contracts: readonly ContractExpectation[];
  readonly providers: readonly JsonRecord[];
  readonly uses: readonly JsonRecord[];
  readonly generatedBindings: readonly JsonRecord[];
  readonly boundaries?: JsonRecord;
}

interface InvalidFixture {
  readonly id: string;
  readonly diagnosticId: string;
  readonly applicableLanguages: readonly SourceFixture["language"][];
  readonly sources: readonly SourceFixture[];
  readonly [key: string]: any;
}

interface ParityExpectations {
  readonly formatVersion: number;
  readonly status: string;
  readonly oracleScope: JsonRecord;
  readonly documentEncoding: JsonRecord;
  readonly languageBaselines: JsonRecord;
  readonly identityOracles: { readonly independentlyVerifiedEqual: boolean };
  readonly stableSourceDiagnosticIds: readonly string[];
  readonly validFixtures: readonly ValidFixture[];
  readonly invalidFixtures: readonly InvalidFixture[];
  readonly sourceOrdering: JsonRecord;
  readonly namingAndDefaults: JsonRecord;
  readonly packageToolCommandUx: JsonRecord;
  readonly selectedPackageProviderResolution: JsonRecord;
  readonly packagePresenceAndReadiness: JsonRecord;
  readonly compatiblePackageInstallation: JsonRecord;
  readonly artifactLocalProvenanceCoalescing: JsonRecord;
  readonly executableShapes: readonly JsonRecord[];
  readonly sourceLimits: JsonRecord;
  readonly fileIndex: JsonRecord;
}

const parityRoot = resolve(__dirname, "../../../../../../../specs/rpc/v1/authoring/parity");

test("Node independently seals the preimplementation parity corpus and exact file index", async () => {
  const expectations = await readExpectations();
  assert.equal(expectations.formatVersion, 1);
  assert.equal(expectations.status, "normative-preimplementation");
  assertOracleScope(expectations.oracleScope);
  assert.equal(expectations.languageBaselines.csharpLanguageVersion, "14.0");
  assert.equal(expectations.languageBaselines.typescriptVersion, "6.0.3");
  assert.equal(expectations.languageBaselines.ecmascriptTarget, "ES2023");
  assert.equal(expectations.identityOracles.independentlyVerifiedEqual, true);

  assert.deepEqual(expectations.validFixtures.map(fixture => fixture.id), [
    "complete",
    "provider-forms",
    "naming-defaults",
    "identity-limits",
  ]);
  const indexedSources = new Set<string>();
  for (const fixture of expectations.validFixtures) {
    assert.deepEqual(Object.keys(fixture.sources), ["csharp", "typescript", "javascript"]);
    for (const language of ["csharp", "typescript", "javascript"] as const) {
      const sourcePath = fixture.sources[language];
      assert.equal(languageFromPath(sourcePath), language);
      assert.equal(indexedSources.has(sourcePath), false, `${sourcePath} is indexed more than once.`);
      indexedSources.add(sourcePath);
    }

    const keys = fixture.contracts.map(contract => contract.contractKey);
    assert.deepEqual(keys, [...keys].sort());
    assert.equal(new Set(keys).size, keys.length);
    for (const contract of fixture.contracts) {
      assert.equal(contract.contractId, `${fixture.packageId}.${contract.contractKey.replaceAll("-", ".")}`);
      assert.equal(contract.contractVersion, fixture.packageVersion);
      await assertContractIdentity(contract);
    }
    assertFixtureInventories(fixture, keys);
  }
  assertFrozenProviderInventory(expectations.validFixtures);

  const stableDiagnostics = new Set(expectations.stableSourceDiagnosticIds);
  assert.equal(stableDiagnostics.size, 10);
  assert.equal(expectations.invalidFixtures.length, 16);
  for (const fixture of expectations.invalidFixtures) {
    assert.equal(stableDiagnostics.has(fixture.diagnosticId), true, fixture.diagnosticId);
    const sourceLanguages = fixture.sources.map(source => source.language);
    assert.equal(new Set(sourceLanguages).size, sourceLanguages.length);
    for (const language of fixture.applicableLanguages) assert.equal(sourceLanguages.includes(language), true, fixture.id);
    for (const source of fixture.sources) {
      assert.equal(languageFromPath(source.path), source.language);
      assert.equal(indexedSources.has(source.path), false, `${source.path} is indexed more than once.`);
      indexedSources.add(source.path);
      if (fixture.applicableLanguages.includes(source.language)) assert.notEqual(source.spans.length, 0, `${fixture.id}/${source.language}`);
      else assert.equal(source.spans.length, 0, `${fixture.id}/${source.language}`);
      const text = (await readFile(resolve(parityRoot, source.path))).toString("utf8");
      assertSpans(text, source);
    }
  }

  await assertFileIndex(expectations, indexedSources);
});

test("pinned TypeScript parses every Node fixture without native duplicate object keys", async () => {
  const expectations = await readExpectations();
  assert.equal(ts.version, expectations.languageBaselines.typescriptVersion);
  const indexed = expectations.fileIndex.sourceFiles as readonly SourceIndexEntry[];
  const paths = indexed.filter(file => file.path.endsWith(".ts") || file.path.endsWith(".js"))
    .map(file => resolve(parityRoot, file.path));
  const program = ts.createProgram(paths, {
    allowJs: true,
    alwaysStrict: true,
    checkJs: true,
    module: ts.ModuleKind.ESNext,
    noEmit: true,
    noResolve: true,
    skipLibCheck: true,
    strict: true,
    target: ts.ScriptTarget.ES2023,
  });

  for (const path of paths) {
    const source = program.getSourceFile(path);
    assert.ok(source, path);
    assert.deepEqual(program.getSyntacticDiagnostics(source).map(diagnostic => diagnostic.code), [], path);
    assert.deepEqual(
      program.getSemanticDiagnostics(source).filter(diagnostic => diagnostic.code === 1117).map(diagnostic => diagnostic.code),
      [],
      `${path} must not rely on TypeScript's duplicate-property diagnostic`,
    );
    assert.deepEqual(duplicateObjectKeys(source), [], `${path} contains duplicate static object keys`);
  }
});

test("Node independently validates naming, ordering, identity boundaries, and collision applicability", async () => {
  const expectations = await readExpectations();
  await assertNamingAndDefaults(expectations);
  assertSourceOrdering(expectations);
  await assertIdentityBoundaries(expectations);
  assertCollisionApplicability(expectations);
});

test("Node materializes deterministic language-neutral source-limit recipes", async () => {
  const expectations = await readExpectations();
  const sourceLimits = expectations.sourceLimits;
  const limits = sourceLimits.inclusiveLimits as JsonRecord;
  assert.deepEqual(limits, {
    normalizedRpcSignificantUtf8Bytes: 4_194_304,
    visitedSyntaxTypeExpressionNodes: 65_536,
    traversalReferenceDepth: 64,
    resolvedLocalConstantBindings: 4_096,
    contracts: 128,
    localProviderBindings: 128,
    contractOrProviderIdentityCharacters: 128,
  });
  assert.equal(sourceLimits.recipeSemantics.oracleKind, "deterministic-language-neutral-preimplementation-materialization");
  assert.equal(sourceLimits.recipeSemantics.frontendExecution, "deferred");

  const seenCounters = new Set<string>();
  for (const fixture of sourceLimits.recipeFixtures as readonly JsonRecord[]) {
    const counter = String(fixture.counter);
    assert.equal(seenCounters.has(counter), false, counter);
    seenCounters.add(counter);
    const maximum: number = Number((sourceLimits.inclusiveLimits as JsonRecord)[counter]);
    assert.deepEqual(fixture.cases, [
      { id: "boundary-valid", counterValue: maximum, expectedValid: true, expectedDiagnosticId: null },
      { id: "over-limit", counterValue: maximum + 1, expectedValid: false, expectedDiagnosticId: "rpc-authoring.source-limit-exceeded" },
    ]);
    await assertLimitMaterialization(fixture);
  }
  assert.deepEqual([...seenCounters].sort(), [
    "contracts",
    "localProviderBindings",
    "normalizedRpcSignificantUtf8Bytes",
    "resolvedLocalConstantBindings",
    "traversalReferenceDepth",
    "visitedSyntaxTypeExpressionNodes",
  ]);
  assert.equal(sourceLimits.materializedIdentityFixtures.counter, "contractOrProviderIdentityCharacters");
  assert.equal(sourceLimits.materializedIdentityFixtures.boundaryValid, "identity-limits");
  assert.deepEqual(sourceLimits.materializedIdentityFixtures.overLimit, [
    "source-limit-contract-identity",
    "source-limit-package-id-derived-contract",
    "source-limit-provider-identity",
  ]);
});

test("Node independently freezes package-tool commands and selected-package provider outcomes", async () => {
  const expectations = await readExpectations();
  assertPackageToolCommandUx(expectations.packageToolCommandUx);
  assertSelectedPackageProviderResolution(expectations);
  await assertPackagePresenceAndReadiness(expectations);
});

test("Node evaluates Compatible Installation independently of restore-selected package bytes", async () => {
  const expectations = await readExpectations();
  const oracle = expectations.compatiblePackageInstallation;
  assert.equal(oracle.status, "outcome-oracle");
  assert.equal(oracle.productionExecution, "deferred");
  assert.equal(oracle.decision, "user-selected-compatible-installation");
  assert.equal(oracle.scope, "one-candidate-and-contract-family-for-included-dependency-requirements");
  assert.equal(oracle.selectionSource, "selectedPackageProviderResolution");
  assert.deepEqual(oracle.baseRequirement, { reviewedRange: true, presence: "required" });
  const fields = ["contractId", "contractVersion", "descriptorSha256", "abiSha256"];
  assert.deepEqual(oracle.exactRpcFields, fields);
  assert.deepEqual(oracle.restoreOnlyFields, ["selectedPackageVersion", "selectedPackageContentIdentity"]);
  assert.equal(oracle.executionRuntimePins, "unchanged");
  assert.equal(oracle.platformAuthentication, "fail-closed-pending-authentication");
  const resolution = expectations.selectedPackageProviderResolution;
  const vectors = oracle.vectors as readonly JsonRecord[];
  assert.equal(vectors.length, 15);
  assert.equal(new Set(vectors.map(vector => vector.id)).size, vectors.length);

  // This is a candidate predicate oracle, not the still-gated production solver.
  for (const vector of vectors) {
    const requirements = (vector.requirementOverrides as readonly JsonRecord[]).map(overrides => ({
      ...resolution.selection, ...oracle.baseRequirement, ...overrides,
    } as JsonRecord));
    assert.ok(requirements.length > 0, vector.id);
    for (const requirement of requirements) {
      assert.ok(["required", "optional"].includes(requirement.presence), vector.id);
      assert.match(requirement.selectedPackageContentIdentity, /^[a-f0-9]{64}$/u);
      if (requirement.reviewedRange)
        assert.equal(isVersionInRange(requirement.selectedPackageVersion, requirement.requestedVersionRange), true, vector.id);
    }
    const candidate = { ...resolution.baseProvider, ...vector.candidateOverrides } as JsonRecord;
    const presence = requirements.some(requirement => requirement.presence === "required") ? "required" : "optional";
    const reason = requirements.some(requirement => !requirement.reviewedRange || typeof requirement.requestedVersionRange !== "string")
      ? "unreviewed-authority-range"
      : requirements.some(requirement => requirement.packageId !== candidate.packageId)
        ? "package-id-mismatch"
        : requirements.some(requirement => fields.some(field => requirement[field] !== requirements[0]![field]))
          ? "rpc-requirement-conflict"
          : requirements.some(requirement => !isVersionInRange(candidate.packageVersion, requirement.requestedVersionRange)
            || (!requirement.includePrerelease && String(candidate.packageVersion).split("+", 1)[0]!.includes("-")))
            ? "package-range-mismatch"
            : requirements.some(requirement => fields.some(field => requirement[field] !== candidate[field]))
              ? "rpc-identity-mismatch" : null;
    assert.deepEqual({ result: reason === null ? "success" : "rejected", reason, presence }, vector.expected, vector.id);
  }
});

test("Node coalesces owner and reexport provenance without comparing artifact-local source", async () => {
  const oracle = (await readExpectations()).artifactLocalProvenanceCoalescing;
  assert.equal(oracle.status, "outcome-oracle");
  assert.equal(oracle.productionExecution, "deferred");
  assert.equal(oracle.artifactSourceScope, "local-to-each-selected-artifact");
  const fields = ["contractId", "contractVersion", "descriptorBytes", "descriptorSha256", "abiSha256",
    "authorityKind", "authorityId", "selectedAuthorityRelease", "selectedAuthorityContentIdentity"];
  assert.deepEqual(oracle.agreementRequired, fields);
  const inventory = await readMetadata(oracle.inventoryPath) as JsonRecord;
  const owner = inventory.contracts[0] as JsonRecord;
  assert.equal(owner.artifactSource, "owned");
  assert.equal(owner.authorityKind, "package");
  assert.equal(owner.authorityId, inventory.packageId);
  assert.equal(owner.selectedAuthorityRelease, inventory.packageVersion);
  assert.equal(owner.selectedAuthorityContentIdentity, inventory.packageContentIdentity);
  const artifact = oracle.reexportArtifact as JsonRecord;
  assert.notEqual(artifact.packageId, inventory.packageId);
  const vectors = oracle.vectors as readonly JsonRecord[];
  assert.equal(vectors.length, 5);
  for (const vector of vectors) {
    const reexport = { ...owner, artifactSource: "imported", ...vector.recordOverrides } as JsonRecord;
    const localSourceValid = reexport.artifactSource === "imported"
      || (reexport.artifactSource === "owned" && reexport.authorityKind === "package"
        && reexport.authorityId === artifact.packageId
        && reexport.selectedAuthorityRelease === artifact.packageVersion
        && reexport.selectedAuthorityContentIdentity === artifact.packageContentIdentity);
    const coalesces = localSourceValid && fields.every(field => reexport[field] === owner[field]);
    assert.equal(coalesces, vector.coalesces, vector.id);
    if (coalesces) {
      assert.notEqual(reexport.artifactSource, owner.artifactSource, vector.id);
      assert.equal(inventory.contracts[0].artifactSource, "owned");
    }
  }
});

test("Node validates executable shapes against referenced schemas, artifacts, and exact projections", async () => {
  const expectations = await readExpectations();
  const shapes = expectations.executableShapes;
  assert.deepEqual(shapes.map(shape => shape.id), [
    "same-package-app-use",
    "whole-package-public-api-import",
    "explicit-package-provider-import",
    "explicit-platform-import",
  ]);
  for (const shape of shapes) {
    assert.equal(shape.status, "artifact-and-projection-oracle");
    assert.equal(shape.frontendExecution, "deferred");
    for (const [name, path] of Object.entries(shape.input as JsonRecord)) {
      if (name.endsWith("Schema")) await assertReferencedSchema(String(path));
    }
  }

  await assertSamePackageShape(expectations, shapes[0]!);
  await assertWholePackageShape(shapes[1]!);
  await assertPackageProviderShape(shapes[2]!);
  await assertPlatformImportShape(shapes[3]!);
});

async function assertFileIndex(expectations: ParityExpectations, indexedSources: Set<string>): Promise<void> {
  const index = expectations.fileIndex;
  assertExactKeys(index, [
    "root", "sourceFileCount", "validFixtureCount", "invalidFixtureCount", "canonicalOutputCount",
    "sourceFiles", "canonicalOutputs", "valid", "invalid",
  ]);
  assert.equal(index.root, "expectations.json");
  assert.equal(index.sourceFileCount, 60);
  assert.equal(index.validFixtureCount, expectations.validFixtures.length);
  assert.equal(index.invalidFixtureCount, expectations.invalidFixtures.length);
  assert.deepEqual(index.valid, expectations.validFixtures.map(fixture => ({ fixtureId: fixture.id, sources: fixture.sources })));
  assert.deepEqual(index.invalid, expectations.invalidFixtures.map(fixture => ({
    fixtureId: fixture.id,
    diagnosticId: fixture.diagnosticId,
    sources: Object.fromEntries(fixture.sources.map(source => [source.language, source.path])),
  })));

  const sourceFiles = index.sourceFiles as readonly SourceIndexEntry[];
  assert.equal(index.sourceFileCount, sourceFiles.length);
  assert.equal(sourceFiles.length, indexedSources.size);
  const paths = sourceFiles.map(file => file.path);
  assert.deepEqual(paths, [...paths].sort());
  assert.equal(new Set(paths).size, paths.length);
  assert.deepEqual(paths, [...indexedSources].sort());
  for (const source of sourceFiles) {
    assertExactKeys(source as unknown as JsonRecord, ["path", "byteLength", "sha256"]);
    const bytes = await readFile(resolve(parityRoot, source.path));
    assert.equal(bytes.byteLength, source.byteLength, source.path);
    assert.equal(createHash("sha256").update(bytes).digest("hex"), source.sha256, source.path);
    assertDeterministicBytes(bytes, source.path);
  }
  assert.deepEqual(await sourcePaths(parityRoot), paths);

  const outputs = expectations.validFixtures.flatMap(fixture => fixture.contracts.map(contract => ({
    fixtureId: fixture.id,
    contractKey: contract.contractKey,
    canonicalDescriptorBytes: contract.canonicalDescriptorBytes,
    descriptorSha256: contract.descriptorSha256,
    canonicalAbiProjectionBytes: contract.canonicalAbiProjectionBytes,
    abiSha256: contract.abiSha256,
  })));
  assert.equal(index.canonicalOutputCount, 7);
  assert.equal((index.canonicalOutputs as readonly unknown[]).length, 7);
  assert.deepEqual(index.canonicalOutputs, outputs);
}

async function assertContractIdentity(expected: ContractExpectation): Promise<void> {
  const descriptorBytes = Buffer.from(expected.canonicalDescriptorBase64, "base64");
  const abiBytes = Buffer.from(expected.canonicalAbiProjectionBase64, "base64");
  const descriptor = parseRpcContractDescriptor(descriptorBytes);
  assert.equal(descriptor.contractId, expected.contractId);
  assert.equal(descriptor.version, expected.contractVersion);
  assert.equal(descriptorBytes.byteLength, expected.canonicalDescriptorBytes);
  assert.equal(abiBytes.byteLength, expected.canonicalAbiProjectionBytes);
  assert.equal(await computeRpcDescriptorSha256(descriptor), expected.descriptorSha256);
  assert.equal(await computeRpcAbiSha256(descriptor), expected.abiSha256);
  assert.equal(Buffer.from(canonicalRpcDescriptorBytes(descriptor)).equals(descriptorBytes), true);
  assert.equal(Buffer.from(canonicalRpcAbiProjectionBytes(descriptor)).equals(abiBytes), true);
  assert.equal(createHash("sha256").update(descriptorBytes).digest("hex"), expected.descriptorSha256);
}

function assertFixtureInventories(fixture: ValidFixture, contractKeys: readonly string[]): void {
  assertUniqueOrdered(fixture.providers, "role", "providerId");
  for (const provider of fixture.providers) {
    assertExactKeys(provider, ["providerId", "role", "audience", "contractKey"]);
    assert.equal(provider.role, "runtime");
    assert.equal(contractKeys.includes(String(provider.contractKey)), true);
    assert.match(String(provider.providerId), /^[a-z0-9]+(?:\.[a-z0-9]+)*$/u);
    assert.ok(String(provider.providerId).length <= 128);
    const contract = fixture.contracts.find(item => item.contractKey === provider.contractKey);
    assert.ok(contract);
    if (contract.exposure === "package") assert.equal(provider.audience, "owner");
  }
  assertUniqueOrdered(fixture.uses, "role", "contractKey");
  assert.deepEqual(fixture.uses, []);
  assert.deepEqual(fixture.generatedBindings, contractKeys.map(contractKey => ({
    contractKey,
    csharpLogicalName: `Sunder.Generated.Rpc.${pascalFromSource(contractKey)}`,
  })));
}

function assertFrozenProviderInventory(fixtures: readonly ValidFixture[]): void {
  assert.deepEqual(fixtures.flatMap(fixture => fixture.providers.map(provider => [
    fixture.id,
    provider.providerId,
    provider.role,
    provider.audience,
    provider.contractKey,
  ].join("|"))), [
    "complete|example.authoring.parity.provider|runtime|owner|parity",
    "provider-forms|example.authoring.extensions.clock.owner|runtime|owner|clock",
    "provider-forms|example.authoring.extensions.clock.primary|runtime|public|clock",
    "naming-defaults|example.naming.o.auth2.provider|runtime|owner|o-auth2",
    "naming-defaults|example.naming.url.lookup.provider|runtime|owner|url-lookup",
    "identity-limits|example.boundary.provider.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.provider|runtime|owner|provider-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  ]);
}

function assertPackageToolCommandUx(ux: JsonRecord): void {
  assertExactKeys(ux, [
    "status", "toolExecution", "commandOwner", "excludedOwner", "commands",
    "packageSelectionOptions", "msbuildProperties", "selectionRules",
  ]);
  assert.equal(ux.status, "command-grammar-oracle");
  assert.equal(ux.toolExecution, "deferred");
  assert.equal(ux.commandOwner, "package-tools");
  assert.equal(ux.excludedOwner, "Sunder.Cli");

  const actualCommands: string[] = [];
  for (const command of ux.commands as readonly JsonRecord[]) {
    assertExactKeys(command, ["family", "operation", "positionals", "syntax", "msbuildTarget"]);
    const positionals = command.positionals as readonly JsonRecord[];
    for (const positional of positionals) {
      assertExactKeys(positional, ["name", "cardinality"]);
      assert.equal(["required", "optional"].includes(String(positional.cardinality)), true);
    }
    const argumentSyntax = positionals.map(positional => positional.cardinality === "required"
      ? `<${positional.name}>`
      : `[${positional.name}]`);
    const prefix = `sunder-package ${command.family} ${command.operation}`;
    assert.equal(command.syntax, prefix + (argumentSyntax.length === 0 ? "" : ` ${argumentSyntax.join(" ")}`));
    const positionalSummary = positionals.map(positional => `${positional.name}:${positional.cardinality}`).join(",");
    actualCommands.push([command.family, command.operation, positionalSummary, command.syntax, command.msbuildTarget].join("|"));
  }
  assert.deepEqual(actualCommands, [
    "api|add|package:required|sunder-package api add <package>|AddSunderPackageApi",
    "api|update|package:optional|sunder-package api update [package]|UpdateSunderPackageApi",
    "api|remove|package:required|sunder-package api remove <package>|RemoveSunderPackageApi",
    "api|list||sunder-package api list|ListSunderPackageApis",
    "api|require|package:required,contract:required|sunder-package api require <package> <contract>|RequireSunderPackageApi",
    "api|unrequire|package:required,contract:required|sunder-package api unrequire <package> <contract>|UnrequireSunderPackageApi",
    "api|implement|package:required,contract:required|sunder-package api implement <package> <contract>|ImplementSunderPackageApi",
    "api|unimplement|package:required,contract:required|sunder-package api unimplement <package> <contract>|UnimplementSunderPackageApi",
    "platform|implement|contract:required|sunder-package platform implement <contract>|ImplementSunderPlatformApi",
    "platform|update|contract:optional|sunder-package platform update [contract]|UpdateSunderPlatformApi",
    "platform|unimplement|contract:required|sunder-package platform unimplement <contract>|UnimplementSunderPlatformApi",
    "platform|list||sunder-package platform list|ListSunderPlatformApis",
    "contracts|restore||sunder-package contracts restore|RestoreSunderContracts",
    "contracts|check||sunder-package contracts check|CheckSunderContracts",
    "contracts|accept|contract-key:optional|sunder-package contracts accept [contract-key]|AcceptSunderContracts",
    "contracts|freeze|contract-key:optional|sunder-package contracts freeze [contract-key]|FreezeSunderContracts",
    "contracts|publication import|contract-key:optional|sunder-package contracts publication import [contract-key]|ImportSunderContractPublication",
  ]);

  const packageSelectionOptions = ux.packageSelectionOptions as readonly string[];
  assert.deepEqual(packageSelectionOptions, [
    "--version", "--registry", "--candidate", "--local-name", "--presence", "--include-prerelease", "--exclude-prerelease",
  ]);
  assert.deepEqual(ux.msbuildProperties, [
    "SunderPackageId",
    "SunderContractId",
    "SunderContractKey",
    "SunderPackageVersionRange",
    "SunderPackageIncludePrerelease",
    "SunderRegistryOrigin",
    "SunderPackageCandidatePath",
    "SunderPlatformCatalogPath",
    "SunderPackageApiLocalName",
    "SunderPackageApiPresence",
    "SunderContractCurrentVersion",
    "SunderContractNextVersion",
    "SunderAcknowledgeBreakingChange",
  ]);

  const rules = ux.selectionRules as JsonRecord;
  assertExactKeys(rules, ["newRegistryBackedSelection", "lockedOrigin", "globalUpdate", "prerelease", "presence", "nullableMsbuildPropertyOmission"]);
  const selection = rules.newRegistryBackedSelection as JsonRecord;
  assertExactKeys(selection, ["requiredOption", "requiredProperty", "candidateOption", "candidateWaivesRegistryRequirement"]);
  assert.equal(selection.requiredOption, "--registry");
  assert.equal(selection.requiredProperty, "SunderRegistryOrigin");
  assert.equal(selection.candidateOption, "--candidate");
  assert.equal(selection.candidateWaivesRegistryRequirement, false);

  const lockedOrigin = rules.lockedOrigin as JsonRecord;
  assertExactKeys(lockedOrigin, ["operations", "overrideOption", "overrideProperty", "overrideAllowed"]);
  assert.deepEqual(lockedOrigin.operations, ["update", "restore"]);
  assert.equal(lockedOrigin.overrideOption, "--registry");
  assert.equal(lockedOrigin.overrideProperty, "SunderRegistryOrigin");
  assert.equal(lockedOrigin.overrideAllowed, false);

  const globalUpdate = rules.globalUpdate as JsonRecord;
  assertExactKeys(globalUpdate, ["command", "rejectedOptionCategories", "rejectedOptions"]);
  assert.equal(globalUpdate.command, "sunder-package api update");
  assert.deepEqual(globalUpdate.rejectedOptionCategories, ["version", "prerelease", "origin", "candidate", "local-name", "presence"]);
  assert.deepEqual(globalUpdate.rejectedOptions, packageSelectionOptions);

  const prerelease = rules.prerelease as JsonRecord;
  assertExactKeys(prerelease, ["includeOption", "excludeOption", "optionsMutuallyExclusive", "msbuildProperty", "updateOmission", "createOmission"]);
  assert.equal(prerelease.includeOption, "--include-prerelease");
  assert.equal(prerelease.excludeOption, "--exclude-prerelease");
  assert.equal(prerelease.optionsMutuallyExclusive, true);
  assert.equal(prerelease.msbuildProperty, "SunderPackageIncludePrerelease");
  assert.equal(prerelease.updateOmission, "retain-locked-policy");
  assert.equal(prerelease.createOmission, false);

  const presence = rules.presence as JsonRecord;
  assertExactKeys(presence, [
    "operations", "option", "allowedValues", "msbuildProperty", "addOmission", "targetedUpdateOmission",
    "globalUpdate", "missingLockMember", "requiredContractsIndependent",
  ]);
  assert.deepEqual(presence.operations, ["api add <package>", "api update <package>"]);
  assert.equal(presence.option, "--presence");
  assert.deepEqual(presence.allowedValues, ["required", "optional"]);
  assert.equal(presence.msbuildProperty, "SunderPackageApiPresence");
  assert.equal(presence.addOmission, "write-required");
  assert.equal(presence.targetedUpdateOmission, "retain-locked-value");
  assert.equal(presence.globalUpdate, "reject");
  assert.equal(presence.missingLockMember, "invalid-no-default");
  assert.equal(presence.requiredContractsIndependent, true);
  assert.equal(rules.nullableMsbuildPropertyOmission, "absent");
}

function assertSelectedPackageProviderResolution(expectations: ParityExpectations): void {
  const resolution = expectations.selectedPackageProviderResolution;
  assertExactKeys(resolution, ["status", "frontendExecution", "resolverMembers", "filter", "discovery", "restoreEvidence", "selection", "baseProvider", "predicateVectors", "outcomes", "neverChooseAmongMultipleMatches", "retry"]);
  assert.equal(resolution.status, "outcome-oracle");
  assert.equal(resolution.frontendExecution, "deferred");

  const members = resolution.resolverMembers as JsonRecord;
  assertExactKeys(members, ["csharp", "node"]);
  assert.equal(members.csharp, "ResolveSelectedPackageAsync");
  assert.equal(members.node, "resolveSelectedPackage");
  const generatedApi = expectations.namingAndDefaults.generatedApiVectors as JsonRecord;
  assert.equal((generatedApi.csharpContractMembers as readonly string[]).includes(String(members.csharp)), true);
  assert.equal((generatedApi.nodeContractMembers as readonly string[]).includes(String(members.node)), true);

  const filter = resolution.filter as JsonRecord;
  const exactContractFields = ["contractId", "contractVersion", "descriptorSha256", "abiSha256"];
  assert.deepEqual(filter, {
    packageId: "exact",
    packageVersion: "requestedVersionRange-semver-precedence",
    prerelease: "includePrerelease-explicit-opt-in",
    state: "Active",
    exactContractFields,
    packageContentIdentity: "not-a-routing-filter",
  });
  assert.equal(resolution.discovery, "all-authorized-exact-contract-providers-without-package-range-or-active-filter");
  assert.equal(resolution.restoreEvidence, "exact-selected-artifact-content-and-inventory-not-runtime-routing");

  // Execute the predicate oracle, not the still-gated production resolver.
  const selection = resolution.selection as JsonRecord;
  for (const vector of resolution.predicateVectors as readonly JsonRecord[]) {
    const provider = { ...resolution.baseProvider, ...vector.providerOverrides } as JsonRecord;
    const discoverable = provider.authorized && exactContractFields.every(field => provider[field] === selection[field]);
    const range = vector.requestedVersionRange ?? selection.requestedVersionRange;
    const includePrerelease = vector.includePrerelease ?? selection.includePrerelease;
    assert.equal(["Active", "Inactive", "Faulted"].includes(provider.state), true, vector.id);
    const selected = discoverable
      && provider.packageId === selection.packageId
      && isVersionInRange(provider.packageVersion, range)
      && (includePrerelease || !String(provider.packageVersion).split("+", 1)[0]!.includes("-"))
      && provider.state === "Active";
    assert.equal(discoverable, vector.discoverable, vector.id);
    assert.equal(selected, vector.selected, vector.id);
  }

  const expectedOutcomes = [
    { matchCardinality: "zero", minimumMatches: 0, maximumMatches: 0, result: "error", diagnosticId: "rpc.provider-not-found", selection: "none" },
    { matchCardinality: "one", minimumMatches: 1, maximumMatches: 1, result: "success", diagnosticId: null, selection: "sole-match" },
    { matchCardinality: "multiple", minimumMatches: 2, maximumMatches: null, result: "error", diagnosticId: "rpc.provider-ambiguous", selection: "none" },
  ] as const;
  const outcomes = resolution.outcomes as readonly JsonRecord[];
  assert.equal(outcomes.length, expectedOutcomes.length);
  for (let index = 0; index < expectedOutcomes.length; index += 1) {
    const outcome = outcomes[index]!;
    const expected = expectedOutcomes[index]!;
    assertExactKeys(outcome, ["matchCardinality", "minimumMatches", "maximumMatches", "result", "diagnosticId", "selection"]);
    assert.equal(outcome.matchCardinality, expected.matchCardinality);
    assert.equal(outcome.minimumMatches, expected.minimumMatches);
    assert.equal(outcome.maximumMatches, expected.maximumMatches);
    assert.equal(outcome.result, expected.result);
    assert.equal(outcome.diagnosticId, expected.diagnosticId);
    assert.equal(outcome.selection, expected.selection);
  }
  assert.equal(resolution.neverChooseAmongMultipleMatches, true);
  assert.equal(resolution.retry, false);
}

async function assertPackagePresenceAndReadiness(expectations: ParityExpectations): Promise<void> {
  const oracle = expectations.packagePresenceAndReadiness;
  assertExactKeys(oracle, [
    "status", "frontendExecution", "allowedPresence", "lockToReceiptProjection", "dependencyCoalescing",
    "outcomes", "optionalProviderContinuity",
  ]);
  assert.equal(oracle.status, "outcome-and-projection-oracle");
  assert.equal(oracle.frontendExecution, "deferred");
  assert.deepEqual(oracle.allowedPresence, ["required", "optional"]);

  const projection = oracle.lockToReceiptProjection as JsonRecord;
  assertExactKeys(projection, ["lockPath", "receiptPath", "packageId", "lockPresence", "receiptPresence", "lockSha256"]);
  const lock = await readMetadata(projection.lockPath) as JsonRecord;
  const receipt = await readMetadata(projection.receiptPath) as JsonRecord;
  const packageId = String(projection.packageId);
  const lockPresence = lock.packageImports[packageId].presence;
  const receiptPresence = findBy(receipt.packageApiImports, "packageId", packageId).presence;
  assert.equal(lockPresence, projection.lockPresence);
  assert.equal(receiptPresence, projection.receiptPresence);
  assert.equal(receiptPresence, lockPresence);
  const lockSha256 = createHash("sha256").update(await readFile(resolve(parityRoot, projection.lockPath))).digest("hex");
  assert.equal(lockSha256, projection.lockSha256);
  assert.equal(receipt.lockSha256, lockSha256);

  const coalescing = oracle.dependencyCoalescing as JsonRecord;
  assertExactKeys(coalescing, [
    "agreementRequired", "rangeRule", "normalizedRangeRepresentation", "contentIdentityScope", "contributors", "normalized", "rule", "sourcePackageApiPresenceAfterCoalescing",
  ]);
  assert.deepEqual(coalescing.agreementRequired, ["packageId", "exactRpcIdentity"]);
  assert.equal(coalescing.rangeRule, "all-reviewed-ranges-must-hold");
  assert.equal(coalescing.normalizedRangeRepresentation, "oracle-conjunction-not-manifest-shape");
  assert.equal(coalescing.contentIdentityScope, "restore-provenance-only-not-install-coalescing");
  const contributors = coalescing.contributors as readonly JsonRecord[];
  assert.deepEqual(contributors.map(contributor => contributor.source), [
    "package-api-selected-artifact", "package-api-ultimate-authority", "provider-import",
  ]);
  for (const contributor of contributors) {
    assertExactKeys(contributor, ["source", "packageId", "versionRange", "reviewedRange", "selectedPackageVersion", "contentIdentity", "exactRpcIdentity", "presence"]);
    assert.equal((oracle.allowedPresence as readonly string[]).includes(String(contributor.presence)), true);
    assert.equal(contributor.reviewedRange, true);
    assert.equal(isVersionInRange(contributor.selectedPackageVersion, contributor.versionRange), true);
    assert.match(contributor.contentIdentity, /^[a-f0-9]{64}$/u);
  }
  assert.notEqual(contributors[0]!.contentIdentity, contributors[2]!.contentIdentity);
  const contributorGroups = new Map<string, JsonRecord[]>();
  for (const contributor of contributors) {
    const packageId = String(contributor.packageId);
    const group = contributorGroups.get(packageId) ?? [];
    group.push(contributor);
    contributorGroups.set(packageId, group);
  }
  const normalized = [...contributorGroups.entries()]
    .sort(([left], [right]) => Buffer.compare(Buffer.from(left), Buffer.from(right)))
    .map(([packageId, group]) => {
      for (const contributor of group.slice(1)) {
        for (const member of coalescing.agreementRequired as readonly string[])
          assert.deepEqual(contributor[member], group[0]![member], `${packageId} ${member}`);
      }
      return {
        packageId,
        versionRanges: [...new Set(group.map(contributor => String(contributor.versionRange)))].sort(),
        presence: group.some(contributor => contributor.presence === "required") ? "required" : "optional",
      };
    });
  assert.deepEqual(coalescing.normalized, normalized);
  assert.equal(coalescing.rule, "required-wins");
  const packageApiContributors = contributors.filter(contributor => String(contributor.source).startsWith("package-api-"));
  assert.deepEqual(coalescing.sourcePackageApiPresenceAfterCoalescing, packageApiContributors.map(contributor => ({
    source: contributor.source,
    packageId: contributor.packageId,
    presence: contributor.presence,
  })));
  assert.equal(packageApiContributors.every(contributor => contributor.presence === "optional"), true);
  assert.equal(contributors.filter(contributor => contributor.source === "provider-import").every(contributor => contributor.presence === "required"), true);

  assert.deepEqual(oracle.outcomes, [
    { packagePresence: "required", providerRequired: false, packageDisposition: "included", authorizedProviderAvailable: false, activation: "ready", selectedPackageIntegration: "unavailable" },
    { packagePresence: "required", providerRequired: true, packageDisposition: "included", authorizedProviderAvailable: false, activation: "blocked", selectedPackageIntegration: "unavailable" },
    { packagePresence: "optional", providerRequired: false, packageDisposition: "omitted-absent-or-incompatible", authorizedProviderAvailable: false, activation: "ready", selectedPackageIntegration: "unavailable" },
    { packagePresence: "optional", providerRequired: true, packageDisposition: "omitted-absent-or-incompatible", authorizedProviderAvailable: false, activation: "blocked", selectedPackageIntegration: "unavailable" },
  ]);
  const resolution = expectations.selectedPackageProviderResolution;
  const selection = resolution.selection as JsonRecord;
  const continuity = oracle.optionalProviderContinuity as readonly JsonRecord[];
  assert.equal(continuity.length, 6);
  for (const vector of continuity) {
    assert.equal(vector.packagePresence, "optional", vector.id);
    assert.equal(vector.selectedPackageDisposition, "omitted", vector.id);
    const provider = { ...resolution.baseProvider, ...vector.providerOverrides } as JsonRecord;
    const available = provider.authorized && provider.state === "Active"
      && ["contractId", "contractVersion", "descriptorSha256", "abiSha256"].every(field => provider[field] === selection[field]);
    const selected = available && provider.packageId === selection.packageId
      && isVersionInRange(provider.packageVersion, selection.requestedVersionRange)
      && (selection.includePrerelease || !String(provider.packageVersion).split("+", 1)[0]!.includes("-"));
    assert.equal(!vector.providerRequired || available ? "ready" : "blocked", vector.activation, vector.id);
    assert.equal(selected ? null : "rpc.provider-not-found", vector.selectedPackageResolutionDiagnosticId, vector.id);
  }
}

async function assertNamingAndDefaults(expectations: ParityExpectations): Promise<void> {
  const naming = expectations.namingAndDefaults;
  assert.deepEqual(naming.identityDefaults, {
    contractId: "<canonical-package-id>.<service-contract-suffix>",
    serviceId: "<service-key>",
    methodId: "<method-key>",
    providerId: "<contract-id>.provider",
    newContractVersion: "1.0.0",
    methodIdempotency: "none",
    methodErrors: [],
    zeroWireParameterRequestDefinition: "Unit",
    multiParameterRequestDefinition: "<ServiceDefinitionKey><MethodDefinitionKey>Request",
    implicitValueDefaults: false,
  });

  for (const vector of naming.frozenVectors as readonly JsonRecord[]) {
    const source = String(vector.source);
    const stripped = source.endsWith("Service") ? source.slice(0, -"Service".length)
      : source.endsWith("Async") ? source.slice(0, -"Async".length)
      : source;
    const tokens = tokenize(stripped);
    assert.equal(vector.stripped, stripped);
    assert.equal(vector.lowerKebab, tokens.map(asciiLower).join("-"));
    assert.equal(vector.pascal, tokens.map(pascalToken).join(""));
    assert.equal(vector.camel, asciiLower(tokens[0]!) + tokens.slice(1).map(pascalToken).join(""));
    assert.equal(vector.tokenDot, tokens.map(asciiLower).join("."));
  }

  const escaping = naming.keywordEscaping as JsonRecord;
  const keywords = await readJson(resolve(parityRoot, String(expectations.languageBaselines.keywordFixture))) as JsonRecord;
  assert.deepEqual(escaping.authoritativeArrays, [
    "csharpReservedKeywords",
    "csharpContextualKeywords",
    "ecmascriptStrictModuleBindingRestrictions",
  ]);
  const csharp = new Set<string>([...keywords.csharpReservedKeywords, ...keywords.csharpContextualKeywords]);
  const ecmascript = new Set<string>(keywords.ecmascriptStrictModuleBindingRestrictions);
  for (const example of escaping.examples as readonly JsonRecord[]) {
    assert.equal(example.csharp, csharp.has(String(example.sourceSymbol)) ? `@${example.sourceSymbol}` : example.sourceSymbol);
    assert.equal(example.typescriptJavaScript, ecmascript.has(String(example.sourceSymbol)) ? `_${example.sourceSymbol}` : example.sourceSymbol);
  }

  const api = naming.generatedApiVectors as JsonRecord;
  assert.equal(api.csharpNamespace, "Sunder.Generated.Rpc");
  assert.equal(api.packageLocalName.output, `${pascalFromSource(api.packageLocalName.input)}PackageApi`);
  assert.equal(api.providerImportKey.output, `I${pascalFromSource(api.providerImportKey.input)}Service`);
  const importedNaming = api.importedContractNaming as JsonRecord;
  assert.equal(importedNaming.contractKey, "canonical-contract-id");
  assert.equal(importedNaming.perContractAliases, false);
  assert.deepEqual(importedNaming.ignoredInputs, ["packageLocalName", "providerImportKey", "packageVersion", "packageContentIdentity"]);
  assert.equal(importedNaming.encoding, "C + uppercase-first-character-per-dot-segment + underscore-join");
  const importSymbols = new Set<string>();
  for (const vector of importedNaming.vectors as readonly JsonRecord[]) {
    const symbol = importedContractSymbol(vector.contractId);
    assert.equal(vector.symbol, symbol);
    assert.equal(importSymbols.has(symbol), false);
    importSymbols.add(symbol);
    assert.equal(asciiLower(symbol.slice(1).replaceAll("_", ".")), vector.contractId);
  }
  assert.deepEqual(api.csharpApiShape, {
    facadeKind: "namespace-level-class",
    bindingNamespace: "Sunder.Generated.Rpc.Imports.<contractSymbol>",
    contractProperty: "<contractSymbol>",
    contractPropertyType: "Sunder.Generated.Rpc.ImportApis.<contractSymbol>.Contract",
    types: ["Provider", "CatalogSnapshot", "CatalogEvent", "Client", "Contract"],
    providerImmutable: true,
    providerExposesEndpoint: false,
    catalogSnapshotMembers: ["Revision", "Sequence", "ResetRequired", "IReadOnlyList<Provider> Providers"],
    catalogEventMembers: ["Revision", "Sequence", "Kind", "Provider? Provider"],
    clientServiceProperties: "S + UpperInitial(serviceId), then escape original _ as _5f, . as _2e, - as _2d",
    providerInterfaceInherits: "all-descriptor-service-provider-interfaces",
    contractSignatures: [
      "ValueTask<CatalogSnapshot> ListAsync(CancellationToken cancellationToken = default)",
      "ValueTask<Provider?> GetAsync(Provider provider, CancellationToken cancellationToken = default)",
      "IAsyncEnumerable<CatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default)",
      "Client CreateClient(ISunderRpcCallScope scope, Provider provider)",
      "ValueTask<Provider> ResolveSelectedPackageAsync(CancellationToken cancellationToken = default)",
    ],
    getRevalidates: "exact-activation-endpoint-no-replacement",
    watchPreserves: ["revision", "sequence", "resets", "lifecycle-events"],
    clientCreatesScope: false,
    clientDisposesScope: false,
  });
  const serviceVectors = api.clientServiceNameVectors as readonly JsonRecord[];
  assertUniqueOrdered(serviceVectors, "serviceId");
  const serviceSymbols = new Set<string>();
  for (const vector of serviceVectors) {
    const serviceId = String(vector.serviceId);
    assert.match(serviceId, /^[a-z0-9](?:[a-z0-9._-]*[a-z0-9])?$/u);
    const symbol = `S${(serviceId[0]!.toUpperCase() + serviceId.slice(1))
      .replaceAll("_", "_5f").replaceAll(".", "_2e").replaceAll("-", "_2d")}`;
    assert.equal(vector.symbol, symbol);
    assert.equal(serviceSymbols.has(symbol), false);
    serviceSymbols.add(symbol);
    const decoded = symbol.slice(1).replace(/_(5f|2e|2d)/gu,
      (_, hex: string) => String.fromCharCode(Number.parseInt(hex, 16)));
    assert.equal(asciiLower(decoded), serviceId);
  }
  assert.deepEqual(api.csharpContractMembers, ["ListAsync", "GetAsync", "WatchAsync", "CreateClient", "ResolveSelectedPackageAsync"]);
  assert.deepEqual(api.nodeModuleExports, ["packageApis", "rpcImports"]);
  assert.deepEqual(api.nodeContractMembers, ["list", "get", "watch", "createClient", "resolveSelectedPackage"]);
}

function assertSourceOrdering(expectations: ParityExpectations): void {
  const ordering = expectations.sourceOrdering;
  assert.deepEqual(ordering.descriptorArrayRules, {
    sourceOrderPreserved: ["string-enum", "untagged-union-variants", "tagged-union-variants", "method-errors"],
    ascendingNumericValue: ["numeric-enum"],
  });
  assert.deepEqual(ordering.abiSetLikeArrays, [
    "method.errors",
    "closed-record.required",
    "primitive.enum",
    "nullable.oneOf",
    "tagged.oneOf",
  ]);
  const contract = findContract(findFixture(expectations.validFixtures, "complete"), "parity");
  const descriptor = JSON.parse(Buffer.from(contract.canonicalDescriptorBase64, "base64").toString("utf8")) as JsonRecord;
  const abi = JSON.parse(Buffer.from(contract.canonicalAbiProjectionBase64, "base64").toString("utf8")) as JsonRecord;
  const vectors = ordering.completeVectors as JsonRecord;
  assert.deepEqual(descriptor.$defs.StringStatus.enum, vectors.stringEnum.sourceAndDescriptor);
  assert.deepEqual(abi.$defs.StringStatus.enum, vectors.stringEnum.abi);
  assert.deepEqual(vectors.numericEnum.source, [10, -1, 2]);
  assert.deepEqual(descriptor.$defs.NumericPriority.enum, vectors.numericEnum.descriptor);
  assert.deepEqual(abi.$defs.NumericPriority.enum, vectors.numericEnum.abiCanonicalJsonByteOrder);
  assert.deepEqual(unionTags(descriptor.$defs.Choice.oneOf, descriptor.$defs), vectors.genericUnionWithExplicitDiscriminator.sourceAndDescriptorTags);
  assert.deepEqual(unionTags(abi.$defs.Choice.oneOf, abi.$defs), vectors.genericUnionWithExplicitDiscriminator.abiTags);
  assert.deepEqual(unionTags(descriptor.$defs.Event.oneOf, descriptor.$defs), vectors.taggedUnionWithSynthesizedDiscriminator.sourceAndDescriptorTags);
  assert.deepEqual(unionTags(abi.$defs.Event.oneOf, abi.$defs), vectors.taggedUnionWithSynthesizedDiscriminator.abiTags);
  assert.deepEqual(descriptor.services.parity.methods["round-trip"].errors, vectors.methodErrors.sourceAndDescriptor);
  assert.deepEqual(abi.services.parity.methods["round-trip"].errors, vectors.methodErrors.abi);
}

async function assertIdentityBoundaries(expectations: ParityExpectations): Promise<void> {
  const maximum = 128;
  const fixture = findFixture(expectations.validFixtures, "identity-limits");
  const boundaries = fixture.boundaries!;
  const contractBoundary = boundaries.contractId as JsonRecord;
  const contractId = `${fixture.packageId}.${String(contractBoundary.sourceContractKey).replaceAll("-", ".")}`;
  assert.equal(contractBoundary.value, contractId);
  assert.equal(contractBoundary.characters, maximum);
  assert.equal(contractId.length, maximum);
  assert.equal(contractBoundary.providerDeclared, false);
  assert.equal(findContract(fixture, String(contractBoundary.sourceContractKey)).contractId, contractId);

  const providerBoundary = boundaries.providerId as JsonRecord;
  const providerId = `${fixture.packageId}.${String(providerBoundary.sourceContractKey).replaceAll("-", ".")}.provider`;
  assert.equal(providerBoundary.value, providerId);
  assert.equal(providerBoundary.characters, maximum);
  assert.equal(providerId.length, maximum);
  assert.equal(fixture.providers.some(provider => provider.providerId === providerId), true);
  await assertIdentitySourcePresence(fixture, String(contractBoundary.sourceContractKey));
  await assertIdentitySourcePresence(fixture, String(providerBoundary.sourceContractKey));

  const overLimit = [
    { fixtureId: "source-limit-contract-identity", kind: "derived-contract-id-characters", actual: 129 },
    { fixtureId: "source-limit-package-id-derived-contract", kind: "derived-contract-id-characters", actual: 130 },
    { fixtureId: "source-limit-provider-identity", kind: "derived-provider-id-characters", actual: 129 },
  ] as const;
  for (const expected of overLimit) {
    const invalid = findInvalidFixture(expectations.invalidFixtures, expected.fixtureId);
    const limit = invalid.limit as JsonRecord;
    assert.equal(invalid.diagnosticId, "rpc-authoring.source-limit-exceeded");
    assert.equal(limit.kind, expected.kind);
    assert.equal(limit.maximum, maximum);
    assert.equal(limit.actual, expected.actual);
    const sourceContractKey = String(limit.sourceContractKey);
    const contract = `${invalid.packageId}.${sourceContractKey.replaceAll("-", ".")}`;
    const identity = limit.kind === "derived-provider-id-characters" ? `${contract}.provider` : contract;
    assert.equal(limit.derivedIdentity, identity);
    assert.equal(identity.length, expected.actual);
    assert.ok(identity.length > maximum);
    const csharp = invalid.sources.find(source => source.language === "csharp")!;
    assert.equal(csharp.spans[0]!.text, `${pascalFromSource(sourceContractKey)}Service`);
  }

  const packageFixture = findInvalidFixture(expectations.invalidFixtures, "source-limit-package-id-derived-contract");
  const packageId = String(packageFixture.packageId);
  const packageBoundary = packageFixture.packageIdBoundary as JsonRecord;
  assertExactKeys(packageBoundary, ["kind", "grammar", "maximum", "actual", "expectedValid"]);
  assert.equal(packageBoundary.kind, "canonical-package-id-characters");
  assert.equal(packageBoundary.grammar, "lowercase-dot-separated-ascii");
  assert.equal(packageBoundary.maximum, maximum);
  assert.equal(packageBoundary.actual, maximum);
  assert.equal(packageId.length, maximum);
  assert.equal(packageBoundary.expectedValid, true);
  assert.match(packageId, /^[a-z0-9]+(?:\.[a-z0-9]+)*$/u);
}

function assertCollisionApplicability(expectations: ParityExpectations): void {
  for (const id of expectations.namingAndDefaults.collisionFixtureIds as readonly string[]) {
    const fixture = findInvalidFixture(expectations.invalidFixtures, id);
    assert.equal(["rpc-authoring.identity-collision", "rpc-authoring.generated-name-collision"].includes(fixture.diagnosticId), true);
    assert.equal(fixture.applicableLanguages.includes("csharp"), true);
  }
  for (const id of ["collision-suffix-service", "collision-suffix-async", "collision-synthetic-request"]) {
    const fixture = findInvalidFixture(expectations.invalidFixtures, id);
    assert.deepEqual(fixture.applicableLanguages, ["csharp"]);
    assert.deepEqual(Object.keys(fixture.nonApplicableControls), ["typescript", "javascript"]);
  }
  assert.equal(findInvalidFixture(expectations.invalidFixtures, "collision-suffix-service").expectedIdentity, "example.collision.status");
  assert.equal(findInvalidFixture(expectations.invalidFixtures, "collision-suffix-async").expectedMethodId, "run");
  assert.equal(findInvalidFixture(expectations.invalidFixtures, "collision-synthetic-request").expectedGeneratedName, "OrderCreateRequest");

  assert.deepEqual(expectations.namingAndDefaults.sourceValidationPrecedenceFixtureIds, ["collision-post-escape-reserved"]);
  const precedence = findInvalidFixture(expectations.invalidFixtures, "collision-post-escape-reserved");
  assert.equal(precedence.diagnosticId, "rpc-authoring.value-invalid");
  assert.equal(precedence.expectedInvalidSourceName, "_await");
  for (const vectors of Object.values(precedence.generatedNameVectors as JsonRecord) as readonly JsonRecord[][]) {
    const invalid = vectors.find(vector => vector.source === "_await")!;
    assert.equal(invalid.generated, null);
    assert.equal(invalid.outcome, "invalid-initial-character");
  }
}

async function assertLimitMaterialization(fixture: JsonRecord): Promise<void> {
  const recipe = fixture.recipe as JsonRecord;
  const cases = fixture.cases as readonly JsonRecord[];
  switch (fixture.id) {
    case "normalized-source-bytes":
      assert.equal(recipe.paddingByte, 120);
      for (const language of fixture.languages as readonly string[]) {
        const seed = normalizeSource((await readFile(resolve(parityRoot, recipe.seedByLanguage[language]))).toString("utf8"));
        const replacement = recipe.replacementByLanguage[language] as JsonRecord;
        assert.notEqual(seed.indexOf(replacement.findFirst), -1, language);
        for (const item of cases) {
          const empty = replaceFirst(seed, replacement.findFirst, replacement.replaceWith.replace("<padding>", ""));
          const paddingLength = item.counterValue - Buffer.byteLength(empty);
          assert.ok(paddingLength >= 0, language);
          const materialized = replaceFirst(seed, replacement.findFirst, replacement.replaceWith.replace("<padding>", "x".repeat(paddingLength)));
          assert.equal(Buffer.byteLength(materialized), item.counterValue, language);
        }
      }
      break;
    case "visited-nodes":
      assert.match(recipe.frontendMapping, /^Deferred:/u);
      for (const item of cases) {
        const buckets = Array<number>(recipe.contractCount).fill(0);
        const keys = new Set<string>();
        for (let index = 0; index < item.counterValue; index++) {
          buckets[index % buckets.length]!++;
          keys.add(`Visit${index.toString().padStart(5, "0")}`);
        }
        assert.equal(keys.size, item.counterValue);
        assert.equal(buckets.reduce((sum, value) => sum + value, 0), item.counterValue);
        assert.ok(Math.max(...buckets) - Math.min(...buckets) <= 1);
      }
      break;
    case "traversal-reference-depth":
      for (const item of cases) {
        const definitions = Array.from({ length: item.counterValue }, (_, index) => `Depth${index.toString().padStart(2, "0")}`);
        assert.equal(new Set(definitions).size, item.counterValue);
        assert.equal(definitions.slice(1).length, item.counterValue - 1);
      }
      break;
    case "resolved-local-constants":
      for (const item of cases) {
        const buckets = Array<number>(recipe.contractCount).fill(0);
        const bindings = new Set<string>();
        for (let index = 0; index < item.counterValue; index++) {
          buckets[index % buckets.length]!++;
          bindings.add(`constant${index.toString().padStart(4, "0")}`);
        }
        assert.equal(bindings.size, item.counterValue);
        assert.equal(buckets.reduce((sum, value) => sum + value, 0), item.counterValue);
        assert.ok(Math.max(...buckets) <= 256);
      }
      break;
    case "contract-count":
      for (const item of cases) {
        const contracts = Array.from({ length: item.counterValue }, (_, index) => `contract-${index.toString().padStart(3, "0")}`);
        assert.equal(new Set(contracts).size, item.counterValue);
        for (const contract of contracts) assert.ok(`example.limit.${contract.replaceAll("-", ".")}`.length <= 128);
      }
      break;
    case "provider-count":
      for (const item of cases) {
        const providers = Array.from({ length: item.counterValue }, (_, index) => `example.limit.extension.provider${index.toString().padStart(3, "0")}`);
        assert.equal(new Set(providers).size, item.counterValue);
        for (const provider of providers) assert.ok(provider.length <= 128);
      }
      break;
    default:
      assert.fail(`Unknown source-limit recipe '${fixture.id}'.`);
  }
}

async function assertSamePackageShape(expectations: ParityExpectations, shape: JsonRecord): Promise<void> {
  const source = shape.input.sourceFixture as JsonRecord;
  const fixture = findFixture(expectations.validFixtures, source.fixtureId);
  const contract = findContract(fixture, source.contractKey);
  const exported = shape.expectedProjectExportContracts[0] as JsonRecord;
  assert.deepEqual(exported, {
    contractKey: contract.contractKey,
    contractId: contract.contractId,
    contractVersion: contract.contractVersion,
    exposure: contract.exposure,
    source: "owned",
    authorityKind: "package",
    authorityId: fixture.packageId,
    descriptorBytes: contract.canonicalDescriptorBytes,
    canonicalDescriptor: Buffer.from(contract.canonicalDescriptorBase64, "base64").toString("utf8"),
    descriptorSha256: contract.descriptorSha256,
    abiSha256: contract.abiSha256,
  });
  const schema = await readMetadata(shape.input.projectExportSchema) as JsonRecord;
  const rootMembers = ["schemaVersion", "producer", "projectIdentity", "configuration", "targetFramework", "inputFingerprint", "lockSha256", "packageId", "contracts"];
  assert.deepEqual(schema.required, rootMembers);
  assert.deepEqual(Object.keys(schema.properties), rootMembers);
  assert.equal(schema.maxProperties, rootMembers.length);
  assert.deepEqual(Object.keys(exported).sort(), [...schema.$defs.exportedContract.required].sort());

  const records = shape.expectedRecords as JsonRecord;
  assert.deepEqual(records.contracts, [receiptContractProjection(contract)]);
  assert.deepEqual(records.packageArtifacts, []);
  assert.deepEqual(records.bundles, [{ contractKey: contract.contractKey, source: "owned", authorityKind: "package", authorityId: fixture.packageId }]);
  for (const name of ["packageArtifactImports", "packageApiImports", "providerImports", "platformImports", "providers", "generatedPackageFacades"])
    assert.deepEqual(records[name], [], name);
  assert.deepEqual(records.uses, [shape.input.appPolicy]);
  assert.deepEqual(records.generatedBindings, [{ contractKey: contract.contractKey, logicalName: fixture.generatedBindings[0]!.csharpLogicalName }]);
}

async function assertWholePackageShape(shape: JsonRecord): Promise<void> {
  const receipt = await readMetadata(shape.input.recordDonorReceiptPath) as JsonRecord;
  const lock = await readMetadata(shape.input.lockPath) as JsonRecord;
  const inventory = await readMetadata(shape.input.publicInventoryPath) as JsonRecord;
  const records = shape.expectedRecords as JsonRecord;
  const selected = new Set<string>(records.contracts.map((contract: JsonRecord) => contract.contractKey));
  assertFilteredReceipt(receipt, records, selected, true);
  assertBundleAndBinding(records, records.contracts[0], "imported");
  for (const name of ["contracts", "bundles", "generatedBindings"])
    assertUniqueOrdered(receipt[name], "contractKey");
  assert.deepEqual(records.packageArtifacts, packageArtifactMapProjection(lock.packageArtifacts));
  assert.deepEqual(records.packageArtifactImports, projectPackageArtifactImports(records.packageArtifacts));

  const artifact = records.packageArtifacts[0] as JsonRecord;
  const packageApiImport = records.packageApiImports[0] as JsonRecord;
  const lockIntent = lock.packageImports[artifact.packageId] as JsonRecord;
  assert.equal(packageApiImport.presence, lockIntent.presence);
  const receiptSchema = await readMetadata(shape.input.receiptSchema) as JsonRecord;
  assert.deepEqual(Object.keys(packageApiImport).sort(), [...receiptSchema.$defs.packageApiImport.required].sort());
  assert.equal(artifact.packageId, inventory.packageId);
  assert.equal(artifact.selectedPackageVersion, inventory.packageVersion);
  assert.equal(artifact.selectedPackageContentIdentity, inventory.packageContentIdentity);
  assert.equal(artifact.publicContractCount, inventory.publicContractCount);
  for (const expected of artifact.contracts as readonly JsonRecord[]) {
    const actual = inventory.contracts.find((contract: JsonRecord) => contract.contractId === expected.contractId) as JsonRecord;
    for (const [name, value] of Object.entries(actual)) assert.deepEqual(expected[name === "contractVersion" ? "resolvedContractVersion" : name], value);
  }
  const platform = inventory.contracts.filter((contract: JsonRecord) => contract.authorityKind === "platform") as JsonRecord[];
  assert.deepEqual(shape.excludedPublicInventoryContracts, platform.map(contract => ({
    contractId: contract.contractId,
    contractVersion: contract.contractVersion,
    descriptorBytes: contract.descriptorBytes,
    descriptorSha256: contract.descriptorSha256,
    abiSha256: contract.abiSha256,
    reason: "authorityKind=platform",
  })));
}

async function assertPackageProviderShape(shape: JsonRecord): Promise<void> {
  const receipt = await readMetadata(shape.input.recordDonorReceiptPath) as JsonRecord;
  const lockBase = await readMetadata(shape.input.lockBasePath) as JsonRecord;
  const transformed = applyTransformations(lockBase, shape.input.lockTransformations);
  const records = shape.expectedRecords as JsonRecord;
  const contract = records.contracts[0] as JsonRecord;
  assert.deepEqual(contract, findBy(receipt.contracts, "contractKey", contract.contractKey));
  assert.deepEqual(records.packageArtifacts, packageArtifactMapProjection(transformed.packageArtifacts));
  assert.deepEqual(records.packageArtifactImports, projectPackageArtifactImports(records.packageArtifacts));

  const binding = shape.input.providerBinding as JsonRecord;
  assert.deepEqual(records.providers, [{
    providerId: binding.providerId,
    role: binding.role,
    audience: binding.audience,
    contractKey: contract.contractKey,
  }]);
  const selected = transformed.providerImports[binding.key] as JsonRecord;
  const providerImport = records.providerImports[0] as JsonRecord;
  assert.equal(providerImport.key, binding.key);
  assert.equal(providerImport.packageId, selected.packageId);
  assert.equal(providerImport.contractId, selected.contractId);
  assert.equal(providerImport.contractVersion, contract.contractVersion);
  assertExactKeys(providerImport, ["key", "packageId", "contractId", "contractVersion", "bundleRef", "providerRefs"]);
  assert.deepEqual(providerImport.bundleRef, { contractId: contract.contractId, contractVersion: contract.contractVersion });
  assert.deepEqual(providerImport.providerRefs, [{
    role: binding.role,
    providerId: binding.providerId,
    contractId: contract.contractId,
    contractVersion: contract.contractVersion,
  }]);
  assertBundleAndBinding(records, contract, "imported");
  assertImportedBundleFromArtifact(records, contract);
  for (const name of ["packageApiImports", "platformImports", "uses", "generatedPackageFacades"]) assert.deepEqual(records[name], [], name);
}

async function assertPlatformImportShape(shape: JsonRecord): Promise<void> {
  const receipt = await readMetadata(shape.input.recordDonorReceiptPath) as JsonRecord;
  const lock = await readMetadata(shape.input.lockPath) as JsonRecord;
  const transformed = applyTransformations(lock, shape.input.lockTransformations);
  const records = shape.expectedRecords as JsonRecord;
  const contract = records.contracts[0] as JsonRecord;
  assert.deepEqual(contract, findBy(receipt.contracts, "contractKey", contract.contractKey));
  assert.deepEqual(records.packageArtifacts, []);
  assert.deepEqual(transformed.packageArtifacts, {});
  assert.deepEqual(transformed.packageImports, {});

  const selected = transformed.platformImports[shape.input.lockKey] as JsonRecord;
  const imported = records.platformImports[0] as JsonRecord;
  assert.equal(imported.key, shape.input.lockKey);
  for (const name of [
    "authorityKind", "authorityId", "authorityReleaseRange", "includePrerelease", "selectedAuthorityRelease",
    "selectedAuthorityContentIdentity", "contractId",
  ]) assert.deepEqual(imported[name], selected[name], name);
  assert.equal(imported.contractVersion, selected.resolvedContractVersion);
  assert.deepEqual(imported.bundleRef, { contractId: contract.contractId, contractVersion: contract.contractVersion });
  assert.deepEqual(records.providers, filterByContractKeys(receipt.providers, new Set([contract.contractKey])));
  assertBundleAndBinding(records, contract, "imported");
  for (const name of [
    "authorityKind", "authorityId", "selectedAuthorityRelease", "selectedAuthorityContentIdentity",
  ]) assert.deepEqual(records.bundles[0][name], selected[name], `platform bundle ${name}`);
  assert.deepEqual(imported.providerRefs, records.providers.map((provider: JsonRecord) => ({
    role: provider.role,
    providerId: provider.providerId,
    contractId: contract.contractId,
    contractVersion: contract.contractVersion,
  })));
  for (const name of ["packageArtifactImports", "packageApiImports", "providerImports", "uses", "generatedPackageFacades"])
    assert.deepEqual(records[name], [], name);
}

function assertFilteredReceipt(receipt: JsonRecord, records: JsonRecord, selected: Set<string>, includeFacade: boolean): void {
  for (const name of ["contracts", "bundles", "providers", "uses", "generatedBindings"])
    assert.deepEqual(records[name], filterByContractKeys(receipt[name], selected), name);
  for (const name of ["packageArtifacts", "packageArtifactImports", "packageApiImports", "providerImports"])
    assert.deepEqual(records[name], receipt[name], name);
  assert.deepEqual(records.platformImports, []);
  if (includeFacade) assert.deepEqual(records.generatedPackageFacades, receipt.generatedPackageFacades);
}

function packageArtifactMapProjection(map: JsonRecord): JsonRecord[] {
  return Object.entries(map).map(([packageId, value]) => {
    const artifact = value as JsonRecord;
    return {
      packageId,
      ...artifact,
      contracts: Object.entries(artifact.contracts).map(([contractId, contract]) => ({ contractId, ...(contract as JsonRecord) })),
    };
  });
}

function projectPackageArtifactImports(artifacts: readonly JsonRecord[]): JsonRecord[] {
  return artifacts.map(artifact => {
    const authorityConstraints: JsonRecord[] = [];
    const seen = new Set<string>();
    for (const contract of artifact.contracts as readonly JsonRecord[]) {
      const identity = [contract.authorityKind, contract.authorityId, contract.selectedAuthorityRelease, contract.selectedAuthorityContentIdentity].join("|");
      if (seen.has(identity)) continue;
      seen.add(identity);
      authorityConstraints.push(Object.fromEntries([
        "authorityKind", "authorityId", "selectedAuthorityRelease", "selectedAuthorityContentIdentity",
      ].filter(name => name in contract).map(name => [name, contract[name]])));
    }
    return {
      packageId: artifact.packageId,
      requestedVersionRange: artifact.requestedVersionRange,
      includePrerelease: artifact.includePrerelease,
      selectedPackageVersion: artifact.selectedPackageVersion,
      selectedPackageContentIdentity: artifact.selectedPackageContentIdentity,
      publicContractCount: artifact.publicContractCount,
      publicInventorySha256: artifact.publicInventorySha256,
      authorityConstraints,
    };
  });
}

function applyTransformations(source: JsonRecord, transformations: readonly JsonRecord[]): JsonRecord {
  const result = structuredClone(source);
  for (const transformation of transformations) {
    const segments = String(transformation.path).split("/").filter(Boolean);
    let parent = result;
    for (const segment of segments.slice(0, -1)) parent = parent[segment] as JsonRecord;
    const leaf = segments.at(-1)!;
    if (transformation.operation === "remove") {
      assert.equal(leaf in parent, true, transformation.path);
      delete parent[leaf];
    } else if (transformation.operation === "set") {
      parent[leaf] = structuredClone(transformation.value);
    } else {
      assert.fail(`Unknown transformation '${transformation.operation}'.`);
    }
  }
  return result;
}

function assertBundleAndBinding(records: JsonRecord, contract: JsonRecord, source: string): void {
  assert.equal(records.bundles.length, 1);
  assert.equal(records.bundles[0].contractKey, contract.contractKey);
  assert.equal(records.bundles[0].source, source);
  assert.equal(source, "imported");
  assert.equal(contract.contractKey, contract.contractId);
  assert.deepEqual(records.generatedBindings, [{
    contractKey: contract.contractKey,
    logicalName: `Sunder.Generated.Rpc.Imports.${importedContractSymbol(contract.contractId)}`,
  }]);
}

function importedContractSymbol(contractId: string): string {
  assert.match(contractId, /^[a-z0-9]+(?:\.[a-z0-9]+)*$/u);
  return `C${contractId.split(".").map(segment => segment[0]!.toUpperCase() + segment.slice(1)).join("_")}`;
}

function assertImportedBundleFromArtifact(records: JsonRecord, contract: JsonRecord): void {
  const artifactContracts = records.packageArtifacts.flatMap((artifact: JsonRecord) => artifact.contracts) as JsonRecord[];
  const artifactContract = findBy(artifactContracts, "contractId", contract.contractId);
  for (const name of ["authorityKind", "authorityId", "selectedAuthorityRelease", "selectedAuthorityContentIdentity"])
    assert.deepEqual(records.bundles[0][name], artifactContract[name], `package bundle ${name}`);
}

function receiptContractProjection(contract: ContractExpectation): JsonRecord {
  return {
    contractKey: contract.contractKey,
    contractId: contract.contractId,
    contractVersion: contract.contractVersion,
    exposure: contract.exposure,
    descriptorBytes: contract.canonicalDescriptorBytes,
    canonicalDescriptor: Buffer.from(contract.canonicalDescriptorBase64, "base64").toString("utf8"),
    descriptorSha256: contract.descriptorSha256,
    abiSha256: contract.abiSha256,
  };
}

async function assertReferencedSchema(relativePath: string): Promise<void> {
  const schema = await readMetadata(relativePath) as JsonRecord;
  assert.equal(schema.$schema, "https://json-schema.org/draft/2020-12/schema");
  assert.equal(typeof schema.$id, "string");
  assert.equal(schema.additionalProperties, false);
  assert.notEqual(schema.required.length, 0);
}

function assertOracleScope(scope: JsonRecord): void {
  assert.equal(scope.sourceFrontendExecution, "deferred");
  assert.deepEqual(scope.currentlyExecutable, [
    "source byte length and SHA-256",
    "source syntax and duplicate-key hygiene",
    "canonical descriptor and ABI identity",
    "metadata projections and referenced artifact shapes",
    "language-neutral source-limit materialization",
    "package-tool command grammar and selected-provider outcome vectors",
    "package presence projection, dependency coalescing, and readiness outcomes",
    "Compatible Installation reviewed-range and exact-RPC outcome vectors",
    "artifact-local source and exact ultimate-provenance coalescing",
  ]);
  assert.deepEqual(scope.deferredUntilFrontendsExist, [
    "C# authoring source compilation",
    "TypeScript/JavaScript authoring static analysis",
    "frontend diagnostic emission",
    "frontend visited-node counter equivalence",
    "package-tool command execution",
    "selected-package provider resolver execution",
    "package presence command, solver, and readiness execution",
    "production Compatible Installation and unreviewed-authority-range migration",
  ]);
}

function duplicateObjectKeys(source: ts.SourceFile): string[] {
  const duplicates: string[] = [];
  const visit = (node: ts.Node): void => {
    if (ts.isObjectLiteralExpression(node)) {
      const names = new Set<string>();
      for (const property of node.properties) {
        const propertyName = property.name;
        const name = staticPropertyName(propertyName);
        if (name !== undefined && propertyName !== undefined && names.has(name)) duplicates.push(`${source.fileName}:${source.getLineAndCharacterOfPosition(propertyName.getStart()).line + 1}:${name}`);
        if (name !== undefined) names.add(name);
      }
    }
    ts.forEachChild(node, visit);
  };
  visit(source);
  return duplicates;
}

function staticPropertyName(name: ts.PropertyName | undefined): string | undefined {
  if (name === undefined || ts.isComputedPropertyName(name) || ts.isPrivateIdentifier(name)) return undefined;
  if (ts.isIdentifier(name) || ts.isStringLiteralLike(name) || ts.isNumericLiteral(name)) return name.text;
  return undefined;
}

function assertSpans(text: string, source: SourceFixture): void {
  const lines = text.split("\n");
  for (const span of source.spans) {
    assert.equal(span.startLine, span.endLine, source.path);
    assert.equal(lines[span.startLine - 1]?.slice(span.startColumn - 1, span.endColumn - 1), span.text, source.path);
  }
}

function assertDeterministicBytes(bytes: Buffer, relativePath: string): void {
  assert.ok(bytes.byteLength > 0, relativePath);
  assert.equal(bytes.at(-1), 0x0a, relativePath);
  assert.equal(bytes.includes(0x0d), false, relativePath);
  assert.equal(bytes.some(value => value > 0x7f), false, relativePath);
  assert.equal(bytes.subarray(0, 3).equals(Buffer.from([0xef, 0xbb, 0xbf])), false, relativePath);
}

function assertUniqueOrdered(values: readonly JsonRecord[], ...fields: readonly string[]): void {
  const keys = values.map(value => fields.map(field => String(value[field])).join("\0"));
  assert.equal(new Set(keys).size, keys.length);
  assert.deepEqual(keys, [...keys].sort());
}

async function assertIdentitySourcePresence(fixture: ValidFixture, contractKey: string): Promise<void> {
  const csharp = await readFile(resolve(parityRoot, fixture.sources.csharp), "utf8");
  assert.equal(csharp.includes(`${pascalFromSource(contractKey)}Service`), true);
  for (const language of ["typescript", "javascript"] as const) {
    const source = await readFile(resolve(parityRoot, fixture.sources[language]), "utf8");
    assert.equal(source.includes(`"${contractKey}"`), true);
  }
}

function unionTags(values: readonly JsonRecord[], definitions: JsonRecord): string[] {
  return values.map(value => definitions[String(value.$ref).split("/").at(-1)!].properties.kind.const);
}

function filterByContractKeys(values: readonly JsonRecord[], selected: Set<string>): JsonRecord[] {
  return values.filter(value => selected.has(String(value.contractKey)));
}

function findBy(values: readonly JsonRecord[], property: string, expected: string): JsonRecord {
  const matches = values.filter(value => value[property] === expected);
  assert.equal(matches.length, 1, `${property}=${expected}`);
  return matches[0]!;
}

function findFixture(fixtures: readonly ValidFixture[], id: string): ValidFixture {
  const fixture = fixtures.find(value => value.id === id);
  assert.ok(fixture, id);
  return fixture;
}

function findInvalidFixture(fixtures: readonly InvalidFixture[], id: string): InvalidFixture {
  const fixture = fixtures.find(value => value.id === id);
  assert.ok(fixture, id);
  return fixture;
}

function findContract(fixture: ValidFixture, key: string): ContractExpectation {
  const contract = fixture.contracts.find(value => value.contractKey === key);
  assert.ok(contract, key);
  return contract;
}

function tokenize(source: string): string[] {
  const tokens: string[] = [];
  let start = 0;
  for (let index = 0; index < source.length; index++) {
    const current = source[index]!;
    if (current === "_" || current === "-") {
      assert.ok(index > start && index + 1 < source.length, source);
      tokens.push(source.slice(start, index));
      start = index + 1;
      continue;
    }
    assert.match(current, /^[A-Za-z0-9]$/u);
    if (index === start || !/^[A-Z]$/u.test(current)) continue;
    const previous = source[index - 1]!;
    const nextIsLower = index + 1 < source.length && /^[a-z]$/u.test(source[index + 1]!);
    if (/^[a-z0-9]$/u.test(previous) || /^[A-Z]$/u.test(previous) && nextIsLower) {
      tokens.push(source.slice(start, index));
      start = index;
    }
  }
  tokens.push(source.slice(start));
  return tokens;
}

function pascalFromSource(value: string): string {
  return tokenize(value).map(pascalToken).join("");
}

function pascalToken(value: string): string {
  const lower = asciiLower(value);
  return `${lower[0]!.toUpperCase()}${lower.slice(1)}`;
}

function asciiLower(value: string): string {
  return value.replace(/[A-Z]/gu, character => character.toLowerCase());
}

function normalizeSource(value: string): string {
  return `${value.replace(/\r\n?/gu, "\n").replace(/\n+$/u, "")}\n`;
}

function replaceFirst(value: string, find: string, replacement: string): string {
  const index = value.indexOf(find);
  assert.notEqual(index, -1);
  return `${value.slice(0, index)}${replacement}${value.slice(index + find.length)}`;
}

function languageFromPath(path: string): SourceFixture["language"] {
  switch (extname(path)) {
    case ".cs": return "csharp";
    case ".ts": return "typescript";
    case ".js": return "javascript";
    default: assert.fail(`Unsupported source path '${path}'.`);
  }
}

function assertExactKeys(value: JsonRecord, expected: readonly string[]): void {
  assert.deepEqual(Object.keys(value).sort(), [...expected].sort());
}

async function readExpectations(): Promise<ParityExpectations> {
  return await readJson(resolve(parityRoot, "expectations.json")) as ParityExpectations;
}

async function readMetadata(relativePath: string): Promise<unknown> {
  return await readJson(resolve(parityRoot, relativePath));
}

async function readJson(path: string): Promise<unknown> {
  return JSON.parse(await readFile(path, "utf8")) as unknown;
}

async function sourcePaths(directory: string, prefix = ""): Promise<string[]> {
  const paths: string[] = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const relative = prefix === "" ? entry.name : `${prefix}/${entry.name}`;
    if (entry.isDirectory()) paths.push(...await sourcePaths(resolve(directory, entry.name), relative));
    else if (entry.isFile() && entry.name !== "expectations.json") paths.push(relative);
  }
  return paths.sort();
}
