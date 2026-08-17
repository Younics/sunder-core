export type JsonPrimitive = null | boolean | number | string;
export type JsonObject = { readonly [key: string]: JsonValue };
export type JsonValue = JsonPrimitive | readonly JsonValue[] | JsonObject;

export interface RpcCallOptions {
  readonly signal?: AbortSignal;
  readonly deadline?: Date;
}

export interface RpcInvoker<TTarget> {
  invoke<TRequest extends JsonValue, TResponse extends JsonValue>(
    target: TTarget,
    serviceId: string,
    methodId: string,
    request: TRequest,
    options?: RpcCallOptions,
  ): Promise<TResponse>;
  subscribe<TRequest extends JsonValue, TEvent extends JsonValue>(
    target: TTarget,
    serviceId: string,
    methodId: string,
    request: TRequest,
    options?: RpcCallOptions,
  ): AsyncIterable<TEvent>;
}
