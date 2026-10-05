import { rpc } from "@sunder/sdk";

const s = rpc.schema;

export const packageRpc = rpc.router({
  contracts: {
    "no-methods": rpc.contract({
      exposure: "public",
      errors: {},
      definitions: {
        Unit: s.object({}),
      },
      services: {
        "no-methods": {},
      },
    }),
  },
  providers: {},
});
