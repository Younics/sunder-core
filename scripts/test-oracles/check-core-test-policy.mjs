import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { loadCoreTestModel, repositoryRoot, validateCharacterization } from "./core-test-policy.mjs";

const { policy, declarations } = loadCoreTestModel();
const characterization = JSON.parse(readFileSync(
  resolve(repositoryRoot, "eng/test-oracles/core-large-object-characterization.json"),
  "utf8",
));
validateCharacterization(
  characterization,
  new Set(declarations.filter((item) => item.framework === "dotnet").map((item) => item.id)),
);

const laneCounts = Object.fromEntries(policy.primaryLanes.map((lane) => [
  lane,
  declarations.filter((item) => item.primaryLane === lane).length,
]));
process.stdout.write(`Verified compact Core test policy: declarations=${declarations.length}; lanes=${JSON.stringify(laneCounts)}\n`);
