import assert from "node:assert/strict";
import test from "node:test";
import {
  isArchiveRelativePath,
  isPackageId,
  isPackageVersionRange,
  isSemanticVersion,
  isVersionInRange,
} from "../src/validation";

test("package id vectors match the canonical lowercase dot-separated grammar", () => {
  for (const value of ["agent", "sunder.package.agent", "a0.b1", "a".repeat(128)]) {
    assert.equal(isPackageId(value), true, value);
  }
  for (const value of ["", ".agent", "agent.", "agent..tool", "agent-tool", "Agent", "a".repeat(129)]) {
    assert.equal(isPackageId(value), false, value);
  }
});

test("strict SemVer vectors match the canonical SDK grammar", () => {
  for (const value of [
    "0.0.0",
    "999999999999999999999999.2.3",
    "1.2.3-alpha-1.2+build.009",
  ]) assert.equal(isSemanticVersion(value), true, value);
  for (const value of [
    "", "1", "1.2", "01.2.3", "1.02.3", "1.2.03", "1.2.3-01", "1.2.3-",
    "1.2.3+", "1.2.3-alpha..1", "1.2.3+build..1", "1.2.3+build+other", "v1.2.3",
    " 1.2.3", "1.2.3 ", "1.2.3_foo",
  ]) assert.equal(isSemanticVersion(value), false, value);
});

test("version range vectors use exact or space-conjoined precedence comparisons", () => {
  for (const value of ["1.2.3", "=1.2.3", ">=1.0.0", ">=1.0.0  <2.0.0"]) {
    assert.equal(isPackageVersionRange(value), true, value);
  }
  for (const value of [
    "", "1.2.3 || 2.0.0", "^1.2.3", "~1.2.3", "1.2.x", "1.2.3 - 2.0.0",
    ">= 1.2.3", ">=1.2.3, <2.0.0", "1.2.3 <2.0.0", ">=1.2.3\t<2.0.0",
    " >=1.2.3", ">=1.2.3 ", "=>1.2.3", ">=01.2.3",
  ]) assert.equal(isPackageVersionRange(value), false, value);
  assert.equal(isVersionInRange("1.2.3+linux", ">=1.0.0 <2.0.0+other"), true);
  assert.equal(isVersionInRange("1.1.0-beta.1", ">=1.1.0 <1.2.0"), false);
});

test("archive path vectors enforce portable canonical segments", () => {
  for (const value of ["contracts/example.rpc.json", "folder/name with space.txt", "a/b/c"] ) {
    assert.equal(isArchiveRelativePath(value, 200, 24), true, value);
  }
  for (const value of [
    "", "/root", "folder\\file", "folder//file", "folder/../file", "folder/name.",
    "folder/name ", "CON", "aux.txt", "folder/a:b", "folder/snowman-\u2603",
  ]) assert.equal(isArchiveRelativePath(value, 200, 24), false, value);
});
