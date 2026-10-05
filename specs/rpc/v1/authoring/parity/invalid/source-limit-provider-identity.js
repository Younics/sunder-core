// @ts-check

import { rpc } from "@sunder/sdk";

const s = rpc.schema;

export const packageRpc = rpc.router({
  contracts: {
    "provider-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa": rpc.contract({
      exposure: "package",
      errors: {},
      definitions: {
        BoundaryValue: s.object({
          ok: s.required(s.boolean()),
        }),
      },
      services: {
        "provider-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa": {
          run: rpc.unary({
            request: s.ref("BoundaryValue"),
            response: s.ref("BoundaryValue"),
          }),
        },
      },
    }),
  },
  providers: {
    providerOverLimit: rpc.provider({
      contract: "provider-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      handlers: {
        "provider-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa": {
          async run(_request, context) {
            context.signal.throwIfAborted();
            throw new Error("fixture handler");
          },
        },
      },
    }),
  },
});
