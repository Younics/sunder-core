import assert from "node:assert/strict";
import test from "node:test";
import { isSemanticVersion, isVersionInRange, SEMANTIC_VERSION_MAXIMUM_LENGTH } from "@sunder/sdk";
import { validateDependencyVersionRange } from "../src/dependency-range";

// Mirrors Sunder.Package.Format.Tests/PackageDependencyRangeValidationTests.cs without changing SDK range semantics.
test("dependency ranges reject empty conjunctions using SemVer precedence", () => {
  for (const range of [
    ">=2.0.0 <1.0.0",
    "<1.0.0 >=2.0.0",
    ">=1.0.0 <2.0.0 >=3.0.0",
    "<=4.0.0 >3.0.0 <3.0.0",
    ">1.0.0 <1.0.0",
    ">1.0.0 <=1.0.0",
    ">=1.0.0 <1.0.0",
    ">1.0.0 >=1.0.0 <=1.0.0",
    ">=1.0.0 >1.0.0 <=1.0.0",
    ">=1.0.0 <1.0.0 <=1.0.0",
    ">=1.0.0 <=1.0.0 <1.0.0",
    "=1.0.0 =2.0.0",
    "=2.0.0 =1.0.0",
    "=1.0.0 >1.0.0",
    "=1.0.0 <1.0.0",
    "=1.0.0 >=2.0.0 <=3.0.0",
    "=2.0.0 >1.0.0 <2.0.0",
    ">1.0.0+first <=1.0.0+second",
    ">=1.0.0+first <1.0.0+second",
    ">=1.0.0-beta <1.0.0-alpha",
    ">=1.0.0-alpha.10 <=1.0.0-alpha.9",
    ">1.0.0 <1.0.1-0",
    ">1.0.0-alpha <1.0.0-alpha.0",
    ">1.0.0-alpha.1 <1.0.0-alpha.1.0",
    ">1.0.0-0 <1.0.0-0.0",
    ">0.0.0-0 <0.0.0-0.0",
    "<0.0.0-0",
    "<0.0.0-0+build",
    ">1.0.99999999999999999999 <1.0.100000000000000000000-0",
    ">=100000000000000000000.0.0 <99999999999999999999.0.0",
  ]) {
    assert.throws(() => validateDependencyVersionRange(range, "versionRange"), /permits no semantic version/u, range);
  }
});

test("dependency ranges preserve valid exact, prerelease, and build-metadata-insensitive constraints", () => {
  for (const [range, witness] of [
    ["1.2.3", "1.2.3"],
    ["1.2.3+reviewed", "1.2.3+selected"],
    ["=1.2.3", "1.2.3"],
    [">=1.0.0 <=1.0.0", "1.0.0"],
    ["<=1.0.0 >=1.0.0", "1.0.0"],
    [">=1.0.0 <2.0.0 =1.2.3", "1.2.3"],
    ["=1.2.3 >=1.0.0 <2.0.0", "1.2.3"],
    ["=1.2.3 =1.2.3", "1.2.3"],
    ["=1.2.3+first =1.2.3+second", "1.2.3+third"],
    [">=1.0.0+z <=1.0.0+a", "1.0.0"],
    [">=1.0.0+a <=1.0.0+z", "1.0.0+other"],
    [">1.0.0 <1.0.1", "1.0.1-0"],
    [">1.0.0 <=1.0.1-0", "1.0.1-0"],
    [">=1.0.0 <1.0.1-0", "1.0.0"],
    ["1.0.0-alpha", "1.0.0-alpha"],
    ["=1.0.0-alpha+first =1.0.0-alpha+second", "1.0.0-alpha"],
    [">1.0.0-alpha <=1.0.0-alpha.0", "1.0.0-alpha.0"],
    [">=1.0.0-alpha <1.0.0-alpha.0", "1.0.0-alpha"],
    [">1.0.0-alpha <1.0.0-beta", "1.0.0-alpha.0"],
    [">1.0.0-alpha.9 <1.0.0-alpha.10", "1.0.0-alpha.9.0"],
    [">1.0.0-9 <1.0.0-A", "1.0.0-9.0"],
    [">1.0.0-Z <1.0.0-a", "1.0.0-Z.0"],
    ["<=0.0.0-0", "0.0.0-0"],
    ["<0.0.0", "0.0.0-0"],
    [">=0.0.0-0 <0.0.0-0.0", "0.0.0-0"],
    [">1.2.3", "1.2.4-0"],
    [">=1.0.0 <4.0.0", "2.0.0"],
    [">1.0.99999999999999999999 <=1.0.100000000000000000000-0", "1.0.100000000000000000000-0"],
    [">=99999999999999999999.0.0 <100000000000000000000.0.0", "99999999999999999999.0.0"],
  ] as const) {
    assert.ok(isVersionInRange(witness, range), range);
    assert.doesNotThrow(() => validateDependencyVersionRange(range, "versionRange"), range);
  }
});

test("dependency ranges reject unsupported syntax and noncanonical whitespace without normalizing", () => {
  for (const range of [
    "",
    "^1.0.0",
    "1.0.0 <2.0.0",
    ">=1.0.0  <2.0.0",
    ">=1.0.0\t<2.0.0",
    " >=1.0.0 <2.0.0",
    ">=1.0.0 <2.0.0 ",
  ]) {
    assert.throws(() => validateDependencyVersionRange(range, "versionRange"), /unsupported or noncanonical/u, range);
  }
});

