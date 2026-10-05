// @ts-check
import { rpc } from "@sunder/sdk";

const s = rpc.schema;

export const packageRpc = rpc.router({
  contracts: {
    parity: rpc.contract({
      exposure: "package",
      errors: {
        "value-invalid": {
          description: "The supplied value is invalid.",
          details: s.ref("ErrorDetails"),
        },
        conflict: {
          description: "The operation conflicts with current state.",
        },
      },
      definitions: {
        BoundedText: s.string({
          minLength: 1,
          maxLength: 128,
          title: "Bounded text",
          description: "Text with explicit scalar-length bounds.",
        }),
        UnboundedText: s.string(),
        Identifier: s.uuid(),
        DateValue: s.date(),
        Moment: s.offsetDateTime(),
        TimeSpanValue: s.duration(),
        Link: s.uri(),
        BigNumber: s.bigInteger({
          maxDigits: 40,
          minimum: "-9999999999999999999999999999999999999999",
          exclusiveMaximum: "9999999999999999999999999999999999999999",
        }),
        DecimalNumber: s.decimal({
          maxPrecision: 20,
          maxScale: 8,
          exclusiveMinimum: "-1000000000.125",
          maximum: "1000000000.125",
        }),
        Blob: s.bytes({ maxDecodedBytes: 4096 }),
        UnboundedBytes: s.bytes(),
        Content: s.contentReference(),
        AnyJson: s.json(),
        JsonObject: s.json({ maxProperties: 16 }),
        SafeCount: s.integer({ minimum: 0, maximum: 9007199254740991 }),
        Float32Value: s.float32({ exclusiveMinimum: -1000.25, maximum: 1000.25 }),
        Float64Value: s.float64({
          minimum: -100000000000000000000,
          exclusiveMaximum: 100000000000000000000,
        }),
        ExactRatio: s.literal(0.5),
        ScalarId: s.string({ minLength: 1, maxLength: 64 }),
        StringList: s.array(s.ref("BoundedText"), {
          minItems: 0,
          maxItems: 32,
          title: "String list",
          description: "A bounded transparent array.",
        }),
        UnboundedList: s.array(s.boolean()),
        IntegerMap: s.map(s.integer(), {
          minLength: 1,
          maxLength: 64,
          minProperties: 0,
          maxProperties: 16,
          title: "Integer map",
          description: "A bounded transparent string-keyed map.",
        }),
        UnboundedMap: s.map(s.string()),
        StringStatus: s.stringEnum(["zeta", "alpha", "middle"], {
          title: "String status",
          description: "Declaration order is wire-significant.",
        }),
        NumericPriority: s.integerEnum([10, -1, 2]),
        Node: s.object({
          value: s.required(s.ref("BoundedText")),
          next: s.optional(s.ref("Node")),
          children: s.required(s.array(s.ref("Node"))),
        }),
        TextChoice: s.object({
          kind: s.required(s.literal("text")),
          text: s.required(s.ref("BoundedText")),
        }),
        CountChoice: s.object({
          kind: s.required(s.literal("count")),
          count: s.required(s.ref("SafeCount")),
        }),
        Choice: s.union([s.ref("TextChoice"), s.ref("CountChoice")], {
          title: "Choice",
          description: "A source-level untagged union.",
        }),
        ProgressEvent: s.object({
          kind: s.required(s.literal("progress")),
          sequence: s.required(s.ref("SafeCount")),
          ratio: s.required(s.ref("ExactRatio")),
        }),
        FinishedEvent: s.object({
          kind: s.required(s.literal("finished")),
          result: s.required(s.ref("Result")),
        }),
        Event: s.taggedUnion("kind", [s.ref("ProgressEvent"), s.ref("FinishedEvent")], {
          title: "Parity event",
          description: "A tagged stream event.",
        }),
        ErrorDetails: s.object({
          field: s.required(s.ref("BoundedText")),
          reason: s.required(s.ref("BoundedText")),
        }),
        PingResult: s.object({
          ok: s.required(s.boolean()),
        }),
        Result: s.object({
          accepted: s.required(s.boolean()),
          note: s.optional(s.nullable(s.string())),
        }),
        WatchRequest: s.object({
          cursor: s.optional(s.ref("BoundedText")),
        }),
        Payload: s.object({
          requiredText: s.required(s.string({ minLength: 1, maxLength: 128 })),
          optionalText: s.optional(s.string({ minLength: 1, maxLength: 128 })),
          requiredNullableText: s.required(s.nullable(s.string({ minLength: 1, maxLength: 128 }))),
          optionalNullableText: s.optional(s.nullable(s.string({ minLength: 1, maxLength: 128 }))),
          unboundedText: s.required(s.ref("UnboundedText")),
          flag: s.required(s.boolean()),
          count: s.required(s.ref("SafeCount")),
          float32: s.required(s.ref("Float32Value")),
          float64: s.required(s.ref("Float64Value")),
          exactRatio: s.required(s.ref("ExactRatio")),
          id: s.required(s.ref("Identifier")),
          date: s.required(s.ref("DateValue")),
          timestamp: s.required(s.ref("Moment")),
          duration: s.required(s.ref("TimeSpanValue")),
          uri: s.required(s.ref("Link")),
          bigInteger: s.required(s.ref("BigNumber")),
          decimal: s.required(s.ref("DecimalNumber")),
          bytes: s.required(s.ref("Blob")),
          unboundedBytes: s.required(s.ref("UnboundedBytes")),
          content: s.required(s.ref("Content")),
          json: s.required(s.ref("AnyJson")),
          jsonObject: s.required(s.ref("JsonObject")),
          tags: s.required(s.ref("StringList")),
          unboundedList: s.required(s.ref("UnboundedList")),
          scores: s.required(s.ref("IntegerMap")),
          unboundedMap: s.required(s.ref("UnboundedMap")),
          status: s.required(s.ref("StringStatus")),
          priority: s.required(s.ref("NumericPriority")),
          scalarId: s.required(s.ref("ScalarId")),
          node: s.required(s.ref("Node")),
          event: s.required(s.ref("Event")),
          choice: s.required(s.ref("Choice")),
        }, {
          minProperties: 29,
          maxProperties: 31,
          title: "Parity payload",
          description: "Exercises the complete V1 value profile.",
        }),
        Unit: s.object({}),
        ParityCombineRequest: s.object({
          left: s.required(s.string({ minLength: 1, maxLength: 32 })),
          right: s.required(s.integer({ minimum: 0, maximum: 100 })),
        }),
      },
      services: {
        parity: {
          ping: rpc.unary({
            request: s.ref("Unit"),
            response: s.ref("PingResult"),
          }),
          "round-trip": rpc.unary({
            request: s.ref("Payload"),
            response: s.ref("Payload"),
            idempotency: "idempotent",
            errors: ["value-invalid", "conflict"],
          }),
          combine: rpc.unary({
            request: s.ref("ParityCombineRequest"),
            response: s.ref("Result"),
          }),
          watch: rpc.serverStream({
            request: s.ref("WatchRequest"),
            event: s.ref("Event"),
            errors: ["value-invalid"],
          }),
        },
      },
    }),
  },
  providers: {
    parity: rpc.provider({
      contract: "parity",
      handlers: {
        parity: {
          async ping(_request, context) {
            context.signal.throwIfAborted();
            throw new Error("fixture handler");
          },
          async "round-trip"(_request, context) {
            context.signal.throwIfAborted();
            throw new Error("fixture handler");
          },
          async combine(_request, context) {
            context.signal.throwIfAborted();
            throw new Error("fixture handler");
          },
          async *watch(_request, context) {
            context.signal.throwIfAborted();
            throw new Error("fixture handler");
          },
        },
      },
    }),
  },
});
