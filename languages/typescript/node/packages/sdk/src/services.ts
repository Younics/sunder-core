import type { RpcInvocationContext } from "./types";
import { serviceTokenDefinition, type ServiceToken } from "./service-reference";

/** Invocation controls are supplied by Sunder, never serialized as service arguments. */
export type ServiceContext = RpcInvocationContext;
export type ServiceMethods = Readonly<Record<string, (...args: never[]) => unknown>>;
type ServiceShape<M> = { readonly [K in keyof M]: (...args: never[]) => unknown };
export interface ServiceUse { readonly required?: boolean; readonly actions?: readonly ("discover" | "invoke" | "subscribe")[] }
/** Explicit Runtime intent for one canonical imported service on a bound provider. */
export interface ServiceRuntimeUse<TService=unknown> extends ServiceUse { readonly service:ServiceToken<TService> }
export interface ServiceConstraints {
  readonly title?: string; readonly description?: string;
  readonly format?: "integer" | "float32" | "float64" | "uuid" | "date" | "date-time" | "duration" | "uri" | "decimal";
  readonly minLength?: number; readonly maxLength?: number; readonly minimum?: number | string; readonly maximum?: number | string;
  readonly exclusiveMinimum?: number | string; readonly exclusiveMaximum?: number | string;
  readonly minItems?: number; readonly maxItems?: number; readonly minProperties?: number; readonly maxProperties?: number;
  readonly maxDecodedBytes?: number; readonly maxDigits?: number; readonly maxPrecision?: number; readonly maxScale?: number;
}
export interface ServiceOptions {
  readonly exposure?: "package" | "public";
  readonly contractId?: string; readonly serviceId?: string; readonly providerId?: string; readonly audience?: "owner" | "public";
  readonly includeSchemaDialect?: boolean;
  readonly appUse?: ServiceUse; readonly runtimeUse?: ServiceUse;
  readonly runtimeUses?: readonly ServiceRuntimeUse[];
  /** Facets on named source types, or method.parameter / method.result paths. */
  readonly constraints?: Readonly<Record<string, ServiceConstraints>>;
  readonly errors?: Readonly<Record<string, ServiceError<unknown>>>;
  readonly methods?: Readonly<Record<string, { readonly name?: string; readonly idempotency?: "none" | "idempotent"; readonly errors?: readonly string[]; readonly timeoutSeconds?: number; readonly completeOnResponseContentRetirement?: boolean }>>;
}
declare const errorDetails: unique symbol;
export interface ServiceError<T = void> { readonly description?: string; readonly [errorDetails]?: T }
/** Error detail types are source types; this supplies semantic error intent only. */
export function defineServiceError<T = void>(options: { readonly description?: string } = {}): ServiceError<T> {
  return Object.freeze({ ...options });
}
declare const declaration: unique symbol;
export interface ServiceContract<M extends ServiceShape<M> = ServiceMethods> { readonly [declaration]: M; readonly key: string }
export interface ServiceDefinition<M extends ServiceShape<M> = ServiceMethods> { readonly methods: M | null; readonly options: ServiceOptions & { readonly declarationOnly?: boolean; readonly implements?: ServiceContract<M> | ServiceDefinition<M> } }

export function defineService<M extends ServiceShape<M>>(methods: NoInfer<M>, options: ServiceOptions & { readonly implements: ServiceContract<M> | ServiceDefinition<M> }): ServiceDefinition<M>;
export function defineService<M extends ServiceShape<M>>(methods: null, options: ServiceOptions & { readonly declarationOnly: true }): ServiceDefinition<M>;
export function defineService<const M extends ServiceShape<M>>(methods: M, options?: ServiceOptions): ServiceDefinition<M>;
export function defineService<M extends ServiceShape<M>>(methods: M | null, options: ServiceOptions & { readonly declarationOnly?: boolean; readonly implements?: ServiceContract<M> | ServiceDefinition<M> } = {}): ServiceDefinition<M> {
  if (methods === null ? options.declarationOnly !== true : typeof methods !== "object" || Array.isArray(methods)) throw new TypeError("A service requires an ordinary method record or an explicit declaration-only type.");
  if (methods) {
    for (const descriptor of Object.values(Object.getOwnPropertyDescriptors(methods)))
      if (!("value" in descriptor) || typeof descriptor.value !== "function") throw new TypeError("Service members must be ordinary functions.");
    if (Reflect.ownKeys(methods).length !== Object.keys(methods).length) throw new TypeError("Service symbols and nonenumerable methods are unsupported.");
    Object.freeze(methods);
  }
  const captured = Object.fromEntries(Object.entries(options).map(([key, value]) => [key, key === "implements" ? value : key==="runtimeUses" ? captureRuntimeUses(value) : immutableData(value)]));
  return Object.freeze({ methods, options: Object.freeze(captured) });
}
function captureRuntimeUses(value:unknown):readonly ServiceRuntimeUse[]{
  if(!Array.isArray(value)||!value.length||value.length>128)throw new TypeError("runtimeUses requires 1..128 explicit service policies.");
  return Object.freeze(value.map(item=>{
    if(!item||typeof item!=="object"||Array.isArray(item))throw new TypeError("Runtime service use must be a closed record.");
    const properties=Object.getOwnPropertyDescriptors(item);
    if(Reflect.ownKeys(properties).some(key=>typeof key!=="string"||!["service","required","actions"].includes(key))||Object.values(properties).some(property=>!("value" in property))||!properties.service)
      throw new TypeError("Runtime service use must contain a canonical service token and optional policy data.");
    const service=properties.service.value as ServiceToken<unknown>;serviceTokenDefinition(service);
    return Object.freeze({service,...(properties.required?{required:immutableData(properties.required.value) as boolean}:{}),...(properties.actions?{actions:immutableData(properties.actions.value) as ServiceUse["actions"]}:{})});
  }));
}
function immutableData(value: unknown): unknown {
  if (value === null || typeof value !== "object") return value;
  if (Array.isArray(value)) return Object.freeze(value.map(immutableData));
  const properties = Object.getOwnPropertyDescriptors(value);
  if (Object.values(properties).some(property => !("value" in property))) throw new TypeError("Service semantic options cannot contain accessors.");
  Object.freeze(value);
  return Object.freeze(Object.fromEntries(Object.entries(properties).map(([key, property]) => [key, immutableData(property.value)])));
}
/** Generated bootstrap compares erased runtime declarations with the checked semantic snapshot. */
export function assertServiceOptions(definition: { readonly options: ServiceOptions & { readonly implements?: unknown } }, expected: unknown): void {
  const canonical = (value: unknown): string => JSON.stringify(value, (_key, child: unknown) => child && typeof child === "object" && !Array.isArray(child)
    ? Object.fromEntries(Object.entries(child).sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0)) : child);
  const { implements: _implementation, ...actual } = definition.options;
  const normalized={...actual,...(actual.runtimeUses===undefined?{}:{runtimeUses:actual.runtimeUses.map(({service,...policy})=>({service:serviceTokenDefinition(service).target,...policy}))})};
  if (canonical(normalized) !== canonical(expected)) throw new Error("Service runtime semantic metadata differs from the checked source.");
}

/** Tool-generated declaration identity. It does not grant authority or register a provider. */
export function createServiceContract<M extends ServiceShape<M>>(key: string): ServiceContract<M> {
  return Object.freeze({ key }) as ServiceContract<M>;
}
