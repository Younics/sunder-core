import { loadCoreTestModel } from "./core-test-policy.mjs";
import { currentOperatingSystem, missingPrerequisites } from "./test-lane-prerequisites.mjs";

const { policy, declarations } = loadCoreTestModel();
const arguments_ = process.argv.slice(2);
const primaryLaneIndex = arguments_.indexOf("--primary-lane");
const testIndex = arguments_.indexOf("--test");
const selectorIndexes = [primaryLaneIndex, testIndex].filter((index) => index >= 0);
if (selectorIndexes.length !== 1) {
  fail("Usage: run-core-test-lane.mjs (--primary-lane <lane> | --test <declaration-id>) [--list].");
}
const selectorIndex = selectorIndexes[0];
if (selectorIndex + 1 >= arguments_.length) fail("The selected test lane or declaration id is missing.");
const selector = arguments_[selectorIndex + 1];
if (arguments_.some((argument, index) => index !== selectorIndex && index !== selectorIndex + 1 && argument !== "--list")) {
  fail("Only one --primary-lane/--test selector and --list are supported.");
}
const selectionKind = primaryLaneIndex >= 0 ? "primary-lane" : "test";
if (selectionKind === "primary-lane" && !policy.primaryLanes.includes(selector)) {
  fail(`Unknown primary lane '${selector}'.`);
}
const selected = selectionKind === "primary-lane"
  ? declarations.filter((test) => test.primaryLane === selector)
  : declarations.filter((test) => test.id === selector);
if (selected.length === 0) fail(`${selectionKind} '${selector}' selected no tests.`);
const operatingSystem = currentOperatingSystem();
const runnable = selected.filter((test) => test.operatingSystems.includes(operatingSystem));
const skipped = selected.filter((test) => !test.operatingSystems.includes(operatingSystem));
for (const test of skipped) {
  process.stdout.write(`SKIP ${test.id} (OS=${test.operatingSystems.join("|")}; current=${operatingSystem})\n`);
}
const missing = missingPrerequisites(runnable);
if (runnable.length === 0) {
  process.stderr.write(`NOT-RUNNABLE ${selectionKind}=${selector}; every selected case is unsupported on ${operatingSystem}\n`);
  process.exitCode = 3;
} else if (missing.length > 0) {
  for (const item of missing) {
    process.stderr.write(`MISSING ${item.reason}; required by ${item.tests.length} selected test(s)\n`);
  }
  process.exitCode = 2;
} else {
  process.stdout.write(`READY ${selectionKind}=${selector}; declared=${selected.length}; runnable=${runnable.length}; skipped=${skipped.length}\n`);
}
if (arguments_.includes("--list")) {
  process.stdout.write(`${JSON.stringify({ selector, operatingSystem, runnable, skipped }, null, 2)}\n`);
}

function fail(message) {
  process.stderr.write(`${message}\n`);
  process.exit(1);
}
