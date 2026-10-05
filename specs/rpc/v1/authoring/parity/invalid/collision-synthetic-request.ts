import { rpc } from "@sunder/sdk";

const s = rpc.schema;

export const packageRpc = rpc.router({
  contracts: {
    order: rpc.contract({
      exposure: "public",
      errors: {},
      definitions: {
        OrderCreateRequest: s.object({
          name: s.required(s.string()),
          count: s.required(s.integer()),
        }),
      },
      services: {
        order: {
          create: rpc.unary({
            request: s.ref("OrderCreateRequest"),
            response: s.ref("OrderCreateRequest"),
          }),
        },
      },
    }),
  },
  providers: {},
});