test("dependency ranges use the least successor within the metadata-free SemVer length budget", () => {
  const maximumLength = SEMANTIC_VERSION_MAXIMUM_LENGTH;
  const prereleaseSuccessors: ReadonlyArray<readonly [string, number, string | null]> = [
    ["a", 3, "a.0"], ["0", 3, "0.0"], ["a", 2, "a-"], ["z", 2, "z-"], ["-", 2, "--"],
    ["0", 1, "1"], ["8", 1, "9"], ["9", 2, "10"], ["99", 3, "100"], ["9", 1, "-"], ["99", 2, "-"],
    ["999999999999999999999999", 24, "-"],
    ["999999999999999999999999", 25, "1000000000000000000000000"],
    ["-", 1, "A"], ["--", 2, "-0"], ["-9", 2, "-A"], ["-Z", 2, "-a"], ["-z", 2, "0-"],
    ["0-", 2, "0A"], ["09-", 3, "09A"], ["0-z", 3, "00-"], ["9-", 2, "9A"],
    ["A", 1, "B"], ["Z", 1, "a"], ["a", 1, "b"], ["az", 2, "b"], ["azz", 3, "b"],
    ["z", 1, null], ["zzz", 3, null],
    ["a.z", 3, "a-"], ["0.z", 3, "1"], ["9.z", 3, "10"], ["a.9", 3, "a.-"],
    ["a.-", 3, "a.A"], ["a.-z", 4, "a.0-"], ["a.zz", 4, "a-"], ["a.0.z", 5, "a.1"],
    ["a.z.z", 5, "a.z-"], ["z.z", 3, "z-"],
  ];
  const adjacent: Array<readonly [string, string]> = prereleaseSuccessors.map(([prerelease, budget, successor]) => {
    const core = "1".repeat(maximumLength - budget - ".0.0-".length) + ".0.0";
    return [`${core}-${prerelease}`, successor === null ? core : `${core}-${successor}`];
  });
  for (const [version, successor] of [["1.0.0", "1.0.1-0"], ["1.0.0-alpha", "1.0.0-alpha.0"]] as const) {
    const lower = `${version}+${"b".repeat(maximumLength - version.length - 1)}`;
    assert.equal(lower.length, maximumLength);
    adjacent.push([lower, successor]);
  }
  const appendLower = "1.0.0-" + "a".repeat(maximumLength - "1.0.0-".length - 2);
  assert.equal((appendLower + ".0").length, maximumLength);
  adjacent.push([appendLower, appendLower + ".0"]);
  for (const length of [255, 256]) {
    const prefix = "1.0.0-" + "a".repeat(length - "1.0.0-".length - 1);
    const lower = prefix + "a";
    assert.equal(lower.length, length);
    assert.ok(!isSemanticVersion(lower + ".0"));
    assert.doesNotThrow(() => validateDependencyVersionRange(`>${lower} <${prefix}c`, "versionRange"));
    adjacent.push([lower, length === maximumLength ? prefix + "b" : lower + "-"]);
    const patchPrefix = "1.0." + "1".repeat(length - "1.0.".length - 1);
    adjacent.push([patchPrefix + "1", patchPrefix + "2"]);
  }
  adjacent.push(["1.0." + "9".repeat(maximumLength - "1.0.".length), "1.1.0-0"]);
  const patch = "9".repeat(maximumLength - "1.0.".length - 1);
  adjacent.push(["1.0." + patch, "1.0.1" + "0".repeat(patch.length)]);
  const major = "9".repeat(maximumLength - ".0.9".length);
  adjacent.push([`${major}.0.9`, `${major}.1.0`]);
  const minor = "9".repeat(maximumLength - "1..9".length);
  adjacent.push([`1.${minor}.9`, "2.0.0-0"]);
  const digits = maximumLength - "1.9.9".length;
  adjacent.push(["1" + "9".repeat(digits) + ".9.9", "2" + "0".repeat(digits) + ".0.0"]);

  for (const [lower, successor] of adjacent) {
    assert.ok(isSemanticVersion(lower), lower);
    assert.ok(isSemanticVersion(successor), successor);
    assert.ok(isVersionInRange(successor, `>${lower}`), `${lower} -> ${successor}`);
    const emptyRange = `>${lower} <${successor}`;
    assert.throws(() => validateDependencyVersionRange(emptyRange, "versionRange"), /permits no semantic version/u, emptyRange);
    for (const range of [`>${lower} <=${successor}`, `>=${lower} <${successor}`]) {
      assert.doesNotThrow(() => validateDependencyVersionRange(range, "versionRange"), range);
    }
  }
});

test("dependency ranges reject bounds above the maximum representable SemVer", () => {
  const maximum = "9".repeat(SEMANTIC_VERSION_MAXIMUM_LENGTH - ".9.9".length) + ".9.9";
  assert.equal(maximum.length, SEMANTIC_VERSION_MAXIMUM_LENGTH);
  assert.ok(isSemanticVersion(maximum));
  assert.throws(() => validateDependencyVersionRange(`>${maximum}`, "versionRange"), /permits no semantic version/u);
  assert.doesNotThrow(() => validateDependencyVersionRange(`>=${maximum}`, "versionRange"));
  assert.doesNotThrow(() => validateDependencyVersionRange(`=${maximum}`, "versionRange"));
});
