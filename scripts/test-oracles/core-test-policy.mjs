import { existsSync, readFileSync, readdirSync } from "node:fs";
import { dirname, extname, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

export const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const policyRelativePath = "eng/test-oracles/core-test-policy.json";

export function loadCoreTestModel(root = repositoryRoot, policyOverride) {
  const policy = policyOverride ?? JSON.parse(readFileSync(resolve(root, policyRelativePath), "utf8"));
  validatePolicyShape(policy);
  const declarations = discoverTestDeclarations(root, policy);
  validateDeclarationIdentity(declarations);
  validateProjectLaneOwnership(declarations, policy, listDotnetTestProjects(root));
  validateExpectedSkipPolicy(declarations, policy);
  return { policy, declarations };
}

export function discoverTestDeclarations(root, policy) {
  const declarations = [];
  for (const source of testSourceFiles(root)) {
    const content = readFileSync(resolve(root, source), "utf8");
    if (source.endsWith(".cs")) declarations.push(...discoverDotnetTests(source, content, policy));
    else if (source.endsWith(".ts") || source.endsWith(".mjs")) declarations.push(...discoverNodeTests(source, content));
    else if (source.endsWith(".py")) declarations.push(...discoverPythonTests(source, content));
    else if (source.endsWith(".fixture.sh")) declarations.push(discoverShellFixture(source));
  }
  return declarations.sort((left, right) => ordinal(left.id, right.id));
}

export function validateProjectLaneOwnership(declarations, policy, dotnetProjects = []) {
  const actual = {};
  for (const declaration of declarations) {
    actual[declaration.project] ??= [];
    if (!actual[declaration.project].includes(declaration.primaryLane)) {
      actual[declaration.project].push(declaration.primaryLane);
      actual[declaration.project].sort(ordinal);
    }
  }
  const expected = Object.fromEntries(Object.entries(policy.projectLanes)
    .sort(([left], [right]) => ordinal(left, right))
    .map(([project, lanes]) => [project, [...lanes].sort(ordinal)]));
  const normalizedActual = Object.fromEntries(Object.entries(actual).sort(([left], [right]) => ordinal(left, right)));
  if (JSON.stringify(normalizedActual) !== JSON.stringify(expected)) {
    throw new Error(`Core project/lane ownership drift: expected=${JSON.stringify(expected)} actual=${JSON.stringify(normalizedActual)}`);
  }

  const declaredDotnetProjects = [...new Set(declarations
    .filter((item) => item.framework === "dotnet")
    .map((item) => item.project))].sort(ordinal);
  if (JSON.stringify(declaredDotnetProjects) !== JSON.stringify(dotnetProjects)) {
    throw new Error(`Every .NET test project must declare at least one test: projects=${dotnetProjects.join(",")} declarations=${declaredDotnetProjects.join(",")}`);
  }
  for (const lane of policy.primaryLanes) {
    if (!declarations.some((item) => item.primaryLane === lane)) {
      throw new Error(`Primary lane '${lane}' has zero declared tests.`);
    }
  }
}

export function validateCharacterization(characterization, declarationIds) {
  if (characterization.schemaVersion !== 2) throw new Error("Characterization map schemaVersion must be 2.");
  const behaviorCategories = new Set([
    "inputs",
    "outputs",
    "errors",
    "cancellation",
    "ordering",
    "persistence",
    "logging",
    "cleanup",
  ]);
  const expectedTargets = [
    "ProcessRuntimeWorker",
    "RuntimeRpcBroker",
    "PackageSessionState",
    "PackageViewHostService",
    "MainWindowViewModel",
    "AppWebBridge",
    "RuntimeWorkerCoordinator",
  ];
  const actualTargets = characterization.targets.map((target) => target.type);
  if (JSON.stringify(actualTargets) !== JSON.stringify(expectedTargets)) {
    throw new Error(`Characterization targets changed: ${actualTargets.join(", ")}`);
  }
  for (const target of characterization.targets) {
    if (!Array.isArray(target.requiredBehaviors) || target.requiredBehaviors.length === 0) {
      throw new Error(`${target.type} has no required behavior characterization.`);
    }
    const behaviorIds = new Set();
    for (const behavior of target.requiredBehaviors) {
      if (behaviorIds.has(behavior.id)) throw new Error(`${target.type} repeats behavior '${behavior.id}'.`);
      behaviorIds.add(behavior.id);
      if (!behaviorCategories.has(behavior.category)) {
        throw new Error(`${target.type}.${behavior.id} has invalid behavior category '${behavior.category}'.`);
      }
      if (!Array.isArray(behavior.evidence)) {
        throw new Error(`${target.type}.${behavior.id} has no explicit evidence list.`);
      }
      if (behavior.status === "covered") {
        if (behavior.evidence.length === 0) {
          throw new Error(`${target.type}.${behavior.id} is marked covered without behavioral evidence.`);
        }
        if (behavior.gap !== undefined) {
          throw new Error(`${target.type}.${behavior.id} is covered but also declares an uncovered gap.`);
        }
      } else if (behavior.status === "uncovered") {
        if (behavior.evidence.length !== 0 || typeof behavior.gap !== "string" || behavior.gap.trim() === "") {
          throw new Error(`${target.type}.${behavior.id} must record one explicit uncovered gap and no evidence.`);
        }
      } else {
        throw new Error(`${target.type}.${behavior.id} has invalid coverage status '${behavior.status}'.`);
      }
      for (const evidence of behavior.evidence) {
        if (!declarationIds.has(evidence)) throw new Error(`${target.type}.${behavior.id} references missing test '${evidence}'.`);
      }
    }
  }
}

export function listDotnetTestProjects(root = repositoryRoot) {
  const testRoot = resolve(root, "languages/csharp/dotnet/tests");
  return readdirSync(testRoot, { withFileTypes: true })
    .filter((entry) => entry.isDirectory() && entry.name.endsWith(".Tests"))
    .filter((entry) => existsSync(resolve(testRoot, entry.name, `${entry.name}.csproj`)))
    .map((entry) => entry.name)
    .sort(ordinal);
}

function validatePolicyShape(policy) {
  if (policy.schemaVersion !== 1) throw new Error("Core test policy schemaVersion must be 1.");
  if (!Array.isArray(policy.primaryLanes) || policy.primaryLanes.length === 0
      || new Set(policy.primaryLanes).size !== policy.primaryLanes.length) {
    throw new Error("Core test policy must declare unique primary lanes.");
  }
  const allowedLanes = new Set(policy.primaryLanes);
  if (!allowedLanes.has(policy.defaultDotnetLane)) throw new Error("The default .NET lane is not allowed.");
  if (!Array.isArray(policy.dotnetLaneRules) || !policy.dotnetLaneRules.every((rule) => allowedLanes.has(rule.lane))) {
    throw new Error("Every .NET lane rule must name an allowed primary lane.");
  }
  if (policy.projectLanes === null || typeof policy.projectLanes !== "object") {
    throw new Error("Core test policy must declare projectLanes.");
  }
  for (const [project, lanes] of Object.entries(policy.projectLanes)) {
    if (lanes.length === 0 || new Set(lanes).size !== lanes.length || lanes.some((lane) => !allowedLanes.has(lane))) {
      throw new Error(`Project '${project}' must name one or more unique allowed lanes.`);
    }
  }
  if (!Array.isArray(policy.reports?.node) || typeof policy.reports?.releaseFixtures !== "string") {
    throw new Error("Core test policy must declare Node and release-fixture reports.");
  }
}

function validateDeclarationIdentity(declarations) {
  const seen = new Set();
  for (const declaration of declarations) {
    if (seen.has(declaration.id)) throw new Error(`Duplicate declared test id '${declaration.id}'.`);
    seen.add(declaration.id);
  }
}

function validateExpectedSkipPolicy(declarations, policy) {
  const allowedLanes = new Set(policy.primaryLanes);
  const ids = new Set();
  for (const expected of policy.expectedSkips ?? []) {
    if (typeof expected.id !== "string" || ids.has(expected.id)) throw new Error("Expected skip ids must be unique strings.");
    ids.add(expected.id);
    if (!allowedLanes.has(expected.owner) || typeof expected.reason !== "string" || expected.reason.trim() === "") {
      throw new Error(`Expected skip '${expected.id}' must declare a reason and allowed owner.`);
    }
    if (expected.runnerReasonPattern !== undefined) new RegExp(expected.runnerReasonPattern, "u");
    if (expected.declarationId !== undefined) {
      const declaration = declarations.find((item) => item.id === expected.declarationId);
      if (declaration === undefined) throw new Error(`Expected skip '${expected.id}' references missing test '${expected.declarationId}'.`);
      if (declaration.primaryLane !== expected.owner) {
        throw new Error(`Expected skip '${expected.id}' is owned by '${expected.owner}', not '${declaration.primaryLane}'.`);
      }
    }
    if (expected.framework === "node" && expected.name !== undefined
        && !declarations.some((item) => item.framework === "node" && item.source === expected.source && item.name === expected.name)) {
      throw new Error(`Expected skip '${expected.id}' references a missing Node declaration.`);
    }
  }
}

function discoverDotnetTests(source, content, policy) {
  const namespace = /\bnamespace\s+([A-Za-z_][A-Za-z0-9_.]*)\s*;/u.exec(content)?.[1];
  if (namespace === undefined) return [];
  const classes = [...content.matchAll(/^[ \t]*(?:public|internal)\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+)*(?:partial\s+)?class\s+([A-Za-z_][A-Za-z0-9_]*)/gmu)]
    .map((match) => ({ index: match.index, name: match[1] }));
  const output = [];
  for (const match of content.matchAll(dotnetTestMethodPattern())) {
    const attributes = match[1];
    if (!/(?:Fact|Theory)(?:Attribute)?\b/u.test(attributes)) continue;
    const containingClass = classes.filter((candidate) => candidate.index < match.index).at(-1)?.name;
    if (containingClass === undefined) continue;
    const name = match[2];
    const project = source.split("/")[4];
    const externalArguments = [...(/ExternalArtifact(?:Fact|Theory)\(([^)]*)\)/u.exec(attributes)?.[1] ?? "")
      .matchAll(/"([^"]+)"/gu)]
      .map((argument) => argument[1]);
    const operatingSystems = operatingSystemsFor(attributes, name, source);
    const declaration = {
      id: `${namespace}.${containingClass}.${name}`,
      framework: "dotnet",
      project,
      source,
      name,
      attributes,
      operatingSystems,
      externalEnvironment: externalArguments[0],
      externalPrerequisites: dotnetPrerequisites(name, source, externalArguments),
    };
    output.push({ ...declaration, primaryLane: dotnetLaneFor(declaration, policy) });
  }
  return output;
}

