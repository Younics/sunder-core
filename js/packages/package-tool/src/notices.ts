import { lstat, readFile, readdir, realpath } from "node:fs/promises";
import { createRequire } from "node:module";
import { dirname, isAbsolute, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const MAXIMUM_PACKAGE_ENTRIES = 10_000;
const MAXIMUM_LEGAL_FILES = 64;
const MAXIMUM_LEGAL_BYTES = 2 * 1024 * 1024;
const LEGAL_FILE_NAME = /^(?:licen[cs]e|copying|notice|third[-_. ]party[-_. ](?:licen[cs]e|notices?))(?:$|[._ -])/iu;
const LICENSE_FILE_NAME = /^(?:licen[cs]e|copying)(?:$|[._ -])/iu;
interface LegalFile {
  readonly path: string;
  readonly text: string;
}

interface BundledPackageNotice {
  readonly name: string;
  readonly version: string;
  readonly license?: string;
  readonly files: readonly LegalFile[];
}

export async function renderBundledDependencyNotice(
  projectRoot: string,
  moduleIds: readonly string[],
  role: "worker" | "web",
): Promise<string> {
  const normalizedProjectRoot = await realpath(resolve(projectRoot));
  const packages = new Map<string, BundledPackageNotice>();
  for (const moduleId of [...new Set(moduleIds)].sort(ordinal)) {
    const virtualPackageRoot = await resolveVirtualPackageRoot(moduleId, role);
    if (virtualPackageRoot !== undefined) {
      addPackageNotice(packages, await readPackageNotice(virtualPackageRoot, role), role);
      continue;
    }
    const modulePath = normalizeModulePath(moduleId, normalizedProjectRoot);
    if (modulePath === null) continue;
    if (isProjectSource(normalizedProjectRoot, modulePath)) {
      let entry;
      try {
        entry = await lstat(modulePath);
      } catch (error) {
        if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
      }
      if (entry === undefined || !entry.isFile() || entry.isSymbolicLink()) {
        throw new Error(`Rendered bundled module '${moduleId}' does not resolve to a regular project source file.`);
      }
      continue;
    }
    const packageRoot = await findPackageRoot(modulePath);
    if (packageRoot === null) {
      throw new Error(
        `Could not establish third-party attribution for bundled ${role} module '${moduleId}': no owning package.json was found.`,
      );
    }
    addPackageNotice(packages, await readPackageNotice(packageRoot, role), role);
  }

  const heading = role === "worker" ? "Worker" : "Web";
  const lines = [
    `Sunder Bundled ${heading} Dependency Notices`,
    "",
    "This file is generated from exact production bundler module metadata.",
    "Only dependencies with modules included in this role's output are listed.",
    "",
  ];
  const ordered = [...packages.values()].sort((left, right) => ordinal(left.name, right.name) || ordinal(left.version, right.version));
  if (ordered.length === 0) {
    lines.push("No third-party package modules were bundled for this role.", "");
    return lines.join("\n");
  }
  for (const dependency of ordered) {
    lines.push(`===============================================================================`, `${dependency.name}@${dependency.version}`);
    if (dependency.license !== undefined) lines.push(`Declared license: ${dependency.license}`);
    lines.push("");
    for (const file of dependency.files) {
      lines.push(`--- ${file.path} ---`, file.text, "");
    }
  }
  return `${lines.join("\n").trimEnd()}\n`;
}

function normalizeModulePath(moduleId: string, projectRoot: string): string | null {
  if (moduleId.length === 0
    || moduleId.startsWith("node:")
    || moduleId.startsWith("sunder-contracts:")) return null;
  if (moduleId.startsWith("\0") || moduleId.startsWith("<") || moduleId.startsWith("virtual:")) {
    throw new Error(`Rendered bundled module '${printableModuleId(moduleId)}' has no attributable project source or known dependency.`);
  }
  if (!moduleId.startsWith("file://") && !isAbsolute(moduleId) && /^[a-z][a-z0-9+.-]*:/iu.test(moduleId)) {
    throw new Error(`Rendered bundled module '${printableModuleId(moduleId)}' has no attributable project source or known dependency.`);
  }
  let value = moduleId.startsWith("file://") ? fileURLToPath(moduleId) : moduleId;
  const queryIndex = value.indexOf("?");
  if (queryIndex >= 0) value = value.slice(0, queryIndex);
  const hashIndex = value.indexOf("#");
  if (hashIndex >= 0) value = value.slice(0, hashIndex);
  if (value.length === 0) return null;
  return resolve(isAbsolute(value) ? value : resolve(projectRoot, value));
}

async function resolveVirtualPackageRoot(moduleId: string, role: string): Promise<string | undefined> {
  const packageName = classifyBundlerVirtualPackage(moduleId);
  if (packageName === undefined) return undefined;
  let packageJsonPath: string;
  try {
    const vitePackageJsonPath = require.resolve("vite/package.json");
    packageJsonPath = packageName === "vite"
      ? vitePackageJsonPath
      : resolveRolldownPackageJsonFromVite(vitePackageJsonPath);
  } catch (error) {
    throw new Error(`Could not resolve attribution package '${packageName}' for rendered ${role} runtime module '${printableModuleId(moduleId)}'.`, { cause: error });
  }
  return await realpath(dirname(packageJsonPath));
}

export function classifyBundlerVirtualPackage(moduleId: string): "vite" | "rolldown" | undefined {
  if (moduleId.startsWith("vite:") || moduleId.startsWith("\0vite/") || moduleId.startsWith("\0vite:")) return "vite";
  if (moduleId.startsWith("rolldown:") || moduleId.startsWith("\0rolldown/") || moduleId.startsWith("\0rolldown:")) return "rolldown";
  return undefined;
}

export function resolveRolldownPackageJsonFromVite(vitePackageJsonPath: string): string {
  return createRequire(resolve(vitePackageJsonPath)).resolve("rolldown/package.json");
}

function addPackageNotice(
  packages: Map<string, BundledPackageNotice>,
  packageNotice: BundledPackageNotice,
  role: string,
): void {
  const key = `${packageNotice.name}\0${packageNotice.version}`;
  const existing = packages.get(key);
  if (existing !== undefined && JSON.stringify(existing) !== JSON.stringify(packageNotice)) {
    throw new Error(
      `Bundled ${role} dependency '${packageNotice.name}@${packageNotice.version}' resolves to conflicting attribution documents.`,
    );
  }
  packages.set(key, packageNotice);
}

function printableModuleId(moduleId: string): string {
  return moduleId.replaceAll("\0", "\\0");
}

function isProjectSource(projectRoot: string, modulePath: string): boolean {
  const traversal = relative(projectRoot, modulePath);
  if (traversal === "" || traversal === ".." || traversal.startsWith(`..${sep}`) || isAbsolute(traversal)) return false;
  return !traversal.split(sep).includes("node_modules");
}

async function findPackageRoot(modulePath: string): Promise<string | null> {
  let directory = dirname(modulePath);
  while (true) {
    const packageJsonPath = resolve(directory, "package.json");
    try {
      const entry = await lstat(packageJsonPath);
      if (!entry.isFile() || entry.isSymbolicLink()) {
        throw new Error(`Bundled dependency metadata '${packageJsonPath}' must be a regular file.`);
      }
      return await realpath(directory);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
    }
    const parent = dirname(directory);
    if (parent === directory) return null;
    directory = parent;
  }
}

async function readPackageNotice(packageRoot: string, role: string): Promise<BundledPackageNotice> {
  const packageJsonPath = resolve(packageRoot, "package.json");
  let value: Record<string, unknown>;
  try {
    value = JSON.parse(await readFile(packageJsonPath, "utf8")) as Record<string, unknown>;
  } catch (error) {
    throw new Error(`Could not read bundled ${role} dependency metadata '${packageJsonPath}'.`, { cause: error });
  }
  const name = value.name;
  const version = value.version;
  const license = value.license;
  if (typeof name !== "string" || !/^(?:@[a-z0-9][a-z0-9._-]*\/)?[a-z0-9][a-z0-9._-]*$/u.test(name)
    || typeof version !== "string" || version.length === 0 || version.length > 128 || /[\u0000-\u001f\u007f]/u.test(version)
    || license !== undefined && (typeof license !== "string" || license.length === 0 || license.length > 256 || /[\u0000-\u001f\u007f]/u.test(license))) {
    throw new Error(`Bundled ${role} dependency metadata '${packageJsonPath}' does not provide safe name/version/license attribution.`);
  }
  const files = await collectLegalFiles(packageRoot);
  if (!files.some((file) => LICENSE_FILE_NAME.test(file.path.split("/").at(-1)!))) {
    throw new Error(
      `Bundled ${role} dependency '${name}@${version}' has no LICENSE, LICENCE, or COPYING file; packaging cannot establish attribution.`,
    );
  }
  return {
    name,
    version,
    ...(license === undefined ? {} : { license }),
    files,
  };
}

async function collectLegalFiles(packageRoot: string): Promise<LegalFile[]> {
  const paths: string[] = [];
  let visitedEntries = 0;
  const visit = async (directory: string): Promise<void> => {
    const entries = await readdir(directory, { withFileTypes: true });
    entries.sort((left, right) => ordinal(left.name, right.name));
    for (const entry of entries) {
      visitedEntries += 1;
      if (visitedEntries > MAXIMUM_PACKAGE_ENTRIES) {
        throw new Error(`Bundled dependency at '${packageRoot}' exceeds the bounded attribution scan limit.`);
      }
      const path = resolve(directory, entry.name);
      if (entry.isSymbolicLink()) {
        if (LEGAL_FILE_NAME.test(entry.name)) throw new Error(`Bundled dependency legal file '${path}' must not be a symbolic link.`);
        continue;
      }
      if (entry.isDirectory()) {
        if (entry.name !== "node_modules" && entry.name !== ".git") await visit(path);
      } else if (entry.isFile() && LEGAL_FILE_NAME.test(entry.name)) {
        paths.push(path);
        if (paths.length > MAXIMUM_LEGAL_FILES) {
          throw new Error(`Bundled dependency at '${packageRoot}' has more than ${MAXIMUM_LEGAL_FILES} legal attribution files.`);
        }
      }
    }
  };
  await visit(packageRoot);
  paths.sort((left, right) => ordinal(relative(packageRoot, left), relative(packageRoot, right)));
  const files: LegalFile[] = [];
  let totalBytes = 0;
  for (const path of paths) {
    const bytes = await readFile(path);
    totalBytes += bytes.byteLength;
    if (totalBytes > MAXIMUM_LEGAL_BYTES) {
      throw new Error(`Bundled dependency at '${packageRoot}' legal attribution exceeds ${MAXIMUM_LEGAL_BYTES} bytes.`);
    }
    const text = bytes.toString("utf8").replaceAll("\r\n", "\n").replaceAll("\r", "\n").trimEnd();
    if (text.length === 0 || text.includes("\ufffd")) throw new Error(`Bundled dependency legal file '${path}' is empty or not UTF-8 text.`);
    files.push({ path: relative(packageRoot, path).split(sep).join("/"), text });
  }
  return files;
}

function ordinal(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}
