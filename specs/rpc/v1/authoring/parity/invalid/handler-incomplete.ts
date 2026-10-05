import { rpc } from "@sunder/sdk";

const s = rpc.schema;

export const packageRpc = rpc.router({
  contracts: {
    "complete-handler": rpc.contract({
      exposure: "public",
      errors: {},
      definitions: {
        HandlerResult: s.object({
          ok: s.required(s.boolean()),
        }),
      },
      services: {
        "complete-handler": {
          first: rpc.unary({
            request: s.ref("HandlerResult"),
            response: s.ref("HandlerResult"),
          }),
          second: rpc.unary({
            request: s.ref("HandlerResult"),
            response: s.ref("HandlerResult"),
          }),
        },
      },
    }),
  },
  providers: {
    incomplete: rpc.provider({
      contract: "complete-handler",
      handlers: {
        "complete-handler": {
          async first(_request, context) {
            context.signal.throwIfAborted();
            throw new Error("fixture handler");
          },
        },
      },
    }),
  },
});
