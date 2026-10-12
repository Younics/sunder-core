import assert from "node:assert/strict";
import test from "node:test";
import { createPackedServiceTools } from "./packed-service-tools";

test("packed service tools prepare once for concurrent consumers and clean up only at scope end", async () => {
  let preparations = 0; let cleanups = 0;
  let release!: () => void;
  const ready = new Promise<void>(resolve => { release = resolve; });
  const fixture = createPackedServiceTools(async () => {
    preparations++;
    await ready;
    return { archives: ["sdk.tgz", "tool.tgz"], creator: "creator.js", dispose: async () => { cleanups++; } };
  });
  const first = fixture.get(); const second = fixture.get();
  assert.equal(first, second); assert.equal(preparations, 1);
  release();
  const [one, two] = await Promise.all([first, second]);
  assert.equal(one, two);
  await assert.rejects(async () => { await fixture.get(); throw new Error("consumer failed"); }, /consumer failed/);
  assert.equal(await fixture.get(), one); assert.equal(cleanups, 0);
  await fixture.dispose();
  assert.equal(cleanups, 1); assert.equal(preparations, 1);
});

test("unselected packed service tools do no work and failed preparation is not retried", async () => {
  let preparations = 0;
  const failure = new Error("packing failed");
  const fixture = createPackedServiceTools(async () => { preparations++; throw failure; });
  await fixture.dispose(); assert.equal(preparations, 0);
  const first = fixture.get(); const second = fixture.get();
  const outcomes = await Promise.allSettled([first, second]);
  assert.equal(preparations, 1);
  for (const outcome of outcomes) { assert.equal(outcome.status, "rejected"); if (outcome.status === "rejected") assert.equal(outcome.reason, failure); }
  await fixture.dispose();
});
