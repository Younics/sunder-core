import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { npmCommand } from "../npm-command.mjs";

test("npm resolution honors PATH order and supports Windows executable and official cmd layouts without a shell", () => {
  const args = ["pack", "path with spaces & %values% | $HOME", ""];
  const files = new Set(["C:\\manager\\npm.exe", "C:\\official\\npm.cmd", "C:\\official\\node_modules\\npm\\bin\\npm-cli.js"]);
  const options = { platform: "win32", environment: { Path: "C:\\manager;C:\\official" }, fileProbe: path => files.has(path), nodeExecutable: "C:\\node\\node.exe" };
  assert.deepEqual(npmCommand(args, options), { command: "C:\\manager\\npm.exe", arguments: args });
  options.environment.Path = "C:\\official;C:\\manager";
  assert.deepEqual(npmCommand(args, options), { command: options.nodeExecutable,
    arguments: ["C:\\official\\node_modules\\npm\\bin\\npm-cli.js", ...args] });
  files.delete("C:\\official\\node_modules\\npm\\bin\\npm-cli.js");
  assert.throws(() => npmCommand(args, options), /no adjacent npm-cli/u, "Do not silently select a different npm installation");
  options.environment.SUNDER_NPM_EXECUTABLE = "npm.exe";
  assert.throws(() => npmCommand(args, options), /absolute/u);
  options.environment.SUNDER_NPM_EXECUTABLE = "C:\\missing\\npm.exe";
  assert.throws(() => npmCommand(args, options), /unavailable/u);
  assert.deepEqual(npmCommand(args, { platform: "linux", environment: { PATH: ".:/tools:/other" }, fileProbe: path => path === "/tools/npm" }),
    { command: "/tools/npm", arguments: args });
});

test("npm CLI override preserves argument bytes and nonzero exit status through the actual launcher", async () => {
  const root = await mkdtemp(resolve(tmpdir(), "sunder npm command "));
  let completed = false;
  try {
    const cli = resolve(root, "npm cli.cjs");
    await writeFile(cli, "console.log(JSON.stringify(process.argv.slice(2))); process.exitCode = 37;\n");
    const args = ["pack", "space & | %PATH% $HOME \\\"quoted\\\"", "", "Unicode-😀"];
    const result = spawnSync(process.execPath, [fileURLToPath(new URL("../npm-command.mjs", import.meta.url)), ...args], {
      encoding: "utf8", env: { ...process.env, SUNDER_NPM_EXECUTABLE: cli },
    });
    assert.equal(result.error, undefined);
    assert.equal(result.status, 37, result.stderr);
    assert.deepEqual(JSON.parse(result.stdout), args);
    const cmd = resolve(root, "npm.cmd");
    const officialCli = resolve(root, "node_modules/npm/bin/npm-cli.js");
    await mkdir(resolve(root, "node_modules/npm/bin"), { recursive: true });
    await writeFile(cmd, "must not run through cmd.exe\n");
    await writeFile(officialCli, "console.log(JSON.stringify(process.argv.slice(2))); process.exitCode = 37;\n");
    const official = spawnSync(process.execPath, [fileURLToPath(new URL("../npm-command.mjs", import.meta.url)), ...args], {
      encoding: "utf8", env: { ...process.env, SUNDER_NPM_EXECUTABLE: cmd },
    });
    assert.equal(official.status, 37, official.stderr);
    assert.deepEqual(JSON.parse(official.stdout), args);
    completed = true;
  } finally {
    if (completed) await rm(root, { recursive: true, force: true });
    else process.stderr.write(`Failed npm launcher fixture retained at ${root}\n`);
  }
});
