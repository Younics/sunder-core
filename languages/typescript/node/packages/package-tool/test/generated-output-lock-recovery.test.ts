import assert from "node:assert/strict";
import { spawn, type ChildProcess } from "node:child_process";
import { once } from "node:events";
import { mkdtemp, mkdir, readFile, realpath, rename, rm, stat, symlink, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, resolve } from "node:path";
import test from "node:test";

interface Probe {
  child: ChildProcess;
  result: Promise<{ code: number | null; error: string }>;
}

async function fixture() {
  const root = await realpath(await mkdtemp(resolve(tmpdir(), "sunder-lock-recovery-")));
  const home = resolve(root, "profile");
  await mkdir(home, { mode: 0o700 });
  const children: Probe[] = [];
  async function start(mode: string, job: string, ready: string, timeout?: number, profile = home): Promise<Probe> {
    const temp = resolve(root, job);
    await mkdir(temp, { recursive: true });
    const args = [resolve(__dirname, "generated-output-lock-child.js"), mode, resolve(root, "output"), resolve(root, ready), resolve(root, "release")];
    if (timeout !== undefined) args.push(String(timeout));
    const child = spawn(process.execPath, args, {
      env: { ...process.env, SUNDER_TEST_ACCOUNT_HOME: home, HOME: profile, USERPROFILE: profile, TMPDIR: temp, TMP: temp, TEMP: temp },
      stdio: ["ignore", "ignore", "pipe"],
    });
    let error = "";
    child.stderr!.on("data", (bytes: Buffer) => { error += bytes.toString(); });
    const probe = { child, result: once(child, "exit").then(([code]) => ({ code: code as number | null, error })) };
    children.push(probe);
    return probe;
  }
  async function close() {
    for (const probe of children) if (probe.child.exitCode === null && probe.child.signalCode === null) probe.child.kill("SIGKILL");
    await Promise.all(children.map((probe) => probe.result));
    await rm(root, { recursive: true, force: true });
  }
  return { root, home, start, close };
}

async function ready(path: string): Promise<Record<string, unknown>> {
  const deadline = performance.now() + 45_000;
  while (performance.now() < deadline) {
    try { return JSON.parse(await readFile(path, "utf8")) as Record<string, unknown>; }
    catch { await new Promise((done) => setTimeout(done, 20)); }
  }
  throw new Error(`Lock probe did not become ready: ${path}`);
}

test("generated output lock survives isolated Maker profiles and Worker temp-root replacement without reclaiming a current holder", { timeout: 120_000 }, async () => {
  const f = await fixture();
  try {
    const holder = await f.start("hold", "job-a", "held");
    const owner = await ready(resolve(f.root, "held"));
    const marker = owner.processMarkerPath as string;
    assert.equal(dirname(marker), resolve(f.home, ".sunder/generated-output/processes"));
    if (process.platform !== "win32") {
      assert.equal((await stat(dirname(marker))).mode & 0o077, 0);
      assert.equal((await stat(marker)).mode & 0o077, 0);
    }
    const makerProfile = resolve(f.root, "maker-profile");
    await mkdir(makerProfile, { mode: 0o700 });
    const contender = await f.start("recover", "job-b", "contended", 1_400, makerProfile);
    const pendingResult = await contender.result;
    assert.equal(pendingResult.code, 1, pendingResult.error);
    assert.match(pendingResult.error, /Timed out/);
    assert.match(pendingResult.error, new RegExp(`Waiting for generated output lock.*Owner PID ${holder.child.pid}:`));
    const waitLines = pendingResult.error.split("\n").filter(line => line.startsWith("Waiting for generated output lock"));
    assert.equal(waitLines.length, new Set(waitLines.map(line => /Owner PID \d+: (alive|unknown)/u.exec(line)?.[1])).size,
      "Each owner state should be reported once, without flooding redirected output.");
    assert.deepEqual(JSON.parse(await readFile(resolve(f.root, "output.sunder-output.lock/owner.json"), "utf8")), owner);
    holder.child.kill("SIGKILL");
    await holder.result;
    await rm(resolve(f.root, "job-a"), { recursive: true });
    await stat(marker); // Evidence is independent of the removed job tree.
    const restarted = await f.start("recover", "job-c", "recovered", undefined, makerProfile);
    const recovered = await restarted.result;
    assert.equal(recovered.code, 0, recovered.error);
    await assert.rejects(stat(resolve(f.root, "output.sunder-output.lock")), { code: "ENOENT" });
  } finally { await f.close(); }
});

