import assert from "node:assert/strict";
import childProcess from "node:child_process";
import { mkdir, mkdtemp, readFile, rm, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import ts from "typescript";
import { canonicalRpcDescriptorBytes, computeRpcAbiSha256, parseRpcContractDescriptor } from "@sunder/sdk";
import { currentRid, loadProjectWithOwnedFrame, type LoadedProject } from "../src/model";
import { aggregateAndPack, bundleProduction, writeTargetLeaf } from "../src/package";
import { generateRpcBindingsForLoadedProject } from "../src/rpc-bindings";
import { emptyLock } from "./service-fixtures";
import { fields, ordered, outerBytes, parseOuter } from "../src/rpc-baseline-json";
import { emptyModelProjection, MAX_MODEL_PROJECTION_BYTES, MODEL_PROJECTION_PATH, MODEL_PROJECTION_ARCHIVE_PATH, modelProjectionBytes, modelProjectionOuter, parseModelProjectionDocument, projectionDocumentBytes, readModelProjection, serviceReferenceSchema, validateServiceReferenceSchema, validateProjectionDocument, validateModelProjectionSet, portableModelName } from "../src/rpc-model-projection";
import { hashBytes, inventoryBytes, parseContractLock, type ArtifactContract, type PackageArtifact } from "../src/rpc-contract-lock";
import { publishContractCache, readContractCache } from "../src/rpc-contract-cache";
import { MODEL_PROJECTION_DIRECTORY, modelProjectionReceiptBytes, publishModelProjectionCache, readModelProjectionCache } from "../src/rpc-model-projection-cache";
import { hydrateContractLock } from "../src/rpc-contract-restore";
import { inspectContractCandidate, type InspectedContractCandidate } from "../src/rpc-contract-candidate";
import { selectPackageArtifact } from "../src/rpc-package-selection";
import { packageApiCommand } from "../src/rpc-package-commands";
import { collectContractCache } from "../src/rpc-contract-cache-gc";
import { rebindFixtureProjection } from "./model-projection-fixtures";

async function fixture(extension: "ts" | "js", source?: string) {
  const root = await mkdtemp(join(tmpdir(), "sunder-projection-")); await mkdir(join(root, "src"));
  const types = extension === "ts" ? `import type {RpcContentReference} from "@sunder/sdk";
export type Attachment = RpcContentReference;
export type Label = string;
export type Status = "ready" | "busy";
export type Detail = { count: bigint };
export type Item = { name: Label; status?: Status | null; detail: Detail; attachment?: Attachment; next?: Item };
export type Secret = { token: string };`
    : `/** @typedef {import("@sunder/sdk").RpcContentReference} Attachment */
/** @typedef {string} Label */
/** @typedef {"ready" | "busy"} Status */
/** @typedef {{count: bigint}} Detail */
/** @typedef {{name: Label, status?: Status | null, detail: Detail, attachment?: Attachment, next?: Item}} Item */
/** @typedef {{token: string}} Secret */
export {};`;
  await writeFile(join(root, `src/models.${extension}`), types);
  await writeFile(join(root, `src/services.${extension}`), source ?? (extension === "ts" ? `import {defineService} from "@sunder/sdk";
import type {Item, Secret} from "./models.js";
export const first = defineService({ echo(value: Item): Item { return value; } }, {exposure:"public"});
export const second = defineService({ echo(value: Item): Item { return value; } }, {exposure:"public"});
export const privateService = defineService({ echo(value: Secret): Secret { return value; } });`
    : `// @ts-check
import {defineService} from "@sunder/sdk";
export const first = defineService({
/** @param {import("./models.js").Item} value @returns {import("./models.js").Item} */
echo(value) {return value;}}, {exposure:"public"});
export const second = defineService({
/** @param {import("./models.js").Item} value @returns {import("./models.js").Item} */
echo(value) {return value;}}, {exposure:"public"});
export const privateService = defineService({
/** @param {import("./models.js").Secret} value @returns {import("./models.js").Secret} */
echo(value) {return value;}});`));
  await writeFile(join(root, "sunder.package.json"), JSON.stringify({ schemaVersion: 3, id: "example.models", name: "Models", version: "1.0.0", workerCapabilities: [], services: { module: `src/services.${extension}` } }));
  await writeFile(join(root, "sunder-contracts.lock.json"), emptyLock);
  await writeFile(join(root, "package.json"), JSON.stringify({ type: "module", imports: { "#sunder/api": { types: "./.sunder/generated/rpc/api.d.ts", default: "./.sunder/generated/rpc/api.js" } } }));
  return { root, load: () => loadProjectWithOwnedFrame(root, { allowDrift: true }), dispose: () => rm(root, { recursive: true, force: true }) };
}
function evidence(project: LoadedProject) {
  return project.contracts.filter(contract => contract.exposure === "public").map(contract => ({ contractId: contract.descriptor.contractId, contractVersion: contract.descriptor.version,
    descriptorSha256: contract.descriptorSha256, abiSha256: contract.abiSha256, modelProjection: contract.modelProjection, descriptor: contract.descriptor, authorityKind: "package", authorityId: project.config.id }));
}
function selectedArtifact(project: LoadedProject, contentIdentity = "1".repeat(64), origin?: string) {
  const contracts = new Map<string, ArtifactContract>(project.contracts.filter(contract => contract.exposure === "public").map(contract => [contract.descriptor.contractId, {
    contractId: contract.descriptor.contractId, contractVersion: contract.descriptor.version, descriptorBytes: contract.canonicalBytes.length,
    descriptorSha256: contract.descriptorSha256, abiSha256: contract.abiSha256, artifactSource: "owned", authorityKind: "package", authorityId: project.config.id,
    selectedAuthorityRelease: project.config.version, selectedAuthorityContentIdentity: contentIdentity, etag: `"${contract.descriptorSha256}"`,
  }]));
  const body = Buffer.from(project.ownedRpc!.files.get(MODEL_PROJECTION_PATH)!);
  const record: PackageArtifact = { packageId: project.config.id, requestedVersionRange: "1.0.0", includePrerelease: false, selectedPackageVersion: "1.0.0",
    selectedPackageContentIdentity: contentIdentity, registryOrigin: origin, publicContractCount: contracts.size, publicInventorySha256: "", modelProjectionBytes: body.length, modelProjectionSha256: hashBytes(body), contracts };
  const { createHash } = require("node:crypto") as typeof import("node:crypto");
  const inventoryHash = createHash("sha256").update("sunder.package-public-api-inventory.v1\0").update(inventoryBytes(record)).digest("hex");
  const lock = parseContractLock(outerBytes({ lockVersion: 1, packageArtifacts: { [record.packageId]: {
    requestedVersionRange: record.requestedVersionRange, includePrerelease: false, selectedPackageVersion: record.selectedPackageVersion, selectedPackageContentIdentity: record.selectedPackageContentIdentity,
    ...(origin === undefined ? {} : {registryOrigin:origin}),
    publicContractCount: record.publicContractCount, publicInventorySha256: inventoryHash, modelProjectionBytes: body.length, modelProjectionSha256: hashBytes(body),
    contracts: ordered([...contracts].map(([id, value]) => [id, { artifactSource: value.artifactSource, authorityKind: value.authorityKind, authorityId: value.authorityId,
      selectedAuthorityRelease: value.selectedAuthorityRelease, selectedAuthorityContentIdentity: value.selectedAuthorityContentIdentity, resolvedContractVersion: value.contractVersion,
      descriptorBytes: value.descriptorBytes, descriptorSha256: value.descriptorSha256, abiSha256: value.abiSha256, etag: value.etag }])) } },
    packageImports: { [record.packageId]: { localName: "dependency", presence: "required", requiredContracts: [], consumerRoles: ["runtime"] } }, providerImports: {}, platformImports: {} }));
  return { body, lock, descriptors: new Map(project.contracts.filter(contract => contract.exposure === "public").map(contract => [contract.descriptorSha256, contract.canonicalBytes])) };
}

test("TS/checkJs source-wide models, transparent aliases and recursive closures share type-only token namespaces and filter private evidence", async () => {
  let expected: Buffer | undefined;
  for (const extension of ["ts", "js"] as const) {
    const c = await fixture(extension);
    try {
      const project = await c.load(); const publicContracts = evidence(project);
      for (const contract of publicContracts) {
        assert.deepEqual({ ...contract.modelProjection.models }, { Attachment: "Attachment", Detail: "Detail", Item: "Item", Label: "Label", Status: "Status" });
        assert.equal((contract.descriptor.$defs.Attachment as Record<string,unknown>).logicalType,"content-reference");
      }
      assert.deepEqual(modelProjectionBytes(publicContracts[0]!.modelProjection), modelProjectionBytes(publicContracts[1]!.modelProjection));
      const bytes = Buffer.from(project.ownedRpc!.files.get(MODEL_PROJECTION_PATH)!);
      if (expected) assert.deepEqual(bytes, expected); else expected = bytes;
      const document = parseModelProjectionDocument(bytes); validateProjectionDocument(document, project.config.id, project.config.version, publicContracts);
      assert.equal(document.contracts.length, 2); assert.doesNotMatch(bytes.toString(), /Secret|private\.service/);
      for (const name of ["authoring-receipt.v1.json", "package-export.v1.json"]) {
        const json = JSON.parse(project.ownedRpc!.files.get(name)!); const secret = json.contracts.find((entry: {contractKey:string}) => entry.contractKey === "private-service");
        assert.deepEqual(secret.modelProjection.models, {Secret:"Secret"});
        assert.equal(Object.keys(secret)[Object.keys(secret).indexOf("abiSha256") + 1], "modelProjection");
      }
      await generateRpcBindingsForLoadedProject(project);
      const api = project.ownedRpc!.files.get("api.d.ts")!; assert.match(api, /namespace SelfModels/); assert.match(api, /type Item = \$binding\d+\.Item/);
      assert.match(project.ownedRpc!.files.get("owned/first.ts")!, /type Item = Exclude<Parameters/);
      assert.match(project.ownedRpc!.files.get("owned/second.d.ts")!, /type Item = import\("\.\/first.types.js"\).Item/);
      assert.doesNotMatch(project.ownedRpc!.files.get("api.js")!, /SelfModels/);
      for (const [name, text] of project.ownedRpc!.files) if (name.endsWith(".d.ts") && (name.startsWith("owned/") || name === "api.d.ts")) assert.ok(!text.includes(c.root), `${name}\n${text}`);
      const consumer = join(c.root, `consumer.${extension}`);
      await writeFile(consumer, extension === "ts" ? `import type {SelfModels} from "#sunder/api"; export const value: SelfModels.Item = {name:"ok",status:"ready",detail:{count:1n}};`
        : `/** @type {import("#sunder/api").SelfModels.Item} */ export const value = {name:"ok",status:"ready",detail:{count:1n}};`);
      const program = ts.createProgram([consumer], { target: ts.ScriptTarget.ES2023, module: ts.ModuleKind.ESNext, moduleResolution: ts.ModuleResolutionKind.Bundler, strict: true, allowJs: true, checkJs: true, noEmit: true,
        types: [], skipLibCheck: false, paths: { "@sunder/sdk/browser": [require.resolve("@sunder/sdk/browser").replace(/\.js$/u, ".d.ts")], "@sunder/sdk/generated": [require.resolve("@sunder/sdk/generated").replace(/\.js$/u, ".d.ts")], "@sunder/sdk/core": [require.resolve("@sunder/sdk/core").replace(/\.js$/u, ".d.ts")] } });
      assert.deepEqual(ts.getPreEmitDiagnostics(program).map(value => `${value.file?.fileName}: ${ts.flattenDiagnosticMessageText(value.messageText, "\n")}`), [], program.getSourceFiles().filter(file => file.fileName.startsWith(c.root)).map(file => file.fileName).join("\n"));
    } finally { await c.dispose(); }
  }
});

test("distinct equal-shaped declarations collide by portable source name across contracts, never merge by schema", async () => {
  const c = await fixture("ts", `import {defineService} from "@sunder/sdk"; import type {Item as A} from "./a.js"; import type {Item as B} from "./b.js";
export const first = defineService({echo(value:A):A{return value;}}); export const second = defineService({echo(value:B):B{return value;}});`);
  try {
    for (const name of ["a", "b"]) await writeFile(join(c.root, `src/${name}.ts`), "export type Item = {value:string};");
    await assert.rejects(c.load(), /identity-collision:.*Model 'Item'.*a\.ts/s);
    const generic = `import {defineService} from "@sunder/sdk"; interface Box<T>{value:T}
export const first=defineService({echo(value:Box<string>):Box<string>{return value;}});
export const second=defineService({echo(value:Box<string>):Box<string>{return value;}});`;
    await writeFile(join(c.root,"src/services.ts"),generic);
    const project=await c.load(); assert.equal(project.contracts.length,2);
    assert.deepEqual(project.contracts[0]!.modelProjection,project.contracts[1]!.modelProjection);
    await writeFile(join(c.root,"src/services.ts"),generic.replace("second=defineService({echo(value:Box<string>):Box<string>","second=defineService({echo(value:Box<number>):Box<number>"));
    await assert.rejects(c.load(),/different constructed source identity/);
  } finally { await c.dispose(); }
});

function publicNameSource(extension: "ts" | "js", sourceName: string, names: {name?: string; publicName?: string}) {
  return `import {defineService} from "@sunder/sdk";
${extension === "ts" ? `export type ${sourceName} = {message:string};` : `/** @typedef {{message:string}} ${sourceName} */`}
export const echo = defineService({
/** @param {${sourceName}} value @returns {${sourceName}} */
echo(value${extension === "ts" ? `:${sourceName}` : ""}) {return value;}
}, {exposure:"public", values:${JSON.stringify({[sourceName]: names})}});`;
}

test("explicit public model names preserve descriptor, ABI and exported projection across TS/checkJs source renames", async () => {
  let expected: Buffer | undefined;
  for (const extension of ["ts", "js"] as const) {
    const names = {name:"WireGreeting", publicName:"HTTPGreeting"};
    const c = await fixture(extension, publicNameSource(extension, "ImplementationValue", names));
    try {
      const before = await c.load();
      await writeFile(join(c.root, `src/services.${extension}`), publicNameSource(extension, "RenamedValue", names));
      const after = await c.load();
      assert.deepEqual(after.contracts[0]!.canonicalBytes, before.contracts[0]!.canonicalBytes);
      assert.equal(after.contracts[0]!.abiSha256, before.contracts[0]!.abiSha256);
      assert.deepEqual({...after.contracts[0]!.modelProjection.models}, {WireGreeting:"HTTPGreeting"});
      const projection = Buffer.from(after.ownedRpc!.files.get(MODEL_PROJECTION_PATH)!);
      assert.deepEqual(projection, Buffer.from(before.ownedRpc!.files.get(MODEL_PROJECTION_PATH)!));
      if (expected) assert.deepEqual(projection, expected); else expected = projection;
      for (const path of ["api.d.ts", "owned/echo.d.ts"])
        assert.equal(after.ownedRpc!.files.get(path), before.ownedRpc!.files.get(path), path);
      assert.match(after.ownedRpc!.files.get("api.d.ts")!, /type HTTPGreeting = \$binding\d+\.WireGreeting/);
      // Owning Self aliases still resolve the actual source method's value type.
      assert.match(after.ownedRpc!.files.get("api.ts")!, /export type HTTPGreeting = Exclude<Parameters<NonNullable<typeof import/);
      assert.match(after.ownedRpc!.files.get("owned/echo.ts")!, /type WireGreeting = Exclude<Parameters/);

      await writeFile(join(c.root, `src/services.${extension}`), publicNameSource(extension, "RenamedValue", {...names, publicName:"_Greeting2"}));
      const changed = await c.load();
      assert.deepEqual(changed.contracts[0]!.canonicalBytes, before.contracts[0]!.canonicalBytes);
      assert.equal(changed.contracts[0]!.abiSha256, before.contracts[0]!.abiSha256);
      assert.equal(changed.contracts[0]!.modelProjection.models.WireGreeting, "_Greeting2");
    } finally { await c.dispose(); }
  }
});

test("explicit public model names reject nonportable spellings before TS/checkJs publication", async () => {
  for (const extension of ["ts", "js"] as const) {
    const c = await fixture(extension);
    try {
      for (const publicName of ["", "bad.name", "bad-name", "9Name", "class", "héllo", " Name", "A".repeat(129)]) {
        await writeFile(join(c.root, `src/services.${extension}`), publicNameSource(extension, "ImplementationValue", {name:"WireGreeting", publicName}));
        await assert.rejects(c.load(), /model-projection-invalid:.*services\.[jt]s:\d+:\d+:.*publicName/);
      }
      for (const publicName of ["A".repeat(128), "lower_name", "Class"]) {
        await writeFile(join(c.root, `src/services.${extension}`), publicNameSource(extension, "ImplementationValue", {publicName}));
        const project = await c.load();
        assert.deepEqual({...project.contracts[0]!.modelProjection.models}, {ImplementationValue:publicName});
      }
      await writeFile(join(c.root, `src/services.${extension}`), publicNameSource(extension, "ImplementationValue", {name:"WireGreeting"}));
      assert.deepEqual({...((await c.load()).contracts[0]!.modelProjection.models)}, {WireGreeting:"ImplementationValue"});
    } finally { await c.dispose(); }
  }
});

test("explicit public model names reject duplicate, inconsistent, ambiguous and unused source metadata", async () => {
  for (const extension of ["ts", "js"] as const) {
    const first = publicNameSource(extension, "FirstValue", {name:"FirstWire", publicName:"SharedValue"});
    const c = await fixture(extension, first);
    try {
      for (const publicName of ["SharedValue", "sharedvalue"]) {
        const second = publicNameSource(extension, "SecondValue", {name:"SecondWire", publicName})
          .replace('import {defineService} from "@sunder/sdk";', "").replace("export const echo", "export const second");
        await writeFile(join(c.root, `src/services.${extension}`), first + second);
        await assert.rejects(c.load(), /identity-collision:.*Model '.*'.*distinct source declaration.*services\.[jt]s/s);
      }
      const inconsistent = `export const second = defineService({
/** @param {FirstValue} value @returns {FirstValue} */
echo(value${extension === "ts" ? ":FirstValue" : ""}) {return value;}
}, {values:{FirstValue:{publicName:"OtherValue"}}});`;
      await writeFile(join(c.root, `src/services.${extension}`), first + inconsistent);
      await assert.rejects(c.load(), /identity-collision:.*same publicName in every owning service/);
      await writeFile(join(c.root, `src/services.${extension}`), first.replace('"FirstValue":{', '"Typo":{'));
      await assert.rejects(c.load(), /model-projection-invalid:.*'Typo'.*reachable named source value/);
      await writeFile(join(c.root, `src/services.${extension}`), publicNameSource(extension, "FirstValue", {name:"bad/name", publicName:"ValidName"}));
      await assert.rejects(c.load(), /value-invalid:.*wire definition name/);
      for (const file of ["a", "b"]) await writeFile(join(c.root, `src/${file}.${extension}`), extension === "ts"
        ? "export type Value = {message:string};" : "/** @typedef {{message:string}} Value */\nexport {};");
      await writeFile(join(c.root, `src/services.${extension}`), `import {defineService} from "@sunder/sdk";
${extension === "ts" ? 'import type {Value as A} from "./a.js"; import type {Value as B} from "./b.js";' : ""}
export const echo = defineService({
/** @param {import("./a.js").Value} value */
first(value${extension === "ts" ? ":A" : ""}) {return value;},
/** @param {import("./b.js").Value} value */
second(value${extension === "ts" ? ":B" : ""}) {return value;}
}, {values:{Value:{name:"WireValue",publicName:"SharedValue"}}});`);
      await assert.rejects(c.load(), /identity-collision:.*Value naming key 'Value'.*distinct source declarations/);
    } finally { await c.dispose(); }
  }
});

test("strict sidecars reject tampering, numeric-key reordering, stale linkage and incompatible complete model closures", async () => {
  const c = await fixture("ts");
  try {
    const project = await c.load(); const expected = evidence(project); const body = Buffer.from(project.ownedRpc!.files.get(MODEL_PROJECTION_PATH)!);
    for (const mutate of [
      (text: string) => text.replace('"models": {', '"models": null, "extra": {'),
      (text: string) => text.replace('"schemaVersion": 1', '"schemaVersion": 2'),
      (text: string) => text.replace('"models": {', '"Models": {}, "models": {'),
      (text: string) => text.replace('"Detail": "Detail"', '"Detail": "Bad.Name"'),
      (text: string) => text.replace('"serviceReferences": {}', '"serviceReferences": {"Item": {"contractId": "example.models.first", "serviceId": "first"}}'),
      (text: string) => text + "\n", (text: string) => "\ufeff" + text,
    ]) assert.throws(() => parseModelProjectionDocument(Buffer.from(mutate(body.toString()))));
    const unknown = body.toString().replaceAll('"Detail": "Detail"', '"DetailMissing": "Detail"');
    assert.throws(() => validateProjectionDocument(parseModelProjectionDocument(Buffer.from(unknown)), project.config.id, project.config.version, expected), /Unknown/);
    assert.throws(() => validateProjectionDocument(parseModelProjectionDocument(body), project.config.id, "2.0.0", expected), /identity/);
    assert.throws(() => validateProjectionDocument(parseModelProjectionDocument(body), project.config.id, project.config.version, expected.slice(1)), /coverage/);
    const numeric = readModelProjection(fields(parseOuter(outerBytes({ models: ordered([["10", "Ten"], ["2", "Two"]]), serviceReferences: {} })), ["models", "serviceReferences"]));
    assert.ok(modelProjectionBytes(numeric).indexOf('"10"') < modelProjectionBytes(numeric).indexOf('"2"'));
    assert.throws(() => readModelProjection(parseOuter(Buffer.from('{\n  "models": {\n    "2": "Two",\n    "10": "Ten"\n  },\n  "serviceReferences": {}\n}\n'))), /ordinal/);
    const changed = structuredClone(expected[1]!); (changed.descriptor.$defs.Detail as any).properties.count.maxDigits = 15;
    changed.descriptorSha256 = hashBytes(canonicalRpcDescriptorBytes(changed.descriptor)); changed.abiSha256 = await computeRpcAbiSha256(changed.descriptor);
    assert.throws(() => validateModelProjectionSet([expected[0]!, changed]), /Incompatible model/);
    assert.doesNotThrow(() => validateModelProjectionSet([expected[0]!, { ...changed, authorityId: "example.other" }]));
    const caseCollision = {...expected[1]!,modelProjection:{...expected[1]!.modelProjection,models:{...expected[1]!.modelProjection.models,Item:"item"}}};
    assert.throws(() => validateModelProjectionSet([expected[0]!,caseCollision]), /case folding/);
    assert.doesNotThrow(() => validateModelProjectionSet([expected[0]!,{...caseCollision,authorityId:"example.other"}]));
  } finally { await c.dispose(); }
});

test("Format parity permits repeated exact model identities only with equal normalized closures and rejects backend keywords", async () => {
  const c = await fixture("ts",`import {defineService} from "@sunder/sdk";
type First = {name:string}; type Second = {name:string}; type Root = {first:First;second:Second};
export const echo=defineService({echo(value:Root):Root{return value;}},{exposure:"public"});`);
  try {
    const project = await c.load(); const entry = evidence(project)[0]!;
    const modelProjection = readModelProjection(modelProjectionOuter({...entry.modelProjection,models:{...entry.modelProjection.models,Second:"First"}}),entry.descriptor);
    assert.equal(modelProjection.models.First,modelProjection.models.Second);
    assert.doesNotThrow(()=>validateModelProjectionSet([{...entry,modelProjection}]));
    const changed=structuredClone(entry.descriptor); (changed.$defs.Second as any).properties.name={type:"number",format:"float64"};
    assert.throws(()=>validateModelProjectionSet([{...entry,modelProjection,descriptor:changed,descriptorSha256:hashBytes(canonicalRpcDescriptorBytes(changed)),
      abiSha256:entry.abiSha256}]),/exact descriptor/);
    const abiSha256=await computeRpcAbiSha256(changed);
    assert.throws(()=>validateModelProjectionSet([{...entry,modelProjection,descriptor:changed,descriptorSha256:hashBytes(canonicalRpcDescriptorBytes(changed)),abiSha256}]),/Incompatible model/);
    // The shared backend keyword list is an exact spelling rule, not a guessed
    // suffix or case-folded keyword ban. Public Pascal identifiers remain valid.
    for(const name of ["abstract","await","base","bool","byte","checked","decimal","delegate","explicit","extern","fixed","foreach","implicit","int","internal","lock","operator","out","override","package","params","readonly","ref","sbyte","sealed","sizeof","stackalloc","struct","super","uint","ulong","unchecked","unsafe","ushort","using","virtual","volatile","with","yield"]){
      assert.equal(portableModelName(name),false,name);
      assert.equal(portableModelName(name[0]!.toUpperCase()+name.slice(1)),true,name);
      assert.throws(()=>readModelProjection(modelProjectionOuter({...entry.modelProjection,models:{First:name,Root:"Root",Second:"Second"}})),/Invalid public model/);
    }
  } finally {await c.dispose();}
});

test("sidecar raw bounds admit exactly 4 MiB and reject byte, depth and structural overflow before graph allocation",()=>{
  const contracts=Array.from({length:128},(_,index)=>({contractId:`example.c${String(index).padStart(3,"0")}`,contractVersion:"1.0.0",descriptorSha256:"0".repeat(64),abiSha256:"0".repeat(64),
    modelProjection:{models:Object.fromEntries(Array.from({length:256},(_,key)=>[`D${String(key).padStart(3,"0")}`,"A"])),serviceReferences:{}}}));
  let remaining=MAX_MODEL_PROJECTION_BYTES-projectionDocumentBytes("example.bounds","1.0.0",contracts).length;
  for(const contract of contracts)for(const key of Object.keys(contract.modelProjection.models)){
    const extra=Math.min(127,remaining);contract.modelProjection.models[key]="A"+"x".repeat(extra);remaining-=extra;
  }
  assert.equal(remaining,0);const bytes=projectionDocumentBytes("example.bounds","1.0.0",contracts);
  assert.equal(bytes.length,MAX_MODEL_PROJECTION_BYTES);assert.equal(parseModelProjectionDocument(bytes).contracts.length,128);
  for(const [body,guard] of [[Buffer.concat([bytes,Buffer.from("\n")]),/byte limit/],
    [Buffer.from("[".repeat(17)+"]".repeat(17)+"\n"),/depth/],
    [Buffer.from('{"entries":['+"0,".repeat(524_288)+'0]}\n'),/structural budget/]] as const)
    assert.throws(()=>parseModelProjectionDocument(body),error=>error instanceof Error && error.message.startsWith("rpc-authoring.model-projection-invalid:") && error.cause instanceof Error && guard.test(error.cause.message));
});

test("selected projection cache requires full pins and descriptors, offline restore never falls back, and GC marks the projection pair", async () => {
  const c = await fixture("ts");
  try {
    const project = await c.load(); const selection = selectedArtifact(project); const cache = join(c.root, "cache");
    await publishContractCache(cache, selection.descriptors, selection.lock.requirements);
    let network = 0; const options = { cacheRoot: cache, fetchImplementation: (async () => { network++; throw new Error("network forbidden"); }) as typeof fetch };
    await assert.rejects(hydrateContractLock(selection.lock, options, async () => {}), /entry-missing/); assert.equal(network, 0);
    await publishModelProjectionCache(cache, new Map([[project.config.id, selection.body]]), selection.lock.packageArtifacts, selection.descriptors);
    const documents = await readModelProjectionCache(cache, selection.lock.packageArtifacts, selection.descriptors); assert.equal(documents.size, 1);
    assert.equal(await hydrateContractLock(selection.lock, options, async () => {}), 0); assert.equal(network, 0);
    const artifact = selection.lock.packageArtifacts.get(project.config.id)!;
    const path = join(cache, MODEL_PROJECTION_DIRECTORY, artifact.modelProjectionSha256 + ".json");
    assert.deepEqual(await readFile(path.replace(/\.json$/u, ".receipt.json")), modelProjectionReceiptBytes(artifact.modelProjectionSha256, artifact.modelProjectionBytes));
    await assert.rejects(readModelProjectionCache(cache, new Map([[artifact.packageId, {...artifact,modelProjectionBytes:artifact.modelProjectionBytes+1}]]), selection.descriptors), /pins/);
    await assert.rejects(readModelProjectionCache(cache, selection.lock.packageArtifacts, new Map()), /descriptors/);
    const lock = join(c.root, "reviewed.lock.json"); await writeFile(lock, selection.lock.exactBytes);
    const future = Date.now() + 8 * 86_400_000;
    assert.equal((await collectContractCache(cache, [lock], { observedNow: future })).removedEntries, 0);
    await writeFile(lock, emptyLock);
    assert.equal((await collectContractCache(cache, [lock], { observedNow: future })).removedEntries, selection.descriptors.size + 1);
    await assert.rejects(readFile(path), {code:"ENOENT"});
    const parsed = JSON.parse(selection.lock.exactBytes.toString()); delete parsed.packageArtifacts[project.config.id].modelProjectionBytes;
    assert.throws(() => parseContractLock(Buffer.from(JSON.stringify(parsed, null, 2) + "\n")), /missing/);
    await writeFile(join(c.root, "src/models.ts"), "export {};\n");
    await assert.rejects(project.ownedRpc!.revalidate(), /changed|differs/);
  } finally { await c.dispose(); }
});

test("private-only packages publish a complete empty public projection document", async () => {
  const c = await fixture("ts", `import {defineService} from "@sunder/sdk"; type PrivateModel = {secret:string}; export const secret = defineService({echo(value:PrivateModel):PrivateModel{return value;}});`);
  try {
    const project = await c.load(); const bytes = Buffer.from(project.ownedRpc!.files.get(MODEL_PROJECTION_PATH)!);
    assert.deepEqual(bytes, projectionDocumentBytes(project.config.id, project.config.version, []));
    assert.deepEqual(project.contracts[0]!.modelProjection.serviceReferences, emptyModelProjection().serviceReferences);
  } finally { await c.dispose(); }
});

test("imported TS/checkJs models share ultimate-authority types and reexport the exact reviewed origin projections", async () => {
  const producer = await fixture("ts");
  try {
    const original = await producer.load(); const selection = selectedArtifact(original); const cache = join(producer.root,"cache");
    await publishContractCache(cache,selection.descriptors,selection.lock.requirements);
    await publishModelProjectionCache(cache,new Map([[original.config.id,selection.body]]),selection.lock.packageArtifacts,selection.descriptors);
    for (const extension of ["ts","js"] as const) {
      const consumer = await fixture(extension,`import {Dependency} from "#sunder/api";
${extension === "ts" ? 'import type {WorkerContext} from "@sunder/sdk";' : ''}
/** @param {import("@sunder/sdk").WorkerContext} context */
export function configure(context${extension === "ts" ? ':WorkerContext' : ''}) {
  /** @type {import("#sunder/api").DependencyModels.Item} */
  const value${extension === "ts" ? ':import("#sunder/api").DependencyModels.Item' : ''}={name:"ok",detail:{count:1n}};
  return {async onActivated(){await context.api(Dependency).example_Models_First.first.echo(value);}};
}`);
      try {
        const config = JSON.parse(await readFile(join(consumer.root,"sunder.package.json"),"utf8")); config.id="example.consumer";
        await writeFile(join(consumer.root,"sunder.package.json"),JSON.stringify(config)); await writeFile(join(consumer.root,"sunder-contracts.lock.json"),selection.lock.exactBytes);
        const loaded = await loadProjectWithOwnedFrame(consumer.root,{contractCacheRoot:cache});
        const second = loaded.ownedRpc!.files.get("imported/example.models.second.ts")!;
        assert.match(second,/type Item = import\("\.\/example.models.first.js"\).Item/);
        assert.match(loaded.ownedRpc!.files.get("api.d.ts")!,/namespace DependencyModels/);
        assert.doesNotMatch(loaded.ownedRpc!.files.get("api.js")!,/DependencyModels/);
        const reexport = parseModelProjectionDocument(Buffer.from(loaded.ownedRpc!.files.get(MODEL_PROJECTION_PATH)!));
        assert.equal(reexport.packageId,"example.consumer");
        assert.deepEqual(reexport.contracts,parseModelProjectionDocument(selection.body).contracts);
        const receipt = JSON.parse(loaded.ownedRpc!.files.get("authoring-receipt.v1.json")!);
        assert.equal(receipt.packageArtifacts[0].modelProjectionBytes,selection.body.length);
        assert.equal(receipt.packageArtifacts[0].modelProjectionSha256,hashBytes(selection.body));
        const fields = Object.keys(receipt.packageArtifacts[0]);
        assert.deepEqual(fields.slice(fields.indexOf("publicInventorySha256"),fields.indexOf("contracts")+1),["publicInventorySha256","modelProjectionBytes","modelProjectionSha256","contracts"]);
      } finally { await consumer.dispose(); }
    }
  } finally { await producer.dispose(); }
});

test("owned TS/checkJs signatures resolve selected model aliases by nominal metadata identity, including reexports and nested values",async()=>{
  const corpus=join(__dirname,"../../../../../../../specs/rpc/v1/authoring/conformance/valid");
  const origin=await readFile(join(corpus,"rpc-model-projection-models.json"));
  const descriptor=parseRpcContractDescriptor(await readFile(join(corpus,"../../../conformance/valid/minimal.rpc.json")));
  const bytes=Buffer.from(canonicalRpcDescriptorBytes(descriptor));const hash=hashBytes(bytes);
  for(const extension of ["ts","js"] as const){
    const source=extension==="ts"?`import {defineService} from "@sunder/sdk";import type {DepModels,ForwardModels} from "#sunder/api";
type Holder={nested:DepModels.SharedValue};
export const first=defineService({echo(value:DepModels.SharedValue):DepModels.SharedValue{return value;}});
export const second=defineService({echo(value:ForwardModels.SharedValue):ForwardModels.SharedValue{return value;}});
export const holder=defineService({echo(value:Holder):Holder{return value;}});`
      :`import {defineService} from "@sunder/sdk";
/** @typedef {{nested:import("#sunder/api").DepModels.SharedValue}} Holder */
export const first=defineService({
/** @param {import("#sunder/api").DepModels.SharedValue} value @returns {import("#sunder/api").DepModels.SharedValue} */
echo(value){return value;}});
export const second=defineService({
/** @param {import("#sunder/api").ForwardModels.SharedValue} value @returns {import("#sunder/api").ForwardModels.SharedValue} */
echo(value){return value;}});
export const holder=defineService({
/** @param {Holder} value @returns {Holder} */
echo(value){return value;}});`;
    const c=await fixture(extension,source);
    try{
      const raw=JSON.parse(await readFile(join(corpus,"package-api-contract-lock.json"),"utf8"));
      const artifact=raw.packageArtifacts["example.dependency"];const projection=rebindFixtureProjection("example.dependency",artifact,origin);
      raw.packageImports["example.dependency"].localName="dep";
      const forward=structuredClone(artifact);forward.selectedPackageContentIdentity="3".repeat(64);forward.contracts[descriptor.contractId].artifactSource="imported";
      const publicInventory=JSON.parse(inventoryBytes(parseContractLock(Buffer.from(JSON.stringify(raw,null,2)+"\n")).packageArtifacts.get("example.dependency")!).toString());
      publicInventory.packageId="example.forward";publicInventory.packageContentIdentity=forward.selectedPackageContentIdentity;publicInventory.contracts[0].artifactSource="imported";
      const {createHash}=require("node:crypto") as typeof import("node:crypto");
      forward.publicInventorySha256=createHash("sha256").update("sunder.package-public-api-inventory.v1\0").update(JSON.stringify(publicInventory)).digest("hex");
      const forwarded=rebindFixtureProjection("example.forward",forward,origin);
      raw.packageArtifacts["example.forward"]=forward;raw.packageImports["example.forward"]={localName:"forward",presence:"required",requiredContracts:[],consumerRoles:["runtime"]};
      const lock=parseContractLock(Buffer.from(JSON.stringify(raw,null,2)+"\n"));const cache=join(c.root,"cache");
      await writeFile(join(c.root,"sunder-contracts.lock.json"),lock.exactBytes);await publishContractCache(cache,new Map([[hash,bytes]]),lock.requirements);
      await publishModelProjectionCache(cache,new Map([["example.dependency",projection],["example.forward",forwarded]]),lock.packageArtifacts,new Map([[hash,bytes]]));
      const project=await loadProjectWithOwnedFrame(c.root,{contractCacheRoot:cache});
      for(const contract of project.ownedRpc!.contracts){assert.equal(contract.modelProjection.models.SharedValue,"SharedValue");assert.equal(contract.modelProjection.models.Empty,undefined);}
      const holder=project.ownedRpc!.contracts.find(contract=>contract.contractKey==="holder")!;
      assert.equal(((holder.descriptor.$defs.Holder as any).properties.nested).$ref,"#/$defs/SharedValue");
    }finally{await c.dispose();}
  }
});

test("projection archive composition indexes shared bytes once and rejects reindexed sidecar or authored shadows", async () => {
  const c = await fixture("ts", `import {defineService} from "@sunder/sdk"; type Item = {text:string}; export const echo = defineService({echo(value:Item):Item{return value;}},{methods:{echo:{timeoutSeconds:2100}}});`);
  try {
    const project = await c.load(); const modulePath = join(c.root, "dist/work/worker.cjs");
    const workerNoticePath = await bundleProduction(project, currentRid(), modulePath);
    const leaf = await writeTargetLeaf({ project, rid: currentRid(), modulePath, workerNoticePath, discoveryRoot: join(c.root, "dist/targets") });
    const packed = await aggregateAndPack(c.root); const before = await readFile(packed.archivePath);
    const manifest = JSON.parse(await readFile(join(packed.packageRoot,"manifest/sunder-package.json"),"utf8"));
    assert.ok(manifest.targets.every((target:{requiredHostCapabilities:string[]})=>target.requiredHostCapabilities.includes("rpc.extended-deadline.v1")));
    const body = await readFile(join(packed.packageRoot, MODEL_PROJECTION_ARCHIVE_PATH));
    assert.deepEqual(body, Buffer.from(project.ownedRpc!.files.get(MODEL_PROJECTION_PATH)!));
    const index = JSON.parse(await readFile(join(packed.packageRoot, "manifest/content-index.json"), "utf8"));
    assert.deepEqual(index.files.filter((file: {path:string}) => file.path.endsWith(MODEL_PROJECTION_PATH)), [{ path: MODEL_PROJECTION_ARCHIVE_PATH, sha256: hashBytes(body), size: body.length }]);
    const tampered = projectionDocumentBytes(project.config.id, "2.0.0", []);
    await writeFile(join(leaf, MODEL_PROJECTION_ARCHIVE_PATH), tampered);
    const leafIndex = JSON.parse(await readFile(join(leaf, "manifest/content-index.json"), "utf8"));
    Object.assign(leafIndex.files.find((file: {path:string}) => file.path === MODEL_PROJECTION_ARCHIVE_PATH), {sha256:hashBytes(tampered),size:tampered.length});
    await writeFile(join(leaf, "manifest/content-index.json"), JSON.stringify(leafIndex,null,2)+"\n");
    await assert.rejects(aggregateAndPack(c.root), /model projection differs/);
    assert.deepEqual(await readFile(packed.archivePath), before);
    const config = JSON.parse(await readFile(join(c.root, "sunder.package.json"), "utf8"));
    config.nativeAssets = { [currentRid()]: [{sourcePath:"assets/shadow.json",targetPath:"Contracts/RPC-Model-Projection.V1.JSON"}] };
    await writeFile(join(c.root, "sunder.package.json"), JSON.stringify(config));
    await assert.rejects(c.load(), /collides.*model projection/);
  } finally { await c.dispose(); }
});

test("Node independently consumes the shared projection corpus and complete eleven-field locator schema", async () => {
  const root = join(__dirname, "../../../../../../../specs/rpc/v1/authoring/conformance");
  const expectations = JSON.parse(await readFile(join(root,"expectations.json"),"utf8"));
  for (const entry of expectations.immutableArtifactEvidence) {
    const bytes = await readFile(join(root,entry.modelProjectionFixturePath)); const document = parseModelProjectionDocument(bytes);
    assert.equal(bytes.length, entry.modelProjectionBytes); assert.equal(hashBytes(bytes),entry.modelProjectionSha256);
    assert.deepEqual(projectionDocumentBytes(document.packageId,document.packageVersion,document.contracts),bytes);
    for (const contract of document.contracts) { assert.equal(Object.getPrototypeOf(contract.modelProjection.models),null); assert.equal(Object.getPrototypeOf(contract.modelProjection.serviceReferences),null); }
  }
  const minimal = parseRpcContractDescriptor(await readFile(join(root,"../../conformance/valid/minimal.rpc.json")));
  const descriptorSources = new Map([[hashBytes(canonicalRpcDescriptorBytes(minimal)), minimal]]);
  const catalog = JSON.parse(await readFile(join(root,"valid/platform-catalog.json"),"utf8"));
  for (const entry of catalog.contracts) descriptorSources.set(entry.descriptorSha256,parseRpcContractDescriptor(entry.canonicalDescriptor));
  for (const fixture of expectations.fixtures.filter((entry: {schemaPath:string}) => entry.schemaPath === "../rpc-model-projection.schema.json")) {
    const bytes = await readFile(join(root,fixture.fixturePath)); const contexts = [];
    for (const path of fixture.semanticContextFixturePaths ?? []) contexts.push(JSON.parse(await readFile(join(root,path),"utf8")));
    const context = contexts[0];
    const verify = () => {
      const document = parseModelProjectionDocument(bytes);
      const expected = (context?.contracts ?? []).map((entry: any) => ({ ...entry,
        descriptor: entry.canonicalDescriptor ? parseRpcContractDescriptor(entry.canonicalDescriptor) : descriptorSources.get(entry.descriptorSha256),
      }));
      validateProjectionDocument(document, context?.packageId ?? document.packageId, context?.packageVersion ?? document.packageVersion, expected);
    };
    if (fixture.semanticValid) assert.doesNotThrow(verify,fixture.fixturePath); else assert.throws(verify,Error,fixture.fixturePath);
  }
  const schema = serviceReferenceSchema(); assert.doesNotThrow(() => validateServiceReferenceSchema(schema));
  for (const name of Object.keys(schema.properties as object)) {
    const incomplete = structuredClone(schema); delete (incomplete.properties as Record<string,unknown>)[name];
    assert.throws(() => validateServiceReferenceSchema(incomplete), /eleven-field/);
  }
  const extra = structuredClone(schema); (extra.properties as Record<string,unknown>).Service = {type:"object"};
  assert.throws(() => validateServiceReferenceSchema(extra), /eleven-field/);
  const epoch = structuredClone(schema); (epoch.properties as Record<string,Record<string,unknown>>).sessionGeneration!.maximum = Number.MAX_SAFE_INTEGER + 1;
  assert.throws(() => validateServiceReferenceSchema(epoch), /eleven-field/);
});

test("authoring acquisition inspects canonical candidates, pins sidecars, reuses cache and rejects inspector membership or pin tampering", async t => {
  const c = await fixture("ts");
  try {
    const project = await c.load(); const manifestSha256 = "b".repeat(64); const contentIndexSha256 = "c".repeat(64);
    const identity = hashBytes(Buffer.concat([Buffer.from(manifestSha256,"hex"),Buffer.from(contentIndexSha256,"hex")]));
    const selection = selectedArtifact(project,identity,"https://registry.example/"); const artifact = selection.lock.packageArtifacts.get(project.config.id)!;
    const archive = Buffer.from("test-only canonical inspector boundary"); const path = join(c.root,"candidate.sunderpkg"); await writeFile(path,archive);
    const recipePath = join(c.root,"inspector-recipe.json"); const executable = join(c.root,"inspector.cjs");
    const recipe = { receipt: {schemaVersion:1,archiveSha256:hashBytes(archive),manifestSha256,contentIndexSha256,inventory:JSON.parse(inventoryBytes(artifact).toString()),
      publicInventorySha256:artifact.publicInventorySha256,modelProjectionBytes:artifact.modelProjectionBytes,modelProjectionSha256:artifact.modelProjectionSha256},
      files: {"rpc-model-projection.v1.json":selection.body.toString(),...Object.fromEntries([...selection.descriptors].map(([hash,bytes])=>[hash+".json",bytes.toString()]))} };
    await writeFile(recipePath,JSON.stringify(recipe));
    // The mock is only the existing Format subprocess boundary. Descriptors and
    // model metadata come from a real TS compilation, never hand-copied schemas.
    await writeFile(executable,`const fs=require("node:fs"),p=require("node:path");const data=JSON.parse(fs.readFileSync(process.argv[2],"utf8"));const args=process.argv.slice(3);const out=args[args.indexOf("--api-output")+1];fs.mkdirSync(out);fs.writeFileSync(p.join(out,"inspection.json"),JSON.stringify(data.receipt));for(const [name,text] of Object.entries(data.files))fs.writeFileSync(p.join(out,name),text);`);
    const spawn = childProcess.spawn; let inspections = 0;
    t.mock.method(childProcess,"spawn",((command:string,args:readonly string[],options:childProcess.SpawnOptions) => {
      inspections++;
      assert.equal(command,process.env.SUNDER_CLI??"sunder"); assert.deepEqual(args.slice(0,3),["dev","package","validate"]);
      return spawn(process.execPath,[executable,recipePath,...args],options);
    }) as typeof childProcess.spawn);
    const paths: string[] = [];
    const request: typeof fetch = async input => {
      const url = new URL(String(input)); paths.push(url.pathname+url.search);
      const listing = url.search !== "";
      assert.ok(listing ? url.pathname.endsWith("/versions") : url.pathname.endsWith("/canonical"));
      const response = new Response(listing ? JSON.stringify([{version:"1.0.0",isYanked:false}]) : new Uint8Array(archive), { headers: listing
        ? {"content-type":"application/json","X-Total-Count":"1","X-Skip":"0","X-Take":"100"}
        : {"content-type":"application/octet-stream","content-length":String(archive.length)} });
      Object.defineProperty(response,"url",{value:url.href}); return response;
    };
    let fences = 0; const cache = join(c.root,"selected-cache");
    const candidates = new Map<string,InspectedContractCandidate>();
    try {
      const selected = await selectPackageArtifact(project.config.id,{versionRange:"1.0.0",includePrerelease:false,registryOrigin:"https://registry.example/",fetchImplementation:request,candidates});
      assert.equal(selected.modelProjectionBytes,selection.body.length); assert.equal(selected.modelProjectionSha256,hashBytes(selection.body));
      assert.equal(await hydrateContractLock(selection.lock,{cacheRoot:cache,fetchImplementation:request},async()=>{fences++;},candidates),selection.descriptors.size);
    } finally {for(const candidate of candidates.values())if("dispose" in candidate)await (candidate.dispose as ()=>Promise<void>)();}
    assert.equal(fences,2);
    assert.deepEqual(paths,[`/api/v1/packages/${project.config.id}/versions?skip=0&take=100`,`/api/v1/packages/${project.config.id}/versions/1.0.0/canonical`]);
    assert.equal(await hydrateContractLock(selection.lock,{cacheRoot:cache,fetchImplementation:request},async()=>{}),0); assert.equal(paths.length,2);
    await hydrateContractLock(selection.lock,{cacheRoot:join(c.root,"cold-cache"),fetchImplementation:request},async()=>{}); assert.equal(paths.length,3);
    const consumer = await fixture("ts","export {};\n");
    try {
      const config = JSON.parse(await readFile(join(consumer.root,"sunder.package.json"),"utf8")); config.id="example.consumer";
      await writeFile(join(consumer.root,"sunder.package.json"),JSON.stringify(config));
      const old = JSON.parse(selection.lock.exactBytes.toString()); delete old.packageArtifacts[project.config.id].modelProjectionBytes; delete old.packageArtifacts[project.config.id].modelProjectionSha256;
      const oldBytes=Buffer.from(JSON.stringify(old,null,2)+"\n"); await writeFile(join(consumer.root,"sunder-contracts.lock.json"),oldBytes);
      assert.throws(()=>parseContractLock(oldBytes),/missing/);
      const lockPath=join(consumer.root,"sunder-contracts.lock.json"), oldTime=(await stat(lockPath)).mtimeMs, beforeInspections=inspections;
      await assert.rejects(packageApiCommand(consumer.root,{operation:"update",packageId:project.config.id,candidate:path,cacheRoot:cache}),/missing/);
      assert.equal(inspections,beforeInspections); assert.deepEqual(await readFile(lockPath),oldBytes); assert.equal((await stat(lockPath)).mtimeMs,oldTime);
      // A complete current selection can still be checked against the canonical
      // candidate; Update never manufactures missing pins from that candidate.
      await writeFile(lockPath,selection.lock.exactBytes); const currentTime=(await stat(lockPath)).mtimeMs;
      const updated = await packageApiCommand(consumer.root,{operation:"update",packageId:project.config.id,candidate:path,cacheRoot:cache});
      assert.equal(updated.changed,false); assert.equal(updated.lock.packageArtifacts.get(project.config.id)!.modelProjectionSha256,hashBytes(selection.body));
      assert.deepEqual(updated.lock.exactBytes,selection.lock.exactBytes);
      assert.deepEqual(await readFile(lockPath),selection.lock.exactBytes); assert.equal((await stat(lockPath)).mtimeMs,currentTime);
    } finally { await consumer.dispose(); }
    for (const mutation of ["missing","extra","hash","private"] as const) {
      const changed = structuredClone(recipe);
      if (mutation === "missing") delete (changed.files as Record<string,string>)["rpc-model-projection.v1.json"];
      if (mutation === "extra") (changed.files as Record<string,string>)["unexpected.json"] = "{}";
      if (mutation === "hash") changed.receipt.modelProjectionSha256 = "0".repeat(64);
      if (mutation === "private") {
        const privateEntry = project.contracts.find(contract=>contract.exposure==="package")!;
        const bytes = projectionDocumentBytes(project.config.id,project.config.version,[...evidence(project),{contractId:privateEntry.descriptor.contractId,contractVersion:privateEntry.descriptor.version,
          descriptorSha256:privateEntry.descriptorSha256,abiSha256:privateEntry.abiSha256,modelProjection:privateEntry.modelProjection}]);
        changed.files["rpc-model-projection.v1.json"]=bytes.toString();changed.receipt.modelProjectionBytes=bytes.length;changed.receipt.modelProjectionSha256=hashBytes(bytes);
      }
      await writeFile(recipePath,JSON.stringify(changed)); await assert.rejects(inspectContractCandidate(path),Error,mutation);
    }
  } finally { await c.dispose(); }
});
