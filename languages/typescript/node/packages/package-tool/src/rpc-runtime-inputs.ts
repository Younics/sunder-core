import { createHash } from "node:crypto";
import { builtinModules } from "node:module";
import { lstat, open, realpath } from "node:fs/promises";
import { basename, dirname, extname, isAbsolute, relative, resolve } from "node:path";
import { build as esbuild, type BuildOptions, type Loader, type OnLoadResult, type OnResolveArgs, type OnResolveResult, type Plugin } from "esbuild";

// Explicit conditions suppress esbuild's implicit `module` condition. Standard
// import/require/default conditions still follow the syntax at each import site.
export const workerResolutionOptions = {
  conditions: ["node"], mainFields: ["main"],
  alias: { "@sunder/sdk": require.resolve("@sunder/sdk"), "@sunder/sdk/browser": require.resolve("@sunder/sdk/browser"), "@sunder/sdk/core": require.resolve("@sunder/sdk/core") },
} satisfies BuildOptions;

export interface OwnedRuntimeInputs {
  readonly fingerprint: string;
  readonly physicalFingerprint: string;
  readonly sourceFiles: readonly string[];
  plugin(revalidate: () => Promise<void>): Plugin;
  revalidate(): Promise<void>;
}

export async function captureOwnedRuntimeInputs(
  root: string, entry: string, modulePath: string, exportName: string, source: string, generated: ReadonlyMap<string, string>,
): Promise<OwnedRuntimeInputs> {
  const realRoot = await realpath(root);
  const realModule = await realpath(modulePath);
  const generatedRoots = [resolve(root, ".sunder/generated/rpc"), resolve(realRoot, ".sunder/generated/rpc")];
  const modules = new Map(generated);
  const inputs = new Map<string, { bytes: Buffer; real: string; loader: Loader }>();
  const configs = new Map<string, Buffer | undefined>();
  const edges = new Map<string, OnResolveResult>();
  const builtin = new Set(builtinModules.flatMap((name) => [name, `node:${name}`]));
  let totalBytes = 0;
  let configBytes = 0;
  const logicalPath = (path: string): string | undefined => {
    const normalized = resolve(path);
    for (const base of generatedRoots) if (normalized.startsWith(`${base}/`) || normalized.startsWith(`${base}\\`)) return normalized.slice(base.length + 1).replaceAll("\\", "/");
    return undefined;
  };
  const key = (args: OnResolveArgs): string => JSON.stringify([args.path, args.importer, args.namespace, args.resolveDir, args.kind]);
  const configParents = async (path: string): Promise<void> => {
    let directory = dirname(path);
    for (let depth = 0; ; depth++) {
      if (depth > 64 || configs.size > 4096) throw new Error("rpc-authoring.source-limit-exceeded: Runtime resolution configuration budget exceeded.");
      const config = resolve(directory, "package.json");
      if (!configs.has(config) && logicalPath(config) === undefined) {
        try {
          const bytes = await readRuntimeFile(config, 1_048_576);
          const prior = configs.get(config);
          if (configs.has(config)) {
            if (!prior?.equals(bytes)) throw new Error("RPC runtime resolution configuration changed during capture.");
          } else {
            configBytes += bytes.length;
            if (configBytes > 16 * 1024 * 1024) throw new Error("rpc-authoring.source-limit-exceeded: Runtime resolution configuration exceeds 16 MiB.");
            configs.set(config, bytes);
          }
        }
        catch (error) { if ((error as NodeJS.ErrnoException).code === "ENOENT") configs.set(config, undefined); else throw error; }
        if (configs.size > 4096) throw new Error("rpc-authoring.source-limit-exceeded: Runtime resolution configuration budget exceeded.");
      }
      const parent = dirname(directory);
      if (parent === directory) return;
      directory = parent;
    }
  };
  const loadGenerated = (name: string): OnLoadResult => ({ contents: modules.get(name)!, loader: name.endsWith(".ts") ? "ts" : "js", resolveDir: dirname(resolve(generatedRoots[0]!, name)) });
  const generatedResolution = (args: OnResolveArgs): OnResolveResult | undefined => {
    let name: string | undefined;
    if (args.path === "sunder-owned-rpc:adapter") name = "adapter.js";
    else if (args.path === "#sunder/rpc") name = "imports";
    else if (args.path.startsWith(".") || isAbsolute(args.path)) name = logicalPath(resolve(args.resolveDir, args.path));
    if (name === undefined) return undefined;
    const candidates = name.endsWith(".js") ? [name, name.replace(/\.js$/u, ".ts")] : [name, `${name}.ts`, `${name}.js`];
    const selected = candidates.find((candidate) => modules.has(candidate) && !candidate.endsWith(".d.ts"));
    if (selected === undefined) throw new Error(`Missing current generated RPC runtime module '${name}'; stale disk output is not an input.`);
    return { path: selected, namespace: "sunder-owned-rpc" };
  };
  const generatedImportAlias = async (args: OnResolveArgs, capture: boolean): Promise<OnResolveResult | undefined> => {
    if (!args.path.startsWith("#") || args.path === "#" || args.path.startsWith("#/")) return undefined;
    if (capture) await configParents(resolve(args.resolveDir, "__rpc_import_scope__"));
    let directory = resolve(args.resolveDir);
    while (basename(directory) !== "node_modules") {
      const bytes = configs.get(resolve(directory, "package.json"));
      if (bytes !== undefined) {
        const imports: unknown = (JSON.parse(bytes.toString("utf8")) as { imports?: unknown }).imports;
        if (!imports || typeof imports !== "object" || Array.isArray(imports)) return undefined;
        const conditions = new Set(["node", "default", args.kind === "require-call" || args.kind === "require-resolve" ? "require" : "import"]);
        const target = packageImportTarget(args.path, imports as Record<string, unknown>, conditions);
        if (typeof target !== "string" || !target.startsWith("./")) return undefined;
        const path = resolve(directory, target);
        if (logicalPath(path) === undefined) return undefined;
        if (target.includes("%") || target.includes("\\") || target.slice(2).split("/").some((segment) => segment === "." || segment === ".." || segment === "node_modules")) {
          throw new Error("rpc-authoring.unsupported-source: Generated package-import targets must be canonical local paths.");
        }
        return generatedResolution({ ...args, path });
      }
      const parent = dirname(directory);
      if (parent === directory) break;
      directory = parent;
    }
    return undefined;
  };
  function plugin(mode: "capture" | "verify" | "replay", validate?: () => Promise<void>): Plugin {
    return {
      name: "sunder-owned-rpc",
      setup(build) {
        if (validate) build.onStart(validate);
        build.onResolve({ filter: /.*/ }, async (args) => {
          if (args.pluginData === bypass) return undefined;
          const identity = key(args);
          if (mode === "replay") {
            const edge = edges.get(identity);
            if (!edge) throw new Error("RPC bundle requested an uncaptured runtime resolution.");
            return edge;
          }
          const virtual = generatedResolution(args) ?? await generatedImportAlias(args, mode === "capture");
          let resolved = virtual ?? await build.resolve(args.path, { importer: args.importer, namespace: args.namespace,
            resolveDir: args.resolveDir, kind: args.kind, pluginData: bypass });
          if (resolved.errors?.length) return resolved;
          // Aliases, package exports and native resolution may enter an owned
          // root indirectly. Such files are still exclusively overlay inputs.
          if (!resolved.external && resolved.path && isAbsolute(resolved.path)) {
            const owned = generatedResolution({ ...args, path: resolved.path });
            if (owned) resolved = { ...resolved, ...owned };
          }
          const edge: OnResolveResult = { path: resolved.path, namespace: resolved.namespace ?? "file", external: resolved.external ?? false,
            suffix: resolved.suffix ?? "", sideEffects: resolved.sideEffects ?? true };
          if (edge.external && !builtin.has(edge.path!)) throw new Error(`rpc-authoring.unsupported-source: Unbundled non-builtin runtime import '${args.path}'.`);
          if (edges.size >= 8192 && !edges.has(identity)) throw new Error("rpc-authoring.source-limit-exceeded: Runtime graph exceeds 8192 import edges.");
          if (mode === "capture") edges.set(identity, edge);
          else if (JSON.stringify(edges.get(identity)) !== JSON.stringify(edge)) throw new Error("rpc-authoring.unsupported-source: RPC runtime resolution input set changed; retry generation.");
          if (mode === "capture") {
            if (args.importer && args.namespace !== "sunder-owned-rpc") await configParents(args.importer);
            if (!edge.external && edge.namespace === "file") await configParents(edge.path!);
          }
          return edge;
        });
        build.onLoad({ filter: /.*/, namespace: "sunder-owned-rpc" }, (args) => loadGenerated(args.path));
        build.onLoad({ filter: /.*/, namespace: "file" }, async (args) => {
          if (logicalPath(args.path) !== undefined) throw new Error("Owned generated modules must not be read as physical runtime inputs.");
          let input = inputs.get(args.path);
          if (!input && mode === "capture") {
            if (inputs.size >= 512) throw new Error("rpc-authoring.source-limit-exceeded: Runtime graph exceeds 512 files.");
            const loader = loaders[extname(args.path)];
            if (!loader) throw new Error(`rpc-authoring.unsupported-source: Unsupported bundled runtime file '${args.path}'.`);
            const bytes = await readRuntimeFile(args.path, 16 * 1024 * 1024);
            totalBytes += bytes.length;
            if (totalBytes > 64 * 1024 * 1024) throw new Error("rpc-authoring.source-limit-exceeded: Runtime graph exceeds 64 MiB.");
            input = { bytes, real: await realpath(args.path), loader };
            inputs.set(args.path, input);
            if (inputs.size > 512) throw new Error("rpc-authoring.source-limit-exceeded: Runtime graph exceeds 512 files.");
          }
          if (!input) throw new Error("RPC bundle requested an uncaptured runtime file.");
          return { contents: input.real === realModule
            ? `${source}\nimport { bind as $sunderOwnedRpcBind } from "sunder-owned-rpc:adapter";\n$sunderOwnedRpcBind(${exportName});\n`
            : input.bytes, loader: input.loader, resolveDir: dirname(args.path) };
        });
        if (validate) build.onEnd(async () => { await validate(); });
      },
    };
  }
  const scan = async (mode: "capture" | "verify"): Promise<void> => {
    await esbuild({ absWorkingDir: root, entryPoints: [entry], bundle: true, platform: "node", format: "cjs", target: "node24",
      ...workerResolutionOptions, tsconfigRaw: {}, write: false, metafile: true, logLevel: "silent", plugins: [plugin(mode)] });
  };
  await scan("capture");
  const revalidate = async (): Promise<void> => {
    const inputEntries = [...inputs];
    for (let offset = 0; offset < inputEntries.length; offset += 16) {
      await Promise.all(inputEntries.slice(offset, offset + 16).map(async ([path, input]) => {
        if (await realpath(path) !== input.real || !(await readRuntimeFile(path, 16 * 1024 * 1024)).equals(input.bytes)) {
          throw new Error(`rpc-authoring.unsupported-source: RPC runtime implementation input changed (${path}); retry generation.`);
        }
      }));
    }
    const configEntries = [...configs];
    for (let offset = 0; offset < configEntries.length; offset += 16) {
      await Promise.all(configEntries.slice(offset, offset + 16).map(async ([path, expected]) => {
        let actual: Buffer | undefined;
        try { actual = await readRuntimeFile(path, 1_048_576); }
        catch (error) { if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error; }
        if (expected === undefined ? actual !== undefined : actual === undefined || !expected.equals(actual)) {
          throw new Error(`rpc-authoring.unsupported-source: RPC runtime resolution configuration changed (${path}); retry generation.`);
        }
      }));
    }
    // A fresh resolver checks negative candidates and export/import selection;
    // captured bytes are used even for this non-executing validation build.
    await scan("verify");
  };
  await revalidate();
  const logicalInput = (path: string): string => {
    if (path.startsWith(`${realRoot}/`)) return relative(realRoot, path).replaceAll("\\", "/");
    let directory = dirname(path);
    while (true) {
      const metadata = configs.get(resolve(directory, "package.json"));
      const name: unknown = metadata && (JSON.parse(metadata.toString("utf8")) as { name?: unknown }).name;
      if (typeof name === "string") return `${name}/${relative(directory, path).replaceAll("\\", "/")}`;
      const parent = dirname(directory);
      if (parent === directory) return `external/${basename(path)}`;
      directory = parent;
    }
  };
  return {
    physicalFingerprint: createHash("sha256").update(JSON.stringify({
      files: [...inputs].map(([path, input]) => [path, input.real, input.loader, createHash("sha256").update(input.bytes).digest("hex")]).sort(),
      configs: [...configs].map(([path, bytes]) => [path, bytes === undefined ? null : createHash("sha256").update(bytes).digest("hex")]).sort(),
      edges: [...edges].sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0),
    })).digest("hex"),
    fingerprint: createHash("sha256").update(JSON.stringify({
      files: [...inputs].map(([path, { bytes }]) => [logicalInput(path), createHash("sha256").update(bytes).digest("hex")]).sort(),
      configs: [...configs].map(([path, bytes]) => [logicalInput(path), bytes === undefined ? null : createHash("sha256").update(bytes).digest("hex")]).sort(),
    })).digest("hex"),
    sourceFiles: [...inputs.keys()].filter((path) => path.startsWith(`${realRoot}/`) && !path.includes("/node_modules/") && /\.[cm]?[jt]sx?$/u.test(path))
      .map((path) => resolve(root, path.slice(realRoot.length + 1)).replaceAll("\\", "/")),
    plugin: (validate) => plugin("replay", validate), revalidate,
  };
}

