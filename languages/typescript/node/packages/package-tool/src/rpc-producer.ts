import { createHash } from "node:crypto";
import { closeSync, fstatSync, openSync, readSync, realpathSync, statSync } from "node:fs";

// This runs synchronously during producer module initialization, not on first
// generation. A long-lived process must never bless newly written implementation
// bytes while continuing to execute the old modules in require.cache.
export function captureRpcProducer(roots: readonly string[]): { fingerprint: string; files: readonly string[]; revalidate(): void } {
  const files = new Map<string, { bytes: Buffer; real: string }>();
  let total = 0;
  const visit = (path: string): void => {
    if (files.has(path)) return;
    if (files.size >= 256) throw new Error("RPC producer exceeds 256 implementation files.");
    const bytes = readProducerFile(path);
    total += bytes.length;
    if (total > 64 * 1024 * 1024) throw new Error("RPC producer exceeds 64 MiB of implementation bytes.");
    files.set(path, { bytes, real: realpathSync(path) });
    for (const child of require.cache[path]?.children ?? []) visit(child.filename);
  };
  for (const path of roots) visit(path);
  const hashes = [...files.values()].map(({ bytes }) => createHash("sha256").update(bytes).digest("hex")).sort();
  return {
    files: Object.freeze([...files.keys()]),
    fingerprint: createHash("sha256").update(JSON.stringify(hashes)).digest("hex"),
    revalidate() {
      for (const [path, expected] of files) {
        try {
          if (realpathSync(path) === expected.real && readProducerFile(path).equals(expected.bytes)) continue;
        } catch { /* Missing or replaced producer code also requires a restart. */ }
        throw new Error(`rpc-authoring.unsupported-source: RPC producer implementation changed (${path}); restart the package tool. Cached code cannot be combined with new files.`);
      }
    },
  };
}

function readProducerFile(path: string): Buffer {
  const handle = openSync(path, "r");
  try {
    const before = fstatSync(handle, { bigint: true });
    if (!before.isFile() || before.size > 16n * 1024n * 1024n) throw new Error("RPC producer implementation must be a regular file of at most 16 MiB.");
    const bytes = Buffer.alloc(Number(before.size));
    let offset = 0;
    while (offset < bytes.length) {
      const count = readSync(handle, bytes, offset, bytes.length - offset, offset);
      if (count === 0) throw new Error("RPC producer changed during capture.");
      offset += count;
    }
    const after = fstatSync(handle, { bigint: true });
    const current = statSync(path, { bigint: true });
    for (const value of [after, current]) {
      if (before.dev !== value.dev || before.ino !== value.ino || before.size !== value.size
        || before.mtimeNs !== value.mtimeNs || before.ctimeNs !== value.ctimeNs) throw new Error("RPC producer changed during capture.");
    }
    return bytes;
  } finally { closeSync(handle); }
}
