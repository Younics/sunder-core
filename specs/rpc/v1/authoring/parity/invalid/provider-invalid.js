// @ts-check

import { rpc } from "@sunder/sdk";

const s = rpc.schema;

export const packageRpc = rpc.router({
  contracts: {
    private: rpc.contract({
      exposure: "package",
      errors: {},
      definitions: {
        Unit: s.object({}),
        ProviderResult: s.object({
          ok: s.required(s.boolean()),
        }),
      },
      services: {
        private: {
          run: rpc.unary({
            request: s.ref("Unit"),
            response: s.ref("ProviderResult"),
          }),
        },
      },
    }),
  },
  providers: {
    private: rpc.provider({
      contract: "private",
      audience: "public",
      handlers: {
        private: {
          async run(_request, context) {
            context.signal.throwIfAborted();
            throw new Error("fixture handler");
          },
        },
      },
    }),
  },
});
