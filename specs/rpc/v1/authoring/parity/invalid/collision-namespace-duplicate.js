// @ts-check

import { rpc } from "@sunder/sdk";

const s = rpc.schema;

export const packageRpc = rpc.router({
  contracts: {
    "first-status": rpc.contract({
      exposure: "public",
      contractId: "example.collision.status",
      serviceId: "status",
      errors: {},
      definitions: {
        CollisionValue: s.object({
          value: s.required(s.boolean()),
        }),
      },
      services: {
        status: {
          run: rpc.unary({
            request: s.ref("CollisionValue"),
            response: s.ref("CollisionValue"),
          }),
        },
      },
    }),
    "second-status": rpc.contract({
      exposure: "public",
      contractId: "example.collision.status",
      serviceId: "status",
      errors: {},
      definitions: {
        CollisionValue: s.object({
          value: s.required(s.boolean()),
        }),
      },
      services: {
        status: {
          run: rpc.unary({
            request: s.ref("CollisionValue"),
            response: s.ref("CollisionValue"),
          }),
        },
      },
    }),
  },
  providers: {},
});
