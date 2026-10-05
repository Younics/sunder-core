import { rpc } from "@sunder/sdk";

const s = rpc.schema;

export const packageRpc = rpc.router({
  contracts: {
    "bad-method": rpc.contract({
      exposure: "public",
      errors: {},
      definitions: {
        Unit: s.object({}),
      },
      services: {
        "bad-method": {
          run: rpc.unary({
            request: s.ref("Unit"),
          }),
        },
      },
    }),
  },
  providers: {},
});
