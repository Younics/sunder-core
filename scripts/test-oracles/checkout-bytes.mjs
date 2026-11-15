import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");

// Inspect actual source inputs, not just Git's checkout filters. Do not parse or
// rewrite JSON: invalid conformance documents and meaningful local edits remain
// owned by their existing tests. Ordinary app settings and NuGet-generated locks
// do not acquire a canonical-byte requirement just because they are JSON.
export function verifyCheckoutBytes(root = repositoryRoot) {
  const paths = [...new Set(execFileSync("git", ["ls-files", "--cached", "--others", "--exclude-standard", "-z", "--", "*.json"], {
    cwd: root, encoding: "utf8",
  }).split("\0").filter(path => path && isCanonicalInput(path)))].sort();
  const issues = [];
  let checked = 0;
  for (const path of paths) {
    let bytes;
    try { bytes = readFileSync(resolve(root, path)); }
    catch (error) {
      if (error.code === "ENOENT") continue; // Respect an authored source deletion.
      throw error;
    }
    checked++;
    const reasons = [];
    if (bytes.includes(13)) reasons.push("CR/CRLF; expected LF");
    if (bytes.subarray(0, 3).equals(Buffer.from([239, 187, 191]))) reasons.push("UTF-8 BOM");
    if (reasons.length > 0) issues.push(`${path}: ${reasons.join(", ")}`);
  }
  if (issues.length > 0) throw new Error(`Canonical JSON checkout bytes are invalid (${issues.length} files):\n${issues.join("\n")}\n`
    + "No files were changed. Review newline/encoding-only drift together; preserve authored changes and original fixture bytes. "
    + "Do not regenerate expectations or normalize inputs while testing/packing.");
  return checked;
}

function isCanonicalInput(path) {
  if (path.endsWith("/packages.lock.json")) return false;
  return /^(?:specs|compatibility|eng\/(?:platform-contracts|compatibility))\//u.test(path)
    || /(?:^|\/)sunder-contracts\.(?:lock|owned)\.json$/u.test(path)
    || path.endsWith(".rpc.json")
    || /(?:^|\/)(?:test|tests|template|template-react-node|templates)\//u.test(path);
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try { console.log(`Verified actual checkout bytes for ${verifyCheckoutBytes()} JSON inputs.`); }
  catch (error) { console.error(error.message); process.exitCode = 1; }
}
