import { readFileSync, readdirSync } from "node:fs";
import { resolve } from "node:path";
import { loadCoreTestModel, repositoryRoot, validateCharacterization, validateWorkflowTestSelections } from "./core-test-policy.mjs";

const { policy, declarations } = loadCoreTestModel();
const workflows = resolve(repositoryRoot, ".github/workflows");
validateWorkflowTestSelections(readdirSync(workflows).filter((name) => /\.ya?ml$/u.test(name)).map((name) => ({
  source: `.github/workflows/${name}`, content: readFileSync(resolve(workflows, name), "utf8"),
})), declarations);
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
