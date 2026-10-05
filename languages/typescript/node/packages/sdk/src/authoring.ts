import type { JsonObject, JsonValue, RpcContentReference, RpcInvocationContext, RpcProviderRegistration } from "./types";

declare const schemaType: unique symbol;
export interface RpcSchema<K extends string = string, A extends readonly unknown[] = readonly unknown[]> {
  readonly [schemaType]: { readonly kind: K; readonly args: A };
}
type S = RpcSchema;
type Facets = Readonly<{ title?: string; description?: string }>;
type StringFacets = Facets & Readonly<{ minLength?: number; maxLength?: number }>;
type NumberFacets = Facets & Readonly<{ minimum?: number; maximum?: number; exclusiveMinimum?: number; exclusiveMaximum?: number }>;
type TextNumberFacets = Facets & Readonly<{ minimum?: string; maximum?: string; exclusiveMinimum?: string; exclusiveMaximum?: string }>;
type ObjectFacets = Facets & Readonly<{ minProperties?: number; maxProperties?: number }>;
type Property = RpcSchema<"required" | "optional", readonly [S]>;
type Definitions = Readonly<Record<string, S>>;

export type RpcSchemaValue<T, D extends Definitions = {}, Depth extends readonly unknown[] = []> =
  Depth["length"] extends 32 ? unknown :
  T extends RpcSchema<infer K, infer A> ?
    K extends "ref" ? A[0] extends keyof D ? RpcSchemaValue<D[A[0]], D, [...Depth, 0]> : never :
    K extends "nullable" ? RpcSchemaValue<A[0], D, [...Depth, 0]> | null :
    K extends "required" | "optional" ? RpcSchemaValue<A[0], D, [...Depth, 0]> :
    K extends "object" ? A[0] extends Readonly<Record<string, Property>> ?
      { readonly [P in keyof A[0] as A[0][P] extends RpcSchema<"required"> ? P : never]: RpcSchemaValue<A[0][P], D, [...Depth, 0]> } &
      { readonly [P in keyof A[0] as A[0][P] extends RpcSchema<"optional"> ? P : never]?: RpcSchemaValue<A[0][P], D, [...Depth, 0]> } : never :
    K extends "array" ? ReadonlyArray<RpcSchemaValue<A[0], D, [...Depth, 0]>> :
    K extends "map" ? Readonly<Record<string, RpcSchemaValue<A[0], D, [...Depth, 0]>>> :
    K extends "union" ? A[0] extends readonly S[] ? RpcSchemaValue<A[0][number], D, [...Depth, 0]> : never :
    K extends "taggedUnion" ? A[1] extends readonly S[] ? RpcSchemaValue<A[1][number], D, [...Depth, 0]> : never :
    K extends "literal" ? A[0] :
    K extends "stringEnum" | "integerEnum" ? A[0] extends readonly unknown[] ? A[0][number] : never :
    K extends "integer" | "float32" | "float64" ? number :
    K extends "boolean" ? boolean : K extends "bigInteger" ? bigint :
    K extends "bytes" ? Uint8Array : K extends "contentReference" ? RpcContentReference :
    K extends "json" ? A[0] extends undefined ? JsonValue : NonNullable<A[0]> extends { readonly objectOnly: true } | { readonly minProperties: number } | { readonly maxProperties: number } ? JsonObject : JsonValue : K extends "string" | "uuid" | "date" | "offsetDateTime" | "duration" | "uri" | "decimal" ? string : unknown
  : never;

