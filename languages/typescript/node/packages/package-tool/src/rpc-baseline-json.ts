// Deterministic, lossless *outer* authoring JSON. Embedded descriptors are parsed
// exclusively by the SDK. Retain ordinal numeric map keys (JS Object.keys would
// reorder them), schema member order, and every admitted evidence field.
export type Outer = string | number | boolean | Outer[] | OuterObject;
export interface OuterObject { readonly [key: string]: Outer }
const orders = new WeakMap<object, readonly string[]>();
export function ordered(entries: readonly (readonly [string, Outer])[]): OuterObject {
  const value: Record<string, Outer> = Object.create(null) as Record<string, Outer>;
  for (const [key, item] of entries) value[key] = item;
  orders.set(value, entries.map(([key]) => key));
  return value;
}
export const outerKeys = (value: OuterObject): readonly string[] => orders.get(value) ?? Object.keys(value);
export function outerBytes(value: Outer): Buffer {
  const write = (item: Outer, depth: number): string => {
    if (depth > 16) invalid("outer depth exceeds 16");
    if (typeof item === "string") { unicode(item); return JSON.stringify(item); }
    if (typeof item === "number") { if (!Number.isSafeInteger(item) || Object.is(item, -0)) invalid("outer numbers must be safe integers"); return String(item); }
    if (typeof item === "boolean") return String(item);
    if (item === null || typeof item !== "object") return invalid("null and non-JSON outer values are forbidden");
    const array = Array.isArray(item);
    const values = array ? item.map((child) => write(child, depth + 1)) : outerKeys(item).map((key) => `${JSON.stringify(key)}: ${write(item[key]!, depth + 1)}`);
    const [open, close] = array ? ["[", "]"] : ["{", "}"];
    return values.length === 0 ? open! + close! : `${open}\n${values.map((line) => "  ".repeat(depth + 1) + line).join(",\n")}\n${"  ".repeat(depth)}${close}`;
  };
  return Buffer.from(`${write(value, 0)}\n`);
}
export const MAX_BASELINE_BYTES = 268_435_456;
// Maximum valid baseline: 128 families * (256 history + 1 current), each with
// 11 snapshot fields, 2 release fields, 6 publication fields, and one six-field
// projection review companion. Embedded projection JSON is an opaque string with
// its own bounded parse. Count BOTH keys/values plus containers at this layer.
const MAX_VALUES_AND_PROPERTIES = 2_097_152;
export function parseOuter(bytes: Buffer, limits: { readonly bytes: number; readonly values: number } = { bytes: MAX_BASELINE_BYTES, values: MAX_VALUES_AND_PROPERTIES }): OuterObject {
  if (!bytes.length || bytes.length > limits.bytes) invalid("outer byte limit exceeded");
  if (bytes[bytes.length - 1] !== 10 || bytes.includes(13) || bytes.subarray(0, 3).equals(Buffer.from([239, 187, 191]))) invalid("strict UTF-8, no BOM, LF endings and exactly one terminal LF required");
  // Scan structural resources BEFORE allocating a JSON object graph. Quoted
  // descriptor/ABI bodies are opaque strings and cannot consume outer depth.
  let depth = 0; let budget = 0; let quoted = false; let escaped = false;
  for (let i = 0; i < bytes.length; i++) {
    const ch = bytes[i]!;
    if (quoted) { if (escaped) escaped = false; else if (ch === 92) escaped = true; else if (ch === 34) quoted = false; continue; }
    if (ch === 34) { quoted = true; budget++; }
    else if (ch === 123 || ch === 91) { if (++depth > 16) invalid("outer depth exceeds 16"); budget++; }
    else if (ch === 125 || ch === 93) depth--;
    else if (ch === 45 || ch >= 48 && ch <= 57 || ch === 116 || ch === 102 || ch === 110) {
      budget++;
      while (i + 1 < bytes.length && ![32, 9, 10, 44, 93, 125].includes(bytes[i + 1]!)) i++;
    }
    if (budget > limits.values) invalid("outer structural budget exceeded before parsing");
  }
  const text = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes);
  let position = 0;
  const whitespace = (): void => { while (/[ \t\n]/u.test(text[position] ?? "x")) position++; };
  const string = (): string => {
    const start = position++;
    while (position < text.length) {
      const ch = text[position++];
      if (ch === "\\") position++;
      else if (ch === '"') { const value: unknown = JSON.parse(text.slice(start, position)); if (typeof value !== "string") invalid("expected string"); unicode(value); return value; }
    }
    return invalid("unterminated string");
  };
  const read = (depth: number): Outer => {
    if (depth > 16) invalid("outer depth exceeds 16");
    whitespace(); const ch = text[position];
    if (ch === '"') return string();
    if (ch === "{" || ch === "[") {
      position++; whitespace(); const array = ch === "["; const close = array ? "]" : "}";
      const values: Outer[] = []; const entries: [string, Outer][] = []; const names = new Set<string>();
      if (text[position] !== close) for (;;) {
        if (values.length + entries.length >= 512) invalid("outer container entry limit exceeded");
        if (array) values.push(read(depth + 1));
        else {
          if (text[position] !== '"') invalid("expected object key");
          const key = string(); const folded = key.replace(/[A-Z]/gu, (c) => c.toLowerCase());
          if (names.has(folded)) invalid("duplicate or ASCII-case-colliding members");
          names.add(folded); whitespace(); if (text[position++] !== ":") invalid("expected colon");
          entries.push([key, read(depth + 1)]);
        }
        whitespace(); if (text[position] === close) break;
        if (text[position++] !== ",") invalid("expected comma"); whitespace();
      }
      position++; Object.freeze(values); return array ? values : Object.freeze(ordered(entries));
    }
    const start = position;
    while (position < text.length && !/[ \t\n,\]}]/u.test(text[position]!)) position++;
    const token = text.slice(start, position);
    if (token === "true" || token === "false") return token === "true";
    if (!/^(?:0|-?[1-9][0-9]*)$/u.test(token) || !Number.isSafeInteger(Number(token))) return invalid("outer numbers must be canonical safe integers; null is forbidden");
    return Number(token);
  };
  const result = read(0); whitespace(); if (position !== text.length) invalid("trailing JSON content");
  const root = object(result);
  if (!outerBytes(root).equals(bytes)) invalid("required deterministic outer JSON form (two spaces, schema order, one LF)");
  return root;
}
export function object(value: Outer | undefined): OuterObject {
  if (value === null || typeof value !== "object" || Array.isArray(value)) return invalid("expected closed object");
  return value;
}
export function fields(value: Outer | undefined, required: readonly string[], optional: readonly string[] = [], order = [...required, ...optional]): OuterObject {
  const result = object(value); const actual = outerKeys(result);
  if (required.some((key) => !Object.hasOwn(result, key)) || actual.some((key) => !order.includes(key))) invalid("missing or unsupported members");
  if (JSON.stringify(actual) !== JSON.stringify(order.filter((key) => Object.hasOwn(result, key)))) invalid("members are not in deterministic schema order");
  return result;
}
export function string(value: Outer | undefined): string { if (typeof value !== "string") return invalid("expected string"); return value; }
function unicode(value: string): void {
  for (let i = 0; i < value.length; i++) {
    const code = value.charCodeAt(i);
    if (code >= 0xd800 && code <= 0xdbff) { const next = value.charCodeAt(++i); if (!(next >= 0xdc00 && next <= 0xdfff)) invalid("non-scalar Unicode string"); }
    else if (code >= 0xdc00 && code <= 0xdfff) invalid("non-scalar Unicode string");
  }
}
export function invalid(message: string): never { throw new Error(`rpc-authoring.baseline-drift: Owned baseline ${message}.`); }
