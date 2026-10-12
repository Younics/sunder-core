export function constructorSource(declared: boolean): string {
  return `import {defineService} from "@sunder/sdk";
export const echo=defineService({run(){return {}; }},{exposure:"public",errors:${declared ? "{constructor:{}}" : "{}"}});`;
}

export function sharedSource(extension: string, reverse: boolean, valid: boolean, typed = false): string {
  const type = '{value:string}';
  const declarations = `
/** @typedef {{call(request:{value:string},context:import("@sunder/sdk").ServiceContext):import("@sunder/sdk").JsonValue}} Wide */
/** @typedef {{call(request:{value:string},context:import("@sunder/sdk").ServiceContext):import("@sunder/sdk").JsonObject}} Narrow */
/*ts type Wide={call(request:{value:string},context:import("@sunder/sdk").ServiceContext):import("@sunder/sdk").JsonValue};*/
/*ts type Narrow={call(request:{value:string},context:import("@sunder/sdk").ServiceContext):import("@sunder/sdk").JsonObject};*/
/** @type {import("@sunder/sdk").ServiceDefinition<Wide>} */
export const aWide=defineService/*<Wide>*/(null,{declarationOnly:true,exposure:"public",serviceId:"main"});
/** @type {import("@sunder/sdk").ServiceDefinition<Narrow>} */
export const zObject=defineService/*<Narrow>*/(null,{declarationOnly:true,exposure:"public",serviceId:"main",constraints:{"call.result":{maxProperties:5}}});`;
  const handlers = `const handlers={
/** @param {{value:string}} request @param {import("@sunder/sdk").ServiceContext} context ${typed ? `@returns {${type}}` : ""} */
call(request/*: {value:string}*/,context/*: import("@sunder/sdk").ServiceContext*/)${typed ? `/*: ${type}*/` : ""}{context.signal.throwIfAborted(); return ${valid ? "{value:request.value.toUpperCase()}" : "42"};}};`;
  const providers = ['export const wide=defineService(handlers,{implements:aWide});', 'export const object=defineService(handlers,{implements:zObject});'];
  if (reverse) providers.reverse();
  return `import {defineService} from "@sunder/sdk";\n${declarations}\n${handlers}\n${providers.join("\n")}`;
}