interface Method<R extends S = S> {
  readonly request: R;
  readonly idempotency?: "none" | "idempotent";
  readonly errors?: readonly string[];
}
export interface Unary<R extends S = S, O extends S = S> extends Method<R> { readonly response: O }
export interface Stream<R extends S = S, O extends S = S> extends Method<R> { readonly event: O }
type Services = Readonly<Record<string, Readonly<Record<string, Unary | Stream>>>>;
export interface RpcContract<D extends Definitions = Definitions, M extends Services = Services> {
  readonly exposure: "package" | "public";
  readonly contractId?: string;
  readonly serviceId?: string;
  /** Retains the fixed V1 JSON Schema dialect annotation for exact descriptor migration. Defaults to false. */
  readonly includeSchemaDialect?: boolean;
  /** Associated App use: public contracts require an explicit opt-in; private contracts default to required use. */
  readonly appUse?: Readonly<{ required?: boolean; actions?: readonly ("discover" | "invoke" | "subscribe")[] }>;
  /** Explicit owning Runtime consumer use. Services/providers grant no Runtime use by default. */
  readonly runtimeUse?: Readonly<{ required?: boolean; actions?: readonly ("discover" | "invoke" | "subscribe")[] }>;
  readonly errors: Readonly<Record<string, Readonly<{ description?: string; details?: S }>>>;
  readonly definitions: D;
  readonly services: M;
}
export type RpcHandlers<C extends RpcContract> = {
  readonly [Service in keyof C["services"]]: {
    readonly [M in keyof C["services"][Service]]: (
      request: RpcSchemaValue<C["services"][Service][M]["request"], C["definitions"]>,
      context: RpcInvocationContext,
    ) => C["services"][Service][M] extends Unary<S, infer O>
      ? RpcSchemaValue<O, C["definitions"]> | Promise<RpcSchemaValue<O, C["definitions"]>>
      : C["services"][Service][M] extends Stream<S, infer E>
        ? AsyncIterable<RpcSchemaValue<E, C["definitions"]>> : never;
  };
};
export interface RpcProvider<K extends string = string, H = unknown> {
  readonly contract: K;
  readonly id?: string;
  readonly audience?: "owner" | "public";
  readonly handlers: H;
}
/** Provider declaration returned by a lock-generated import builder. */
export interface RpcImportedProvider<H = unknown> {
  readonly importKey: string;
  readonly id?: string;
  readonly audience?: "owner" | "public";
  readonly handlers: H;
}

const bindKey = Symbol.for("sunder.rpc.owned-provider-bind.v1");
export interface RpcRouter<C, P> {
  readonly contracts: C;
  readonly providers: P;
  readonly providerRegistrations: readonly RpcProviderRegistration[];
}
function router<const C extends Readonly<Record<string, RpcContract>>, P extends Record<string, keyof C & string>>(value: {
  readonly contracts: C;
  readonly providers: { readonly [K in keyof P]: RpcProvider<P[K], RpcHandlers<NoInfer<C>[P[K]]>> };
}): RpcRouter<C, typeof value.providers>;
function router<const C extends Readonly<Record<string, RpcContract>>, const P extends Readonly<Record<string, RpcProvider | RpcImportedProvider>>>(value: {
  readonly contracts: C;
  readonly providers: P & { readonly [K in keyof P]: P[K] extends RpcImportedProvider ? P[K]
    : P[K] extends RpcProvider<infer Key> ? Key extends keyof C ? RpcProvider<Key, RpcHandlers<NoInfer<C>[Key]>> : never : never };
}): RpcRouter<C, P>;
function router(value: { readonly contracts: Readonly<Record<string, RpcContract>>; readonly providers: Readonly<Record<string, RpcProvider | RpcImportedProvider>> }): RpcRouter<unknown, unknown> {
  const contracts = immutable(value.contracts);
  const providers = immutable(value.providers);
  Object.freeze(value);
  let registrations: readonly RpcProviderRegistration[] | undefined;
  return Object.freeze({
    contracts,
    providers,
    get providerRegistrations(): readonly RpcProviderRegistration[] {
      if (registrations === undefined) throw new Error("RPC router has not been bound by sunder-package. Build the Worker through the package tool.");
      return registrations;
    },
    // Tool-private, deliberately absent from the public DSL. Binding occurs in the
    // analyzed module, before an importing Worker can read registrations.
    [bindKey](factory: (providers: typeof value.providers) => readonly RpcProviderRegistration[]) {
      if (registrations !== undefined) throw new Error("RPC router is already bound.");
      registrations = Object.freeze(factory(providers));
    },
  });
}

