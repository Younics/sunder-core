import { canonicalRpcAbiProjectionBytes, canonicalRpcDescriptorBytes, isSemanticVersion, isVersionInRange, type RpcContractDescriptor } from "@sunder/sdk";

// Private port of Format/SunderRpcContractCompatibility. Admission, authority and
// strict descriptor validation belong to callers; this proves normalized ABI
// containment, never TypeScript assignability or a selection of sample values.
export type ChangeKind = "Unchanged" | "Patch" | "Minor" | "Major";
export interface Change { readonly path: string; readonly code: string }
export interface CompatibilityResult { readonly kind: ChangeKind; readonly changes: readonly Change[] }
const kinds: readonly ChangeKind[] = ["Unchanged", "Patch", "Minor", "Major"];
const prereleaseDiagnostic = "contract-version.prerelease-policy-required: Reuse an exact immutable version or freeze an explicit prerelease transition policy before allocating a new version.";
export const ordinal = (a: string, b: string): number => a < b ? -1 : a > b ? 1 : 0;
export function versionParts(version: string): { core: string[]; prerelease: boolean } {
  if (!isSemanticVersion(version)) throw new Error("A valid strict descriptor SemVer is required.");
  const text = version.split("+", 1)[0]!;
  return { core: text.split("-", 1)[0]!.split("."), prerelease: text.includes("-") };
}
export function compareVersions(a: string, b: string): number {
  versionParts(a); versionParts(b);
  return isVersionInRange(a, `>${b}`) ? 1 : isVersionInRange(a, `<${b}`) ? -1 : 0;
}
function compareStable(a: string, b: string): number {
  const left = a.split("."); const right = b.split(".");
  for (let i = 0; i < 3; i++) { const order = left[i]!.length - right[i]!.length || ordinal(left[i]!, right[i]!); if (order) return order; }
  return 0;
}
function minimumStable(version: string, kind: ChangeKind): string {
  const core = versionParts(version).core;
  const index = kinds.indexOf(kind);
  if (index < 0) throw new Error("Invalid contract change kind.");
  if (index) {
    const component = 3 - index;
    core[component] = (BigInt(core[component]!) + 1n).toString();
    for (let i = component + 1; i < 3; i++) core[i] = "0";
  }
  return core.join(".");
}
export function minimumVersion(version: string, kind: ChangeKind): string {
  const parsed = versionParts(version);
  if (!kinds.includes(kind)) throw new Error("Invalid contract change kind.");
  if (kind === "Unchanged") return version;
  if (parsed.prerelease) throw new Error(prereleaseDiagnostic);
  const minimum = minimumStable(version, kind);
  if (!isSemanticVersion(minimum)) throw new Error("contract-version.version-limit-exceeded: The minimum increment exceeds the descriptor version length limit; select a higher stable bump that fits the limit.");
  return minimum;
}
export function minimumOwnedVersion(version: string, kind: ChangeKind): string {
  for (;;) {
    try { return minimumVersion(version, kind); }
    catch (error) {
      if (versionParts(version).prerelease || kind === "Major" || kind === "Unchanged") throw error;
      kind = kind === "Patch" ? "Minor" : "Major";
    }
  }
}
export function validateVersion(candidate: RpcContractDescriptor, history: readonly RpcContractDescriptor[]): readonly string[] {
  const bytes = Buffer.from(canonicalRpcDescriptorBytes(candidate));
  let reused = false;
  let highest: RpcContractDescriptor | undefined;
  for (const previous of history) {
    if (previous.contractId !== candidate.contractId) return ["contract-version.bump-required: Immutable history must contain only the candidate contract family."];
    if (previous.version === candidate.version) {
      if (!bytes.equals(Buffer.from(canonicalRpcDescriptorBytes(previous)))) return [`contract-version.bump-required: Candidate '${candidate.version}' changes immutable canonical bytes at the same version; allocate a new version.`];
      reused = true;
    }
    if (!highest || compareVersions(previous.version, highest.version) > 0
      || compareVersions(previous.version, highest.version) === 0 && ordinal(previous.version, highest.version) > 0) highest = previous;
  }
  if (reused) return [];
  const parsed = versionParts(candidate.version);
  if (parsed.prerelease) return [prereleaseDiagnostic];
  if (!highest) return [];
  if (versionParts(highest.version).prerelease) return [prereleaseDiagnostic];
  if (compareVersions(candidate.version, highest.version) <= 0) return [`contract-version.bump-required: Candidate '${candidate.version}' must have greater SemVer precedence than highest immutable version '${highest.version}'; backfills and build-metadata-only allocations are forbidden.`];
  let failure: string | undefined; let strictest: string | undefined; let prior = "";
  for (const previous of history) {
    const version = versionParts(previous.version);
    if (version.core[0] !== parsed.core[0] && previous.version !== highest.version) continue;
    if (version.prerelease) return [prereleaseDiagnostic];
    const kind = classify(previous, candidate).kind;
    const minimum = minimumStable(previous.version, kind);
    if (compareStable(parsed.core.join("."), minimum) >= 0) continue;
    if (!strictest || compareStable(minimum, strictest) > 0 || compareStable(minimum, strictest) === 0 && ordinal(previous.version, prior) < 0) {
      strictest = minimum; prior = previous.version;
      failure = `contract-version.bump-required: Candidate '${candidate.version}' requires ${kind} relative to immutable version '${previous.version}'.`;
    }
  }
  return failure ? [failure] : [];
}

