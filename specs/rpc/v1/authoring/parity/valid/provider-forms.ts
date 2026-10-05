import { rpc } from "@sunder/sdk";

const s = rpc.schema;

export const packageRpc = rpc.router({
  contracts: {
    audit: rpc.contract({
      exposure: "public",
      errors: {},
      definitions: {
        AuditRequest: s.object({
          cursor: s.optional(s.string()),
        }),
        AuditEvent: s.object({
          sequence: s.required(s.integer()),
        }),
      },
      services: {
        audit: {
          observe: rpc.serverStream({
            request: s.ref("AuditRequest"),
            event: s.ref("AuditEvent"),
          }),
        },
      },
    }),
    clock: rpc.contract({
      exposure: "public",
      errors: {},
      definitions: {
        Unit: s.object({}),
        ClockResult: s.object({
          timestamp: s.required(s.offsetDateTime()),
        }),
      },
      services: {
        clock: {
          now: rpc.unary({
            request: s.ref("Unit"),
            response: s.ref("ClockResult"),
            idempotency: "idempotent",
          }),
        },
      },
    }),
  },
  providers: {
    clockPrimary: rpc.provider({
      contract: "clock",
      id: "example.authoring.extensions.clock.primary",
      handlers: {
        clock: {
          async now(_request, context) {
            context.signal.throwIfAborted();
            throw new Error("fixture handler");
          },
        },
      },
    }),
    clockOwner: rpc.provider({
      contract: "clock",
      id: "example.authoring.extensions.clock.owner",
      audience: "owner",
      handlers: {
        clock: {
          async now(_request, context) {
            context.signal.throwIfAborted();
            throw new Error("fixture handler");
          },
        },
      },
    }),
  },
});
