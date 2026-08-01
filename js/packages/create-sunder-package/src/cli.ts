#!/usr/bin/env node
import { mkdir, readdir, readFile, stat, writeFile } from "node:fs/promises";
import { basename, dirname, relative, resolve } from "node:path";
import { canonicalizeDescriptor, type JsonValue, type RpcContractDescriptor } from "@sunder/sdk";
import { createHash } from "node:crypto";

async function main(): Promise<void> {
  const options = parse(process.argv.slice(2));
  const output = resolve(options.output ?? options.name ?? (options.template === "react-node" ? "sunder-react-node-package" : "sunder-node-package"));
  const packageId = options.packageId ?? derivePackageId(basename(output));
  const packageName = options.packageName ?? title(packageId);
  if (!/^[a-z0-9]+(?:[.-][a-z0-9]+)*$/u.test(packageId)) throw new Error("--package-id must be lowercase dot-separated ASCII.");
  try {
    const existing = await readdir(output);
    if (existing.length > 0) throw new Error(`Output directory '${output}' is not empty.`);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
  }
  const templateRoot = resolve(__dirname, "..", options.template === "react-node" ? "template-react-node" : "template");
  const descriptor = JSON.parse(await readFile(resolve(templateRoot, "contracts", "example.rpc.json"), "utf8")) as RpcContractDescriptor;
  const contractSha = createHash("sha256").update(canonicalizeDescriptor(descriptor as unknown as JsonValue)).digest("hex");
  const replacements = new Map([
    ["SUNDER_PACKAGE_ID", packageId],
    ["SUNDER_PACKAGE_NAME", packageName],
    ["SUNDER_NPM_PACKAGE_NAME", packageId.replaceAll(".", "-")],
    ["SUNDER_CONTRACT_SHA256", contractSha],
  ]);
  await copyTemplate(templateRoot, output, replacements);
  const kind = options.template === "react-node" ? "React App + Node process" : "Node process";
  process.stdout.write(`Created Sunder ${kind} package at ${output}\n\nNext steps:\n  npm install\n  npm test\n  npm run sunder:dev\n  npm run build\n  npm run smoke\n  npm run package\n`);
}

async function copyTemplate(source: string, destination: string, replacements: ReadonlyMap<string, string>): Promise<void> {
  await mkdir(destination, { recursive: true });
  for (const entry of await readdir(source, { withFileTypes: true })) {
    const sourcePath = resolve(source, entry.name);
    const targetName = entry.name === "gitignore" ? ".gitignore" : entry.name;
    const targetPath = resolve(destination, targetName);
    if (entry.isDirectory()) {
      await copyTemplate(sourcePath, targetPath, replacements);
    } else if (entry.isFile()) {
      let content = await readFile(sourcePath, "utf8");
      for (const [token, value] of replacements) content = content.replaceAll(token, value);
      await mkdir(dirname(targetPath), { recursive: true });
      await writeFile(targetPath, content, "utf8");
    }
  }
}

function parse(argumentsList: readonly string[]): Options {
  let name: string | undefined;
  let output: string | undefined;
  let packageId: string | undefined;
  let packageName: string | undefined;
  let template: TemplateKind = "node";
  for (let index = 0; index < argumentsList.length; index++) {
    const argument = argumentsList[index];
    if (argument === "--yes" || argument === "-y") continue;
    if (argument === "--output") output = value(argumentsList, ++index, argument);
    else if (argument === "--package-id") packageId = value(argumentsList, ++index, argument);
    else if (argument === "--package-name") packageName = value(argumentsList, ++index, argument);
    else if (argument === "--template") {
      const configured = value(argumentsList, ++index, argument);
      if (configured !== "node" && configured !== "react-node") throw new Error("--template must be 'node' or 'react-node'.");
      template = configured;
    }
    else if (argument?.startsWith("-") === true) throw new Error(`Unknown option '${argument}'.`);
    else if (name === undefined) name = argument;
    else throw new Error(`Unexpected argument '${argument}'.`);
  }
  return { name, output, packageId, packageName, template };
}

function value(argumentsList: readonly string[], index: number, option: string): string {
  const result = argumentsList[index];
  if (result === undefined || result.startsWith("-")) throw new Error(`${option} requires a value.`);
  return result;
}

function derivePackageId(value: string): string {
  const result = value.toLowerCase().replace(/[^a-z0-9]+/gu, ".").replace(/^\.+|\.+$/gu, "");
  return result.length === 0 ? "example.sunder.package" : result;
}

function title(value: string): string {
  return value.split(/[.-]/u).map((part) => `${part.slice(0, 1).toUpperCase()}${part.slice(1)}`).join(" ");
}

interface Options {
  readonly name?: string;
  readonly output?: string;
  readonly packageId?: string;
  readonly packageName?: string;
  readonly template: TemplateKind;
}

type TemplateKind = "node" | "react-node";

void main().catch((error: unknown) => {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
});
