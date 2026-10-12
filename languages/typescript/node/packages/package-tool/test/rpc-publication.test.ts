import assert from "node:assert/strict";
import { appendFile, mkdir, mkdtemp, readFile, rm, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import test from "node:test";
import { canonicalRpcAbiProjectionBytes, canonicalRpcDescriptorBytes, computeRpcAbiSha256, parseRpcContractDescriptor } from "@sunder/sdk";
import { atVersion, emptyBaseline, parseBaseline, sha256, writeBaseline, type OwnedSnapshot } from "../src/rpc-baseline";
import { ordered } from "../src/rpc-baseline-json";
import { importContractPublication, planPublication } from "../src/rpc-publication";
import * as candidates from "../src/rpc-contract-candidate";
import { publicationInspection } from "./rpc-publication-fixtures";

const fixturePath = resolve(__dirname, "../../../../../../../specs/rpc/v1/conformance/valid/minimal.rpc.json");
async function fixture(state: OwnedSnapshot["state"] = "release-frozen", exposure: OwnedSnapshot["exposure"] = "public") {
  const descriptor = parseRpcContractDescriptor(await readFile(fixturePath));
  const bytes = Buffer.from(canonicalRpcDescriptorBytes(descriptor)); const abi = Buffer.from(canonicalRpcAbiProjectionBytes(descriptor));
  const snapshot: OwnedSnapshot = { state, exposure, descriptor, json: ordered([
    ["state", state], ["exposure", exposure], ["contractVersion", descriptor.version], ["descriptorBytes", bytes.length], ["canonicalDescriptor", bytes.toString()],
    ["descriptorSha256", sha256(bytes)], ["abiProjectionBytes", abi.length], ["canonicalAbiProjection", abi.toString()], ["abiSha256", await computeRpcAbiSha256(descriptor)],
    ...(state === "draft" ? [] : [["packageRelease", { packageId: "example.owner", packageVersion: "7.6.5" }] as const]),
  ]) };
  const baseline = parseBaseline(writeBaseline("example.owner", new Map([["sample", { contractId: descriptor.contractId, current: snapshot, history: [] }]])));
  const publication = { registryOrigin: "https://registry.example/", packageId: "example.owner", packageVersion: "7.6.5", contentIdentity: "a".repeat(64),
    publishedAtUtc: "2026-09-17T12:34:56.1234000+00:00", ownedContracts: [{ contractId: descriptor.contractId, version: descriptor.version,
      descriptorSha256: sha256(bytes), abiSha256: await computeRpcAbiSha256(descriptor), exposure }] };
  return { baseline, snapshot, publication };
}

test("publication confirms immutable public bytes and preserves no-op evidence", async () => {
  const { baseline, publication } = await fixture();
  const bytes = planPublication(baseline, publication); const confirmed = parseBaseline(bytes);
  const current = confirmed.contracts.get("sample")!.current!;
  assert.equal(current.state, "published");
  assert.deepEqual(canonicalRpcDescriptorBytes(current.descriptor), canonicalRpcDescriptorBytes(baseline.contracts.get("sample")!.current!.descriptor));
  assert.equal((current.json.publication as Record<string, unknown>).publishedAtUtc, "2026-09-17T12:34:56.1234Z");
  assert.deepEqual(planPublication(confirmed, publication), bytes);
  assert.throws(() => planPublication(confirmed, { ...publication, contentIdentity: "b".repeat(64) }), /conflicts/u);
});

async function mixed() {
  const original = await fixture();
  const privateSnapshot = async (version: string, contractId = "example.owner.private"): Promise<OwnedSnapshot> => {
    const descriptor = parseRpcContractDescriptor(JSON.stringify({...atVersion(original.snapshot.descriptor, version), contractId}));
    const bytes = Buffer.from(canonicalRpcDescriptorBytes(descriptor)); const abi = Buffer.from(canonicalRpcAbiProjectionBytes(descriptor));
    return {state:"release-frozen", exposure:"package", descriptor, json: ordered([
      ["state","release-frozen"], ["exposure","package"], ["contractVersion",version], ["descriptorBytes",bytes.length], ["canonicalDescriptor",bytes.toString()],
      ["descriptorSha256",sha256(bytes)], ["abiProjectionBytes",abi.length], ["canonicalAbiProjection",abi.toString()], ["abiSha256",await computeRpcAbiSha256(descriptor)],
      ["packageRelease",{packageId:"example.owner",packageVersion:version}],
    ])};
  };
  const retired = await privateSnapshot("1.0.0"); const current = await privateSnapshot("2.0.0");
  const publicSnapshot: OwnedSnapshot = {...original.snapshot, json:ordered(Object.entries({...original.snapshot.json, packageRelease:{packageId:"example.owner",packageVersion:"2.0.0"}}))};
  const baseline = parseBaseline(writeBaseline("example.owner",new Map([["sample",{contractId:publicSnapshot.descriptor.contractId,current:publicSnapshot,history:[]}]]),
    new Map([["private",{contractId:retired.descriptor.contractId,current:retired,history:[]}]])));
  const publication = {...original.publication, packageVersion:"2.0.0", ownedContracts:[original.publication.ownedContracts[0]!, {
    contractId:current.descriptor.contractId, version:current.descriptor.version, descriptorSha256:current.json.descriptorSha256 as string, abiSha256:current.json.abiSha256 as string, exposure:"package" as const,
  }]};
  return {baseline,publication,retired,current,privateSnapshot};
}

test("mixed publication confirms public evidence while retired private 1.0.0 stays byte-identical beside current package 2.0.0", async () => {
  const {baseline,publication} = await mixed(); const bytes = planPublication(baseline,publication); const confirmed = parseBaseline(bytes);
  assert.equal(confirmed.contracts.get("sample")!.current!.state,"published"); assert.equal(confirmed.contracts.size,1);
  assert.deepEqual(writeBaseline(baseline.packageId,new Map(),confirmed.retiredPrivateContracts),writeBaseline(baseline.packageId,new Map(),baseline.retiredPrivateContracts));
  assert.equal(confirmed.retiredPrivateContracts!.get("private")!.current!.descriptor.version,"1.0.0");
  assert.equal(confirmed.retiredPrivateContracts!.get("private")!.current!.state,"release-frozen");
  assert.deepEqual(planPublication(confirmed,publication),bytes);
  assert.throws(()=>planPublication(confirmed,{...publication,contentIdentity:"b".repeat(64)}),/conflicts/);
});

test("private-only publication is a no-op for absent, retired-only and legacy-private baselines and checks the complete package SemVer", async () => {
  const {baseline,publication,retired} = await mixed(); const privateOnly = {...publication,ownedContracts:publication.ownedContracts.filter(value=>value.exposure==="package")};
  assert.equal(planPublication(undefined,privateOnly),undefined);
  for (const retained of [parseBaseline(writeBaseline(baseline.packageId,new Map(),baseline.retiredPrivateContracts)),
    parseBaseline(writeBaseline(baseline.packageId,new Map([["legacy",{contractId:retired.descriptor.contractId,current:retired,history:[]}]])))])
    assert.deepEqual(planPublication(retained,privateOnly),retained.bytes);
  const fullVersion = {...privateOnly,packageVersion:"2.0.0-beta.1+Build.7",ownedContracts:privateOnly.ownedContracts.map(value=>({...value,version:"2.0.0-beta.1+Build.7"}))};
  assert.equal(planPublication(undefined,fullVersion),undefined);
  for (const version of ["1.0.0","2.0.0","2.0.0-beta.1","2.0.0-beta.1+build.7"])
    assert.throws(()=>planPublication(undefined,{...fullVersion,ownedContracts:fullVersion.ownedContracts.map(value=>({...value,version}))}),/full owning package version/);
  assert.throws(()=>planPublication(undefined,{...privateOnly,publishedAtUtc:"2026-02-30T12:34:56Z"}),/calendar/);
  assert.throws(()=>planPublication(undefined,publication),/immutable baseline snapshots/);
});

test("public-first publication classification rejects demotion through current, historical-only and mixed-history identities", async () => {
  const {baseline,publication,privateSnapshot} = await mixed(); const entry = baseline.contracts.get("sample")!;
  const demoted = {...publication,ownedContracts:publication.ownedContracts.map(value=>({...value,exposure:"package" as const}))};
  const laterPrivate = await privateSnapshot("3.0.0",entry.contractId);
  for (const current of [entry.current,undefined,laterPrivate]) {
    const governed = parseBaseline(writeBaseline(baseline.packageId,new Map([["renamed-key",{contractId:entry.contractId,...(current?{current}:{}),history:current===entry.current?[]:[entry.current!]}]]),baseline.retiredPrivateContracts));
    assert.throws(()=>planPublication(governed,demoted),/immutable baseline snapshots/);
    assert.throws(()=>planPublication(governed,{...publication,ownedContracts:publication.ownedContracts.filter(value=>value.exposure==="package")}),/frozen snapshot is absent/);
  }
});

test("publication import fences mixed evidence, retired bytes, replay mtimes and private Registry tuple tampering", async t => {
  const {baseline,publication} = await mixed(); const root = await mkdtemp(resolve(tmpdir(),"sunder-publication-")); const path = resolve(root,"sunder-contracts.owned.json");
  const inspected = publicationInspection(publication); let revalidated = 0;
  t.mock.method(candidates,"inspectContractCandidate",async (_path: string,_signal: AbortSignal|undefined,owned: boolean) => {assert.equal(owned,true);return {...inspected.candidate,revalidate:async()=>{revalidated++;}};});
  const run = (details = inspected.details) => importContractPublication(root,{packagePath:"foreign-csharp.sunderpkg",registryOrigin:publication.registryOrigin,
    fetchImplementation:inspected.fetch(details)});
  try {
    await writeFile(path,baseline.bytes); assert.equal(await run(),true); const confirmed = await readFile(path); const time = (await stat(path)).mtimeMs;
    assert.deepEqual(confirmed,planPublication(baseline,{...publication,contentIdentity:inspected.contentIdentity}));
    assert.equal(await run(),false); assert.deepEqual(await readFile(path),confirmed); assert.equal((await stat(path)).mtimeMs,time); assert.ok(revalidated>=3);
    for (const mutation of ["missing","extra","descriptor","abi","version","exposure"] as const) {
      const details = structuredClone(inspected.details); const bundles = details.contractBundles; const privateBundle = bundles.find(value=>value.exposure==="package")!;
      if (mutation==="missing") details.contractBundles=bundles.filter(value=>value!==privateBundle);
      else if (mutation==="extra") bundles.push({...privateBundle,contractId:"example.owner.invented"});
      else if (mutation==="descriptor") privateBundle.descriptorSha256="f".repeat(64);
      else if (mutation==="abi") privateBundle.abiSha256="f".repeat(64);
      else if (mutation==="version") privateBundle.version="1.0.0";
      else privateBundle.exposure="public";
      await assert.rejects(run(details),/publication-evidence-mismatch/); assert.deepEqual(await readFile(path),confirmed); assert.equal((await stat(path)).mtimeMs,time);
    }
  } finally { await rm(root,{recursive:true,force:true}); }
});

test("baseline-free private publication validates Registry and inspector evidence without manufacturing a baseline or hiding public evidence", async t => {
  const {publication} = await mixed(); const privateOnly = {...publication,ownedContracts:publication.ownedContracts.filter(value=>value.exposure==="package")};
  const root = await mkdtemp(resolve(tmpdir(),"sunder-private-publication-")); const path = resolve(root,"sunder-contracts.owned.json");
  const inspected = publicationInspection(privateOnly); let calls = 0;
  t.mock.method(candidates,"inspectContractCandidate",async (_path: string,_signal: AbortSignal|undefined,owned: boolean)=>{calls++;assert.equal(owned,true);return inspected.candidate;});
  const run = () => importContractPublication(root,{packagePath:"foreign-csharp.sunderpkg",registryOrigin:publication.registryOrigin,fetchImplementation:inspected.fetch(inspected.details)});
  try {
    assert.equal(await run(),false); assert.equal(await run(),false); assert.equal(calls,2); await assert.rejects(stat(path),{code:"ENOENT"});
    const generated = resolve(root,".sunder/generated/rpc"); await mkdir(generated,{recursive:true});
    await writeFile(resolve(generated,"owned-baseline.sha256"),"a".repeat(64)+"\n"); await assert.rejects(run(),/baseline/);
    await writeFile(resolve(generated,"owned-baseline.sha256"),"absent\n");
    await writeFile(resolve(generated,"authoring-receipt.v1.json"),JSON.stringify({schemaVersion:1,packageId:"example.owner",packageVersion:"2.0.0",contracts:[],bundles:[]},null,2)+"\n");
    await writeFile(resolve(generated,"package-export.v1.json"),JSON.stringify({schemaVersion:1,packageId:"example.owner",packageVersion:"2.0.0",contracts:[{contractVersion:"2.0.0",source:"owned",exposure:"public"}]},null,2)+"\n");
    await assert.rejects(run(),/baseline/); await assert.rejects(stat(path),{code:"ENOENT"});
  } finally { await rm(root,{recursive:true,force:true}); }
});

test("publication failure and no-op paths revalidate archive bytes and baseline absence under the lease", async t => {
  const {baseline,publication} = await mixed(); const root = await mkdtemp(resolve(tmpdir(),"sunder-publication-race-")); const path=resolve(root,"sunder-contracts.owned.json");
  const archive=resolve(root,"foreign.sunderpkg"); await writeFile(archive,"original"); let inspected=publicationInspection(publication);
  t.mock.method(candidates,"inspectContractCandidate",async()=>({...inspected.candidate,revalidate:async()=>assert.equal(await readFile(archive,"utf8"),"original","archive changed")}));
  try {
    await writeFile(path,baseline.bytes);
    await assert.rejects(importContractPublication(root,{packagePath:archive,registryOrigin:publication.registryOrigin,fetchImplementation:async(input,init)=>{
      if(String(input).endsWith("/public-api")) await appendFile(archive,"changed"); return inspected.fetch(inspected.details)(input,init);
    }}),/archive changed/); assert.deepEqual(await readFile(path),baseline.bytes);
    await rm(path); await writeFile(archive,"original"); inspected=publicationInspection({...publication,ownedContracts:publication.ownedContracts.filter(value=>value.exposure==="package")});
    await assert.rejects(importContractPublication(root,{packagePath:archive,registryOrigin:publication.registryOrigin,fetchImplementation:async(input,init)=>{
      if(String(input).endsWith("/public-api")) await writeFile(path,baseline.bytes); return inspected.fetch(inspected.details)(input,init);
    }}),/baseline changed/i); assert.deepEqual(await readFile(path),baseline.bytes);
  } finally { await rm(root,{recursive:true,force:true}); }
});

test("publication recovery confirms retained history without resurrecting a current contract", async () => {
  const { baseline, snapshot, publication } = await fixture();
  const historyOnly = parseBaseline(writeBaseline(baseline.packageId, new Map([["sample", { contractId: snapshot.descriptor.contractId, history: [snapshot] }]])));
  const result = parseBaseline(planPublication(historyOnly, publication));
  assert.equal(result.contracts.get("sample")!.current, undefined); assert.equal(result.contracts.get("sample")!.history[0]!.state, "published");
});

for (const mismatch of ["draft", "owner", "release", "descriptor", "abi", "exposure", "missing", "time", "empty-baseline"]) test(`publication rejects ${mismatch} rather than inventing authority`, async () => {
  const { baseline, publication } = await fixture(mismatch === "draft" ? "draft" : "release-frozen");
  const invalid = { ...publication, ownedContracts: publication.ownedContracts.map(value => ({ ...value })) };
  if (mismatch === "owner") invalid.packageId = "other.owner";
  if (mismatch === "release") invalid.packageVersion = "99.0.0";
  if (mismatch === "descriptor") invalid.ownedContracts[0]!.descriptorSha256 = "f".repeat(64);
  if (mismatch === "abi") invalid.ownedContracts[0]!.abiSha256 = "f".repeat(64);
  if (mismatch === "exposure") invalid.ownedContracts[0]!.exposure = "package";
  if (mismatch === "missing") invalid.ownedContracts = [];
  if (mismatch === "time") invalid.publishedAtUtc = "2026-09-17T12:34:56+01:00";
  assert.throws(() => planPublication(mismatch === "empty-baseline" ? emptyBaseline("example.owner") : baseline, invalid));
});
