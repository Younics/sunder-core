import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");

export function ciScenarios(components, profile, root = repositoryRoot) {
  const scenarios = JSON.parse(readFileSync(resolve(root, "eng/test-oracles/ci-scenarios.json"), "utf8"));
  return Object.entries(scenarios).filter(([, scenario]) => scenario.profile === profile && components.includes(scenario.component)
    && (scenario.consumers ?? []).every(component => components.includes(component)))
    .map(([name]) => name);
}

export function selectScenario(name, declarations, operatingSystem, root = repositoryRoot) {
  const scenarios = JSON.parse(readFileSync(resolve(root, "eng/test-oracles/ci-scenarios.json"), "utf8"));
  const scenario = scenarios[name];
  if (!scenario || !Array.isArray(scenario.selectors) || scenario.selectors.length === 0) {
    throw new Error(`Unknown or empty CI scenario '${name}'.`);
  }
  const tests = new Map();
  for (const prefix of scenario.selectors) {
    if (typeof prefix !== "string" || prefix.length === 0) throw new Error(`Empty selector in scenario '${name}'.`);
    const matches = declarations.filter(test => test.framework === "dotnet" && test.id.startsWith(prefix));
    if (matches.length === 0) throw new Error(`Scenario '${name}' selects no declared tests for '${prefix}'.`);
    for (const test of matches) {
      if (operatingSystem === undefined || test.operatingSystems.includes(operatingSystem)) tests.set(test.id, test);
    }
  }
  if (tests.size === 0) throw new Error(`Scenario '${name}' has no runnable tests on ${operatingSystem}.`);
  return { tests: [...tests.values()], environment: scenario.environment ?? {} };
}
