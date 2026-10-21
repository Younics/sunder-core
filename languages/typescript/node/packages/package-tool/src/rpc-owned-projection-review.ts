import { createHash } from "node:crypto";
import { canonicalRpcDescriptorBytes, canonicalRpcAbiProjectionBytes, SUNDER_RPC_V1_ABI_DOMAIN_SEPARATOR, type RpcContractDescriptor } from "@sunder/sdk";
import type { LoadedContract } from "./model";
import type { OwnedBaseline, OwnedSnapshot } from "./rpc-baseline";
import { fields, ordered, outerBytes, parseOuter, string, type Outer, type OuterObject } from "./rpc-baseline-json";
import { classify, compareVersions, minimumOwnedVersion, type ChangeKind } from "./rpc-evolution";
import { methodPoliciesOuter, modelProjectionOuter, readMethodPolicies, readModelProjection, type RpcModelProjection, type RpcMethodPolicies, type RpcModelProjectionContract } from "./rpc-model-projection";

export interface OwnedProjectionReview {
  readonly reviewKind: "accepted-source" | "initial-source";
  readonly modelProjection: RpcModelProjection;
  readonly methodPolicies?: RpcMethodPolicies;
  readonly json: OuterObject;
}
const digest = (bytes: Uint8Array): string => createHash("sha256").update(bytes).digest("hex");
export function reviewedProjectionBytes(projection: RpcModelProjection, policies?: RpcMethodPolicies): Buffer {
  return outerBytes({ modelProjection: modelProjectionOuter(projection), ...methodPoliciesOuter(policies) });
}
export function readProjectionReview(value: Outer, descriptor: RpcContractDescriptor): OwnedProjectionReview {
  const json = fields(value, ["contractVersion", "descriptorSha256", "abiSha256", "reviewKind", "canonicalProjection", "projectionSha256"]);
  const abi = createHash("sha256").update(SUNDER_RPC_V1_ABI_DOMAIN_SEPARATOR).update(canonicalRpcAbiProjectionBytes(descriptor)).digest("hex");
  if (json.contractVersion !== descriptor.version || json.descriptorSha256 !== digest(canonicalRpcDescriptorBytes(descriptor)) || json.abiSha256 !== abi)
    throw new Error("Owned source projection review requires exact retained descriptor/ABI identity.");
  if (json.reviewKind !== "accepted-source" && json.reviewKind !== "initial-source") throw new Error("Invalid owned source projection review kind.");
  const bytes = Buffer.from(string(json.canonicalProjection));
  const root = fields(parseOuter(bytes, { bytes: 4_194_304, values: 262_144 }), ["modelProjection"], ["methodPolicies"]);
  const modelProjection = readModelProjection(root.modelProjection, descriptor), methodPolicies = readMethodPolicies(root.methodPolicies, descriptor);
  if (!bytes.equals(reviewedProjectionBytes(modelProjection, methodPolicies)) || json.projectionSha256 !== digest(bytes))
    throw new Error("Owned source projection review requires exact canonical projection bytes and hash.");
  return Object.freeze({ reviewKind: json.reviewKind, modelProjection, ...(methodPolicies === undefined ? {} : { methodPolicies }), json });
}
export function createProjectionReview(descriptor: RpcContractDescriptor, source: Pick<LoadedContract, "modelProjection" | "methodPolicies">): OwnedProjectionReview {
  const bytes = reviewedProjectionBytes(source.modelProjection, source.methodPolicies);
  return readProjectionReview(ordered([
    ["contractVersion", descriptor.version], ["descriptorSha256", digest(canonicalRpcDescriptorBytes(descriptor))],
    ["abiSha256", createHash("sha256").update(SUNDER_RPC_V1_ABI_DOMAIN_SEPARATOR).update(canonicalRpcAbiProjectionBytes(descriptor)).digest("hex")],
    ["reviewKind", "accepted-source"], ["canonicalProjection", bytes.toString()], ["projectionSha256", digest(bytes)],
  ]), descriptor);
}
export function sameReviewedProjection(review: OwnedProjectionReview | undefined, source: Pick<LoadedContract, "modelProjection" | "methodPolicies">): boolean {
  return !!review && reviewedProjectionBytes(review.modelProjection, review.methodPolicies).equals(reviewedProjectionBytes(source.modelProjection, source.methodPolicies));
}
/** Exported-source classification is independent of descriptor/ABI compatibility. */
export function projectionChange(previous: OwnedSnapshot, source: Pick<LoadedContract, "descriptor" | "modelProjection" | "methodPolicies">): ChangeKind {
  const review = previous.projectionReview;
  if (!review) return "Major"; // Unknown, never inferred from changed current source.
  const shape = descriptorSourceShape(previous.descriptor, source.descriptor);
  if (shape === "Major") return shape;
  if (sameReviewedProjection(review, source)) return shape;
  const before = review.modelProjection, after = source.modelProjection;
  if (Object.entries(before.models).some(([key, name]) => after.models[key] !== name)
    || Object.entries(before.serviceReferences).some(([key, target]) => after.serviceReferences[key]?.contractId !== target.contractId || after.serviceReferences[key]?.serviceId !== target.serviceId)
    || Object.keys(after.models).some(key => !Object.hasOwn(before.models, key) && Object.hasOwn(previous.descriptor.$defs, key))
    || Object.keys(after.serviceReferences).some(key => !Object.hasOwn(before.serviceReferences, key) && Object.hasOwn(previous.descriptor.$defs, key))
    || !outerBytes(modelProjectionOuter(before).optionalServiceReferenceMembers ?? {}).equals(outerBytes(modelProjectionOuter(after).optionalServiceReferenceMembers ?? {}))) return "Major";
  for (const [service, value] of Object.entries(previous.descriptor.services)) for (const method of Object.keys(value.methods))
    if (review.methodPolicies?.[service]?.[method]?.timeoutSeconds !== source.methodPolicies?.[service]?.[method]?.timeoutSeconds) return "Major";
  return "Minor";
}
interface SourceAbi {
  readonly services: Readonly<Record<string, unknown>>;
  readonly $defs: Readonly<Record<string, unknown>>;
  readonly errors: Readonly<Record<string, unknown>>;
}
function descriptorSourceShape(previous: RpcContractDescriptor, candidate: RpcContractDescriptor): ChangeKind {
  // Wire containment does not prove caller/provider source assignability. New
  // methods/services require new provider members; an optional output member may
  // still be read by existing source. Compare exact normalized shapes conservatively.
  const beforeBytes = Buffer.from(canonicalRpcAbiProjectionBytes(previous)), afterBytes = Buffer.from(canonicalRpcAbiProjectionBytes(candidate));
  if (beforeBytes.equals(afterBytes)) return "Unchanged";
  // Parser-bounded size/depth and definition/service/method counts. No recursive
  // reference expansion: all existing definitions (including indirect/anonymous
  // values) are compared once. Canonical ABI removes annotations and version text.
  const before = JSON.parse(beforeBytes.toString()) as SourceAbi, after = JSON.parse(afterBytes.toString()) as SourceAbi;
  const same = (left: unknown, right: unknown): boolean => JSON.stringify(left) === JSON.stringify(right);
  const preservesEntries = (oldMap: SourceAbi["$defs"], newMap: SourceAbi["$defs"]): boolean => Object.keys(oldMap)
    .every(key => Object.hasOwn(newMap, key) && same(oldMap[key], newMap[key]));
  if (previous.contractId !== candidate.contractId || previous.descriptorVersion !== candidate.descriptorVersion || previous.schemaProfile !== candidate.schemaProfile
    || !same(before.services, after.services) || !preservesEntries(before.$defs, after.$defs) || !preservesEntries(before.errors, after.errors)) return "Major";
  return "Minor"; // Only new definitions/error declarations, with all existing shapes intact.
}
export function requireReviewedProjection(key: string, snapshot: OwnedSnapshot, source: LoadedContract): void {
  if (!snapshot.projectionReview) throw new Error(`rpc-authoring.source-projection-unreviewed: '${key}@${snapshot.descriptor.version}' has descriptor/ABI evidence only. Restore the reviewed source projection or explicitly accept a new Major tuple. Current source cannot supply historical projection evidence.`);
  if (!sameReviewedProjection(snapshot.projectionReview, source)) throw new Error(`rpc-authoring.source-projection-drift: '${key}@${snapshot.descriptor.version}' wire classification ${classify(snapshot.descriptor, source.descriptor).kind}; exported source classification ${projectionChange(snapshot, source)}. Model names, service-reference/optional policies and method policies must match reviewed evidence; run contracts accept.`);
}
export function validateProjectionReviewHistory(descriptor: RpcContractDescriptor, review: OwnedProjectionReview | undefined, comparisons: readonly OwnedSnapshot[]): void {
  for (const previous of comparisons) {
    const kind = review ? projectionChange(previous, { ...review, descriptor }) : "Major";
    const minimum = minimumOwnedVersion(previous.descriptor.version, kind);
    if (compareVersions(descriptor.version, minimum) < 0) throw new Error(`rpc-authoring.source-projection-bump-required: Exported source is ${kind} relative to immutable history '${previous.descriptor.version}'${previous.projectionReview ? "" : " (historical source projection unavailable)"}; minimum '${minimum}'.`);
  }
}
export function validateReviewedArtifactProjections(baseline: OwnedBaseline,
  owned: readonly { readonly contractId: string; readonly version: string; readonly exposure: string }[], projections: readonly RpcModelProjectionContract[]): void {
  const snapshots = [...baseline.contracts.values()].flatMap(entry => [...entry.history, ...entry.current ? [entry.current] : []]);
  for (const bundle of owned.filter(bundle => bundle.exposure === "public")) {
    const snapshot = snapshots.find(value => value.descriptor.contractId === bundle.contractId && value.descriptor.version === bundle.version);
    if (!snapshot?.projectionReview) continue; // Legacy confirmation never invents a source review.
    const actual = projections.find(value => value.contractId === bundle.contractId && value.contractVersion === bundle.version);
    if (!actual || actual.descriptorSha256 !== snapshot.json.descriptorSha256 || actual.abiSha256 !== snapshot.json.abiSha256 || !sameReviewedProjection(snapshot.projectionReview, actual))
      throw new Error(`rpc-authoring.publication-evidence-mismatch: Published artifact projection '${bundle.contractId}@${bundle.version}' differs from the retained source review.`);
  }
}