function schema<K extends string, const A extends readonly unknown[]>(kind: K, ...args: A): RpcSchema<K, A> {
  const captured = args.map((value) => value !== null && typeof value === "object"
    ? Array.isArray(value) ? Object.freeze([...Object.freeze(value)]) : immutable(value) : value);
  return Object.freeze({ kind, args: Object.freeze(captured) }) as unknown as RpcSchema<K, A>;
}

function immutable<T extends object>(value: T): T {
  Object.freeze(value);
  return Object.freeze({ ...value });
}

/** Closed implementation-first authoring surface; metadata is analyzed, never executed, by tooling. */
export const rpc = Object.freeze({
  router,
  contract: <const D extends Definitions, const M extends Services>(value: RpcContract<D, M>): RpcContract<D, M> => immutable(value),
  provider: <const K extends string, H>(value: RpcProvider<K, H>): RpcProvider<K, H> => immutable(value),
  unary: <const R extends S, const O extends S>(value: Unary<R, O>): Unary<R, O> => immutable(value),
  serverStream: <const R extends S, const O extends S>(value: Stream<R, O>): Stream<R, O> => immutable(value),
  schema: Object.freeze({
    required: <const T extends S>(value: T) => schema("required", value),
    optional: <const T extends S>(value: T) => schema("optional", value),
    nullable: <const T extends S>(value: T) => schema("nullable", value),
    ref: <const N extends string>(name: N) => schema("ref", name),
    object: <const P extends Readonly<Record<string, Property>>>(properties: P, facets?: ObjectFacets) => schema("object", properties, facets),
    map: <const T extends S>(values: T, facets?: ObjectFacets & StringFacets) => schema("map", values, facets),
    array: <const T extends S>(items: T, facets?: Facets & Readonly<{ minItems?: number; maxItems?: number }>) => schema("array", items, facets),
    string: (facets?: StringFacets) => schema("string", facets),
    stringEnum: <const V extends readonly string[]>(values: V, facets?: StringFacets) => schema("stringEnum", values, facets),
    integer: (facets?: NumberFacets) => schema("integer", facets),
    integerEnum: <const V extends readonly number[]>(values: V, facets?: NumberFacets) => schema("integerEnum", values, facets),
    float32: (facets?: NumberFacets) => schema("float32", facets),
    float64: (facets?: NumberFacets) => schema("float64", facets),
    boolean: (facets?: Facets) => schema("boolean", facets),
    uuid: (facets?: StringFacets) => schema("uuid", facets),
    date: (facets?: StringFacets) => schema("date", facets),
    offsetDateTime: (facets?: StringFacets) => schema("offsetDateTime", facets),
    duration: (facets?: StringFacets) => schema("duration", facets),
    uri: (facets?: StringFacets) => schema("uri", facets),
    bigInteger: (facets?: TextNumberFacets & Readonly<{ maxDigits?: number }>) => schema("bigInteger", facets),
    decimal: (facets?: TextNumberFacets & Readonly<{ maxPrecision?: number; maxScale?: number }>) => schema("decimal", facets),
    bytes: (facets?: Facets & Readonly<{ maxDecodedBytes?: number }>) => schema("bytes", facets),
    contentReference: (facets?: Facets) => schema("contentReference", facets),
    json: <const F extends Facets & Readonly<{ objectOnly?: boolean; minProperties?: number; maxProperties?: number }> | undefined = undefined>(facets?: F) => schema("json", facets),
    literal: <const V extends string | number | boolean | null>(value: V) => schema("literal", value),
    union: <const V extends readonly S[]>(variants: V, facets?: Facets) => schema("union", variants, facets),
    taggedUnion: <const N extends string, const V extends readonly S[]>(discriminator: N, variants: V, facets?: Facets) => schema("taggedUnion", discriminator, variants, facets),
  }),
});
