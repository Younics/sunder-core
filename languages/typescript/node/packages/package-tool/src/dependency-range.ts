import {
  isPackageVersionRange,
  isSemanticVersion,
  isVersionInRange,
  SEMANTIC_VERSION_MAXIMUM_LENGTH,
} from "@sunder/sdk";

export function validateDependencyVersionRange(range: string, label: string): void {
  if (!isPackageVersionRange(range) || range.includes("  ")) {
    throw new Error(`${label} '${range}' is unsupported or noncanonical.`);
  }

  let lower = "0.0.0-0";
  let lowerExclusive = false;
  for (const token of range.split(" ")) {
    if (token.startsWith("<")) continue;
    const version = token.replace(/^[>=]+/u, "");
    const exclusive = token.startsWith(">") && !token.startsWith(">=");
    // Reuse the SDK parser and precedence comparator, including build-metadata equality.
    if (isVersionInRange(version, `${exclusive ? ">=" : ">"}${lower}`)) {
      lower = version;
      lowerExclusive = exclusive;
    }
  }

  // A conjunction is satisfiable exactly when its least lower-bound witness satisfies every token.
  const witness = lowerExclusive ? leastSuccessor(lower) : lower;
  if (witness === undefined || !isVersionInRange(witness, range)) {
    throw new Error(`${label} '${range}' permits no semantic version.`);
  }
}

function leastSuccessor(version: string): string | undefined {
  // Metadata-free representatives leave the entire 256-character budget available for precedence.
  const precedence = version.split("+", 1)[0]!;
  const separator = precedence.indexOf("-");
  const core = separator < 0 ? precedence : precedence.slice(0, separator);
  if (separator >= 0) {
    if (isSemanticVersion(`${precedence}.0`)) return `${precedence}.0`;

    const identifiers = precedence.slice(separator + 1).split(".");
    for (let index = identifiers.length - 1; index >= 0; index--) {
      const prefix = `${core}-${identifiers.slice(0, index).join(".")}${index > 0 ? "." : ""}`;
      const next = nextPrereleaseIdentifier(identifiers[index]!, SEMANTIC_VERSION_MAXIMUM_LENGTH - prefix.length);
      if (next !== undefined) return prefix + next;
    }
    return core;
  }

  const components = core.split(".");
  for (let index = components.length - 1; index >= 0; index--) {
    components[index] = (BigInt(components[index]!) + 1n).toString();
    const nextCore = components.join(".");
    if (isSemanticVersion(`${nextCore}-0`)) return `${nextCore}-0`;
    if (isSemanticVersion(nextCore)) return nextCore;
    components[index] = "0";
  }
  return undefined;
}

function nextPrereleaseIdentifier(identifier: string, maximumLength: number): string | undefined {
  if (/^[0-9]+$/u.test(identifier)) {
    const next = (BigInt(identifier) + 1n).toString();
    // The least nonnumeric identifier follows every length-bounded numeric identifier.
    return next.length <= maximumLength ? next : "-";
  }
  if (identifier.length < maximumLength) return `${identifier}-`;

  const alphabet = "-0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
  for (let index = identifier.length - 1; index >= 0; index--) {
    for (let next = alphabet.indexOf(identifier[index]!) + 1; next < alphabet.length; next++) {
      let candidate = identifier.slice(0, index) + alphabet[next]!;
      if (/^[0-9]+$/u.test(candidate)) {
        // A numeric token would lower precedence despite being ordinally greater.
        if (candidate.length === maximumLength) continue;
        candidate += "-";
      }
      return candidate;
    }
  }
  return undefined;
}
