import { defineService, type ServiceContext } from "@sunder/sdk";
const packageName = "__SUNDER_PACKAGE_NAME_JSON__";

export const messages = defineService({
  async send(message: string, context: ServiceContext): Promise<string> {
    context.signal.throwIfAborted();
    return `Hello from ${packageName}: ${message}`;
  },
  async *watch(message: string, context: ServiceContext): AsyncIterable<string> {
    context.signal.throwIfAborted();
    yield `Watching ${message}`;
  },
}, {
  methods: { send: { idempotency: "idempotent" } },
  constraints: {
    "send.message": { minLength: 1, maxLength: 256 },
    "send.result": { minLength: 1, maxLength: 1024 },
    "watch.message": { minLength: 1, maxLength: 256 },
    "watch.result": { minLength: 1, maxLength: 1024 },
  },
});
