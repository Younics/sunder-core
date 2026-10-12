import type { ApiProvider, ApiTransport } from "../src/api";
import type { RpcCallOptions } from "../src/core";

export const provider:ApiProvider={packageId:"example.provider",packageVersion:"1.0.0",providerId:"example.provider.main",contractId:"example.service",contractVersion:"1.0.0",descriptorSha256:"a".repeat(64),abiSha256:"b".repeat(64),activationId:"11223344556677889900aabbccddeeff",activationEpoch:1,sessionGeneration:1,endpointReference:"endpoint",state:"active",catalogRevision:8};
export type Event={revision:number;sequence:number;kind:string;provider:ApiProvider|null};
export function watchFeed(signal?:AbortSignal){
  const queue:IteratorResult<Event>[]=[];let pending:((value:IteratorResult<Event>)=>void)|undefined;let ended=false;
  const send=(value:IteratorResult<Event>)=>{if(pending){const resolve=pending;pending=undefined;resolve(value);}else queue.push(value);};
  const end=()=>{if(ended)return;ended=true;send({done:true,value:undefined});};signal?.addEventListener("abort",end,{once:true});if(signal?.aborted)end();
  return {emit(value:Event){send({done:false,value});},end,iterator:{
    [Symbol.asyncIterator](){return this;},
    next():Promise<IteratorResult<Event>>{return queue.length?Promise.resolve(queue.shift()!):ended?Promise.resolve({done:true,value:undefined}):new Promise(resolve=>{pending=resolve;});},
    async return():Promise<IteratorResult<Event>>{end();signal?.removeEventListener("abort",end);return {done:true,value:undefined};},
  }};
}
export function liveWatch(_revision:number,_sequence:number,options?:RpcCallOptions):AsyncIterable<Event>{return watchFeed(options?.signal).iterator;}
export function bindingFixture(){
  let providers:ApiProvider[]=[{...provider}],revision=8,sequence=19;
  const watches:Array<ReturnType<typeof watchFeed>&{revision:number;sequence:number}>=[];
  let discovers=0,invokes=0,reports=0;let discoverFailure:unknown,getFailure:unknown,reportFailure:unknown;
  let onWatch:(()=>void)|undefined;
  const transport:ApiTransport={
    async discover(contractId){discovers++;if(discoverFailure)throw discoverFailure;return {revision,sequence,resetRequired:false,providers:providers.filter(item=>item.contractId===contractId).map(item=>({...item}))};},
    async getProvider(endpoint){if(getFailure)throw getFailure;return providers.find(value=>value.endpointReference===endpoint)??null;},
    async reportInvariantViolation(){reports++;if(reportFailure)throw reportFailure;return true;},
    watch(afterRevision,afterSequence,options){const feed=watchFeed(options?.signal);watches.push({...feed,revision:afterRevision,sequence:afterSequence});onWatch?.();return feed.iterator;},
    async invoke(){invokes++;return "ok" as never;},async *subscribe(){yield "ok" as never;},
    async createCallScope(options){const controller=new AbortController();return {...transport,createCallScope:undefined,signal:controller.signal,deadline:options?.deadline??new Date(Date.now()+60000),async close(){controller.abort();}};},
  };
  return {transport,watches,get discovers(){return discovers;},get invokes(){return invokes;},get reports(){return reports;},
    setProviders(value:ApiProvider[]){providers=value;},setDiscoverFailure(error:unknown){discoverFailure=error;},setGetFailure(error:unknown){getFailure=error;},setReportFailure(error:unknown){reportFailure=error;},onWatch(callback:()=>void){onWatch=callback;},
    emit(kind:string,value:ApiProvider|null={...provider}){revision++;sequence++;if(value){value={...value,catalogRevision:revision};providers=providers.filter(item=>item.providerId!==value!.providerId);if(!["removed","deactivated","faulted"].includes(kind)&&value.state==="active")providers.push(value);}
      for(const watch of watches)watch.emit({revision,sequence,kind,provider:value});},
  };
}
export async function until(predicate:()=>boolean):Promise<void>{const deadline=Date.now()+5000;while(!predicate()){if(Date.now()>deadline)throw new Error("Timed out waiting for binding observation.");await new Promise(resolve=>setTimeout(resolve,1));}}
