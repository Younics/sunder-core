import assert from "node:assert/strict";
import test from "node:test";
import {defineService,assertServiceOptions,createServiceContract,type ServiceUse} from "../src/services";
import {createServiceToken} from "../src/service-reference";

test("typed Runtime semantic options retain canonical token identity and immutable policy snapshots",()=>{
  const target={contractId:"example.target",contractVersion:"1.0.0",descriptorSha256:"a".repeat(64),abiSha256:"b".repeat(64),serviceId:"target"};
  const service=createServiceToken(target,()=>({run:async()=>true}));const actions:Array<NonNullable<ServiceUse["actions"]>[number]>=["discover"];
  const provider=defineService({run(){return true;}},{implements:createServiceContract<{run():boolean}>("provider"),runtimeUses:[{service,actions}]});
  actions.push("invoke");assert.equal(provider.options.runtimeUses![0]!.service,service);assert.deepEqual(provider.options.runtimeUses![0]!.actions,["discover"]);
  assertServiceOptions(provider,{runtimeUses:[{service:target,actions:["discover"]}]});
  for(const expected of [{runtimeUses:[{service:target,actions:["discover","invoke"]}]},{runtimeUses:[{service:{...target,descriptorSha256:"c".repeat(64)},actions:["discover"]}]},{}])
    assert.throws(()=>assertServiceOptions(provider,expected),/metadata differs/);
});
test("typed Runtime option capture rejects counterfeit tokens and accessor/unknown policy fields",()=>{
  const token=createServiceToken({contractId:"example.target",contractVersion:"1.0.0",descriptorSha256:"a".repeat(64),abiSha256:"b".repeat(64),serviceId:"target"},()=>({}));
  for(const runtimeUses of [[],[{service:{contractId:"example.target",serviceId:"target"}}],[{get service(){return token;}}],[{service:token,extra:true}]])
    assert.throws(()=>defineService({run(){}},{runtimeUses:runtimeUses as never}));
});
