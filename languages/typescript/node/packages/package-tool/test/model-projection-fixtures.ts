import { readFile, writeFile } from "node:fs/promises";
import { createHash } from "node:crypto";
import { resolve } from "node:path";
import { hashBytes, type ContractLock } from "../src/rpc-contract-lock";
import { publishModelProjectionCache } from "../src/rpc-model-projection-cache";
import { parseModelProjectionDocument, projectionDocumentBytes } from "../src/rpc-model-projection";
import { verifyCacheDescriptor } from "../src/rpc-contract-cache";
import { inventoryBytes, type ArtifactContract, type PackageArtifact } from "../src/rpc-contract-lock";

const corpus = resolve(__dirname, "../../../../../../../specs/rpc/v1/authoring/conformance");
/** Consume the coordinated current wire corpus by exact pins. No descriptor-name
 * inference or rewriting of historical descriptor/lock oracles is involved. */
export async function fixtureModelProjections(lock: ContractLock): Promise<ReadonlyMap<string, Buffer>> {
  const expectations = JSON.parse(await readFile(resolve(corpus, "expectations.json"), "utf8")) as {
    immutableArtifactEvidence: readonly { modelProjectionSha256: string; modelProjectionFixturePath: string }[];
  };
  const entries = new Map<string, Buffer>();
  for (const artifact of lock.packageArtifacts.values()) {
    const evidence = expectations.immutableArtifactEvidence.find(entry => entry.modelProjectionSha256 === artifact.modelProjectionSha256);
    if (!evidence) throw new Error("Fixture has no coordinated projection evidence: " + artifact.modelProjectionSha256);
    const bytes = await readFile(resolve(corpus, evidence.modelProjectionFixturePath));
    if (bytes.length !== artifact.modelProjectionBytes || hashBytes(bytes) !== artifact.modelProjectionSha256) throw new Error("Fixture projection pins differ from their corpus evidence.");
    entries.set(artifact.packageId, bytes);
  }
  return entries;
}
export async function publishFixtureModelProjections(cache: string, lock: ContractLock, descriptors: ReadonlyMap<string, Buffer>): Promise<void> {
  await publishModelProjectionCache(cache, await fixtureModelProjections(lock), lock.packageArtifacts, descriptors);
}
/** Mutated security fixtures retain the reviewed origin maps while deliberately
 * changing their enclosing artifact/descriptor evidence. Never infer model names. */
export function rebindFixtureProjection(packageId: string, artifact: Record<string, any>, origin: Buffer): Buffer {
  const projection = parseModelProjectionDocument(origin);
  const contracts = Object.entries(artifact.contracts as Record<string, any>).map(([id, record]) => {
    const entry = projection.contracts.find(entry => entry.contractId === id);
    if (!entry) throw new Error("Derived fixture needs explicit source projection evidence for " + id);
    return { ...entry, contractVersion: record.resolvedContractVersion, descriptorSha256: record.descriptorSha256, abiSha256: record.abiSha256 };
  });
  const bytes = projectionDocumentBytes(packageId,artifact.selectedPackageVersion,contracts);
  artifact.modelProjectionBytes = bytes.length; artifact.modelProjectionSha256 = hashBytes(bytes); return bytes;
}

/** A mock of the external Format inspector boundary, not a second ZIP validator.
 * Current corpus descriptors/projections keep their exact bytes. Derived test
 * identities have real manifest/index hash preimages instead of sentinel hashes. */
