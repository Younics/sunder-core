import { rpc } from "@sunder/sdk";

const s = rpc.schema;

export const packageRpc = rpc.router({
  contracts: {
    status: rpc.contract({
      exposure: "public",
      errors: {},
      definitions: {
        CollisionValue: s.object({
          value: s.required(s.boolean()),
        }),
      },
      services: {
        status: {
          read: rpc.unary({
            request: s.ref("CollisionValue"),
            response: s.ref("CollisionValue"),
          }),
        },
      },
    }),
    "status-service": rpc.contract({
      exposure: "public",
      errors: {},
      definitions: {
        CollisionValue: s.object({
          value: s.required(s.boolean()),
        }),
      },
      services: {
        "status-service": {
          read: rpc.unary({
            request: s.ref("CollisionValue"),
            response: s.ref("CollisionValue"),
          }),
        },
      },
    }),
  },
  providers: {},
});