const bypass = Symbol("esbuild-native-resolution");
const loaders: Readonly<Record<string, Loader>> = { ".js": "js", ".mjs": "js", ".cjs": "js", ".ts": "ts", ".tsx": "tsx", ".jsx": "jsx", ".json": "json" };

// This is only package-import target selection, not a second file resolver.
// Non-owned targets still go through esbuild. Conditions retain JSON key order.
function packageImportTarget(specifier: string, imports: Record<string, unknown>, conditions: ReadonlySet<string>, seen = new Set<string>()): string | null | undefined {
  if (seen.has(specifier) || seen.size >= 64 || Object.keys(imports).length > 4096) throw new Error("rpc-authoring.unsupported-source: Package-import alias graph is cyclic or exceeds its bounds.");
  seen.add(specifier);
  let key = Object.hasOwn(imports, specifier) ? specifier : undefined;
  let matched: string | undefined;
  if (key === undefined) {
    const candidates = Object.keys(imports).filter((name) => name.includes("*") && name.indexOf("*") === name.lastIndexOf("*"))
      .sort((a, b) => b.indexOf("*") - a.indexOf("*") || b.length - a.length);
    key = candidates.find((name) => {
      const index = name.indexOf("*");
      return specifier.startsWith(name.slice(0, index)) && specifier.endsWith(name.slice(index + 1)) && specifier.length >= name.length;
    });
    if (key !== undefined) matched = specifier.slice(key.indexOf("*"), specifier.length - (key.length - key.indexOf("*") - 1));
  }
  if (key === undefined) return undefined;
  const select = (value: unknown, depth = 0): string | null | undefined => {
    if (depth > 64) throw new Error("rpc-authoring.source-limit-exceeded: Package-import conditions exceed depth 64.");
    if (typeof value === "string") return matched === undefined ? value : value.replaceAll("*", matched);
    if (value === null) return null;
    if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error("rpc-authoring.unsupported-source: Package-import aliases support strings, null and condition records, not arrays or dynamic targets.");
    for (const [condition, target] of Object.entries(value)) {
      if (/^(0|[1-9][0-9]*)$/u.test(condition)) throw new Error("rpc-authoring.unsupported-source: Numeric package-import conditions are invalid.");
      if (conditions.has(condition)) {
        const selected = select(target, depth + 1);
        if (selected !== undefined) return selected;
      }
    }
    return undefined;
  };
  const target = select(imports[key]);
  return target?.startsWith("#") ? packageImportTarget(target, imports, conditions, seen) : target;
}

async function readRuntimeFile(path: string, maximum: number): Promise<Buffer> {
  const handle = await open(path, "r");
  try {
    const before = await handle.stat({ bigint: true });
    if (!before.isFile() || before.size > BigInt(maximum)) throw new Error(`rpc-authoring.source-limit-exceeded: Runtime input must be a regular file of at most ${maximum} bytes.`);
    const bytes = Buffer.alloc(Number(before.size));
    let offset = 0;
    while (offset < bytes.length) {
      const { bytesRead } = await handle.read(bytes, offset, bytes.length - offset, offset);
      if (!bytesRead) throw new Error("RPC runtime input changed during capture.");
      offset += bytesRead;
    }
    const after = await handle.stat({ bigint: true });
    const current = await lstat(path, { bigint: true });
    for (const value of [after, current]) {
      if (before.dev !== value.dev || before.ino !== value.ino || before.size !== value.size || before.mtimeNs !== value.mtimeNs || before.ctimeNs !== value.ctimeNs) {
        throw new Error("RPC runtime input changed during capture.");
      }
    }
    return bytes;
  } finally { await handle.close(); }
}
