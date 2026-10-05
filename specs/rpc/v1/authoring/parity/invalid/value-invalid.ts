import { rpc } from "@sunder/sdk";

const s = rpc.schema;

export const packageRpc = rpc.router({
  contracts: {
    "bad-value": rpc.contract({
      exposure: "public",
      errors: {},
      definitions: {
        BadValue: s.object({
          name: s.string(),
        }),
      },
      services: {
        "bad-value": {
          check: rpc.unary({
            request: s.ref("BadValue"),
            response: s.ref("BadValue"),
          }),
        },
      },
    }),
  },
  providers: {},
});
