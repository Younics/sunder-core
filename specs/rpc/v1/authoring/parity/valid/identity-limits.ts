import { rpc } from "@sunder/sdk";

const s = rpc.schema;

export const packageRpc = rpc.router({
  contracts: {
    "contract-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa": rpc.contract({
      exposure: "package",
      errors: {},
      definitions: {
        BoundaryValue: s.object({
          ok: s.required(s.boolean()),
        }),
      },
      services: {
        "contract-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa": {
          run: rpc.unary({
            request: s.ref("BoundaryValue"),
            response: s.ref("BoundaryValue"),
          }),
        },
      },
    }),
    "provider-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa": rpc.contract({
      exposure: "package",
      errors: {},
      definitions: {
        BoundaryValue: s.object({
          ok: s.required(s.boolean()),
        }),
      },
      services: {
        "provider-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa": {
          run: rpc.unary({
            request: s.ref("BoundaryValue"),
            response: s.ref("BoundaryValue"),
          }),
        },
      },
    }),
  },
  providers: {
    providerBoundary: rpc.provider({
      contract: "provider-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      handlers: {
        "provider-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa": {
          async run(_request, context) {
            context.signal.throwIfAborted();
            throw new Error("fixture handler");
          },
        },
      },
    }),
  },
});
