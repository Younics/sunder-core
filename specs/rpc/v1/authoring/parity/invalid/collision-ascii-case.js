// @ts-check

import { rpc } from "@sunder/sdk";

const s = rpc.schema;

export const packageRpc = rpc.router({
  contracts: {
    case: rpc.contract({
      exposure: "public",
      errors: {},
      definitions: {
        Status: s.object({
          value: s.required(s.boolean()),
        }),
        STATUS: s.object({
          value: s.required(s.boolean()),
        }),
      },
      services: {
        case: {
          convert: rpc.unary({
            request: s.ref("Status"),
            response: s.ref("STATUS"),
          }),
        },
      },
    }),
  },
  providers: {},
});
