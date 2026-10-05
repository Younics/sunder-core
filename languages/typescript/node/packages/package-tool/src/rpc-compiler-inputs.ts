import { createHash } from "node:crypto";
import { closeSync, constants, fstatSync, lstatSync, openSync, readSync, realpathSync, type BigIntStats } from "node:fs";
import { relative, resolve, sep } from "node:path";

const MiB = 1024 * 1024;
const policies = {
  source: { file: 4 * MiB, total: 4 * MiB, files: 512, normalize: true },
  declaration: { file: 4 * MiB, total: 32 * MiB, files: 512, normalize: false },
  dependency: { file: 4 * MiB, total: 16 * MiB, files: 512, normalize: false },
  configuration: { file: MiB, total: 16 * MiB, files: 4096, normalize: false },
} as const;
type Kind = keyof typeof policies;
interface Snapshot { readonly real: string; readonly text: string; readonly sha256: string; readonly normalizedBytes: number; readonly kind: Kind }

/** A physical compiler read is budgeted before allocation and before TS parsing. */
export function captureRpcCompilerInputs(root: string, canonicalRoot: string) {
  const paths = new Map<string, Snapshot | undefined>();
  const files = new Map<string, Snapshot>();
  const links = new Map<string, string>();
  const totals: Record<Kind, number> = { source: 0, declaration: 0, dependency: 0, configuration: 0 };
  const counts = { ...totals };
  let failure: unknown;
  const inside = (base: string, path: string): boolean => {
    const part = relative(base, path);
    return part !== ".." && !part.startsWith(`..${sep}`) && !part.startsWith(sep) && !/^[A-Za-z]:/u.test(part);
  };
  const authored = (path: string): boolean => (inside(root, path) || inside(canonicalRoot, path))
    && !path.replaceAll("\\", "/").split("/").includes("node_modules");
  const inspect = (path: string, resolutionDirectory = false): BigIntStats => {
    if (authored(path)) {
      const base = inside(root, path) ? root : canonicalRoot;
      let current = base;
      for (const segment of relative(base, path).split(sep).filter(Boolean)) {
        current = resolve(current, segment);
        const entry = lstatSync(current, { bigint: true });
        if (entry.isSymbolicLink() || (current !== path && !entry.isDirectory())) throw new Error("rpc-authoring.unsupported-source: Authored compiler inputs must not pass through symbolic links.");
      }
    }
    const stat = lstatSync(path, { bigint: true });
    if ((!stat.isFile() && !(resolutionDirectory && stat.isDirectory())) || stat.isSymbolicLink()) throw new Error(`rpc-authoring.unsupported-source: Compiler input must be a regular, non-link file (${path}).`);
    return stat;
  };
  const read = (path: string, kind: Kind, remaining: number): Snapshot => {
    const policy = policies[kind];
    const before = inspect(path);
    // CRLF normalization can remove at most half of raw source bytes. Even in
    // that best case, an oversized sparse input cannot fit the remaining budget.
    const allowed = Math.min(policy.file, remaining);
    if (before.size > BigInt(allowed * (policy.normalize ? 2 : 1))) limit(path);
    const real = realpathSync(path);
    const handle = openSync(path, constants.O_RDONLY | (process.platform === "win32" ? 0 : constants.O_NOFOLLOW));
    try {
      if (!stable(before, fstatSync(handle, { bigint: true }))) changed(path);
      const decoder = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true });
      const buffer = Buffer.alloc(64 * 1024);
      const hash = createHash("sha256");
      const parts: string[] = [];
      let bytes = 0;
      let position = 0;
      let pending = "";
      const append = (value: string, final = false): void => {
        value = pending + value;
        pending = policy.normalize && !final && value.endsWith("\r") ? "\r" : "";
        if (pending) value = value.slice(0, -1);
        if (policy.normalize) value = value.replaceAll("\r\n", "\n");
        bytes += Buffer.byteLength(value, "utf8");
        if (bytes > allowed) limit(path);
        parts.push(value);
      };
      while (position < Number(before.size)) {
        const count = readSync(handle, buffer, 0, Math.min(buffer.length, Number(before.size) - position), position);
        if (!count) changed(path);
        position += count;
        hash.update(buffer.subarray(0, count));
        append(decoder.decode(buffer.subarray(0, count), { stream: true }));
      }
      append(decoder.decode(), true);
      if (!stable(before, fstatSync(handle, { bigint: true })) || !stable(before, inspect(path)) || realpathSync(path) !== real) changed(path);
      return { real, text: parts.join(""), sha256: hash.digest("hex"), normalizedBytes: bytes, kind };
    } finally { closeSync(handle); }
  };
  return {
    readFile(path: string): string | undefined {
      if (failure) throw failure;
      path = resolve(path);
      if (paths.has(path)) return paths.get(path)?.text;
      let existed = false;
      try {
        if (paths.size >= 4096) limit(path);
        inspect(path);
        existed = true;
        const real = realpathSync(path);
        const cached = files.get(real);
        if (cached) { paths.set(path, cached); return cached.text; }
        const kind: Kind = path.endsWith(".json") ? "configuration" : authored(path) ? "source"
          : /\.d\.[cm]?ts$/u.test(path) ? "declaration" : "dependency";
        if (counts[kind] >= policies[kind].files) limit(path);
        const snapshot = read(path, kind, policies[kind].total - totals[kind]);
        totals[kind] += snapshot.normalizedBytes;
        counts[kind]++;
        files.set(real, snapshot);
        paths.set(path, snapshot);
        return snapshot.text;
      } catch (error) {
        if (existed || (error as NodeJS.ErrnoException).code !== "ENOENT" && (error as NodeJS.ErrnoException).code !== "ENOTDIR") { failure = error; throw error; }
        paths.set(path, undefined);
        return undefined;
      }
    },
    realpath(path: string): string {
      try {
        path = resolve(path);
        // Type-reference resolution canonicalizes its package directory as well
        // as individual files. Actual reads still require regular files above.
        inspect(path, true);
        const real = realpathSync(path);
        if (links.size >= 4096 && !links.has(path)) limit(path);
        links.set(path, real);
        return real;
      } catch (error) { failure = error; throw error; }
    },
    assertValid(): void {
      // TypeScript can convert host read exceptions to diagnostics. Resource and
      // path-safety failures must still abort the prepass, not reach bundling.
      if (failure) throw failure;
    },
    fingerprint(): string {
      return createHash("sha256").update(JSON.stringify({
        paths: [...paths].map(([path, value]) => [path, value ? [value.real, value.sha256, value.kind] : null]).sort(),
        links: [...links].sort(),
      })).digest("hex");
    },
    revalidate(): void {
      for (const [path, expected] of paths) {
        try {
          if (expected === undefined) {
            try { lstatSync(path); } catch (error) { if (["ENOENT", "ENOTDIR"].includes((error as NodeJS.ErrnoException).code ?? "")) continue; throw error; }
          } else {
            const actual = read(path, expected.kind, policies[expected.kind].file);
            if (actual.sha256 === expected.sha256 && actual.real === expected.real) continue;
          }
        } catch (error) { throw new Error(`rpc-authoring.unsupported-source: RPC compiler input changed (${path}); retry generation.`, { cause: error }); }
        changed(path);
      }
      for (const [path, real] of links) if (realpathSync(path) !== real) changed(path);
    },
  };
}

function stable(a: BigIntStats, b: BigIntStats): boolean {
  return a.dev === b.dev && a.ino === b.ino && a.mode === b.mode && a.nlink === b.nlink && a.size === b.size && a.mtimeNs === b.mtimeNs && a.ctimeNs === b.ctimeNs;
}
function limit(path: string): never { throw new Error(`rpc-authoring.source-limit-exceeded: Compiler input byte/read budget exceeded before parsing (${path}).`); }
function changed(path: string): never { throw new Error(`rpc-authoring.unsupported-source: RPC compiler input changed (${path}); retry generation.`); }
