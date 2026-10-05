import type { ContractUseConfig, LoadedContract } from "./model";

export function applicableUseActions(contract: Pick<LoadedContract, "descriptor">): ("discover" | "invoke" | "subscribe")[] {
  const methods = Object.values(contract.descriptor.services).flatMap((service) => Object.values(service.methods));
  const actions: ("discover" | "invoke" | "subscribe")[] = ["discover"];
  if (methods.some((method) => method.kind === "unary")) actions.push("invoke");
  if (methods.some((method) => method.kind === "server-stream")) actions.push("subscribe");
  return actions;
}

/** Only an explicit owning-service policy grants a Runtime consumer use. */
export function ownedRuntimeUses(contracts: readonly LoadedContract[]): readonly ContractUseConfig[] {
  return contracts.flatMap(contract => contract.imported || contract.runtimeUse === undefined ? [] : [{
    role: "runtime" as const, contractId: contract.descriptor.contractId, contractVersion: contract.descriptor.version,
    required: contract.runtimeUse.required, actions: contract.runtimeUse.actions,
  }]);
}
