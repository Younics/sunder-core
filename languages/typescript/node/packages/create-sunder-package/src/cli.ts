#!/usr/bin/env node
import { mkdir, readdir, readFile, writeFile } from "node:fs/promises";
import { basename, dirname, resolve } from "node:path";
import { isPackageId } from "@sunder/sdk";

const PRESETS: ReadonlyMap<string, ScaffoldPreset> = new Map([
  ["node", {
    templateDirectory: "template",
    defaultOutput: "sunder-node-package",
    label: "Node process",
  }],
  ["react-node", {
    templateDirectory: "template-react-node",
    defaultOutput: "sunder-react-node-package",
    label: "React App + Node process",
  }],
]);

async function main(): Promise<void> {
  const options = parse(process.argv.slice(2));
  const preset = PRESETS.get(options.preset);
  if (preset === undefined) throw new Error(`Unknown scaffold preset '${options.preset}'. Available presets: ${[...PRESETS.keys()].join(", ")}.`);
  const output = resolve(options.output ?? options.name ?? preset.defaultOutput);
  const packageId = options.packageId ?? derivePackageId(basename(output));
  const packageName = options.packageName ?? title(packageId);
  if (!isPackageId(packageId)) throw new Error("--package-id must be lowercase dot-separated ASCII of at most 128 characters.");
  if (packageName.length === 0 || packageName.length > 256 || packageName.trim() !== packageName || /\p{Cc}/u.test(packageName)) {
    throw new Error("--package-name must be trimmed, non-empty, control-free, and at most 256 characters.");
  }
  try {
    const existing = await readdir(output);
    if (existing.length > 0) throw new Error(`Output directory '${output}' is not empty.`);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
  }
  const templateRoot = resolve(__dirname, "..", preset.templateDirectory);
  const replacements = new Map([
    ["__SUNDER_PACKAGE_ID_JSON__", escapeJsonString(packageId)],
    ["__SUNDER_PROVIDER_ID_JSON__", escapeJsonString(`${packageId}.provider`)],
    ["__SUNDER_VIEW_ID_JSON__", escapeJsonString(`${packageId}.main`)],
    ["__SUNDER_PACKAGE_NAME_JSON__", escapeJsonString(packageName)],
    ["__SUNDER_NPM_PACKAGE_NAME_JSON__", escapeJsonString(packageId.replaceAll(".", "-"))],
    ["__SUNDER_PACKAGE_NAME_HTML__", escapeHtml(packageName)],
    ["SUNDER_PACKAGE_ID", escapeMarkdown(packageId)],
    ["SUNDER_PACKAGE_NAME", escapeMarkdown(packageName)],
  ]);
  await copyTemplate(templateRoot, output, replacements);
  process.stdout.write(`Created Sunder ${preset.label} package at ${output}\n\nNext steps:\n  npm install\n  npm test\n  npm run sunder:dev\n  npm run build\n  npm run smoke\n  npm run package\n`);
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
      const content = (await readFile(sourcePath, "utf8")).replace(/__SUNDER_[A-Z0-9_]+__|SUNDER_PACKAGE_(?:ID|NAME)/gu, (token) => {
        const replacement = replacements.get(token);
        if (replacement === undefined) throw new Error(`Template contains unknown scaffold token '${token}'.`);
        return replacement;
      });
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
  let preset = "node";
  for (let index = 0; index < argumentsList.length; index++) {
    const argument = argumentsList[index];
    if (argument === "--yes" || argument === "-y") continue;
    if (argument === "--output") output = value(argumentsList, ++index, argument);
    else if (argument === "--package-id") packageId = value(argumentsList, ++index, argument);
    else if (argument === "--package-name") packageName = value(argumentsList, ++index, argument);
    else if (argument === "--preset" || argument === "--template") {
      const configured = value(argumentsList, ++index, argument);
      preset = configured;
    }
    else if (argument?.startsWith("-") === true) throw new Error(`Unknown option '${argument}'.`);
    else if (name === undefined) name = argument;
    else throw new Error(`Unexpected argument '${argument}'.`);
  }
  return { name, output, packageId, packageName, preset };
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

function escapeMarkdown(value: string): string {
  return value.replace(/[\\`*_{}\[\]()<>#+.!|~-]/gu, "\\$&");
}

function escapeJsonString(value: string): string {
  return JSON.stringify(value).slice(1, -1);
}

function escapeHtml(value: string): string {
  return value.replace(/[&<>"']/gu, (character) => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    "\"": "&quot;",
    "'": "&#39;",
  })[character]!);
}

interface Options {
  readonly name?: string;
  readonly output?: string;
  readonly packageId?: string;
  readonly packageName?: string;
  readonly preset: string;
}

interface ScaffoldPreset {
  readonly templateDirectory: string;
  readonly defaultOutput: string;
  readonly label: string;
}

void main().catch((error: unknown) => {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
});
