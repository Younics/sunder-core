import { createHash } from "node:crypto";
import type { InspectedOwnedContract, InspectedContractCandidate } from "../src/rpc-contract-candidate";
import { emptyModelProjection, projectionDocumentBytes } from "../src/rpc-model-projection";

/** Test-only inspector/Registry response boundary. Production still invokes Format. */
export function publicationInspection(publication: {packageId:string;packageVersion:string;publishedAtUtc:string;ownedContracts:readonly InspectedOwnedContract[]}) {
  const manifestSha256="b".repeat(64); const contentIndexSha256="c".repeat(64);
  const contentIdentity=createHash("sha256").update(Buffer.concat([Buffer.from(manifestSha256,"hex"),Buffer.from(contentIndexSha256,"hex")])).digest("hex");
  const inventory={schemaVersion:1,packageId:publication.packageId,packageVersion:publication.packageVersion,packageContentIdentity:contentIdentity,
    publicContractCount:publication.ownedContracts.filter(value=>value.exposure==="public").length,
    contracts:publication.ownedContracts.filter(value=>value.exposure==="public").map(value=>({contractId:value.contractId,contractVersion:value.version,
      descriptorBytes:487,descriptorSha256:value.descriptorSha256,abiSha256:value.abiSha256,artifactSource:"owned",authorityKind:"package",authorityId:publication.packageId,
      selectedAuthorityRelease:publication.packageVersion,selectedAuthorityContentIdentity:contentIdentity}))};
  const publicInventorySha256=createHash("sha256").update("sunder.package-public-api-inventory.v1\0").update(JSON.stringify(inventory)).digest("hex");
  const modelProjectionBytes = projectionDocumentBytes(publication.packageId, publication.packageVersion, inventory.contracts.map(contract => ({ ...contract, modelProjection: emptyModelProjection() })));
  const candidate:InspectedContractCandidate={archiveSha256:"a".repeat(64),archiveBytes:1024,inventory,publicInventorySha256,descriptors:new Map(),
    modelProjectionBytes, modelProjectionSha256: createHash("sha256").update(modelProjectionBytes).digest("hex"),
    ownedContracts:publication.ownedContracts,revalidate:async()=>{}};
  const details={packageId:publication.packageId,version:publication.packageVersion,canonicalArtifact:{sha256:candidate.archiveSha256,size:candidate.archiveBytes},
    publication:{package:{packageId:publication.packageId,version:publication.packageVersion,packageContentIdentity:contentIdentity},publicInventorySha256,
      ownerPrincipalId:"a".repeat(32),initialAvailability:"Yanked"},publishedAtUtc:publication.publishedAtUtc,contractBundles:publication.ownedContracts.map(value=>({...value}))};
  return {candidate,details,contentIdentity,fetch:(value:typeof details):typeof fetch=>async input=>{
    const url=String(input);const response=new Response(JSON.stringify(url.endsWith("/public-api")?{inventory,publicInventorySha256}:value),{headers:{"content-type":"application/json"}});
    Object.defineProperty(response,"url",{value:url});return response;
  }};
}