function discoverNodeTests(source, content) {
  const output = [];
  const patterns = [
    /\btest\s*\(\s*"((?:\\.|[^"\\])*)"/gu,
    /\btest\s*\(\s*'((?:\\.|[^'\\])*)'/gu,
    /\btest\s*\(\s*`((?:\\.|[^`\\])*)`/gu,
  ];
  const seen = new Set();
  for (const pattern of patterns) {
    for (const match of content.matchAll(pattern)) {
      const name = match[1].replaceAll(/\\([\\"'`])/gu, "$1");
      const id = `node:${source}:${name}`;
      if (seen.has(id)) continue;
      seen.add(id);
      output.push({
        id,
        framework: "node",
        project: nodeProject(source),
        source,
        name,
        dynamicTemplate: name.includes("${"),
        operatingSystems: ["Windows", "Linux", "MacOS"],
        externalPrerequisites: [{ kind: "executable", name: "node", minimumMajorVersion: 24, requiredWhenSelected: true }],
        primaryLane: "node",
      });
    }
  }
  return output;
}

function discoverPythonTests(source, content) {
  const output = [];
  let className;
  for (const line of content.split(/\r?\n/u)) {
    const classMatch = /^class\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(/u.exec(line);
    if (classMatch !== null) className = classMatch[1];
    const testMatch = /^\s+def\s+(test_[A-Za-z0-9_]+)\s*\(/u.exec(line);
    if (testMatch === null || className === undefined) continue;
    output.push({
      id: `python:${source}:${testMatch[1]}`,
      framework: "python",
      project: "scripts/release",
      source,
      name: testMatch[1],
      runnerSelector: `${className}.${testMatch[1]}`,
      operatingSystems: ["Windows", "Linux", "MacOS"],
      externalPrerequisites: [{ kind: "executable", name: "python3", requiredWhenSelected: true }],
      primaryLane: "release-artifact",
    });
  }
  return output;
}

function discoverShellFixture(source) {
  const name = source.split("/").at(-1);
  return {
    id: `shell:${source}`,
    framework: "shell",
    project: "scripts/release",
    source,
    name,
    operatingSystems: ["Windows", "Linux", "MacOS"],
    externalPrerequisites: [{ kind: "executable", name: "bash", requiredWhenSelected: true }],
    primaryLane: "release-artifact",
  };
}

function dotnetLaneFor(declaration, policy) {
  for (const rule of policy.dotnetLaneRules) {
    if (rule.project !== undefined && declaration.project !== rule.project) continue;
    if (rule.source !== undefined && !new RegExp(rule.source, "u").test(declaration.source)) continue;
    if (rule.attributes !== undefined && !new RegExp(rule.attributes, "u").test(declaration.attributes)) continue;
    if (rule.name !== undefined && !new RegExp(rule.name, "u").test(declaration.name)) continue;
    return rule.lane;
  }
  return policy.defaultDotnetLane;
}

function dotnetPrerequisites(name, source, externalArguments) {
  const prerequisites = [{ kind: "executable", name: "dotnet", requiredWhenSelected: true }];
  if (/CrossLanguage|Interop/iu.test(`${name} ${source}`)) {
    prerequisites.push({ kind: "executable", name: "node", minimumMajorVersion: 24, requiredWhenSelected: true });
  }
  if (externalArguments[0] !== undefined) {
    prerequisites.push({
      kind: "environment",
      name: externalArguments[0],
      expectedValue: "1",
      requiredWhenSelected: true,
    });
    for (const variable of externalArguments.slice(1)) {
      prerequisites.push(externalInputPrerequisite(variable));
    }
  }
  return [...new Map(prerequisites.map((item) => [`${item.kind}:${item.name}:${item.pathKind ?? ""}`, item])).values()];
}

function operatingSystemsFor(attributes, name, source) {
  if (/MacOs[A-Za-z0-9_]*(?:Fact|Theory)|MacOS/iu.test(`${attributes} ${name} ${source}`)) return ["MacOS"];
  if (/Windows[A-Za-z0-9_]*(?:Fact|Theory)/u.test(attributes)) return ["Windows"];
  if (/Linux[A-Za-z0-9_]*(?:Fact|Theory)/u.test(attributes)) return ["Linux"];
  if (/Unix[A-Za-z0-9_]*(?:Fact|Theory)/u.test(attributes)) return ["Linux", "MacOS"];
  return ["Windows", "Linux", "MacOS"];
}

function externalInputPrerequisite(name) {
  const kinds = {
    SUNDER_AGENT_UI_SNAPSHOT_DIR: "directory",
    SUNDER_AGENT_PACKAGE_ARCHIVE_DIR: "directory",
    SUNDER_NODE_MODULE_PACKAGE_PATH: "file",
    SUNDER_NODE_MODULE_LEAF_PATH: "directory",
    SUNDER_NODE_TEMPLATE_DEV_PATH: "directory",
    SUNDER_DOTNET_WORKER_ARCHIVE_PATH: "file",
    SUNDER_DOTNET_TEMPLATE_DEV_PATH: "directory",
    SUNDER_LIVE_PLATFORM_INPUT: "file",
    SUNDER_LIVE_REGISTRY_IMPORTS_INPUT: "file",
    SUNDER_LIVE_INSTALLATION_INPUT: "file",
    SUNDER_LIVE_PLATFORM_PHASE: "environment",
    SUNDER_LIVE_REGISTRY_IMPORTS_PHASE: "environment",
    SUNDER_LIVE_INSTALLATION_PHASE: "environment",
  };
  const kind = kinds[name];
  if (kind === undefined) throw new Error(`External artifact input '${name}' has no declared kind.`);
  return kind === "environment"
    ? { kind, name, requiredWhenSelected: true }
    : { kind: "environmentPath", name, pathKind: kind, requiredWhenSelected: true };
}

function testSourceFiles(root) {
  const paths = [];
  walk(resolve(root, "languages/csharp/dotnet/tests"), (path) => extname(path) === ".cs", paths, root);
  walk(resolve(root, "languages/typescript/node"), (path) =>
    extname(path) === ".ts" && path.split(sep).includes("test"), paths, root);
  walk(resolve(root, "scripts"), (path) =>
    path.endsWith(".test.mjs")
    || path.endsWith(".fixture.sh")
    || /^test_.*\.py$/u.test(path.split(sep).at(-1) ?? ""), paths, root);
  return [...new Set(paths)].sort(ordinal);
}

function walk(directory, include, output, root) {
  if (!existsSync(directory)) return;
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    if (entry.isDirectory() && ["bin", "obj", "node_modules", "dist", "dist-test"].includes(entry.name)) continue;
    const path = resolve(directory, entry.name);
    if (entry.isDirectory()) walk(path, include, output, root);
    else if (entry.isFile() && include(path)) output.push(relative(root, path).split(sep).join("/"));
  }
}

function dotnetTestMethodPattern() {
  const attribute = String.raw`\[(?:[^\]"']|"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*')*\]`;
  return new RegExp(
    `((?:\\s*${attribute}\\s*)+)\\s*public\\s+(?:async\\s+)?(?:[A-Za-z_][A-Za-z0-9_]*\\.)*(?:void|Task(?:<[^>]+>)?|ValueTask(?:<[^>]+>)?)\\s+([A-Za-z_][A-Za-z0-9_]*)\\s*\\(`,
    "gu",
  );
}

function nodeProject(source) {
  const match = /languages\/typescript\/node\/packages\/([^/]+)\//u.exec(source);
  if (match !== null) return match[1];
  return source.startsWith("scripts/test-oracles/") ? "scripts/test-oracles" : "scripts/release";
}

function ordinal(left, right) {
  return left < right ? -1 : left > right ? 1 : 0;
}
