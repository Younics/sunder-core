import type { ContractUseConfig, LoadedContract } from "./model";
import { applicableUseActions } from "./rpc-owned-uses";

/** Project only source-normalized App intent. Public visibility/provider bindings
 * alone grant nothing; ordinary concrete defaults are resolved by service-source. */
export function ownedAppUses(contracts: readonly LoadedContract[]): readonly ContractUseConfig[] {
  return contracts.flatMap((contract) => {
    if (contract.imported || (contract.exposure === "public" && contract.appUse === undefined)) return [];
    return [{ role: "app" as const, contractId: contract.descriptor.contractId, contractVersion: contract.descriptor.version,
      required: contract.appUse?.required ?? true, actions: contract.appUse?.actions ?? applicableUseActions(contract) }];
  });
}
