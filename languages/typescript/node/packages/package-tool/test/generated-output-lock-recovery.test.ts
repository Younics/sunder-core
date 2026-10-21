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
  async function start(mode: string, job: string, ready: string, timeout?: number): Promise<Probe> {
    const temp = resolve(root, job);
    await mkdir(temp, { recursive: true });
    const args = [resolve(__dirname, "generated-output-lock-child.js"), mode, resolve(root, "output"), resolve(root, ready), resolve(root, "release")];
    if (timeout !== undefined) args.push(String(timeout));
    const child = spawn(process.execPath, args, {
      env: { ...process.env, HOME: home, USERPROFILE: home, TMPDIR: temp, TMP: temp, TEMP: temp },
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
  const deadline = performance.now() + 10_000;
  while (performance.now() < deadline) {
    try { return JSON.parse(await readFile(path, "utf8")) as Record<string, unknown>; }
    catch { await new Promise((done) => setTimeout(done, 20)); }
  }
  throw new Error(`Lock probe did not become ready: ${path}`);
}

test("generated output lock survives Worker temp-root replacement without reclaiming a current holder", { timeout: 20_000 }, async () => {
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
    const contender = await f.start("recover", "job-b", "contended", 1_400);
    const pendingResult = await contender.result;
    assert.equal(pendingResult.code, 1, pendingResult.error);
    assert.match(pendingResult.error, /Timed out/);
    assert.deepEqual(JSON.parse(await readFile(resolve(f.root, "output.sunder-output.lock/owner.json"), "utf8")), owner);
    holder.child.kill("SIGKILL");
    await holder.result;
    await rm(resolve(f.root, "job-a"), { recursive: true });
    await stat(marker); // Evidence is independent of the removed job tree.
    const restarted = await f.start("recover", "job-c", "recovered");
    const recovered = await restarted.result;
    assert.equal(recovered.code, 0, recovered.error);
    await assert.rejects(stat(resolve(f.root, "output.sunder-output.lock")), { code: "ENOENT" });
  } finally { await f.close(); }
});

test("normal shutdown cleans unused evidence but an unreleased lease retains evidence across restart", { timeout: 20_000 }, async () => {
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
  test(`unverifiable ${damage} evidence produces an actionable failure and preserves selected paths`, { timeout: 20_000 }, async () => {
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
      assert.ok(performance.now() - clock < 10_000, result.error);
      assert.deepEqual(await readFile(lockOwner), before);
      assert.equal(await readFile(resolve(f.root, "output"), "utf8"), "user-selected output");
    } finally { await f.close(); }
  });
}
