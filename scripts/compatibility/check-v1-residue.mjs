import { access, readFile, readdir } from "node:fs/promises";
import { dirname, extname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");

const forbiddenPaths = [
  ".github/workflows/sunder-execution-runtime-native-release.yml",
  ".github/workflows/sunder-execution-runtime-publication.yml",
  "scripts/release/execution-runtimes/native-release-preflight.sh",
  "scripts/release/execution-runtimes/native-release.sh",
  "scripts/release/execution-runtimes/publication_evidence.py",
  "scripts/release/execution-runtimes/publish-release-set.sh",
  "scripts/release/execution-runtimes/retained_native.py",
  "scripts/release/execution-runtimes/validate-lock.sh",
  "scripts/release/upload-execution-runtime-release-evidence.sh",
  "scripts/test/release/publication-dry-run.fixture.sh",
  "scripts/test/release/publication-resume.fixture.sh",
  "scripts/test/release/upload-execution-runtime-release-evidence.fixture.sh",
  ".github/workflows/sunder-node-sea-ci.yml",
  "languages/typescript/node/packages/package-tool/node-distributions.json",
  "languages/typescript/node/packages/package-tool/src/download.ts",
  "languages/typescript/node/packages/package-tool/src/node-cache.ts",
  "languages/typescript/node/packages/package-tool/src/sea.ts",
  "languages/typescript/node/packages/sdk/src/descriptor-v2.ts",
  "specs/rpc/v2",
  "specs/worker/v3",
];

const scanRoots = [
  ".github",
  "docs",
  "eng",
  "languages",
  "scripts",
  "specs",
];

const rootScanFiles = [
  ".dockerignore",
  ".gitignore",
  ".npmrc",
  ".nvmrc",
  "AGENTS.md",
  "CODE_OF_CONDUCT.md",
  "CONTRIBUTING.md",
  "LICENSE",
  "opencode.json",
  "README.md",
  "SECURITY.md",
  "global.json",
  "Sunder.Core.slnx",
];

const ignoredGeneratedFiles = new Set([
  // Protected migration captures are inert history, never regenerated or joined
  // to current runtime/build checks. Current candidates are scanned normally.
  "eng/compatibility/core-compatibility-candidate.json",
  "eng/compatibility/stack-contributor-rpc-migration-baseline.json",
  "languages/csharp/dotnet/tests/Sunder.Sdk.PublicApi.Tests/StackContributorRpcMigrationBaselineTests.cs",
]);

const scannerFiles = new Set([
  "scripts/compatibility/check-v1-residue.mjs",
  "scripts/test-oracles/v1-residue.test.mjs",
]);

const residueRules = [
  { id: "manifest-v3-v4", pattern: /manifestVersion\\?["']?\s*[:=]\s*\\?["']?[34]\b/u },
  { id: "worker-wire-v3", pattern: /sunder\.worker\.v3/u },
  { id: "worker-capability-v3", pattern: /worker-protocol\.v3/u },
  { id: "rpc-capability-v2", pattern: /rpc\.v2/u },
  { id: "descriptor-v2", pattern: /DescriptorVersionV2|["']?descriptorVersion["']?\s*[:=]\s*2\b/u },
  { id: "browser-bridge-v2", pattern: /sunder\.bridge\.v2/u },
  { id: "runtime-feature-v2", pattern: /(?:schema-first-rpc|rpc-permissions|app-web-rpc)\.v2/u },
  { id: "runtime-revision-8", pattern: /(?:Current|MinimumSupported|MaximumSupported)Revision\s*=\s*8\b|Runtime protocol revision `?8`?|["']?runtimeProtocolRevision["']?\s*[:=]\s*8\b/u },
  { id: "host-revision-2", pattern: /Host protocol revision `?2`?|["']?hostProtocolRevision["']?\s*[:=]\s*2\b/u },
  { id: "package-self-contained", pattern: /<SelfContained>true<\/SelfContained>/u },
  { id: "package-apphost", pattern: /<UseAppHost>true<\/UseAppHost>/u },
  { id: "package-apphost-signing", pattern: /platform-sign (?:its|the) apphost and native closure/u },
  { id: "package-single-file", pattern: /<PublishSingleFile>true<\/PublishSingleFile>/u },
  { id: "node-sea", pattern: /NODE_SEA_BLOB|SUNDER_(?:REQUIRE_)?NODE_SEA|sunder\.sea\.smoke|\bnodeSea\b|\bpostject\b|sentinel fuse/u },
  { id: "direct-worker", pattern: /direct\/self-contained Worker|self-contained Worker V[134]/u },
];

const activeNodeProfilePatterns = [
  />=24\.0\.0(?: <25\.0\.0)?/u,
  /nodeSea/u,
  /sunder\.sea\.smoke/u,
  /SUNDER_(?:REQUIRE_)?NODE_SEA/u,
];

// These exact development dependencies carry their publishers' engine ranges.
// They do not choose Sunder's supported runtime floor. Only the engines.node
// member of the reviewed, canonical lock entry is exempt from the floor check.
const reviewedVendorNodeProfiles = new Map([
  ["@exodus/bytes", "1.16.0"],
  ["html-encoding-sniffer", "6.0.0"],
  ["jsdom", "27.4.0"],
]);
const reviewedVendorNodeRange = "^20.19.0 || ^22.12.0 || >=24.0.0";

const exactFiles = new Map([
  ["languages/csharp/dotnet/host/Sunder.App/Services/AppWebBridge.cs", [/sunder\.bridge\.v2/u]],
  ["languages/typescript/node/packages/sdk/src/browser.ts", [/sunder\.bridge\.v2/u]],
  [".github/workflows/sunder-sdk-release.yml", [/<SelfContained>true<\/SelfContained>/u, /<UseAppHost>true<\/UseAppHost>/u]],
  ["docs/SUNDER-CORE-COMPATIBILITY-CANDIDATE.md", [
    /remain on Manifest V3/u,
    /self-contained AppHost publish closure/u,
  ]],
  ["docs/SUNDER-PACKAGE-DEVELOPMENT.md", [/per self-contained leaf/u]],
  ["docs/SUNDER-PACKAGE-STANDARD.md", [/self-contained exact-RID leaf/u]],
  ["docs/package-development/BUILD-PUBLISH-VERSIONING.md", [/complete self-contained publish closure/u]],
  ["docs/package-development/GETTING-STARTED.md", [/self-contained Worker leaves/u]],
  ["docs/package-development/PACKAGE-ANATOMY.md", [/complete self-contained executable closure/u]],
  [".github/workflows/sunder-ci.yml", [
    ...activeNodeProfilePatterns,
    /NODE_SEA_BLOB/u,
    /postject/u,
    /allowUnsignedWindows|allow-unsigned-windows/u,
  ]],
  ["docs/SUNDER-NODE-PROCESS-PACKAGES.md", activeNodeProfilePatterns],
  ["eng/release/worker-v1-thresholds.json", activeNodeProfilePatterns],
  ["languages/csharp/dotnet/tests/Sunder.Runtime.Host.Tests/CrossLanguageWorkerRuntimeHostTests.cs", activeNodeProfilePatterns],
  ["languages/typescript/node/package-lock.json", activeNodeProfilePatterns],
  ["languages/typescript/node/package.json", activeNodeProfilePatterns],
  ["languages/typescript/node/packages/create-sunder-package/package.json", activeNodeProfilePatterns],
  ["languages/typescript/node/packages/create-sunder-package/template/package.json", activeNodeProfilePatterns],
  ["languages/typescript/node/packages/create-sunder-package/template-react-node/package.json", activeNodeProfilePatterns],
  ["languages/typescript/node/packages/package-tool/package.json", activeNodeProfilePatterns],
  ["languages/typescript/node/packages/package-tool/README.md", activeNodeProfilePatterns],
  ["languages/typescript/node/packages/sdk/package.json", activeNodeProfilePatterns],
  ["specs/worker/v1/conformance/corpus.json", activeNodeProfilePatterns],
]);

const allowedMatches = new Map([
  allowance("languages/csharp/dotnet/tests/Sunder.Core.TestSupport.Tests/TestSupportFaultProbeTests.cs", "manifest-v3-v4", 3, "negative manifest fixtures"),
  allowance("languages/csharp/dotnet/tests/Sunder.Package.Build.Tests/PackageBuildManifestTests.cs", "worker-capability-v3", 4, "negative build fixtures and assertions"),
  allowance("languages/csharp/dotnet/tests/Sunder.Package.Format.Tests/SunderPackageArchiveInspectorTests.cs", "worker-capability-v3", 1, "negative archive fixture"),
  allowance("languages/csharp/dotnet/tests/Sunder.Package.Templates.Tests/PackageTemplateTests.cs", "worker-capability-v3", 1, "negative template assertion"),
  allowance("languages/csharp/dotnet/tests/Sunder.Runtime.Host.Tests/RuntimeArchitectureRatchetTests.cs", "worker-wire-v3", 1, "negative architecture inventory"),
  allowance("languages/csharp/dotnet/tests/Sunder.Runtime.Host.Tests/RuntimePackageManifestValidatorTests.cs", "worker-capability-v3", 1, "negative manifest fixture"),
  allowance("languages/csharp/dotnet/tests/Sunder.Runtime.Host.TestWorker/Program.cs", "worker-wire-v3", 1, "negative protocol fixture"),
  allowance("languages/csharp/dotnet/tests/Sunder.Sdk.Worker.Tests/WorkerV1RuntimeTests.cs", "worker-wire-v3", 1, "negative Worker SDK fixture"),
  allowance("languages/typescript/node/packages/package-tool/test/package-tool.test.ts", "rpc-capability-v2", 1, "negative Node capability fixture"),
  allowance("languages/typescript/node/packages/package-tool/test/package-tool.test.ts", "worker-capability-v3", 1, "negative Node capability fixture"),
  allowance("languages/typescript/node/packages/package-tool/test/package-tool.test.ts", "node-sea", 2, "negative SEA dependency assertions"),
  allowance("languages/typescript/node/packages/package-tool/test/package-tool.test.ts", "descriptor-v2", 1, "negative descriptor fixture"),
  allowance("languages/typescript/node/packages/package-tool/test/registry.test.ts", "descriptor-v2", 1, "negative Registry descriptor fixture"),
  allowance(".github/workflows/sunder-cli-release.yml", "package-single-file", 1, "unrelated CLI executable profile"),
  allowance("languages/csharp/dotnet/tests/Sunder.Runtime.Host.Tests/Sunder.Runtime.Host.Tests.csproj", "package-apphost", 1, "test fixture executable profile"),
]);

const sourceExtensions = new Set([
  ".axaml", ".cjs", ".cs", ".cshtml", ".csproj", ".css", ".hcl", ".html", ".js", ".json",
  ".jsx", ".md", ".mjs", ".proj", ".props", ".proto", ".ps1", ".py", ".razor", ".resx",
  ".ruleset", ".sh", ".slnx", ".sql", ".swift", ".targets", ".tf", ".toml", ".ts", ".tsx",
  ".txt", ".xaml", ".xml", ".yaml", ".yml",
]);

const sourceFileNames = new Set([
  ".dockerignore", ".editorconfig", ".gitignore", ".ignore", ".npmrc", ".nvmrc", "Dockerfile", "LICENSE",
]);

export async function findV1Residue(repositoryRoot = scriptRoot) {
  const failures = [];
  const usedAllowances = new Map();
  for (const relativePath of forbiddenPaths) {
    if (await exists(resolve(repositoryRoot, relativePath))) {
      failures.push(`${relativePath}: obsolete path still exists`);
    }
  }

  const inspectFile = async (filePath) => {
    const relativePath = relative(repositoryRoot, filePath);
    if (ignoredGeneratedFiles.has(relativePath) || scannerFiles.has(relativePath)) return;
    const text = await readFile(filePath, "utf8");
    for (const [index, line] of text.split(/\r?\n/u).entries()) {
      for (const rule of residueRules) {
        if (rule.pattern.test(line) && !isAllowedMatch(relativePath, rule.id, usedAllowances)) {
          failures.push(`${relativePath}:${index + 1}: contains forbidden ${rule.id} residue`);
        }
      }
    }
  };

  for (const relativeRoot of scanRoots) {
    const absoluteRoot = resolve(repositoryRoot, relativeRoot);
    if (!await exists(absoluteRoot)) continue;
    for (const filePath of await sourceFiles(absoluteRoot)) {
      await inspectFile(filePath);
    }
  }
  for (const relativePath of rootScanFiles) {
    const filePath = resolve(repositoryRoot, relativePath);
    if (await exists(filePath)) await inspectFile(filePath);
  }

  for (const [relativePath, patterns] of exactFiles) {
    const filePath = resolve(repositoryRoot, relativePath);
    if (!await exists(filePath)) continue;
    const text = await readFile(filePath, "utf8");
    for (const pattern of patterns) {
      const inspected = relativePath === "languages/typescript/node/package-lock.json"
        && pattern === activeNodeProfilePatterns[0] ? withoutReviewedVendorNodeProfiles(text) : text;
      if (pattern.test(inspected)) failures.push(`${relativePath}: contains ${pattern}`);
    }
  }

  return failures.sort();
}

function withoutReviewedVendorNodeProfiles(text) {
  let lock;
  try {
    lock = JSON.parse(text);
  } catch {
    return text;
  }
  if (lock?.lockfileVersion !== 3 || !lock.packages || Array.isArray(lock.packages)) return text;
  for (const [name, version] of reviewedVendorNodeProfiles) {
    const key = `node_modules/${name}`;
    const entry = lock.packages[key];
    const leaf = name.slice(name.lastIndexOf("/") + 1);
    if (entry?.version !== version || entry.dev !== true
        || entry.resolved !== `https://registry.npmjs.org/${name}/-/${leaf}-${version}.tgz`
        || entry.engines?.node !== reviewedVendorNodeRange) continue;
    const serialize = (value) => `    ${JSON.stringify(key)}: ${JSON.stringify(value, null, 2).replaceAll("\n", "\n    ")}`;
    // Replace one complete canonical entry only. A duplicate entry or another
    // occurrence anywhere else remains visible to the normal residue check.
    text = text.replace(serialize(entry), serialize({
      ...entry, engines: { ...entry.engines, node: "reviewed vendor development engine" },
    }));
  }
  return text;
}

async function sourceFiles(root) {
  const results = [];
  for (const entry of await readdir(root, { withFileTypes: true })) {
    const path = resolve(root, entry.name);
    if (entry.isDirectory()) {
      if (!["bin", "dist", "dist-test", "node_modules", "obj"].includes(entry.name)) {
        results.push(...await sourceFiles(path));
      }
    } else if (sourceExtensions.has(extname(entry.name)) || sourceFileNames.has(entry.name)) {
      results.push(path);
    }
  }
  return results;
}

function allowance(path, ruleId, count, reason) {
  return [`${path}\0${ruleId}`, { count, reason }];
}

function isAllowedMatch(path, ruleId, usedAllowances) {
  const key = `${path}\0${ruleId}`;
  const allowed = allowedMatches.get(key);
  if (allowed === undefined) return false;
  const used = usedAllowances.get(key) ?? 0;
  if (used >= allowed.count) return false;
  usedAllowances.set(key, used + 1);
  return true;
}

function relative(root, path) {
  return path.slice(resolve(root).length + 1).replaceAll("\\", "/");
}

async function exists(path) {
  try {
    await access(path);
    return true;
  } catch {
    return false;
  }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const failures = await findV1Residue();
  if (failures.length > 0) {
    console.error(failures.join("\n"));
    process.exitCode = 1;
  } else {
    console.log("Active Core surfaces contain no supported pre-V1 runtime architecture residue.");
  }
}
