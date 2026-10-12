import assert from "node:assert/strict";
import { mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import ts from "typescript";
import { loadProjectWithOwnedFrame, type LoadedProject } from "../src/model";
import { contractsCommand } from "../src/rpc-contracts";
import { generateRpcBindingsForLoadedProject } from "../src/rpc-bindings";
import { MODEL_PROJECTION_PATH, methodPoliciesBytes, modelProjectionBytes, validateModelProjectionSet } from "../src/rpc-model-projection";
import { parseContractLock } from "../src/rpc-contract-lock";
import { publishContractCache } from "../src/rpc-contract-cache";
import { publishModelProjectionCache } from "../src/rpc-model-projection-cache";
import { preparePackageImports } from "../src/rpc-package-imports";
import { canonicalCandidateFixture } from "./model-projection-fixtures";
import { emptyLock, fixtureSource } from "./service-fixtures";

type Action = "discover" | "invoke" | "subscribe";
interface Intent { behavior?: readonly Action[]; control?: readonly Action[]; runtimeBehavior?: boolean; streamError?: boolean }
const behaviorId = "example.roles.behavior", controlId = "example.roles.run.control";

function source(language: "ts" | "js", intent: Intent = {}): string {
  const app = (actions: readonly Action[] | undefined) => actions ? `,appUse:{actions:${JSON.stringify(actions)}}` : "";
  return fixtureSource(`import {defineService,defineServiceError} from "@sunder/sdk";
/*ts import type {ServiceReference} from "@sunder/sdk";*/
/** @typedef {{controls:Record<string,import("@sunder/sdk").ServiceReference<typeof runControl>>,next?:BehaviorRequest,children?:BehaviorRequest[]}} BehaviorRequest */
/*ts type BehaviorRequest={controls:Record<string,ServiceReference<typeof runControl>>;next?:BehaviorRequest;children?:BehaviorRequest[]};*/
/** @typedef {{behaviors:Record<string,import("@sunder/sdk").ServiceReference<typeof behavior>>,next?:ControlRequest}} ControlRequest */
/*ts type ControlRequest={behaviors:Record<string,ServiceReference<typeof behavior>>;next?:ControlRequest};*/
/** @typedef {{control:import("@sunder/sdk").ServiceReference<typeof runControl>}} Failure */
/*ts type Failure={control:ServiceReference<typeof runControl>};*/
/** @typedef {{run:(request:BehaviorRequest)=>string,watch:()=>AsyncIterable<string>}} Behavior */
/*ts interface Behavior {run(request:BehaviorRequest):string;watch():AsyncIterable<string>}*/
/** @typedef {{step:()=>ControlRequest}} RunControl */
/*ts interface RunControl {step():ControlRequest}*/
/** @type {import("@sunder/sdk").ServiceDefinition<Behavior>} */
export const behavior=defineService/*<Behavior>*/(null,{declarationOnly:true,exposure:"public"${intent.runtimeBehavior === false ? "" : ',runtimeUse:{actions:["discover"]}'}${app(intent.behavior)},
 errors:{"example.failure":/** @type {import("@sunder/sdk").ServiceError<Failure>} */(defineServiceError/*<Failure>*/())},
 methods:{run:{timeoutSeconds:2100},watch:{errors:${JSON.stringify(intent.streamError ? ["example.failure"] : [])}}}});
/** @type {import("@sunder/sdk").ServiceDefinition<RunControl>} */
export const runControl=defineService/*<RunControl>*/(null,{declarationOnly:true,exposure:"public",runtimeUse:{actions:["discover"]}${app(intent.control)}});
export const behaviorProvider=defineService({
 /** @param {BehaviorRequest} request @returns {string} */
 run(request/*:BehaviorRequest*/)/*:string*/{return "state";},
 /** @returns {AsyncIterable<string>} */
 async *watch()/*:AsyncIterable<string>*/{yield "state";}
},{implements:behavior});
export const controlProvider=defineService({
 /** @returns {ControlRequest} */
 step()/*:ControlRequest*/{return {behaviors:{}};}
},{implements:runControl});
`, language);
}

async function fixture(language: "ts" | "js") {
  const root = await mkdtemp(join(tmpdir(), "sunder-reference-roles-"));
  await mkdir(join(root, "src")); await mkdir(join(root, "app"));
  const config = { schemaVersion: 3, id: "example.roles", name: "Role closure", version: "1.0.0", workerCapabilities: [], services: {module: `src/services.${language}`} };
  const path = join(root, config.services.module);
  await writeFile(path, source(language)); await writeFile(join(root, "sunder-contracts.lock.json"), emptyLock);
  await writeFile(join(root, "package.json"), JSON.stringify({ type: "module", imports: {"#sunder/api": {types: "./.sunder/generated/rpc/api.d.ts", default: "./.sunder/generated/rpc/api.js"}} }));
  const configure = async (app: boolean, id = config.id) => writeFile(join(root, "sunder.package.json"), JSON.stringify({...config,id,...(app ? {app: {
    root: "app", entryPoint: "index.html", views: [{viewId: `${id}.state`,displayName: "State",route: "/",defaultPlacement: "middle",showInHotbar: true}],
  }} : {})}));
  await configure(false); await writeFile(join(root, "app/index.html"), "<!doctype html><title>Unrelated App state</title>");
  return { root, path, configure, write: (intent: Intent) => writeFile(path, source(language, intent)), dispose: () => rm(root, {recursive:true,force:true}) };
}

function receipt(project: LoadedProject, role: "app" | "runtime") {
  return JSON.parse(project.ownedRpc!.files.get(role === "app" ? "app-authoring-receipt.v1.json" : "authoring-receipt.v1.json")!);
}
async function checkAppState(root: string, language: "ts" | "js", namespace = "SelfModels"): Promise<void> {
  const path = join(root, `app/state.${language}`);
  const model = `import("#sunder/api").${namespace}.BehaviorRequest`;
  await writeFile(path, language === "ts" ? `export const state: {active?: ${model}} = {};` : `/** @type {{active?: ${model}}} */ export const state = {};`);
  const program = ts.createProgram([path], {target:ts.ScriptTarget.ES2023,module:ts.ModuleKind.ESNext,moduleResolution:ts.ModuleResolutionKind.Bundler,
    strict:true,allowJs:true,checkJs:true,noEmit:true,types:[],skipLibCheck:false,paths:{
      "@sunder/sdk/browser":[require.resolve("@sunder/sdk/browser").replace(/\.js$/u,".d.ts")],
      "@sunder/sdk/core":[require.resolve("@sunder/sdk/core").replace(/\.js$/u,".d.ts")],
    }});
  assert.deepEqual(ts.getPreEmitDiagnostics(program).map(value=>ts.flattenDiagnosticMessageText(value.messageText,"\n")),[]);
}

for (const language of ["ts", "js"] as const) test(`${language} reference role closure follows execution intent, not shared Models or unrelated App state`, async t => {
  const c = await fixture(language);
  try {
    let baseline: Buffer, runtime: LoadedProject;
    await t.test("Runtime providers activate all methods with only explicit Runtime discover uses", async () => {
      await contractsCommand("accept", c.root); await contractsCommand("check", c.root);
      runtime = await loadProjectWithOwnedFrame(c.root); await generateRpcBindingsForLoadedProject(runtime);
      baseline = await readFile(join(c.root,"sunder-contracts.owned.json"));
      assert.deepEqual(runtime.config.usesContracts!.map(use=>[use.role,use.contractId,use.actions]), [
        ["runtime",behaviorId,["discover"]],["runtime",controlId,["discover"]],
      ]);
    });
    await t.test("Unrelated App Models add no uses; unused invalid metadata still fails the full graph", async () => {
      await c.configure(true); await contractsCommand("check", c.root);
      const project = await loadProjectWithOwnedFrame(c.root); await generateRpcBindingsForLoadedProject(project); await checkAppState(c.root,language);
      const app = receipt(project,"app"); assert.deepEqual(app.uses,[]); assert.deepEqual(app.providers,[]);
      assert.deepEqual(app.contracts.map((item:{contractId:string})=>item.contractId),[behaviorId,controlId]);
      assert.equal(project.ownedRpc!.files.get(MODEL_PROJECTION_PATH),runtime.ownedRpc!.files.get(MODEL_PROJECTION_PATH));
      const graph = project.contracts.map(contract=>({contractId:contract.descriptor.contractId,contractVersion:contract.descriptor.version,
        descriptorSha256:contract.descriptorSha256,abiSha256:contract.abiSha256,modelProjection:structuredClone(contract.modelProjection),descriptor:contract.descriptor,
        authorityKind:"package",authorityId:project.config.id}));
      const target = Object.values(graph[0]!.modelProjection.serviceReferences)[0]!;
      Object.assign(target,{contractId:"example.missing"});
      assert.throws(()=>validateModelProjectionSet(graph),/Service target must resolve exactly once/);
    });
    const cases: readonly {name:string;intent:Intent;valid:boolean;drift?:boolean}[] = [
      {name:"App discover-only holder roots no value graph",intent:{behavior:["discover"]},valid:true},
      {name:"App subscribe skips unary dictionary refs and undeclared error details",intent:{behavior:["subscribe"]},valid:true},
      {name:"App invocation reaches recursive dictionary request references",intent:{behavior:["invoke"]},valid:false},
      {name:"Target invoke permission cannot replace same-role discover",intent:{behavior:["invoke"],control:["invoke"]},valid:false},
      {name:"Target discover suffices without activating its own reference-bearing methods",intent:{behavior:["invoke"],control:["discover"]},valid:true},
      {name:"Providers root output references even without consumer invoke intent",intent:{runtimeBehavior:false},valid:false},
      {name:"Declared stream error details activate their reference graph",intent:{behavior:["subscribe"],streamError:true},valid:false,drift:true},
      {name:"Declared stream error references require only target discover",intent:{behavior:["subscribe"],control:["discover"],streamError:true},valid:true,drift:true},
    ];
    for (const item of cases) await t.test(item.name, async () => {
      await c.write(item.intent);
      const load = () => loadProjectWithOwnedFrame(c.root,{allowDrift:item.drift});
      if (!item.valid) { await assert.rejects(load(), /rpc-authoring\.service-reference-use-missing:.*no reviewed discover/); return; }
      const project = await load();
      assert.deepEqual(receipt(project,"app").uses.map((use:{contractKey:string;actions:string[]})=>[use.contractKey,use.actions]),
        [["behavior",item.intent.behavior],["run-control",item.intent.control]].filter(([,actions])=>actions!==undefined));
      if (!item.drift) {
        assert.equal(project.ownedRpc!.files.get(MODEL_PROJECTION_PATH),runtime.ownedRpc!.files.get(MODEL_PROJECTION_PATH));
        assert.deepEqual(project.contracts.map(contract=>[contract.descriptorSha256,contract.abiSha256]),runtime.contracts.map(contract=>[contract.descriptorSha256,contract.abiSha256]));
      }
      assert.deepEqual(await readFile(join(c.root,"sunder-contracts.owned.json")),baseline);
    });
  } finally { await c.dispose(); }
});

for (const language of ["ts", "js"] as const) test(`${language} imported reference intent preserves exact metadata, role receipts and materialized-target checks`, async () => {
  const producer = await fixture(language), consumer = await fixture(language);
  try {
    const project = await loadProjectWithOwnedFrame(producer.root,{allowDrift:true});
    const descriptors = new Map(project.contracts.map(contract=>[contract.descriptorSha256,contract.canonicalBytes]));
    const records = Object.fromEntries(project.contracts.map(contract=>[contract.descriptor.contractId,{artifactSource:"owned",authorityKind:"package",authorityId:project.config.id,
      selectedAuthorityRelease:project.config.version,selectedAuthorityContentIdentity:"0".repeat(64),resolvedContractVersion:contract.descriptor.version,descriptorBytes:contract.canonicalBytes.length,
      descriptorSha256:contract.descriptorSha256,abiSha256:contract.abiSha256,etag:`"${contract.descriptorSha256}"`}]));
    const candidate = await canonicalCandidateFixture(project.config.id,{requestedVersionRange:"1.0.0",includePrerelease:false,selectedPackageVersion:"1.0.0",selectedPackageContentIdentity:"0".repeat(64),
      publicContractCount:project.contracts.length,publicInventorySha256:"0".repeat(64),modelProjectionBytes:1,modelProjectionSha256:"0".repeat(64),contracts:records},
      Buffer.from(project.ownedRpc!.files.get(MODEL_PROJECTION_PATH)!),descriptors);
    const lockFor = (roles:readonly ("app"|"runtime")[], providerIds:readonly string[]=[]) => parseContractLock(Buffer.from(JSON.stringify({lockVersion:1,
      packageArtifacts:{[project.config.id]:candidate.artifact},packageImports:roles.length?{[project.config.id]:{localName:"library",presence:"required",requiredContracts:[],consumerRoles:roles}}:{},
      providerImports:Object.fromEntries(providerIds.map((contractId,index)=>[`provider${index}`,{packageId:project.config.id,contractId}])),platformImports:{}},null,2)+"\n"));
    const lock = lockFor(["runtime"]), cache = join(consumer.root,"cache");
    await publishContractCache(cache,descriptors,lock.requirements);
    await publishModelProjectionCache(cache,new Map([[project.config.id,candidate.projection]]),lock.packageArtifacts,descriptors);
    await consumer.configure(true,"example.consumer"); await writeFile(consumer.path,"export {};\n"); await writeFile(join(consumer.root,"sunder-contracts.lock.json"),lock.exactBytes);
    const loaded = await loadProjectWithOwnedFrame(consumer.root,{contractCacheRoot:cache});
    await generateRpcBindingsForLoadedProject(loaded); await checkAppState(consumer.root,language,"LibraryModels");
    assert.deepEqual(receipt(loaded,"app").uses,[]); assert.deepEqual(receipt(loaded,"app").packageArtifacts,[]);
    const runtime = receipt(loaded,"runtime"); assert.deepEqual(runtime.contracts.map((item:{contractKey:string})=>item.contractKey),[behaviorId,controlId]);
    assert.deepEqual(runtime.packageArtifacts,[{packageId:project.config.id,...candidate.artifact,
      contracts:Object.entries(candidate.artifact.contracts).map(([contractId,record])=>({contractId,...record as object})),
    }]);
    assert.deepEqual(await readFile(join(consumer.root,"sunder-contracts.lock.json")),lock.exactBytes);
    for (const contract of loaded.contracts) {
      const original = project.contracts.find(value=>value.descriptor.contractId===contract.descriptor.contractId)!;
      assert.deepEqual(contract.canonicalBytes,original.canonicalBytes);
      assert.deepEqual(modelProjectionBytes(contract.modelProjection),modelProjectionBytes(original.modelProjection));
      assert.deepEqual(methodPoliciesBytes(contract.methodPolicies),methodPoliciesBytes(original.methodPolicies));
      const entry=runtime.contracts.find((value:{contractId:string})=>value.contractId===original.descriptor.contractId);
      assert.equal(entry.contractKey,original.descriptor.contractId);assert.equal(entry.canonicalDescriptor,original.canonical);
      assert.equal(entry.descriptorSha256,original.descriptorSha256);assert.equal(entry.abiSha256,original.abiSha256);
      assert.deepEqual(modelProjectionBytes(entry.modelProjection),modelProjectionBytes(original.modelProjection));
      assert.deepEqual(methodPoliciesBytes(entry.methodPolicies),methodPoliciesBytes(original.methodPolicies));
    }
    const provided = await preparePackageImports(lockFor(["runtime"],[behaviorId]),cache,"example.consumer",[],new Set(["app","runtime"]));
    assert.equal(provided.providerSurfaces.size,1); assert.ok(provided.uses.every(use=>use.role==="runtime"));
    const appOnly=await preparePackageImports(lockFor(["app"],[behaviorId]),cache,"example.consumer",[],new Set(["app","runtime"]));
    assert.ok(appOnly.uses.every(use=>use.role==="app"));
    for(const roots of [[behaviorId],[behaviorId,controlId]]){
      const metadata=await preparePackageImports(lockFor([],roots),cache,"example.consumer",[],new Set(["app","runtime"]));
      assert.deepEqual(metadata.uses,[]);assert.equal(metadata.contracts.length,2);
      assert.equal(metadata.providerSurfaces.size,roots.length,"Recursive metadata is not provider registration.");
    }
  } finally { await consumer.dispose(); await producer.dispose(); }
});
