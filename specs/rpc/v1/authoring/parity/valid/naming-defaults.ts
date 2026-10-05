import { rpc } from "@sunder/sdk";

const s = rpc.schema;

export const packageRpc = rpc.router({
  contracts: {
    "url-lookup": rpc.contract({
      exposure: "package",
      errors: {},
      definitions: {
        NamingRequest: s.object({
          urlValue: s.required(s.string()),
          oAuthToken: s.required(s.string()),
        }),
        NamingResult: s.object({
          urlValue: s.required(s.string()),
        }),
      },
      services: {
        "url-lookup": {
          "get-url": rpc.unary({
            request: s.ref("NamingRequest"),
            response: s.ref("NamingResult"),
          }),
        },
      },
    }),
    "o-auth2": rpc.contract({
      exposure: "package",
      errors: {},
      definitions: {
        Unit: s.object({}),
        OAuth2Result: s.object({
          ok: s.required(s.boolean()),
        }),
      },
      services: {
        "o-auth2": {
          run: rpc.unary({
            request: s.ref("Unit"),
            response: s.ref("OAuth2Result"),
          }),
        },
      },
    }),
  },
  providers: {
    urlLookup: rpc.provider({
      contract: "url-lookup",
      handlers: {
        "url-lookup": {
          async "get-url"(_request, context) {
            context.signal.throwIfAborted();
            throw new Error("fixture handler");
          },
        },
      },
    }),
    oAuth2: rpc.provider({
      contract: "o-auth2",
      handlers: {
        "o-auth2": {
          async run(_request, context) {
            context.signal.throwIfAborted();
            throw new Error("fixture handler");
          },
        },
      },
    }),
  },
});
