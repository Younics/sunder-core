import { authoringBindingModel } from "@sunder/sdk";
import type { ContractUseConfig, LoadedContract } from "./model";
import { applicableUseActions } from "./rpc-owned-uses";

/** Same-package App topology imports owned contracts, never external authority or Worker grants. */
export function ownedAppUses(contracts: readonly LoadedContract[]): readonly ContractUseConfig[] {
  return contracts.flatMap((contract) => {
    if (contract.imported || (contract.exposure === "public" && contract.appUse === undefined)) return [];
    return [{ role: "app" as const, contractId: contract.descriptor.contractId, contractVersion: contract.descriptor.version,
      required: contract.appUse?.required ?? true, actions: contract.appUse?.actions ?? applicableUseActions(contract) }];
  });
}

export function ownedAppClientSource(packageId: string, contracts: readonly LoadedContract[]): string {
  const imports = contracts.map((contract, index) => `import * as $contract${index} from "./${contract.contractKey!}.js";`);
  const bindings = contracts.map((contract, index) => {
    const module = `$contract${index}`;
    const client = `${module}.${authoringBindingModel(contract.descriptor).clientFacadeTypeName}`;
    return `${JSON.stringify(contract.contractKey)}: Object.freeze({
      async resolve(options?: BrowserCallOptions): Promise<${client}> {
        const snapshot = await rpc.discover(${module}.CONTRACT_ID, options);
        if (snapshot.resetRequired) throw new Error("Same-package RPC discovery requires a fresh catalog snapshot.");
        const matches = snapshot.providers.filter((provider) => provider.packageId === ${JSON.stringify(packageId)}
          && provider.state === "active" && provider.contractId === ${module}.CONTRACT_ID
          && provider.contractVersion === ${module}.CONTRACT_VERSION
          && provider.descriptorSha256 === ${module}.CONTRACT_DESCRIPTOR_SHA256
          && provider.abiSha256 === ${module}.CONTRACT_ABI_SHA256);
        if (matches.length === 0) throw new Error("No active same-package RPC provider matches the generated contract.");
        if (matches.length !== 1) throw new Error("Multiple active same-package RPC providers match; automatic selection is ambiguous.");
        return new ${client}(rpc, matches[0]!.providerHandle);
      },
    })`;
  });
  return `${imports.join("\n")}
import type { BrowserCallOptions, SunderBrowserRpc } from "@sunder/sdk/browser";
export function createPackageApi(rpc: SunderBrowserRpc) {
  return Object.freeze({ ${bindings.join(",\n    ")} });
}
`;
}