export async function canonicalCandidateFixture(packageId: string, input: Record<string, any>, origin: Buffer, descriptors: ReadonlyMap<string, Buffer>) {
  const artifact = structuredClone(input);
  const projection = rebindFixtureProjection(packageId,artifact,origin);
  const manifestSha256 = hashBytes(Buffer.from(JSON.stringify({packageId,version:artifact.selectedPackageVersion,
    contracts:Object.entries(artifact.contracts).map(([contractId,value])=>{
      const record=value as Record<string,any>;
      return {contractId,version:record.resolvedContractVersion,descriptorSha256:record.descriptorSha256,abiSha256:record.abiSha256,
        source:record.artifactSource,authorityKind:record.authorityKind,authorityId:record.authorityId,
        ...(record.artifactSource==="imported"?{selectedAuthorityRelease:record.selectedAuthorityRelease,selectedAuthorityContentIdentity:record.selectedAuthorityContentIdentity}:{})};
    })})));
  const contentIndexSha256 = hashBytes(Buffer.from(JSON.stringify([...descriptors].map(([hash,bytes])=>[hash,bytes.length]).sort().concat([[hashBytes(projection),projection.length]]))));
  const contentIdentity = hashBytes(Buffer.concat([Buffer.from(manifestSha256,"hex"),Buffer.from(contentIndexSha256,"hex")]));
  artifact.selectedPackageContentIdentity = contentIdentity;
  const contracts = new Map<string,ArtifactContract>();
  for (const [id,record] of Object.entries(artifact.contracts) as [string,Record<string,any>][]) {
    if(record.artifactSource==="owned")record.selectedAuthorityContentIdentity=contentIdentity;
    const entry:ArtifactContract={contractId:id,contractVersion:record.resolvedContractVersion,descriptorBytes:record.descriptorBytes,descriptorSha256:record.descriptorSha256,abiSha256:record.abiSha256,
      artifactSource:record.artifactSource,authorityKind:record.authorityKind,authorityId:record.authorityId,selectedAuthorityRelease:record.selectedAuthorityRelease,
      selectedAuthorityContentIdentity:record.selectedAuthorityContentIdentity,etag:record.etag};
    const bytes=descriptors.get(entry.descriptorSha256);if(!bytes)throw new Error("Candidate fixture lacks exact descriptor evidence.");
    await verifyCacheDescriptor(entry,bytes);contracts.set(id,entry);
  }
  const selected:PackageArtifact={packageId,contracts,requestedVersionRange:artifact.requestedVersionRange,includePrerelease:artifact.includePrerelease,
    selectedPackageVersion:artifact.selectedPackageVersion,selectedPackageContentIdentity:contentIdentity,registryOrigin:artifact.registryOrigin,
    publicContractCount:artifact.publicContractCount,publicInventorySha256:artifact.publicInventorySha256,modelProjectionBytes:projection.length,modelProjectionSha256:hashBytes(projection)};
  const inventory=JSON.parse(inventoryBytes(selected).toString());
  artifact.publicInventorySha256=createHash("sha256").update("sunder.package-public-api-inventory.v1\0").update(JSON.stringify(inventory)).digest("hex");
  const recipe={inspection:{manifestSha256,contentIndexSha256,inventory,publicInventorySha256:artifact.publicInventorySha256,
    modelProjectionBytes:projection.length,modelProjectionSha256:hashBytes(projection)},
    files:{"rpc-model-projection.v1.json":projection.toString(),...Object.fromEntries([...descriptors].map(([hash,bytes])=>[hash+".json",bytes.toString()]))}};
  return {artifact,projection,recipe,bytes:Buffer.from(JSON.stringify(recipe))};
}

export async function installFixtureInspector(root:string):Promise<()=>void>{
  const path=resolve(root,"format-inspector-fixture.cjs");const previous=process.env.SUNDER_CLI;
  await writeFile(path,`#!${process.execPath}
const fs=require("node:fs"),p=require("node:path"),crypto=require("node:crypto");
try {
 const args=process.argv.slice(2),body=fs.readFileSync(args[3]),data=JSON.parse(body);const output=args[args.indexOf("--api-output")+1];
 const value={schemaVersion:1,archiveSha256:crypto.createHash("sha256").update(body).digest("hex"),...data.inspection};
 fs.mkdirSync(output);fs.writeFileSync(p.join(output,"inspection.json"),data.receiptText??JSON.stringify(value));
 for(const [name,text] of Object.entries(data.files)){if(!/^[A-Za-z0-9][A-Za-z0-9._-]*\\.json$/.test(name))throw Error("invalid fixture path");fs.writeFileSync(p.join(output,name),text);}
} catch {process.exitCode=1;}
`,{mode:0o755});
  process.env.SUNDER_CLI=path;
  return ()=>{if(process.env.SUNDER_CLI===path){if(previous===undefined)delete process.env.SUNDER_CLI;else process.env.SUNDER_CLI=previous;}};
}
