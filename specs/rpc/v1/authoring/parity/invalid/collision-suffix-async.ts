import { rpc } from "@sunder/sdk";

const s = rpc.schema;

export const packageRpc = rpc.router({
  contracts: {
    run: rpc.contract({
      exposure: "public",
      errors: {},
      definitions: {
        CollisionValue: s.object({
          value: s.required(s.boolean()),
        }),
      },
      services: {
        run: {
          run: rpc.unary({
            request: s.ref("CollisionValue"),
            response: s.ref("CollisionValue"),
          }),
          "run-async": rpc.unary({
            request: s.ref("CollisionValue"),
            response: s.ref("CollisionValue"),
          }),
        },
      },
    }),
  },
  providers: {},
});
