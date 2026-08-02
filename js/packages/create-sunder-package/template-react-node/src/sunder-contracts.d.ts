declare module "sunder:contracts" {
  import type { RpcContractIdentity } from "@sunder/sdk";

  export function contractIdentity(configuredPath: string): RpcContractIdentity;
}
