import { sha256Hex } from "./sha256";

export interface SunderContentOptions {
  readonly mediaType?: string;
  /** Display-only name; omitted local byte/stream names default to content.bin. */
  readonly fileName?: string;
}
export interface SunderContentStreamOptions extends SunderContentOptions { readonly length?: number; readonly leaveOpen?: boolean }
export interface ContentMetadata {
  readonly mediaType: string; readonly fileName: string; readonly length: number | null;
  readonly sha256: string | null; readonly expiresAtUtc: string | null; readonly isRepeatable: boolean;
}
/** @internal A generated/runtime adapter owns the read, never the author's resource. */
export interface ContentRead { readonly stream: AsyncIterable<Uint8Array>; close(): Promise<void> }
interface ContentState {
  readonly metadata: ContentMetadata; readonly lifetime: AbortController;
  readonly open: (signal: AbortSignal) => Promise<ContentRead>;
  readonly release: () => Promise<void>; readonly reads: Set<() => Promise<void>>; readonly opens: Set<Promise<unknown>>;
  disposed?: Promise<void>;
}
const states = new WeakMap<SunderContent, ContentState>();
const owned = (content: SunderContent): ContentState => states.get(content) ?? (() => { throw new TypeError("Content must be a SunderContent resource."); })();

/** Portable content. Local repeatability does not change Host single-use acquisition policy. */
export class SunderContent {
  private constructor(state: ContentState) { states.set(this,state); Object.freeze(this); }
  static fromBytes(bytes: Uint8Array, options: SunderContentOptions = {}): SunderContent {
    if (!(bytes instanceof Uint8Array)) throw new TypeError("Content bytes must be a Uint8Array.");
    const copy = bytes.slice();
    return createContentResource({...contentMetadata(options,copy.length),sha256:sha256Hex(copy),isRepeatable:true},async signal=>({
      stream:{async *[Symbol.asyncIterator](){signal.throwIfAborted();yield copy.slice();}},async close(){},
    }));
  }
  static fromStream(source: ReadableStream<Uint8Array> | AsyncIterable<Uint8Array>, options: SunderContentStreamOptions = {}): SunderContent {
    if (!source || typeof source !== "object" || !("getReader" in source) && !(Symbol.asyncIterator in source)) throw new TypeError("Content requires a ReadableStream or AsyncIterable byte body.");
    if(options.leaveOpen!==undefined&&typeof options.leaveOpen!=="boolean")throw new TypeError("leaveOpen must be an explicit Boolean.");
    let used=false; let reader: ReadableStreamDefaultReader<Uint8Array> | undefined; let iterator: AsyncIterator<Uint8Array> | undefined;
    let released=false;
    const releaseSource=async()=>{
      if(released)return;released=true;
      if(options.leaveOpen)return;
      if("getReader" in source){if(reader)await reader.cancel();else if(!source.locked)await source.cancel();}
      else {
        const symbol=(Symbol as SymbolConstructor&{asyncDispose?:symbol}).asyncDispose;
        const dispose=symbol?(source as unknown as Record<symbol,unknown>)[symbol]:undefined;
        if(typeof dispose==="function")await (dispose as ()=>Promise<void>).call(source);
        else {iterator??=source[Symbol.asyncIterator]();await iterator.return?.();}
      }
    };
    return createContentResource(contentMetadata(options,options.length??null),async signal=>{
      signal.throwIfAborted();if(used)throw new Error("Stream content can be opened only once.");used=true;
      if("getReader" in source)reader=source.getReader();else iterator=source[Symbol.asyncIterator]();
      return {stream:{async *[Symbol.asyncIterator](){
        while(true){signal.throwIfAborted();const result=await waitForResource(reader?reader.read():iterator!.next(),signal);signal.throwIfAborted();
          if(result.done)return;if(!(result.value instanceof Uint8Array))throw new TypeError("Content yielded a non-byte chunk.");yield result.value;}
      }},async close(){if(reader){reader.releaseLock();reader=undefined;}}};
    },releaseSource);
  }
  get mediaType():string{return owned(this).metadata.mediaType;}
  get fileName():string{return owned(this).metadata.fileName;}
  get length():number|null{return owned(this).metadata.length;}
  get sha256():string|null{return owned(this).metadata.sha256;}
  get expiresAtUtc():string|null{return owned(this).metadata.expiresAtUtc;}
  get isRepeatable():boolean{return owned(this).metadata.isRepeatable;}
  async openRead(signal?:AbortSignal):Promise<ReadableStream<Uint8Array>>{
    const state=owned(this);const readLifetime=new AbortController();const linked=AbortSignal.any([state.lifetime.signal,readLifetime.signal,...(signal?[signal]:[])]);linked.throwIfAborted();
    let opened:ContentRead|undefined;let closePromise:Promise<void>|undefined;let controller:ReadableStreamDefaultController<Uint8Array>|undefined;
    const close=():Promise<void>=>{
      if(closePromise)return closePromise;
      linked.removeEventListener("abort",abort);
      closePromise=Promise.resolve().then(()=>opened?.close()).then(()=>{state.reads.delete(close);});
      readLifetime.abort(new Error("The content read has closed."));
      void closePromise.catch(()=>{});return closePromise;
    };
    const abort=():void=>{try{controller?.error(linked.reason);}catch{} void close();};
    const opening=Promise.resolve().then(()=>state.open(linked)).then(async read=>{
      opened=read;
      // Abandoned opens are drained even when the backend completes after abort.
      if(linked.aborted){await read.close();throw linked.reason;}
      return read;
    });
    state.opens.add(opening);void opening.finally(()=>state.opens.delete(opening)).catch(()=>{});
    const read=await waitForResource(opening,linked);linked.throwIfAborted();
    const iterator=read.stream[Symbol.asyncIterator]();state.reads.add(close);
    const stream=new ReadableStream<Uint8Array>({
      start(value){controller=value;linked.addEventListener("abort",abort,{once:true});if(linked.aborted)abort();},
      async pull(value){try{linked.throwIfAborted();const next=await waitForResource(iterator.next(),linked);linked.throwIfAborted();
        if(next.done){await close();value.close();}else{if(!(next.value instanceof Uint8Array))throw new TypeError("Content yielded a non-byte chunk.");value.enqueue(next.value);}
      }catch(error){await close().catch(()=>{});try{value.error(error);}catch{}}},
      async cancel(){await close();},
    });
    return stream;
  }
  dispose():Promise<void>{
    const state=owned(this);if(state.disposed)return state.disposed;
    state.disposed=Promise.resolve().then(async()=>{
      // Start source disposal before draining an open/read which it may unblock.
      const release=Promise.resolve().then(state.release);
      const cleanup=Promise.allSettled([release,...[...state.reads].map(close=>close())]);
      await Promise.allSettled([...state.opens]);const results=await cleanup;
      const failure=results.find(result=>result.status==="rejected");if(failure?.status==="rejected")throw failure.reason;
    });void state.disposed.catch(()=>{});state.lifetime.abort(new Error("Content has been disposed."));return state.disposed;
  }
  toJSON():never{throw new TypeError("SunderContent requires generated service marshalling.");}
  /** @internal Used by portable and Node-specific resource adapters. */
  static create(metadata:ContentMetadata,open:(signal:AbortSignal)=>Promise<ContentRead>,release:()=>Promise<void>):SunderContent{
    contentMetadata(metadata,metadata.length);
    if(typeof metadata.isRepeatable!=="boolean"||metadata.sha256!==null&&!/^[a-f0-9]{64}$/u.test(metadata.sha256)||metadata.expiresAtUtc!==null&&typeof metadata.expiresAtUtc!=="string")throw new TypeError("Content metadata is invalid.");
    return new SunderContent({metadata:Object.freeze({...metadata}),open,release,lifetime:new AbortController(),reads:new Set(),opens:new Set()});
  }
}
function contentMetadata(options:SunderContentOptions,length:number|null):ContentMetadata{
  const mediaType=options.mediaType??"application/octet-stream";const fileName=options.fileName??"content.bin";
  if(typeof mediaType!=="string"||!/^[\x20-\x7e]{1,256}$/u.test(mediaType)||!mediaType.trim()||typeof fileName!=="string"||[...fileName].length>1024||/[\ud800-\udfff]/u.test(fileName)||length!==null&&(!Number.isSafeInteger(length)||length<0))throw new TypeError("Content metadata is invalid.");
  return {mediaType,fileName,length,sha256:null,expiresAtUtc:null,isRepeatable:false};
}
/** @internal Does not confer transport authority; received resources supply a lease release. */
export function createContentResource(metadata:ContentMetadata,open:(signal:AbortSignal)=>Promise<ContentRead>,release:()=>Promise<void>=async()=>{}):SunderContent{
  return SunderContent.create(metadata,open,release);
}
export function isSunderContent(value:unknown):value is SunderContent{return !!value&&typeof value==="object"&&states.has(value as SunderContent);}
/** @internal Cancellation bounds the returned read, including a blocked next(). */
export async function waitForResource<T>(operation:Promise<T>,signal:AbortSignal):Promise<T>{
  signal.throwIfAborted();let abort:()=>void=()=>{};
  try{return await Promise.race([operation,new Promise<never>((_,reject)=>{abort=()=>reject(signal.reason);signal.addEventListener("abort",abort,{once:true});if(signal.aborted)abort();})]);}
  finally{signal.removeEventListener("abort",abort);}
}
/** @internal Known byte-body adapter used by generated registration. */
export async function* contentChunks(stream:ReadableStream<Uint8Array>,signal:AbortSignal):AsyncIterable<Uint8Array>{
  const reader=stream.getReader();try{while(true){signal.throwIfAborted();const next=await waitForResource(reader.read(),signal);if(next.done)return;yield next.value;}}
  finally{try{await reader.cancel();}finally{reader.releaseLock();}}
}
