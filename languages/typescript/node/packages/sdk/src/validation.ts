export const PACKAGE_ID_MAXIMUM_LENGTH = 128;
export const SEMANTIC_VERSION_MAXIMUM_LENGTH = 256;
export const PACKAGE_VERSION_RANGE_MAXIMUM_LENGTH = 1024;

const WINDOWS_RESERVED_NAMES = new Set([
  "CON", "PRN", "AUX", "NUL",
  "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
  "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
]);

export function isPackageId(value: string): boolean {
  return value.length > 0
    && value.length <= PACKAGE_ID_MAXIMUM_LENGTH
    && value.split(".").every((segment) => segment.length > 0 && /^[a-z0-9]+$/u.test(segment));
}

export function isSemanticVersion(value: string): boolean {
  if (value.length === 0 || value.length > SEMANTIC_VERSION_MAXIMUM_LENGTH) return false;
  const match = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$/u.exec(value);
  return match !== null
    && (match[4] === undefined
      || match[4].split(".").every((identifier) => !/^[0-9]+$/u.test(identifier)
        || identifier === "0"
        || !identifier.startsWith("0")));
}

export function isPackageVersionRange(value: string): boolean {
  if (value.length === 0
    || value.length > PACKAGE_VERSION_RANGE_MAXIMUM_LENGTH
    || value.trim() !== value
    || [...value].some((character) => /\s/u.test(character) && character !== " ")) return false;
  const tokens = value.split(" ").filter((token) => token.length > 0);
  if (tokens.length === 0) return false;
  return tokens.every((token) => {
    const match = /^(>=|<=|>|<|=)?(.*)$/u.exec(token);
    if (match === null) return false;
    const operator = match[1] ?? "";
    return (operator !== "" || tokens.length === 1) && isSemanticVersion(match[2] ?? "");
  });
}

export function isVersionInRange(version: string, range: string): boolean {
  const candidate = parseSemanticVersion(version);
  if (candidate === null || !isPackageVersionRange(range)) return false;
  return range.split(" ").filter((token) => token.length > 0).every((token) => {
    const match = /^(>=|<=|>|<|=)?(.*)$/u.exec(token);
    const expected = parseSemanticVersion(match?.[2] ?? "");
    if (expected === null) return false;
    const comparison = compareSemanticPrecedence(candidate, expected);
    switch (match?.[1] ?? "") {
      case "<": return comparison < 0;
      case "<=": return comparison <= 0;
      case ">": return comparison > 0;
      case ">=": return comparison >= 0;
      case "":
      case "=": return comparison === 0;
      default: return false;
    }
  });
}

export function isArchiveRelativePath(value: string, maximumLength: number, maximumDepth: number): boolean {
  if (value.length === 0
    || value.length > maximumLength
    || value.startsWith("/")
    || value.includes("\\")
    || [...value].some((character) => {
      const code = character.charCodeAt(0);
      return code < 0x20 || code > 0x7e;
    })) return false;
  const segments = value.split("/");
  if (segments.length > maximumDepth) return false;
  return segments.every((segment) => {
    const deviceName = segment.split(".", 1)[0] ?? "";
    return segment.length > 0
      && segment !== "."
      && segment !== ".."
      && !segment.endsWith(" ")
      && !segment.endsWith(".")
      && !/[<>:"|?*]/u.test(segment)
      && !WINDOWS_RESERVED_NAMES.has(deviceName.toUpperCase());
  });
}

interface ParsedSemanticVersion {
  readonly core: readonly [string, string, string];
  readonly prerelease: readonly string[] | null;
}

function parseSemanticVersion(value: string): ParsedSemanticVersion | null {
  if (!isSemanticVersion(value)) return null;
  const withoutBuild = value.split("+", 1)[0] ?? "";
  const separator = withoutBuild.indexOf("-");
  const coreText = separator < 0 ? withoutBuild : withoutBuild.slice(0, separator);
  const core = coreText.split(".");
  if (core.length !== 3) return null;
  return {
    core: [core[0]!, core[1]!, core[2]!],
    prerelease: separator < 0 ? null : withoutBuild.slice(separator + 1).split("."),
  };
}

function compareSemanticPrecedence(left: ParsedSemanticVersion, right: ParsedSemanticVersion): number {
  for (let index = 0; index < 3; index++) {
    const comparison = compareNumeric(left.core[index]!, right.core[index]!);
    if (comparison !== 0) return comparison;
  }
  if (left.prerelease === null) return right.prerelease === null ? 0 : 1;
  if (right.prerelease === null) return -1;
  for (let index = 0; index < Math.min(left.prerelease.length, right.prerelease.length); index++) {
    const leftIdentifier = left.prerelease[index]!;
    const rightIdentifier = right.prerelease[index]!;
    const leftNumeric = /^[0-9]+$/u.test(leftIdentifier);
    const rightNumeric = /^[0-9]+$/u.test(rightIdentifier);
    const comparison = leftNumeric && rightNumeric
      ? compareNumeric(leftIdentifier, rightIdentifier)
      : leftNumeric !== rightNumeric
        ? leftNumeric ? -1 : 1
        : ordinal(leftIdentifier, rightIdentifier);
    if (comparison !== 0) return comparison;
  }
  return Math.sign(left.prerelease.length - right.prerelease.length);
}

function compareNumeric(left: string, right: string): number {
  return Math.sign(left.length - right.length) || ordinal(left, right);
}

function ordinal(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}
