import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { chmod, mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const verifierPath = resolve(repositoryRoot, "scripts/release/verify-npm-release.mjs");
const publisherPath = resolve(repositoryRoot, "scripts/release/publish-sdk.sh");
const source = "a".repeat(40);
const python = execFileSync("python3", ["-B", "-c", "import sys; print(sys.executable)"], { encoding: "utf8" }).trim();

async function fixture(t, kind, { version = "1.2.3", history = { present: false, newer: true }, ...overrides } = {}) {
  const root = await mkdtemp(join(tmpdir(), "sunder-publish-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  const bin = join(root, "bin");
  const log = join(root, "commands.log");
  await mkdir(bin);
  await writeFile(log, "");
  const tool = async (name, body) => {
    const path = join(bin, name);
    await writeFile(path, `#!/usr/bin/env bash\nset -euo pipefail\n${body}\n`);
    await chmod(path, 0o755);
  };
  await tool("python3", `if [[ "$3" == history ]]; then
  printf 'history\\n' >> "$MOCK_LOG"
  [[ "$(wc -l < "$MOCK_LOG" | tr -d '[:space:]')" == 1 ]] || exit 91
  printf '%s\\n' "$MOCK_HISTORY"
  exit "\${MOCK_HISTORY_STATUS:-0}"
fi
exec "$REAL_PYTHON" "$@"`);
  await tool("npm", `printf 'npm %s\\n' "$*" >> "$MOCK_LOG"
case "$1" in
  whoami) exit 0 ;;
  publish)
    [[ "$*" != *' --dry-run '* ]] || exit 0
    exit "\${MOCK_PUBLISH_STATUS:-0}" ;;
  *) exit 92 ;;
esac`);
  await tool("dotnet", `printf 'dotnet %s\\n' "$*" >> "$MOCK_LOG"
[[ "$1 $2" == 'nuget push' ]] || exit 93
exit "\${MOCK_PUBLISH_STATUS:-0}"`);
  for (const name of ["curl", "node", "sleep"]) {
    await tool(name, `printf 'forbidden ${name}\\n' >> "$MOCK_LOG"\nexit 94`);
  }
  const name = kind === "npm" ? "@younics/sunder-sdk" : "Sunder.Sdk";
  const archive = join(root, kind === "npm" ? `younics-sunder-sdk-${version}.tgz` : `Sunder.Sdk.${version}.nupkg`);
  if (kind === "npm") {
    await mkdir(join(root, "package"));
    await writeFile(join(root, "package/package.json"), JSON.stringify({
      name, version, license: "GPL-3.0-only", repository: { url: "git+https://github.com/Younics/sunder-core.git" },
      publishConfig: { access: "public", provenance: true },
    }));
    execFileSync("tar", ["-czf", archive, "-C", root, "package"]);
  } else {
    await writeFile(archive, "retained NuGet bytes");
  }
  const result = spawnSync("bash", [publisherPath, kind, name, version, archive, source], {
    encoding: "utf8", timeout: 10_000,
    env: {
      ...process.env, PATH: `${bin}:${process.env.PATH}`, REAL_PYTHON: python, MOCK_LOG: log,
      MOCK_HISTORY: JSON.stringify(history), NODE_AUTH_TOKEN: "fixture-token", NUGET_API_KEY: "fixture-key",
      GITHUB_SHA: source, GITHUB_REPOSITORY: "Younics/sunder-core", GITHUB_REF: "refs/heads/main",
      GITHUB_WORKFLOW_REF: "Younics/sunder-core/.github/workflows/sunder-sdk-release.yml@refs/heads/main",
      ACTIONS_ID_TOKEN_REQUEST_URL: "https://actions.example.test/oidc", ACTIONS_ID_TOKEN_REQUEST_TOKEN: "fixture-oidc",
      ...overrides,
    },
  });
  assert.ifError(result.error);
  return { ...result, commands: (await readFile(log, "utf8")).trim().split("\n") };
}

test("accepted npm uploads target the consumer channel without registry readback or promotion", async t => {
  for (const [version, channel] of [["1.2.3", "latest"], ["1.2.3-rc.1", "next"]]) {
    const result = await fixture(t, "npm", { version });
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.commands.length, 4); // One history lookup, whoami, dry-run, upload.
    assert.match(result.commands[3], new RegExp(` --tag ${channel} `, "u"));
    assert.match(result.commands[3], / --ignore-scripts --access public .* --provenance$/u);
    assert.doesNotMatch(result.commands[3], /--dry-run|accept-v/u);
    assert.match(result.stdout, /Registry accepted/u);
  }
});

test("accepted NuGet uploads finish without waiting for download availability", async t => {
  const result = await fixture(t, "nuget");
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.commands.length, 2);
  assert.match(result.commands[1], /^dotnet nuget push .* --skip-duplicate$/u);
  assert.match(result.stdout, /Registry accepted/u);
});

test("npm and NuGet publishing failures propagate immediately", async t => {
  for (const kind of ["npm", "nuget"]) {
    const result = await fixture(t, kind, { MOCK_PUBLISH_STATUS: "42" });
    assert.equal(result.status, 42, result.stderr);
    assert.doesNotMatch(result.stdout, /Registry accepted/u);
    assert.doesNotMatch(result.commands.join("\n"), /forbidden/u);
  }
});

test("published versions skip upload without credentials, including older-source recovery", async t => {
  for (const kind of ["npm", "nuget"]) {
    const result = await fixture(t, kind, {
      history: { present: true, newer: false }, GITHUB_SHA: "b".repeat(40), NODE_AUTH_TOKEN: "", NUGET_API_KEY: "",
    });
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(result.commands, ["history"]);
    assert.match(result.stdout, /Skipping already published/u);
  }
});

test("a newer version or a failed registry lookup prevents publication", async t => {
  for (const kind of ["npm", "nuget"]) {
    for (const options of [{ history: { present: false, newer: false } }, { MOCK_HISTORY_STATUS: "43" }]) {
      const result = await fixture(t, kind, options);
      assert.notEqual(result.status, 0);
      assert.deepEqual(result.commands, ["history"]);
    }
  }
});

test("later-source recovery can publish retained NuGets but cannot misattribute new npm provenance", async t => {
  const npm = await fixture(t, "npm", { GITHUB_SHA: "b".repeat(40) });
  assert.equal(npm.status, 1);
  assert.match(npm.stderr, /Unpublished npm recovery requires the original source workflow/u);
  assert.deepEqual(npm.commands, ["history"]);
  const nuget = await fixture(t, "nuget", { GITHUB_SHA: "b".repeat(40) });
  assert.equal(nuget.status, 0, nuget.stderr);
  assert.equal(nuget.commands.length, 2);
});

test("npm release verifier rejects an unknown single-package selector before registry access", () => {
  assert.throws(
    () => execFileSync(process.execPath, [
      verifierPath,
      "@other/package",
      "1.2.3", "other-1.2.3.tgz", "0123456789012345678901234567890123456789", "publication",
    ], { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] }),
    (error) => error?.stderr?.includes("Expected one public npm package archive"),
  );
});
