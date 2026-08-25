import assert from "node:assert/strict";
import test from "node:test";
import { mkdir, rm, writeFile } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { startOutputLockContender } from "./observable-barriers";

test("observable operation barrier catches a seeded missing-lock mutation", async () => {
  const root = resolve(tmpdir(), "sunder-node-lock-barrier-probe", randomUUID());
  await mkdir(root, { recursive: true });
  try {
    const outputPath = resolve(root, "generated");
    const mutated = startOutputLockContender(
      outputPath,
      async () => {
        await new Promise<void>((resolvePromise) => setTimeout(resolvePromise, 25));
        await writeFile(resolve(root, "unlocked-output.txt"), "seeded mutation\n", "utf8");
        return "completed without acquiring the lock";
      },
      () => "seeded contender performed delayed I/O without acquiring the lock",
      100,
    );

    await assert.rejects(mutated.attempting, /without acquiring the lock/u);
    assert.equal(await mutated.completion, "completed without acquiring the lock");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
