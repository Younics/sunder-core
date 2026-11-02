import assert from "node:assert/strict";
import { mkdtemp, readFile, realpath, rm, stat } from "node:fs/promises";
import * as os from "node:os";
import { resolve } from "node:path";
import test, { mock } from "node:test";
import * as identityModule from "../src/process-start-identity";
import { createProcessStartIdentityProbe, PROCESS_IDENTITY_BOOTSTRAP_MILLISECONDS } from "../src/process-start-identity";

const ticks = "639318872000000001";

function fixture(execute: (file: string, args: string[], timeout: number) => Promise<{ stdout: string; stderr: string }>, exists: () => boolean | undefined = () => true) {
  return createProcessStartIdentityProbe({
    platform: "win32", pid: 1234, environment: { SystemRoot: "C:\\Windows" },
    execute, exists, now: () => 6_001, read: async () => { throw new Error("Unexpected read"); },
  });
}

test("Windows bootstrap shares a cold probe, preserves exact .NET ticks, and caches only success", async () => {
  let complete!: (value: { stdout: string; stderr: string }) => void;
  let calls = 0;
  const probe = fixture(async (file, args, timeout) => {
    calls++;
    assert.equal(file, "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe");
    assert.equal(timeout, PROCESS_IDENTITY_BOOTSTRAP_MILLISECONDS);
    assert.ok(timeout > 5_000);
    assert.ok(args.includes("-NoProfile") && args.includes("-NonInteractive"));
    assert.match(args.at(-1)!, /GetProcessById\(1234\).*StartTime.ToUniversalTime\(\).Ticks/u);
    return new Promise((done) => { complete = done; });
  });
  const first = probe.currentIdentity();
  assert.equal(probe.currentIdentity(), first);
  complete({ stdout: `\r\n${ticks}\r\n`, stderr: "" });
  assert.equal(await first, `windows:${ticks}`);
  assert.equal(await probe.currentIdentity(), `windows:${ticks}`);
  assert.equal(calls, 1);
});

test("Windows probe failures retain bounded CLI-visible diagnostics and permit later initialization", async () => {
  for (const failure of [
    Object.assign(new Error("timed out"), { killed: true, signal: "SIGTERM", code: null }),
    Object.assign(new Error("spawn failed"), { code: "ENOENT" }),
    Object.assign(new Error("PowerShell failed"), { code: 1, stderr: "access denied\n" + "x".repeat(10_000) }),
  ]) {
    let calls = 0;
    const probe = fixture(async () => {
      if (++calls === 1) throw failure;
      return { stdout: ticks, stderr: "" };
    });
    await assert.rejects(probe.currentIdentity(), (error: Error) => {
      assert.match(error.message, /current process start identity.*powershell.exe.*elapsed=.*budget=30000ms/u);
      assert.ok(error.message.includes(failure.message));
      assert.ok(error.message.length < 2_000);
      assert.ok(!error.message.includes("\n"));
      if (failure.message === "timed out") assert.match(error.message, /signal=SIGTERM; killed=true/u);
      if (failure.message === "spawn failed") assert.match(error.message, /code=ENOENT/u);
      return true;
    });
    assert.equal(await probe.currentIdentity(), `windows:${ticks}`);
    assert.equal(calls, 2);
  }
});

test("invalid Windows output and inaccessible owners are unknown, never dead-process evidence", async () => {
  for (const output of ["", "0", "-1", "3155378976000000000", `${ticks}\nother`, "not ticks"]) {
    const probe = fixture(async () => ({ stdout: output, stderr: "" }));
    const result = await probe.ownerIdentity(4321, 73);
    assert.equal(result.state, "unknown");
    await assert.rejects(probe.currentIdentity(), /Invalid Windows process-start output/u);
  }
  for (const exists of [true, undefined, false]) {
    const probe = fixture(async (_file, _args, timeout) => {
      assert.equal(timeout, 73);
      throw Object.assign(new Error("denied"), { code: "EACCES" });
    }, () => exists);
    assert.equal((await probe.ownerIdentity(4321, 73)).state, exists === false ? "absent" : "unknown");
  }
});

test("failed identity publishes no lock or marker and the same lease module can recover without stealing an unknown owner", async () => {
  const home = await realpath(await mkdtemp(resolve(os.tmpdir(), "sunder-identity-")));
  const output = resolve(home, "output");
  let fail = true;
  const probe = fixture(async () => {
    if (fail) throw new Error("temporary bootstrap failure");
    return { stdout: ticks, stderr: "" };
  });
  const factory = mock.method(identityModule, "createProcessStartIdentityProbe", () => probe);
  const profile = mock.method(require("node:os") as typeof os, "homedir", () => home);
  const modulePath = require.resolve("../src/output");
  delete require.cache[modulePath];
  const leases = require(modulePath) as typeof import("../src/output");
  try {
    await assert.rejects(leases.withOutputLeases([output], async () => assert.fail("must not run")), /bootstrap failure/u);
    await assert.rejects(stat(`${output}.sunder-output.lock`), { code: "ENOENT" });
    await assert.rejects(stat(resolve(home, ".sunder/generated-output/processes")), { code: "ENOENT" });
    fail = false;
    await leases.withOutputLeases([output], async () => {
      const path = `${output}.sunder-output.lock/owner.json`;
      const before = await readFile(path);
      fail = true; // Self identity is cached; the owner's inspection now fails.
      await assert.rejects(leases.withOutputLeases([output], async () => assert.fail("must not steal"), 50), /Timed out/u);
      assert.deepEqual(await readFile(path), before);
    });
    await assert.rejects(stat(`${output}.sunder-output.lock`), { code: "ENOENT" });
  } finally {
    factory.mock.restore(); profile.mock.restore(); delete require.cache[modulePath];
    await rm(home, { recursive: true, force: true });
  }
});
