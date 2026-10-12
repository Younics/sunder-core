// @ts-check
import { defineService } from "@sunder/sdk";
const packageName = "__SUNDER_PACKAGE_NAME_JSON__";

export const messages = defineService({
  /** @param {string} message @param {import("@sunder/sdk").ServiceContext} context @returns {Promise<string>} */
  async send(message, context) {
    context.signal.throwIfAborted();
    return `Hello from ${packageName}: ${message}`;
  },
  /** @param {string} message @param {import("@sunder/sdk").ServiceContext} context @returns {AsyncIterable<string>} */
  async *watch(message, context) {
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
