// @ts-check

import { rpc } from "@sunder/sdk";

const s = rpc.schema;

export const packageRpc = rpc.router({
  contracts: {
    x: rpc.contract({
      exposure: "package",
      errors: {},
      definitions: {
        BoundaryValue: s.object({
          ok: s.required(s.boolean()),
        }),
      },
      services: {
        x: {
          run: rpc.unary({
            request: s.ref("BoundaryValue"),
            response: s.ref("BoundaryValue"),
          }),
        },
      },
    }),
  },
  providers: {},
});
