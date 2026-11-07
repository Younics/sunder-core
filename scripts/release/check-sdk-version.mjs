#!/usr/bin/env node
// Check source metadata without forcing unrelated packages to share a version.
import { readFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { isDeepStrictEqual } from "node:util";
import { policy, synchronize } from "../toolchains.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const json = async path => JSON.parse(await readFile(resolve(root, path), "utf8"));
const inventory = await json("eng/test-oracles/core-public-packages.json");
const versionPattern = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$/u;
const packages = [];
await synchronize(true);
for (const kind of ["nuget", "npm"]) {
  for (const item of inventory[kind]) {
    const source = kind === "npm" ? await json(`${item.project}/package.json`) : null;
    const name = item.packageId ?? item.name;
    const version = source?.version ?? /<Version>([^<]+)<\/Version>/u.exec(await readFile(resolve(root, item.project), "utf8"))?.[1];
    const match = typeof version === "string" && versionPattern.exec(version);
    if (!match || match[4]?.split(".").some(value => /^0\d+$/u.test(value))) throw new Error(`Invalid authored version: ${item.project}`);
    if (source && (source.name !== name || source.private === true || source.license !== "GPL-3.0-only"
      || source.repository?.url !== "git+https://github.com/Younics/sunder-core.git"
      || source.publishConfig?.access !== "public" || source.publishConfig?.provenance !== true)) {
      throw new Error(`Unexpected npm publication metadata: ${item.project}`);
    }
    packages.push({ kind, name, version, project: item.project });
  }
}
const lock = await json("languages/typescript/node/package-lock.json");
for (const item of packages.filter(item => item.kind === "npm")) {
  const relative = item.project.replace("languages/typescript/node/", "");
  const source = await json(`${item.project}/package.json`);
  if (lock.packages?.[relative]?.version !== item.version
    || lock.packages[relative].license !== source.license
    || !isDeepStrictEqual(lock.packages[relative].dependencies, source.dependencies)) {
    throw new Error(`npm lockfile differs from ${item.project}/package.json`);
  }
}
const workspace = await json("languages/typescript/node/package.json");
if (workspace.packageManager !== `npm@${policy.npm}`) throw new Error("Node workspace must use the pinned npm toolchain");
console.log(JSON.stringify({ packages }));
