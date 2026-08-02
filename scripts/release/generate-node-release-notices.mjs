#!/usr/bin/env node

import { mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";

const [lockArgument, outputArgument] = process.argv.slice(2);
if (lockArgument === undefined || outputArgument === undefined) {
  throw new Error("Usage: generate-node-release-notices.mjs <package-lock.json> <output-directory>");
}

const lockPath = resolve(lockArgument);
const jsRoot = dirname(lockPath);
const outputDirectory = resolve(outputArgument);
const releasePackages = [
  { name: "@sunder/sdk", directory: "packages/sdk" },
  { name: "@sunder/package-tool", directory: "packages/package-tool" },
  { name: "create-sunder-package", directory: "packages/create-sunder-package" },
];

const licenses = await Promise.all(releasePackages.map((item) => readFile(resolve(jsRoot, item.directory, "LICENSE"), "utf8")));
if (licenses.some((license) => license !== licenses[0])) {
  throw new Error("The three npm packages must ship the same MIT LICENSE text.");
}

const notices = await Promise.all(releasePackages.map(async (item) => ({
  package: item.name,
  text: (await readFile(resolve(jsRoot, item.directory, "NOTICE"), "utf8")).trimEnd(),
})));
const lock = JSON.parse(await readFile(lockPath, "utf8"));
if (lock.lockfileVersion !== 3 || lock.packages === null || typeof lock.packages !== "object") {
  throw new Error("Expected an npm lockfileVersion 3 packages map.");
}

const dependencies = Object.entries(lock.packages)
  .filter(([path, value]) => path.includes("node_modules/") && value?.link !== true && typeof value?.version === "string")
  .map(([path, value]) => ({
    name: value.name ?? path.slice(path.lastIndexOf("node_modules/") + "node_modules/".length),
    version: value.version,
    license: value.license ?? "UNKNOWN",
    development: value.dev === true,
    optional: value.optional === true,
    resolved: value.resolved ?? null,
    integrity: value.integrity ?? null,
  }))
  .sort((left, right) => left.name.localeCompare(right.name) || left.version.localeCompare(right.version));

await mkdir(outputDirectory, { recursive: true });
await writeFile(resolve(outputDirectory, "npm-MIT-LICENSE.txt"), licenses[0], "utf8");
await writeFile(
  resolve(outputDirectory, "npm-NOTICES.txt"),
  `${notices.map((notice) => `===== ${notice.package} =====\n${notice.text}`).join("\n\n")}\n`,
  "utf8",
);
await writeFile(
  resolve(outputDirectory, "node-dependency-notices.json"),
  `${JSON.stringify({ schemaVersion: 1, source: "js/package-lock.json", dependencies }, null, 2)}\n`,
  "utf8",
);