interface Schema {
  $ref?: string; oneOf?: Schema[]; type?: string; format?: string; logicalType?: string; contentEncoding?: string;
  properties?: Record<string, Schema>; required?: string[]; additionalProperties?: Schema | false; propertyNames?: Schema; items?: Schema;
  const?: unknown; enum?: unknown[];
  minLength?: number; maxLength?: number; minItems?: number; maxItems?: number; minProperties?: number; maxProperties?: number;
}
interface Abi { contractId: string; $defs: Record<string, Schema>; errors: Record<string, { detailsSchema?: Schema }>; services: Record<string, { methods: Record<string, { kind: string; idempotency: string; errors: string[]; requestSchema: Schema; responseSchema?: Schema; eventSchema?: Schema }> }> }
// Enumeration is own-only. A lookup by a key from the OTHER ABI must separately
// prove ownership: JSON.parse records inherit constructor/toString/__proto__.
const entries = <T>(value: Record<string, T>): [string, T][] => Object.keys(value).sort(ordinal).map((key) => [key, value[key]!]);
const same = (a: unknown, b: unknown): boolean => JSON.stringify(a) === JSON.stringify(b);
class ProofLimit extends Error {}
export function classify(previous: RpcContractDescriptor, candidate: RpcContractDescriptor): CompatibilityResult {
  if (Buffer.from(canonicalRpcDescriptorBytes(previous)).equals(Buffer.from(canonicalRpcDescriptorBytes(candidate)))) return { kind: "Unchanged", changes: [] };
  const oldAbi = Buffer.from(canonicalRpcAbiProjectionBytes(previous)); const newAbi = Buffer.from(canonicalRpcAbiProjectionBytes(candidate));
  if (oldAbi.equals(newAbi)) return { kind: "Patch", changes: [{ path: "", code: "abi-equivalent" }] };
  const proof = new Proof(JSON.parse(oldAbi.toString()) as Abi, JSON.parse(newAbi.toString()) as Abi);
  try { proof.compare(); } catch (error) { if (!(error instanceof ProofLimit)) throw error; proof.change("Major", "", "analysis-limit-exceeded"); }
  return proof.result();
}
class Proof {
  private readonly oldRefs = new Map<string, string[]>();
  private readonly newRefs = new Map<string, string[]>();
  private readonly equal = new Set<string>();
  private readonly examined = new Set<string>();
  private readonly proved = new Map<string, boolean>();
  private readonly changes: Change[] = [];
  private kind: ChangeKind = "Minor";
  private steps = 0;
  constructor(private readonly previous: Abi, private readonly candidate: Abi) {}
  result(): CompatibilityResult { return { kind: this.kind, changes: this.changes.sort((a, b) => ordinal(a.path, b.path) || ordinal(a.code, b.code)) }; }
  change(kind: ChangeKind, path: string, code: string): void {
    if (kinds.indexOf(kind) > kinds.indexOf(this.kind)) this.kind = kind;
    if (this.changes.some((change) => change.path === path && change.code === code)) return;
    if (this.changes.length < 63) this.changes.push({ path, code });
    else if (this.changes.length === 63) this.changes.push({ path: "", code: "changes-truncated" });
  }
  private step(depth = 0): void { if (++this.steps > 65_536 || depth > 128) throw new ProofLimit(); }
  private at(path: string, member: string): string {
    const result = `${path}/${member.replaceAll("~", "~0").replaceAll("/", "~1")}`;
    if (result.length > 1024) throw new ProofLimit();
    return result;
  }
  private fail(path: string, code: string): false { this.change("Major", path, code); return false; }
  compare(): void {
    if (this.previous.contractId !== this.candidate.contractId) this.fail("/contractId", "contract-id-changed");
    this.graph(); this.services(); this.errors();
    for (const [name] of entries(this.previous.$defs)) {
      this.step(); const path = this.at("/$defs", name);
      if (!Object.hasOwn(this.candidate.$defs, name)) this.fail(path, "definition-removed");
      else if (!this.equal.has(name) && !this.examined.has(name)) this.fail(path, "definition-change-unclassified");
    }
    for (const [name] of entries(this.candidate.$defs)) { this.step(); if (!Object.hasOwn(this.previous.$defs, name)) this.change("Minor", this.at("/$defs", name), "definition-added"); }
  }
  private graph(): void {
    const dependents = new Map<string, string[]>();
    for (const [name, schema] of entries(this.previous.$defs)) {
      this.step(); const refs = [...new Set(this.references(schema))]; this.oldRefs.set(name, refs);
      for (const ref of refs) { const names = dependents.get(ref) ?? []; names.push(name); dependents.set(ref, names); }
      if (Object.hasOwn(this.candidate.$defs, name) && same(schema, this.candidate.$defs[name])) this.equal.add(name);
    }
    for (const [name, schema] of entries(this.candidate.$defs)) this.newRefs.set(name, [...new Set(this.references(schema))]);
    const changed = [...this.oldRefs.keys()].filter((name) => !this.equal.has(name));
    for (let i = 0; i < changed.length; i++) { this.step(); for (const dependent of dependents.get(changed[i]!) ?? []) if (this.equal.delete(dependent)) changed.push(dependent); }
  }
  private *references(schema: Schema, depth = 0): Generator<string> {
    this.step(depth);
    if (Object.hasOwn(schema, "$ref")) { yield schema.$ref!.slice(8); return; }
    if (Object.hasOwn(schema, "properties")) for (const [, child] of entries(schema.properties!)) yield* this.references(child, depth + 1);
    for (const keyword of ["items", "additionalProperties", "propertyNames"] as const) if (Object.hasOwn(schema, keyword)) {
      const child = schema[keyword]; if (child && typeof child === "object") yield* this.references(child, depth + 1);
    }
    if (Object.hasOwn(schema, "oneOf")) for (const child of schema.oneOf!) yield* this.references(child, depth + 1);
  }
  private cycle(name: string, graph: Map<string, string[]>, active = new Set<string>(), complete = new Set<string>()): boolean {
    this.step(active.size); if (complete.has(name)) return false; if (active.has(name)) return true;
    active.add(name);
    for (const ref of graph.get(name)!) if (this.cycle(ref, graph, active, complete)) return true;
    active.delete(name); complete.add(name); return false;
  }
  private services(): void {
    for (const [name, service] of entries(this.previous.services)) {
      this.step(); const path = this.at("/services", name);
      if (!Object.hasOwn(this.candidate.services, name)) { this.fail(path, "service-removed"); continue; }
      const other = this.candidate.services[name]!;
      for (const [id, method] of entries(service.methods)) {
        this.step(); const at = this.at(`${path}/methods`, id);
        if (!Object.hasOwn(other.methods, id)) { this.fail(at, "method-removed"); continue; }
        const next = other.methods[id]!;
        if (method.kind !== next.kind) this.fail(`${at}/kind`, "method-kind-changed");
        if (method.idempotency !== next.idempotency) this.fail(`${at}/idempotency`, "idempotency-changed");
        if (next.errors.some((error) => !method.errors.includes(error))) this.fail(`${at}/errors`, "method-error-added");
        else if (method.errors.some((error) => !next.errors.includes(error))) this.change("Minor", `${at}/errors`, "method-error-removed");
        this.subset(method.requestSchema, next.requestSchema, true, `${at}/requestSchema`);
        if (method.kind === next.kind) { const output = method.kind === "unary" ? "responseSchema" : "eventSchema"; this.subset(method[output]!, next[output]!, false, this.at(at, output)); }
      }
      for (const [id] of entries(other.methods)) { this.step(); if (!Object.hasOwn(service.methods, id)) this.change("Minor", this.at(`${path}/methods`, id), "method-added"); }
    }
    for (const [name] of entries(this.candidate.services)) { this.step(); if (!Object.hasOwn(this.previous.services, name)) this.change("Minor", this.at("/services", name), "service-added"); }
  }
  private errors(): void {
    for (const [name, error] of entries(this.previous.errors)) {
      this.step(); const path = this.at("/errors", name);
      if (!Object.hasOwn(this.candidate.errors, name)) { this.fail(path, "error-removed"); continue; }
      const next = this.candidate.errors[name]!;
      const oldDetails = Object.hasOwn(error, "detailsSchema"); const newDetails = Object.hasOwn(next, "detailsSchema");
      if (oldDetails !== newDetails) this.fail(`${path}/detailsSchema`, "error-details-presence-changed");
      else if (oldDetails) this.subset(error.detailsSchema!, next.detailsSchema!, false, `${path}/detailsSchema`);
    }
    for (const [name] of entries(this.candidate.errors)) { this.step(); if (!Object.hasOwn(this.previous.errors, name)) this.change("Minor", this.at("/errors", name), "error-added"); }
  }
  private subset(old: Schema, next: Schema, request: boolean, path: string, depth = 0): boolean {
    this.step(depth);
    if (same(old, next) && [...this.references(old)].every((name) => this.equal.has(name))) return true;
    const oldNullable = this.nullable(old, this.previous.$defs); const newNullable = this.nullable(next, this.candidate.$defs);
    if (oldNullable || newNullable) {
      if ((request ? oldNullable : newNullable) && !(request ? newNullable : oldNullable)) return this.fail(path, "null-not-accepted");
      const nulls = !oldNullable || !newNullable || this.subset(oldNullable[1], newNullable[1], request, `${path}/oneOf/0`, depth + 1);
      return this.subset(oldNullable?.[0] ?? old, newNullable?.[0] ?? next, request, `${path}/oneOf/1`, depth + 1) && nulls;
    }
    if (Object.hasOwn(old, "$ref") || Object.hasOwn(next, "$ref")) {
      if (!Object.hasOwn(old, "$ref") || !Object.hasOwn(next, "$ref") || old.$ref !== next.$ref) return this.fail(path, "reference-changed");
      const name = old.$ref!.slice(8); const key = `${name}/${request}`; this.examined.add(name);
      if (this.proved.has(key)) return this.proved.get(key)!;
      const at = this.at("/$defs", name);
      if (!Object.hasOwn(this.previous.$defs, name) || !Object.hasOwn(this.candidate.$defs, name)) return this.fail(at, "reference-changed");
      if (this.cycle(name, this.oldRefs) || this.cycle(name, this.newRefs)) { this.proved.set(key, false); return this.fail(at, "recursive-change-unclassified"); }
      const proved = this.subset(this.previous.$defs[name]!, this.candidate.$defs[name]!, request, at, depth + 1);
      this.proved.set(key, proved); if (proved) this.change("Minor", at, "schema-compatible"); return proved;
    }
    if (Object.hasOwn(old, "oneOf") || Object.hasOwn(next, "oneOf")) return this.fail(path, "tagged-union-change-unclassified");
    if (!Object.hasOwn(old, "type") || !Object.hasOwn(next, "type") || old.type !== next.type) return this.fail(path, "schema-form-changed");
    if (["format", "logicalType", "contentEncoding"].some((keyword) => Object.hasOwn(old, keyword) || Object.hasOwn(next, keyword))) return this.fail(path, "exact-format-change-unclassified");
    const source = request ? old : next; const target = request ? next : old;
    switch (old.type) {
      case "string": { const literals = this.literals(source, target, path); return this.bounds(source, target, "minLength", "maxLength", path) && literals; }
      case "boolean": case "null": return this.literals(source, target, path);
      case "array": { const bounds = this.bounds(source, target, "minItems", "maxItems", path); return this.subset(old.items!, next.items!, request, `${path}/items`, depth + 1) && bounds; }
      case "object": {
        if (!Object.hasOwn(old, "additionalProperties") || !Object.hasOwn(next, "additionalProperties") || typeof old.additionalProperties !== typeof next.additionalProperties) return this.fail(path, "object-form-change-unclassified");
        let valid = this.bounds(source, target, "minProperties", "maxProperties", path);
        if (typeof old.additionalProperties === "object") {
          const values = this.subset(old.additionalProperties, next.additionalProperties as Schema, request, `${path}/additionalProperties`, depth + 1);
          const keys = this.subset(old.propertyNames!, next.propertyNames!, request, `${path}/propertyNames`, depth + 1);
          return values && keys && valid;
        }
        for (const name of [...target.required!].sort(ordinal)) if (!source.required!.includes(name)) valid = this.fail(this.at(`${path}/properties`, name), "required-property-not-guaranteed");
        for (const [name, property] of entries(source.properties!)) {
          this.step(); const at = this.at(`${path}/properties`, name);
          if (!Object.hasOwn(target.properties!, name)) valid = this.fail(at, "property-not-accepted");
          else {
            const accepted = target.properties![name]!;
            valid = this.subset(request ? property : accepted, request ? accepted : property, request, at, depth + 1) && valid;
          }
        }
        return valid;
      }
      default: return this.fail(path, "numeric-change-unclassified");
    }
  }
  private nullable(schema: Schema, defs: Record<string, Schema>): [Schema, Schema] | undefined {
    if (!Object.hasOwn(schema, "oneOf") || schema.oneOf?.length !== 2) return undefined;
    for (let i = 0; i < 2; i++) {
      let resolved = schema.oneOf[i]!; let depth = 0;
      while (Object.hasOwn(resolved, "$ref")) {
        this.step(++depth); const name = resolved.$ref!.slice(8);
        if (!Object.hasOwn(defs, name)) return undefined;
        resolved = defs[name]!;
      }
      if (Object.hasOwn(resolved, "type") && resolved.type === "null") return [schema.oneOf[1 - i]!, schema.oneOf[i]!];
    }
    return undefined;
  }
  private literals(source: Schema, target: Schema, path: string): boolean {
    for (const keyword of ["const", "enum"] as const) {
      const hasSource = Object.hasOwn(source, keyword); const hasTarget = Object.hasOwn(target, keyword);
      if (hasSource !== hasTarget) return this.fail(this.at(path, keyword), "literal-form-changed");
      if (!hasSource) continue;
      if (keyword === "const") { if (!same(source.const, target.const)) return this.fail(`${path}/const`, "const-changed"); }
      else { const allowed = new Set(target.enum!.map((value) => JSON.stringify(value))); for (const value of source.enum!) { this.step(); if (!allowed.has(JSON.stringify(value))) return this.fail(`${path}/enum`, "enum-value-not-accepted"); } }
    }
    return true;
  }
  private bounds(source: Schema, target: Schema, minimum: "minLength" | "minItems" | "minProperties", maximum: "maxLength" | "maxItems" | "maxProperties", path: string): boolean {
    let valid = true; let lower = Object.hasOwn(source, minimum) ? source[minimum]! : 0; let upper = Object.hasOwn(source, maximum) ? source[maximum]! : Infinity;
    if (minimum === "minProperties" && Object.hasOwn(source, "additionalProperties") && source.additionalProperties === false) { lower = Math.max(lower, source.required!.length); upper = Math.min(upper, Object.keys(source.properties!).length); }
    if (lower < (Object.hasOwn(target, minimum) ? target[minimum]! : 0)) valid = this.fail(this.at(path, minimum), "lower-bound-not-contained");
    if (upper > (Object.hasOwn(target, maximum) ? target[maximum]! : Infinity)) valid = this.fail(this.at(path, maximum), "upper-bound-not-contained");
    return valid;
  }
}
