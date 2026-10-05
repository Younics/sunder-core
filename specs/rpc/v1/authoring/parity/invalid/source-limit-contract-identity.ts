import { rpc } from "@sunder/sdk";

const s = rpc.schema;

export const packageRpc = rpc.router({
  contracts: {
    "contract-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa": rpc.contract({
      exposure: "package",
      errors: {},
      definitions: {
        BoundaryValue: s.object({
          ok: s.required(s.boolean()),
        }),
      },
      services: {
        "contract-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa": {
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
