import { rpc, type RpcHandlers } from "@sunder/sdk";

const s = rpc.schema;
const packageName = "__SUNDER_PACKAGE_NAME_JSON__";

const messages = rpc.contract({
  exposure: "package",
  errors: {},
  definitions: {
    Request: s.object({ message: s.required(s.string({ minLength: 1, maxLength: 256 })) }),
    Response: s.object({ message: s.required(s.string({ minLength: 1, maxLength: 1024 })) }),
  },
  services: {
    messages: {
      send: rpc.unary({ request: s.ref("Request"), response: s.ref("Response"), idempotency: "idempotent" }),
      watch: rpc.serverStream({ request: s.ref("Request"), event: s.ref("Response") }),
    },
  },
});

const handlers: RpcHandlers<typeof messages> = {
  messages: {
    async send(request, context) {
      context.signal.throwIfAborted();
      return { message: `Hello from ${packageName}: ${request.message}` };
    },
    async *watch(request, context) {
      context.signal.throwIfAborted();
      yield { message: `Watching ${request.message}` };
    },
  },
};

export const packageRpc = rpc.router({
  contracts: { messages },
  providers: {
    messages: rpc.provider({
      contract: "messages",
      id: "sunderpackageidscaffoldtoken.provider",
      handlers,
    }),
  },
});
