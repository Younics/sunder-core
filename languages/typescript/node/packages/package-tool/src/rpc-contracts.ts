import { loadProjectWithOwnedFrame } from "./model";
import { emptyBaseline, parseBaseline, planAcceptance, planFreeze, planPrivateRetirement, publicLifecycleCandidates, type AcceptOptions } from "./rpc-baseline";
import { captureBaseline, commitBaseline, verifyFreshOwnedSource, withBaselineLease } from "./rpc-baseline-store";
import type { RegistryOriginOptions } from "./rpc-contract-lock";
import { modelProjectionBytes,methodPoliciesBytes } from "./rpc-model-projection";
import { roleUsesBytes } from "./rpc-owned-uses";

export async function contractsCommand(operation: "check" | "accept" | "freeze" | "migrate-private", projectPath: string, options: AcceptOptions = {}, registryOptions: RegistryOriginOptions = {}): Promise<boolean> {
  const mode = registryOptions.allowDevelopmentRegistryOrigin === undefined ? {} : { allowDevelopmentRegistryOrigin: registryOptions.allowDevelopmentRegistryOrigin };
  // This helper is also callable from JavaScript. The TS union and the CLI
  // parser do not guard a deep-imported call from falling through to Freeze.
  if (operation !== "check" && operation !== "accept" && operation !== "freeze" && operation !== "migrate-private") throw new Error("Unknown contracts operation. Expected check, accept, freeze, or migrate-private.");
  const input = await captureBaseline(projectPath);
  if (operation !== "accept" && (options.currentVersion !== undefined || options.nextVersion !== undefined || options.acknowledgeBreakingChange)
    || operation === "check" && options.key !== undefined) throw new Error("Check takes no key/version flags; Freeze takes only an optional key.");
  const verifyBootstrap = async (): Promise<void> => { await input.revalidate(); };
  await verifyBootstrap();
  const project = await loadProjectWithOwnedFrame(projectPath, { ...mode, physicalBaseline: input, allowDrift: operation === "accept" || operation === "migrate-private" });
  const analysis = project.ownedRpc!;
  const baseline = input.baseline ?? emptyBaseline(project.config.id);
  const verify = async (): Promise<void> => { await analysis.revalidate(); await verifyBootstrap(); };
  if (operation === "migrate-private") {
    if (!input.baseline) { await verify(); return false; }
    return commitBaseline(input, planPrivateRetirement(baseline, analysis.contracts, options.key), verify);
  }
  if (!input.baseline && publicLifecycleCandidates(baseline, analysis.contracts).length === 0) { await verify(); return false; }
  if (operation === "check") { await withBaselineLease(input, verify); return false; }
  const proposal = operation === "accept" ? planAcceptance(baseline, analysis.contracts, options) : planFreeze(baseline, analysis.contracts, project.config.version, options.key);
  const proposed = parseBaseline(proposal);
  if (operation === "accept") {
    // Physical input remains the ORIGINAL baseline (or proven absence). Only the
    // logical compilation frame changes. Re-run the entire source/type/runtime
    // prepass at allocated versions; never publish generated trees from Accept.
    const verified = await loadProjectWithOwnedFrame(projectPath, { ...mode, physicalBaseline: input, identityBaseline: proposed, requireBaseline: true });
    if (analysis.physicalFingerprint !== verified.ownedRpc!.physicalFingerprint) throw new Error("RPC physical input bytes or input set changed between acceptance compilations.");
    if(!roleUsesBytes(analysis.roleUses).equals(roleUsesBytes(verified.ownedRpc!.roleUses)))throw new Error("RPC typed Runtime source intent changed between acceptance compilations.");
    if (analysis.contracts.length !== verified.ownedRpc!.contracts.length || analysis.contracts.some(contract => {
      const accepted = verified.ownedRpc!.contracts.find(value => value.contractKey === contract.contractKey);
      return !accepted || !methodPoliciesBytes(contract.methodPolicies).equals(methodPoliciesBytes(accepted.methodPolicies)) || contract.naturalContent!==accepted.naturalContent || !modelProjectionBytes(contract.modelProjection).equals(modelProjectionBytes(accepted.modelProjection));
    })) throw new Error("RPC source-derived model projection changed between acceptance compilations.");
    return commitBaseline(input, proposal, async () => { await verify(); await verified.ownedRpc!.revalidate(); });
  }
  return commitBaseline(input, proposal, verify);
}
