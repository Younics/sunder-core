// @ts-check

import { rpc } from "@sunder/sdk";

const s = rpc.schema;

export const packageRpc = rpc.router({
  contracts: {
    renamed: rpc.contract({
      exposure: "public",
      errors: {},
      definitions: {
        BaselineValue: s.object({
          value: s.required(s.boolean()),
        }),
      },
      services: {
        renamed: {
          run: rpc.unary({
            request: s.ref("BaselineValue"),
            response: s.ref("BaselineValue"),
          }),
        },
      },
    }),
  },
  providers: {},
});
