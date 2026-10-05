export function constructorSource(declared: boolean): string {
  return `import {rpc} from "@sunder/sdk"; const s=rpc.schema;
export const packageRpc=rpc.router({contracts:{echo:rpc.contract({exposure:"package",errors:${declared ? "{constructor:{}}" : "{}"},definitions:{Unit:s.object({})},
  services:{echo:{run:rpc.unary({request:s.ref("Unit"),response:s.ref("Unit")})}}})},
  providers:{echo:rpc.provider({contract:"echo",handlers:{echo:{run(request){return request;}}}})}});`;
}

export function sharedSource(extension: string, reverse: boolean, valid: boolean, typed = false): string {
  const facets = extension === "ts" ? "const facets: {maxProperties?: number} = {maxProperties:5};" : "/** @type {{maxProperties?: number}} */ const facets = {maxProperties:5};";
  const handlerType = '{main:{call(request:{value:string},context:import("@sunder/sdk").RpcInvocationContext):{value:string}}}';
  const annotation = !typed ? "const handlers" : extension === "ts" ? `const handlers: ${handlerType}` : `/** @type {${handlerType}} */ const handlers`;
  const contracts = ['"a-wide":wide', '"z-object":object']; const providers = ['wide:rpc.provider({contract:"a-wide",handlers})', 'object:rpc.provider({contract:"z-object",handlers})'];
  if (reverse) { contracts.reverse(); providers.reverse(); }
  return `import {rpc} from "@sunder/sdk"; const s=rpc.schema; ${facets}
const input=s.object({value:s.required(s.string())});
const wide=rpc.contract({exposure:"package",errors:{},definitions:{Input:input,Output:s.json()},services:{main:{call:rpc.unary({request:s.ref("Input"),response:s.ref("Output")})}}});
const object=rpc.contract({exposure:"package",errors:{},definitions:{Input:input,Output:s.json(facets)},services:{main:{call:rpc.unary({request:s.ref("Input"),response:s.ref("Output")})}}});
${annotation}={main:{call(request,context){context.signal.throwIfAborted(); request.value.toUpperCase(); return ${valid ? '{value:request.value.toUpperCase()}' : "42"};}}};
export const packageRpc=rpc.router({contracts:{${contracts.join(",")}},providers:{${providers.join(",")}}});`;
}
