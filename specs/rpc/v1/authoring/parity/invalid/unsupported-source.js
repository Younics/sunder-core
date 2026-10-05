// @ts-check

import { rpc } from "@sunder/sdk";

const s = rpc.schema;
const makeSchema = () => s.string();

export const packageRpc = rpc.router({
  contracts: {
    generic: rpc.contract({
      exposure: "public",
      errors: {},
      definitions: {
        DynamicValue: makeSchema(),
      },
      services: {
        generic: {
          echo: rpc.unary({
            request: s.ref("DynamicValue"),
            response: s.ref("DynamicValue"),
          }),
        },
      },
    }),
  },
  providers: {},
});