test("denied dead-owner renames fail promptly with the filesystem cause and retain output; transient failures recover", { timeout: 120_000 }, async () => {
  for (const code of ["EACCES", "EPERM"] as const) {
    const f = await fixture();
    try {
      const abandoned = await f.start("abandon", "old-temp", "abandoned");
      assert.equal((await abandoned.result).code, 23);
      const owner = await ready(resolve(f.root, "abandoned"));
      const lockOwner = resolve(f.root, "output.sunder-output.lock/owner.json");
      const before = await readFile(lockOwner);
      await writeFile(resolve(f.root, "output"), "last successful output");
      const denied = await f.start(`recover-denied-${code}`, "denied-temp", "not-acquired", 10_000);
      const result = await denied.result;
      assert.equal(result.code, 1, result.error);
      assert.match(result.error, new RegExp(`Cannot retire generated output lock.*${code}`));
      assert.match(result.error, /open handle.*retry/);
      assert.doesNotMatch(result.error, /Timed out after/);
      assert.deepEqual(await readFile(lockOwner), before);
      await stat(owner.processMarkerPath as string);
      assert.equal(await readFile(resolve(f.root, "output"), "utf8"), "last successful output");
      const transient = await f.start("recover-transient", "retry-temp", "recovered");
      const retried = await transient.result;
      assert.equal(retried.code, 0, retried.error);
      await assert.rejects(stat(resolve(f.root, "output.sunder-output.lock")), { code: "ENOENT" });
      assert.equal(await readFile(resolve(f.root, "output"), "utf8"), "last successful output");
    } finally { await f.close(); }
  }
});

test("normal shutdown cleans unused evidence but an unreleased lease retains evidence across restart", { timeout: 120_000 }, async () => {
  const f = await fixture();
  try {
    const normal = await f.start("recover", "normal-temp", "normal");
    const normalResult = await normal.result;
    assert.equal(normalResult.code, 0, normalResult.error);
    const normalOwner = await ready(resolve(f.root, "normal"));
    await assert.rejects(stat(normalOwner.processMarkerPath as string), { code: "ENOENT" });
    const abandoned = await f.start("abandon", "old-temp", "abandoned");
    assert.equal((await abandoned.result).code, 23);
    const owner = await ready(resolve(f.root, "abandoned"));
    await stat(owner.processMarkerPath as string);
    await rm(resolve(f.root, "old-temp"), { recursive: true });
    const restarted = await f.start("recover", "new-temp", "recovered");
    const recovered = await restarted.result;
    assert.equal(recovered.code, 0, recovered.error);
  } finally { await f.close(); }
});

for (const damage of ["missing", "tampered", "arbitrary-path", "symlink", "foreign-host", "legacy-removed-temp", "symlink-root"] as const) {
  test(`unverifiable ${damage} evidence produces an actionable failure and preserves selected paths`, { timeout: 120_000 }, async () => {
    const f = await fixture();
    try {
      const abandoned = await f.start("abandon", "old-temp", "abandoned");
      assert.equal((await abandoned.result).code, 23);
      const owner = await ready(resolve(f.root, "abandoned"));
      const marker = owner.processMarkerPath as string;
      const lockOwner = resolve(f.root, "output.sunder-output.lock/owner.json");
      const arbitrary = resolve(f.root, "arbitrary.json");
      if (damage === "missing") await rm(marker);
      if (damage === "tampered") await writeFile(marker, "{}");
      if (damage === "arbitrary-path" || damage === "symlink") {
        await rename(marker, arbitrary);
        if (damage === "symlink") await symlink(arbitrary, marker);
        else owner.processMarkerPath = arbitrary;
      }
      if (damage === "foreign-host") {
        owner.hostname = "foreign.invalid";
        const proof = JSON.parse(await readFile(marker, "utf8")) as Record<string, unknown>;
        proof.hostname = owner.hostname;
        await writeFile(marker, JSON.stringify(proof));
      }
      if (damage === "legacy-removed-temp") {
        owner.processMarkerPath = resolve(f.root, "old-temp/sunder-generated-output/processes", `${owner.processMarkerToken as string}.json`);
        await rm(resolve(f.root, "old-temp"), { recursive: true });
      }
      if (damage === "symlink-root") {
        await rename(dirname(marker), resolve(f.root, "real-markers"));
        await symlink(resolve(f.root, "real-markers"), dirname(marker), "junction");
      }
      await writeFile(lockOwner, JSON.stringify(owner));
      await writeFile(resolve(f.root, "output"), "user-selected output");
      const before = await readFile(lockOwner);
      const clock = performance.now();
      const recovery = await f.start("recover", "new-temp", "should-not-acquire");
      const result = await recovery.result;
      assert.equal(result.code, 1, result.error);
      assert.match(result.error, damage === "symlink-root" ? /marker ancestor/ : /unverifiable ownership evidence/);
      if (damage !== "symlink-root") assert.match(result.error, /independently confirm/);
      assert.ok(performance.now() - clock < 45_000, result.error);
      assert.deepEqual(await readFile(lockOwner), before);
      assert.equal(await readFile(resolve(f.root, "output"), "utf8"), "user-selected output");
    } finally { await f.close(); }
  });
}
