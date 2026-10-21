import assert from "node:assert/strict";
import { test } from "node:test";
import { currentCandidatePath, parseCandidateArguments, requireCleanCandidateSource } from "./generate-core-compatibility-candidate.mjs";

const commit = "a".repeat(40);

test("current candidate uses a separate destination and requires explicit source before collection", () => {
  assert.equal(currentCandidatePath, "eng/compatibility/core-current-compatibility-candidate.json");
  assert.deepEqual(parseCandidateArguments(["--write", "--source-commit", commit]), { write: true, sourceCommit: commit });
  assert.deepEqual(parseCandidateArguments(["--check", "--source-commit", commit]), { write: false, sourceCommit: commit });
  for (const args of [[], ["--write"], ["--check"], ["--write", "--source-commit", "HEAD"],
    ["--write", "--source-commit", commit, "--output", "eng/compatibility/core-compatibility-candidate.json"]]) {
    assert.throws(() => parseCandidateArguments(args), /exact-clean-commit/u);
  }
});

test("only the current output can differ from the exact clean source frame", () => {
  const calls = [];
  requireCleanCandidateSource(commit, (args) => {
    calls.push(args);
    return ["rev-parse", "merge-base"].includes(args[0]) ? [commit] : [];
  });
  assert.deepEqual(calls.slice(2), [
    ["status", "--porcelain", "--untracked-files=all", "--", ".", `:(exclude)${currentCandidatePath}`],
    ["diff", "--name-only", commit, "--", ".", `:(exclude)${currentCandidatePath}`],
  ]);
});

for (const [label, dirtyCommand, changed] of [
  ["edited source", "status", " M source.cs"],
  ["untracked expectation", "status", "?? expected.json"],
  ["different committed inputs", "diff", "source.cs"],
  ["historical ledger edit", "diff", "eng/compatibility/core-compatibility-candidate.json"],
]) {
  test(`rejects ${label} without collecting or writing candidate output`, () => {
    assert.throws(() => requireCleanCandidateSource(commit, (args) =>
      ["rev-parse", "merge-base"].includes(args[0]) ? [commit] : args[0] === dirtyCommand ? [changed] : []), /clean reviewed source/u);
  });
}

test("an unrelated or unresolved source pin fails closed", () => {
  assert.throws(() => requireCleanCandidateSource("HEAD", () => assert.fail("must not infer a commit")), /exact clean source commit/u);
  assert.throws(() => requireCleanCandidateSource(commit, () => ["b".repeat(40)]), /exact ancestor commit/u);
});
