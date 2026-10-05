// @ts-check

import { rpc } from "@sunder/sdk";

const s = rpc.schema;

export const packageRpc = rpc.router({
  contracts: {
    reserved: rpc.contract({
      exposure: "public",
      errors: {},
      definitions: {
        ReservedRunRequest: s.object({
          await: s.required(s.string()),
          _await: s.required(s.string()),
        }),
        ReservedResult: s.object({
          ok: s.required(s.boolean()),
        }),
      },
      services: {
        reserved: {
          run: rpc.unary({
            request: s.ref("ReservedRunRequest"),
            response: s.ref("ReservedResult"),
          }),
        },
      },
    }),
  },
  providers: {},
});
