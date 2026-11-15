import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { policy, synchronize } from "../toolchains.mjs";

test("build, CI, templates and Host policy retain the same exact development versions", async () => {
  await synchronize(true);
  assert.ok(policy.supportedDotnetRuntimeMajors.includes(policy.dotnetRuntimeMajor));
  assert.ok(policy.supportedNodeRuntimeMajors.includes(policy.nodeRuntimeMajor));
});

test("toolchain checks accept only checkout newline differences and writers retain LF", async () => {
  const root = await mkdtemp(resolve(tmpdir(), "sunder-toolchain-newlines-"));
  const repositoryRoot = fileURLToPath(new URL("../../", import.meta.url));
  const generatedFiles = [
    "languages/csharp/dotnet/libraries/Sunder.Package.Format/SunderToolchainPolicy.Generated.cs",
    "languages/csharp/dotnet/packages/Sunder.Package.Build/build/Sunder.Toolchains.props",
    "languages/typescript/node/packages/package-tool/src/toolchains.generated.ts",
    "global.json",
    "languages/typescript/node/.nvmrc",
    "languages/csharp/dotnet/packages/Sunder.Package.Templates/templates/sunder-package/Sunder.Package.Template/global.json",
    ...["template", "template-react-node"].flatMap(folder => [
      `languages/typescript/node/packages/create-sunder-package/${folder}/.node-version`,
      `languages/typescript/node/packages/create-sunder-package/${folder}/package.json`,
    ]),
  ];
  const run = (...args) => spawnSync(process.execPath, [resolve(root, "scripts/toolchains.mjs"), ...args], {
    cwd: root, encoding: "utf8",
  });
  const assertSuccess = result => assert.equal(result.status, 0, result.stderr || String(result.error ?? result.stdout));
  try {
    for (const relative of ["scripts/toolchains.mjs", "eng/toolchains.json", "languages/typescript/node/package.json", ...generatedFiles]) {
      const destination = resolve(root, relative);
      await mkdir(dirname(destination), { recursive: true });
      await writeFile(destination, await readFile(resolve(repositoryRoot, relative)));
    }
    assertSuccess(run("--write"));
    const canonical = new Map();
    for (const relative of generatedFiles) {
      const content = await readFile(resolve(root, relative), "utf8");
      assert.ok(!content.includes("\r") && content.endsWith("\n"), `${relative}: writer must use LF`);
      canonical.set(relative, content);
    }
    for (const newline of ["\n", "\r\n"]) {
      for (const [relative, content] of canonical) await writeFile(resolve(root, relative), content.replace(/\n/gu, newline));
      assertSuccess(run());
      for (const [relative, content] of canonical) {
        assert.equal(await readFile(resolve(root, relative), "utf8"), content.replace(/\n/gu, newline), "check must not rewrite inputs");
      }
    }
    const relative = generatedFiles[0];
    const original = canonical.get(relative);
    for (const [description, mutate] of [
      ["version drift", content => content.replace(`"${policy.dotnetSdk}"`, '"0.0.0"')],
      ["spacing drift", content => content.replace("    internal", "   internal")],
      ["missing final newline", content => content.slice(0, -1)],
      ["bare carriage return", content => content.replace("using ", "using\r ")],
    ]) {
      await writeFile(resolve(root, relative), mutate(original));
      const result = run();
      assert.equal(result.status, 1, `${description}: ${result.stderr}`);
      assert.ok(result.stderr.includes(`${relative} differs from eng/toolchains.json`), `${description}: ${result.stderr}`);
    }
    assertSuccess(run("--write"));
    for (const [relative, content] of canonical) assert.equal(await readFile(resolve(root, relative), "utf8"), content);
    assertSuccess(run());
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
