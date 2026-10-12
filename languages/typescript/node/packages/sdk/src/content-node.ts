import { createReadStream } from "node:fs";
import { basename } from "node:path";
import { createContentResource, type SunderContent, type SunderContentOptions } from "./content";

/** A lazy, repeatable local file source. The file is never deleted. Node entry point only. */
export function contentFromFile(filePath:string,options:SunderContentOptions={}):SunderContent{
  if(typeof filePath!=="string"||!filePath.trim())throw new TypeError("Content file path is required.");
  return createContentResource({mediaType:options.mediaType??"application/octet-stream",fileName:options.fileName??basename(filePath),length:null,sha256:null,expiresAtUtc:null,isRepeatable:true},async signal=>{
    signal.throwIfAborted();const stream=createReadStream(filePath,{signal});
    // An abandoned open may be aborted before its async iterator attaches. The
    // iterator still observes stored errors; keep that interval exception-safe.
    stream.on("error",()=>{});
    return {stream,async close(){stream.destroy();if(!stream.closed)await new Promise<void>(resolve=>stream.once("close",resolve));}};
  });
}
