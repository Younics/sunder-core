import { createHash } from "node:crypto";
import { closeSync, lstatSync, openSync, readFileSync, readSync, realpathSync, statSync } from "node:fs";
import { dirname, isAbsolute, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { comparePublicApi, maximumSnapshotBytes } from "./public-api-comparison.mjs";

export const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
export const manifestPath = "eng/compatibility/supported-releases.json";
export const maximumManifestBytes = 1024 * 1024;
const schema = JSON.parse(readFileSync(resolve(repositoryRoot, "eng/compatibility/supported-releases.schema.json"), "utf8"));
const corpusKinds = ["old-package-new-host", "old-source-new-sdk", "new-feature-old-host"];

export function parseManifest(bytes) {
  const manifest = parseBoundedJson(bytes);
  validateManifest(manifest);
  return manifest;
}

export function validateManifest(manifest) {
  validateSchema(manifest, schema, "$manifest");
  if (manifest.status === "UNFROZEN") {
    if (manifest.releaseStatus !== "UNRELEASED" || manifest.releases.length !== 0) throw new Error("UNFROZEN must be UNRELEASED with no released references.");
  } else if (manifest.releaseStatus !== "RELEASED" || manifest.releases.length === 0) {
    throw new Error("FROZEN requires RELEASED status and retained releases.");
  }
  uniqueIds(manifest.releases, "release");
  if (new Set(manifest.releases.map(release => release.version)).size !== manifest.releases.length) throw new Error("Duplicate release version.");
  for (const release of manifest.releases) {
    validateReleaseReference(release);
  }
  return manifest;
}

// A retained, tested candidate uses this same immutable reference shape without
// claiming that it has been published or changing the source ledger's status.
export function validateReleaseReference(release) {
  validateSchema(release, { $ref: "#/$defs/release" }, "$reference");
  uniqueIds(release.artifacts, "artifact");
  uniqueIds(release.surfaces, "surface");
  uniqueIds(release.corpus, "corpus");
  validateSource(release.source);
  const artifacts = new Map(release.artifacts.map(artifact => [artifact.id, artifact]));
  const retainedPaths = new Set();
  for (const artifact of release.artifacts) {
    validateSource(artifact.source);
    validateUrl(artifact.retainedIdentity);
    retainedPath(artifact.path);
  }
  for (const surface of release.surfaces) {
    retainedPath(surface.snapshot.path);
    safeRelativePath(surface.currentPath);
    if (surface.currentPath.startsWith("eng/compatibility/releases/")) throw new Error("Current surface cannot point into retained history.");
    const artifact = artifacts.get(surface.artifactId);
    if (!artifact) throw new Error(`Surface ${surface.id} has no retained artifact.`);
    if (canonical(artifact.source) !== canonical(release.source)) throw new Error(`Surface ${surface.id} artifact source differs from release source.`);
  }
  for (const kind of corpusKinds) {
    if (!release.corpus.some(entry => entry.kind === kind)) throw new Error(`Release ${release.id} lacks ${kind} corpus inputs.`);
  }
  for (const entry of release.corpus) {
    retainedPath(entry.recipe.path);
    for (const id of entry.artifactIds) if (!artifacts.has(id)) throw new Error(`Corpus ${entry.id} has no retained artifact ${id}.`);
    const kinds = entry.artifactIds.map(id => artifacts.get(id).kind);
    const required = entry.kind === "old-package-new-host" ? ["package"]
      : entry.kind === "old-source-new-sdk" ? ["source"] : ["host"];
    for (const kind of required) if (!kinds.includes(kind)) throw new Error(`Corpus ${entry.id} needs a retained ${kind} input.`);
    if (entry.kind === "old-source-new-sdk" && !kinds.some(kind => kind === "nuget" || kind === "npm")) {
      throw new Error(`Corpus ${entry.id} needs its original SDK/tooling artifact inputs.`);
    }
  }
  function retainedPath(path) {
    safeRelativePath(path);
    if (!path.startsWith(`eng/compatibility/releases/${release.id}/`)) throw new Error(`Retained path must be under eng/compatibility/releases/${release.id}/: ${path}`);
    if (retainedPaths.has(path)) throw new Error(`Duplicate retained path: ${path}`);
    retainedPaths.add(path);
  }
}

// The trusted base comes from a protected Git base or a separately retained manifest,
// never from the same editable candidate document. No release can be reset/retired here.
export function compareReleaseHistory(current, base) {
  validateManifest(current);
  if (base === null) {
    if (current.status !== "UNFROZEN") throw new Error("Initial release freeze requires an explicit pre-freeze base manifest.");
    return;
  }
  validateManifest(base);
  if (base.status === "FROZEN" && current.status !== "FROZEN") throw new Error("Released baseline cannot be reset to UNFROZEN/UNRELEASED.");
  const currentReleases = new Map(current.releases.map(release => [release.id, release]));
  for (const released of base.releases) {
    if (!currentReleases.has(released.id)) throw new Error(`Released reference removed: ${released.id}`);
    if (canonical(currentReleases.get(released.id)) !== canonical(released)) throw new Error(`Immutable released reference changed: ${released.id}`);
  }
}

export function checkSupportedReleases({ current, base, currentRoot, retainedRoot = currentRoot, environment = process.env }) {
  compareReleaseHistory(current, base);
  const updates = ["SUNDER_UPDATE_PUBLIC_API_ROOT", "SUNDER_UPDATE_PUBLIC_API_ASSEMBLIES", "SUNDER_UPDATE_PUBLIC_API_FIDELITY_ROOT"];
  if ((current.status === "FROZEN" || environment.CI || environment.GITHUB_ACTIONS) && updates.some(name => environment[name]?.trim())) {
    throw new Error("API update variables cannot bypass CI or frozen-release comparison.");
  }
  let surfaceCount = 0;
  for (const release of current.releases) {
    surfaceCount += checkReleaseReference(release, currentRoot, retainedRoot);
  }
  return { status: current.status, releaseStatus: current.releaseStatus, releases: current.releases.length, surfaces: surfaceCount };
}

export function checkReleaseReference(release, currentRoot, retainedRoot, exact = false) {
  validateReleaseReference(release);
  const errors = [];
  for (const artifact of release.artifacts) verifyRetainedFile(retainedRoot, artifact);
  for (const corpus of release.corpus) verifyRetainedFile(retainedRoot, corpus.recipe);
  for (const surface of release.surfaces) {
    const releasedPath = verifyRetainedFile(retainedRoot, surface.snapshot);
    const currentPath = containedFile(currentRoot, surface.currentPath);
    if (realpathSync(releasedPath) === realpathSync(currentPath)) throw new Error("Released and editable current snapshot must be distinct files.");
    const oldBytes = readBounded(releasedPath, maximumSnapshotBytes);
    const newBytes = readBounded(currentPath, maximumSnapshotBytes);
    const changes = !exact && surface.format === "dotnet-public-api-v2"
      ? comparePublicApi(decode(oldBytes), decode(newBytes))
      : oldBytes.equals(newBytes) ? [] : [exact ? "Candidate exact-byte surface changed after qualification."
        : "Released exact-byte surface changed (no semantic comparator declared)."];
    errors.push(...changes.map(change => `${release.id}/${surface.id}: ${change}`));
  }
  if (errors.length) throw new Error(`Supported release compatibility failed:\n${errors.join("\n")}`);
  return release.surfaces.length;
}

export function readBounded(path, maximum) {
  const stat = statSync(path);
  if (!stat.isFile() || stat.size > maximum) throw new Error(`File is missing, not regular or oversized: ${path}`);
  const bytes = readFileSync(path);
  if (bytes.length > maximum) throw new Error(`File exceeds size bound: ${path}`);
  return bytes;
}

export function verifyRetainedFile(root, record) {
  const path = containedFile(root, record.path);
  if (statSync(path).size !== record.size) throw new Error(`Retained size mismatch: ${record.path}`);
  const hash = createHash("sha256");
  const buffer = Buffer.alloc(1024 * 1024);
  const descriptor = openSync(path, "r");
  let count = 0;
  try {
    let length;
    while ((length = readSync(descriptor, buffer)) > 0) {
      count += length;
      if (count > record.size) throw new Error(`Retained size changed: ${record.path}`);
      hash.update(buffer.subarray(0, length));
    }
  } finally { closeSync(descriptor); }
  if (count !== record.size || hash.digest("hex") !== record.sha256) throw new Error(`Retained SHA-256 mismatch: ${record.path}`);
  return path;
}

function safeRelativePath(path) {
  if (path.split("/").some(part => part === "." || part === ".." || part === "")) throw new Error(`Unsafe relative path: ${path}`);
}

export function containedFile(root, path) {
  safeRelativePath(path);
  const absoluteRoot = realpathSync(root);
  const absolute = resolve(absoluteRoot, path);
  let parent = absoluteRoot;
  for (const part of path.split("/")) {
    parent = resolve(parent, part);
    if (lstatSync(parent).isSymbolicLink()) throw new Error(`Symlink not allowed in compatibility inputs: ${path}`);
  }
  const relation = relative(absoluteRoot, realpathSync(absolute));
  if (relation.startsWith(`..${sep}`) || relation === ".." || isAbsolute(relation) || !statSync(absolute).isFile()) throw new Error(`Compatibility input escapes root or is not regular: ${path}`);
  return absolute;
}

function validateSource(source) {
  validateUrl(source.repository);
  if (/^0+$/u.test(source.commit)) throw new Error("Source commit cannot be a zero placeholder.");
}

function validateUrl(value) {
  const url = new URL(value);
  if (url.protocol !== "https:" || url.username || url.password || url.hash || url.search) throw new Error("Retention/source identity must be credential-free HTTPS without query/fragment.");
}

function decode(bytes) { return new TextDecoder("utf-8", { fatal: true }).decode(bytes); }
function uniqueIds(values, kind) {
  if (new Set(values.map(value => value.id)).size !== values.length) throw new Error(`Duplicate ${kind} identity.`);
}
export function canonical(value) {
  if (Array.isArray(value)) return `[${value.map(canonical).join(",")}]`;
  if (value !== null && typeof value === "object") return `{${Object.keys(value).sort().map(key => `${JSON.stringify(key)}:${canonical(value[key])}`).join(",")}}`;
  return JSON.stringify(value);
}

// Closed subset used by the checked-in JSON Schema. Unsupported schema keywords
// fail rather than silently weakening validation. Semantic/cross-file rules above
// supplement, not replace, this schema.
export function validateSchema(value, rule, path, document = schema) {
  const known = new Set(["$schema", "title", "$defs", "$ref", "type", "additionalProperties", "required", "properties", "items", "enum", "const", "pattern", "minLength", "maxLength", "minItems", "maxItems", "uniqueItems", "minimum", "maximum"]);
  for (const key of Object.keys(rule)) if (!known.has(key)) throw new Error(`Unsupported schema keyword: ${key}`);
  if (rule.$ref) {
    if (rule.$ref.startsWith("supported-releases.schema.json#/$defs/")) {
      return validateSchema(value, schema.$defs[rule.$ref.split("/$defs/")[1]], path, schema);
    }
    if (!rule.$ref.startsWith("#/$defs/")) throw new Error("Only local schema references are supported.");
    return validateSchema(value, document.$defs[rule.$ref.slice(8)], path, document);
  }
  const fail = () => { throw new Error(`Invalid supported-release manifest at ${path}`); };
  if (Object.hasOwn(rule, "const") && value !== rule.const) fail();
  if (rule.enum && !rule.enum.includes(value)) fail();
  if (rule.type === "object") {
    if (!value || typeof value !== "object" || Array.isArray(value)) fail();
    for (const name of rule.required ?? []) if (!Object.hasOwn(value, name)) fail();
    for (const [name, child] of Object.entries(value)) {
      if (!Object.hasOwn(rule.properties ?? {}, name)) fail();
      validateSchema(child, rule.properties[name], `${path}.${name}`, document);
    }
  } else if (rule.type === "array") {
    if (!Array.isArray(value) || value.length < (rule.minItems ?? 0) || value.length > rule.maxItems) fail();
    if (rule.uniqueItems && new Set(value.map(canonical)).size !== value.length) fail();
    value.forEach((child, index) => validateSchema(child, rule.items, `${path}[${index}]`, document));
  } else if (rule.type === "string") {
    if (typeof value !== "string" || value.length < (rule.minLength ?? 0) || value.length > rule.maxLength || (rule.pattern && !new RegExp(rule.pattern, "u").test(value))) fail();
  } else if (rule.type === "integer") {
    if (!Number.isSafeInteger(value) || value < rule.minimum || value > rule.maximum) fail();
  }
}

// JSON.parse alone silently accepts duplicate properties. Scan the bounded JSON
// grammar first so a receipt has one interpretation across the JS/Python gates.
export function parseBoundedJson(bytes) {
  if (Buffer.byteLength(bytes) > maximumManifestBytes) throw new Error("Release manifest/receipt exceeds 1 MiB bound.");
  const text = typeof bytes === "string" ? bytes : decode(bytes);
  let offset = 0;
  const whitespace = () => { while (/[\x20\t\r\n]/u.test(text[offset] ?? "x")) offset++; };
  const string = () => {
    const start = offset++;
    while (offset < text.length) {
      const character = text[offset++];
      if (character === "\\") offset++;
      else if (character === '"') return JSON.parse(text.slice(start, offset));
    }
    throw new Error("Unterminated JSON string.");
  };
  const visit = depth => {
    if (depth > 32) throw new Error("Release JSON nesting exceeds bound.");
    whitespace();
    const opening = text[offset];
    if (opening === "{" || opening === "[") {
      offset++;
      const end = opening === "{" ? "}" : "]";
      const keys = new Set();
      whitespace();
      if (text[offset] === end) { offset++; return; }
      for (;;) {
        whitespace();
        if (opening === "{") {
          if (text[offset] !== '"') throw new Error("Invalid JSON member.");
          const key = string().toLowerCase();
          if (keys.has(key)) throw new Error("Duplicate/case-colliding JSON member.");
          keys.add(key);
          whitespace();
          if (text[offset++] !== ":") throw new Error("Invalid JSON member separator.");
        }
        visit(depth + 1);
        whitespace();
        const separator = text[offset++];
        if (separator === end) return;
        if (separator !== ",") throw new Error("Invalid JSON separator.");
      }
    }
    if (opening === '"') { string(); return; }
    const token = /^(?:true|false|null|-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)/u.exec(text.slice(offset));
    if (!token) throw new Error("Invalid JSON value.");
    offset += token[0].length;
  };
  visit(0);
  whitespace();
  if (offset !== text.length) throw new Error("Trailing JSON content.");
  return JSON.parse(text);
}
