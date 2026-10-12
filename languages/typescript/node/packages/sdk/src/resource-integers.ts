// Portable raw-token evidence. It is out-of-band and never serialized as data.
const evidence=new WeakMap<object,Map<string,{value:number;valid:boolean}>>();
export function recordResourceIntegerToken(holder:object,name:string,token:string):void{
  let fields=evidence.get(holder);if(!fields)evidence.set(holder,fields=new Map());
  fields.set(name,{value:Number(token),valid:mathematicalSafeInteger(token)});
}
export function exactResourceInteger(holder:object,name:string,value:unknown):value is number{
  if(typeof value!=="number"||!Number.isSafeInteger(value)||value<0)return false;
  const proof=evidence.get(holder)?.get(name);return !proof||proof.valid&&Object.is(proof.value,value);
}
export function copyResourceIntegerTokens(source:object,target:object):void{
  const values=evidence.get(source);if(values)evidence.set(target,new Map(values));
}
function mathematicalSafeInteger(token:string):boolean{
  const value=Number(token);if(!Number.isSafeInteger(value)||value<0)return false;
  const match=/^(-?)(\d+)(?:\.(\d+))?(?:[eE]([+-]?\d+))?$/u.exec(token);if(!match)return false;
  const digits=(match[2]!+(match[3]??"")).replace(/^0+/u,"");if(!digits)return true;
  const scale=Number(match[4]??0)-(match[3]?.length??0);
  return scale>=0||Number.isFinite(scale)&&-scale<=digits.length&&digits.endsWith("0".repeat(-scale));
}
