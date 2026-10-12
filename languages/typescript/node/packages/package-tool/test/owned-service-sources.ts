// Ordinary source fixtures: explicit TS annotations and independently checked
// JS JSDoc share bodies. No descriptor/schema/router is an authoring input.
export const numericSource = `import {defineService} from "@sunder/sdk";
/** @typedef {{choice:1|2,ratio:0.5,zero:0}} Payload */
/*ts type Payload={choice:1|2,ratio:0.5,zero:0};*/
export const echo=defineService({
/** @param {Payload} request @param {import("@sunder/sdk").ServiceContext} context @returns {Payload} */
run(request/*:Payload*/,context/*:import("@sunder/sdk").ServiceContext*/)/*:Payload*/{context.signal.throwIfAborted(); return { ...request };}
},{exposure:"package"});`;

export const echoSource = `import {defineService,RpcDomainError} from "@sunder/sdk";
/** @typedef {{kind:"text",text:string}|{kind:"count",number:bigint}} Choice */
/** @typedef {{count:bigint,bytes:Uint8Array,scores:Record<string,number>,flag:boolean,nullable:string|null,note?:string|null,chunks?:Uint8Array[],choice:Choice}} Payload */
/*ts type Choice={kind:"text",text:string}|{kind:"count",number:bigint};*/
/*ts type Payload={count:bigint,bytes:Uint8Array,scores:Record<string,number>,flag:boolean,nullable:string|null,note?:string|null,chunks?:Uint8Array[],choice:Choice};*/
export const echo=defineService({
/** @param {Payload} request @param {import("@sunder/sdk").ServiceContext} context @returns {Payload} */
roundTrip(request/*:Payload*/,context/*:import("@sunder/sdk").ServiceContext*/)/*:Payload*/{context.signal.throwIfAborted(); if(typeof request.count!=="bigint"||!(request.bytes instanceof Uint8Array))throw new Error("Missing value codec"); return request;},
/** @param {Payload} request @returns {Promise<Payload>} */
async asyncTrip(request/*:Payload*/)/*:Promise<Payload>*/{return request;},
/** @param {Payload} request @returns {Payload} */
oversized(request/*:Payload*/)/*:Payload*/{return {...request,bytes:new Uint8Array(800000)};},
/** @param {Payload} request @returns {Payload} */
fail(request/*:Payload*/)/*:Payload*/{throw new RpcDomainError("rejected","Expected domain failure");},
/** @param {Payload} request @returns {Payload} */
fault(request/*:Payload*/)/*:Payload*/{throw new Error("Private implementation failure");},
/** @param {Payload} request @param {import("@sunder/sdk").ServiceContext} context @returns {AsyncIterable<Payload>} */
async *wait(request/*:Payload*/,context/*:import("@sunder/sdk").ServiceContext*/)/*:AsyncIterable<Payload>*/{yield request; await new Promise(done=>context.signal.addEventListener("abort",done,{once:true}));context.signal.throwIfAborted();},
/** @param {Payload} request @param {import("@sunder/sdk").ServiceContext} context @returns {AsyncIterable<Payload>} */
async *watch(request/*:Payload*/,context/*:import("@sunder/sdk").ServiceContext*/)/*:AsyncIterable<Payload>*/{context.signal.throwIfAborted();yield request;}
},{exposure:"package",errors:{rejected:{}},methods:{fail:{errors:["rejected"]}},constraints:{"Payload.scores[]":{format:"integer"}}});`;
