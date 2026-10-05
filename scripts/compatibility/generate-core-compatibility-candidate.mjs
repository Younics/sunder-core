import { createHash } from "node:crypto";
import { existsSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { dirname, extname, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const ledgerRelativePath = "eng/compatibility/core-compatibility-candidate.json";
const ledgerPath = resolve(repositoryRoot, ledgerRelativePath);
const cliProjectRelativePath = "languages/csharp/dotnet/host/Sunder.Cli/Sunder.Cli.csproj";
const cliAssemblyRelativePath = "languages/csharp/dotnet/host/Sunder.Cli/bin/Release/net10.0/sunder.dll";
const cliAssembly = resolve(repositoryRoot, cliAssemblyRelativePath);
const nodeWorkspaceRelativePath = "languages/typescript/node";
const arguments_ = process.argv.slice(2);
const write = arguments_.includes("--write");
const check = arguments_.includes("--check");
if (write === check) fail("Specify exactly one of --write or --check.");
if (arguments_.some((argument) => argument !== "--write" && argument !== "--check")) {
  fail("Only --write or --check is supported.");
}
buildCandidateInputs();
if (!existsSync(cliAssembly)) fail(`CLI assembly was not found: ${cliAssembly}`);

const candidate = generateCandidate();
const generated = `${JSON.stringify(candidate, null, 2)}\n`;
if (write) {
  writeFileSync(ledgerPath, generated, "utf8");
  process.stdout.write(`Wrote ${ledgerRelativePath}\n`);
} else {
  if (!existsSync(ledgerPath)) fail(`Compatibility candidate is missing: ${ledgerRelativePath}`);
  const current = readFileSync(ledgerPath, "utf8").replaceAll("\r\n", "\n");
  if (current !== generated) {
    fail(
      `${ledgerRelativePath} is stale. Regenerate it with:\n`
      + "node scripts/compatibility/generate-core-compatibility-candidate.mjs --write",
    );
  }
  process.stdout.write(`Verified ${ledgerRelativePath}\n`);
}

function generateCandidate() {
  const coordinated = readText("languages/csharp/dotnet/Directory.Build.props");
  const developerVersion = matchRequired(
    coordinated,
    /<SunderDeveloperPackageVersion>([^<]+)<\/SunderDeveloperPackageVersion>/u,
    "coordinated developer package version",
  );
  const developerVersionRange = matchRequired(
    coordinated,
    /<SunderDeveloperPackageVersionRange>([^<]+)<\/SunderDeveloperPackageVersionRange>/u,
    "coordinated developer package version range",
  );
  const publicApiBaselines = collectPublicApiBaselines();
  const nugetProjects = [
    "Sunder.Sdk",
    "Sunder.Sdk.Avalonia",
    "Sunder.Sdk.Stacks",
    "Sunder.Sdk.Worker",
    "Sunder.Package.Build",
    "Sunder.Package.Templates",
  ];
  const tags = gitLines(["tag", "--list"])
    .filter((tag) => /^(?:app|cli|host|sdk)\/v\d+\.\d+\.\d+$/u.test(tag))
    .sort(compareOrdinal);
  const runtimeProblems = JSON.parse(readText(
    "languages/csharp/dotnet/tests/Sunder.Runtime.Host.Tests/Fixtures/ProblemDetails.golden.json",
  ));
  const rpcContractsPath = "languages/csharp/dotnet/packages/Sunder.Sdk/Rpc/SunderRpcContracts.cs";
  const packageFormatPath = "languages/csharp/dotnet/libraries/Sunder.Package.Format/SunderPackageFormat.cs";
  const stackFormatPath = "languages/csharp/dotnet/libraries/Sunder.Package.Format/SunderStackFormat.cs";
  const projectionFormatPath = "languages/csharp/dotnet/libraries/Sunder.Package.Format/SunderPackageProjectionFormat.cs";

  return {
    schemaVersion: 1,
    candidateId: "sunder-core-v1-pre-implementation-first-migration",
    status: "phase-0-reopened-migration-oracle",
    generatedBy: "scripts/compatibility/generate-core-compatibility-candidate.mjs",
    generatorSha256: sourceFingerprint("scripts/compatibility/generate-core-compatibility-candidate.mjs").sha256,
    decisions: {
      retained: [
        {
          surface: "RPC descriptor V1",
          classification: "current canonical C# deployable schema",
          deployableInManifestV1: true,
          evidence: [
            "specs/rpc/v1/README.md",
            "languages/csharp/dotnet/tests/Sunder.Sdk.PublicApi.Tests/SunderRpcContractDescriptorTests.cs",
            "languages/csharp/dotnet/tests/Sunder.Package.Build.Tests/PackageBuildManifestTests.cs",
          ],
        },
        {
          surface: "App Runtime and Registry composite interfaces",
          classification: "unshipped but active composition boundaries",
          evidence: [
            "languages/csharp/dotnet/host/Sunder.App/Services/IRuntimeApiClient.cs",
            "languages/csharp/dotnet/host/Sunder.App/Services/IRegistryApiClient.cs",
          ],
        },
        {
          surface: "archive/content-index/Stack/value-profile .v1 identities",
          classification: "current contracts; version suffix does not mean obsolete",
        },
      ],
      removed: [
        {
          surface: "SunderStackContributorRpc.CreateWorkerProviderRegistration",
          classification: "approved unshipped Worker-specific convenience",
          replacement: "Worker packages construct SunderWorkerProviderRegistration at their Worker boundary.",
          effect: "Sunder.Sdk.Stacks no longer references or packages Sunder.Sdk.Worker.",
        },
      ],
      pendingCutovers: [
        "implementation-first-csharp-and-node-rpc-authoring",
        "protocol-project-package-and-authored-descriptor-retirement",
        "source-derived-signed-platform-contract-catalog",
      ],
      rationale: "The technical V1 reset remains evidence while implementation-first RPC authoring replaces descriptor-first Protocol projects without changing immutable semantic contract tuples.",
    },
    publicArtifacts: {
      coordinatedVersion: developerVersion,
      coordinatedNuGetRange: developerVersionRange,
      nuget: nugetProjects.map((name) => nugetArtifact(name, developerVersion, publicApiBaselines)),
      dotnetApiBaselines: publicApiBaselines,
      hostContracts: baselineReference(publicApiBaselines, "Sunder.Host.Contracts"),
      runtimeContracts: baselineReference(publicApiBaselines, "Sunder.Runtime.Contracts"),
      registryContracts: baselineReference(publicApiBaselines, "Sunder.Registry.Contracts"),
      packageBuildMsbuild: collectMsbuildSurface(publicApiBaselines),
      npm: collectNpmSurfaces(),
      npmReleaseContract: {
        workspaceManifest: sourceFingerprint("languages/typescript/node/package.json"),
        lockfile: sourceFingerprint("languages/typescript/node/package-lock.json"),
        inventory: sourceFingerprint("eng/test-oracles/core-public-packages.json"),
        verifier: sourceFingerprint("scripts/release/verify-node-package-artifacts.mjs"),
      },
      cli: collectCliSurface(),
    },
    protocols: {
      rpcDescriptor: {
        current: readIntegerConstant(rpcContractsPath, "DescriptorVersion"),
        standaloneCompatibility: [],
        unversionedConstantSelects: readIntegerConstant(rpcContractsPath, "DescriptorVersion"),
        valueProfile: readStringConstant(rpcContractsPath, "ValueSchemaProfile"),
        packageManifestV1RequiresDescriptor: 1,
      },
      runtime: constantsFromBaseline(publicApiBaselines, "Sunder.Runtime.Contracts", "RuntimeProtocol"),
      host: constantsFromBaseline(publicApiBaselines, "Sunder.Host.Contracts", "HostProtocol"),
      browserBridge: {
        identity: "sunder.bridge.v1",
        version: 1,
        resourceProfileDeclaration: sourceFingerprint(
          "languages/typescript/node/packages/sdk/src/browser.ts",
        ),
        injectedImplementationSources: [
          sourceFingerprint("languages/csharp/dotnet/host/Sunder.App/Services/AppWebBridge.cs"),
          sourceFingerprint("languages/csharp/dotnet/host/Sunder.App/Services/AppWebBootstrapScriptGenerator.cs"),
          sourceFingerprint("languages/csharp/dotnet/host/Sunder.App/Services/AppWebPendingRequest.cs"),
          sourceFingerprint("languages/csharp/dotnet/host/Sunder.App/Services/AppWebPendingRequestRegistry.cs"),
        ],
      },
      workerConformance: listFiles("specs/worker/v1/conformance").map(sourceFingerprint),
    },
    http: {
      runtime: {
        authentication: "Bearer token required by RuntimeBearerAuthenticationMiddleware for every route",
        rateLimit: "none",
        endpoints: collectRuntimeEndpoints(),
        statusAndErrorMetadata: "Minimal APIs do not declare complete OpenAPI response metadata; exact implementations are frozen by source hash and the Runtime Problem Details golden inventory.",
        metadataSources: runtimeHttpMetadataSources(),
      },
      host: {
        authentication: "Bearer token required by Host Supervisor middleware for every route",
        rateLimit: "none",
        endpoints: collectHostEndpoints(),
        statusAndErrorMetadata: "Exact Host endpoint, gateway, authentication, and response implementations are frozen by source hash.",
        metadataSources: hostHttpMetadataSources(),
      },
      registryClients: {
        trustBoundary: "bare configured Registry origin; redirects and artifact URLs remain same-origin",
        endpoints: collectRegistryClientEndpoints(),
        metadataSources: registryClientMetadataSources(),
      },
    },
    errors: {
      runtimeProblemDetails: runtimeProblems,
      hostCodes: collectStringCodes(
        "languages/csharp/dotnet/host/Sunder.Host.Supervisor",
        /^host\.[a-z0-9.-]+$/u,
        new Set(["host.json"]),
      ),
      registryCodes: collectStringCodes(
        "languages/csharp/dotnet/libraries/Sunder.Registry.Contracts",
        /^registry\.v1\.[a-z0-9._-]+$/u,
      ),
      cli: collectCliErrors(),
      runtimeRpcValidationClassification: {
        mode: "structured code and path emitted by SunderRpcSchemaProfile",
        sources: [
          sourceFingerprint("languages/csharp/dotnet/packages/Sunder.Sdk/Rpc/SunderRpcSchemaProfile.cs"),
          sourceFingerprint("languages/csharp/dotnet/packages/Sunder.Sdk/Rpc/SunderRpcSchemaProfile.InstanceValidation.cs"),
          sourceFingerprint("languages/csharp/dotnet/packages/Sunder.Sdk/Rpc/SunderRpcSchemaProfile.SchemaForm.cs"),
          sourceFingerprint("languages/csharp/dotnet/packages/Sunder.Sdk/Rpc/SunderRpcSchemaProfile.SchemaGraph.cs"),
          sourceFingerprint("languages/csharp/dotnet/packages/Sunder.Sdk/Rpc/SunderRpcSchemaProfile.SchemaLiterals.cs"),
          sourceFingerprint("languages/csharp/dotnet/packages/Sunder.Sdk/Rpc/SunderRpcSchemaProfile.ValueValidation.cs"),
        ],
      },
    },
    configuration: collectConfiguration(),
    persistence: collectPersistence(),
    telemetry: collectTelemetry(),
    schemas: {
      packageArchive: readIntegerConstant(packageFormatPath, "CurrentArchiveFormatVersion"),
      packageManifest: readIntegerConstant(packageFormatPath, "CurrentManifestVersion"),
      packageContentIndex: readIntegerConstant(packageFormatPath, "CurrentContentIndexVersion"),
      projection: readIntegerConstant(projectionFormatPath, "CurrentProjectionFormatVersion"),
      projectionContentIndex: readIntegerConstant(projectionFormatPath, "CurrentContentIndexVersion"),
      stack: readIntegerConstant(stackFormatPath, "CurrentSchemaVersion"),
      stackContentIndex: readIntegerConstant(stackFormatPath, "CurrentContentIndexVersion"),
      stackReader: readIntegerConstant(stackFormatPath, "CurrentReaderVersion"),
      nodeProject: readTypeLiteral(
        "languages/typescript/node/packages/package-tool/src/model.ts",
        "schemaVersion",
      ),
      localStackIndex: readIntegerConstant(
        "languages/csharp/dotnet/host/Sunder.App/Services/LocalStackLibraryService.cs",
        "CurrentSchemaVersion",
      ),
      generatedOutput: {
        dotnetOwner: 2,
        nodeOwner: 2,
        processMarker: 1,
        transaction: 1,
        evidence: [
          sourceFingerprint("languages/csharp/dotnet/packages/Sunder.Package.Build/Tasks/GeneratedOutputLock.cs"),
          sourceFingerprint("languages/csharp/dotnet/packages/Sunder.Package.Build/Tasks/GeneratedOutputTransaction.cs"),
          sourceFingerprint("languages/typescript/node/packages/package-tool/src/output.ts"),
        ],
      },
      releaseEvidence: 1,
      npmLockfile: JSON.parse(readText("languages/typescript/node/package-lock.json")).lockfileVersion,
      directRejectionTests: [
        "SunderPackageArchiveInspectorTests.ExtractAndValidateAsync_RejectsOldAndFutureArchiveFormatVersions",
        "SunderPackageArchiveInspectorTests.ExtractAndValidateAsync_RejectsOldAndFutureManifestVersions",
        "SunderPackageArchiveInspectorTests.ExtractAndValidateAsync_RejectsOldAndFutureContentIndexVersions",
        "SunderPackageProjectionArchiveTests.Inspector_RejectsOldAndFutureProjectionFormatVersions",
        "SunderPackageProjectionArchiveTests.Inspector_RejectsOldAndFutureProjectionContentIndexVersions",
        "SunderStackArchiveInspectorTests.ExtractAndValidateAsync_RejectsOldAndFutureManifestSchemaVersions",
        "SunderStackArchiveInspectorTests.ExtractAndValidateAsync_RejectsOldAndFutureContentIndexVersions",
        "SunderStackArchiveInspectorTests.ExtractAndValidateAsync_RejectsFutureReaderVersion",
        "SunderStackArchiveInspectorTests.ValidateExtractedStackAsync_RejectsDuplicateMetadataProperties",
        "LocalStackLibraryServiceTests.ListAsync_WhenIndexVersionIsNotCurrent_RejectsAndPreservesIndex",
        "LocalStackLibraryServiceTests.ListAsync_WhenIndexIsMalformed_RejectsAndPreservesIndex",
        "package-tool: schema V2 rejects schema V1 and closed-shape V1 aliases with migration diagnostics",
      ],
    },
    templates: collectTemplates(),
    releases: {
      repository: "https://github.com/Younics/sunder-core",
      publicTags: tags,
      latestSdkTag: latestTag(tags, "sdk/v"),
      latestAppTag: latestTag(tags, "app/v"),
      latestCliTag: latestTag(tags, "cli/v"),
      latestHostTag: latestTag(tags, "host/v"),
      tagFamilies: ["app/v<semver>", "cli/v<semver>", "host/v<semver>", "sdk/v<semver>"],
      workerV1Thresholds: sourceFingerprint("eng/release/worker-v1-thresholds.json"),
      workflows: listFiles(".github/workflows")
        .filter((path) => /(?:ci|release|validation|promotion)\.yml$/u.test(path))
        .map(sourceFingerprint),
    },
    artifactIdentities: {
      packageArchive: { extension: ".sunderpkg", mediaType: "application/vnd.sunder.package" },
      stackArchive: { extension: ".sunderstack", mediaType: "application/vnd.sunder.stack" },
      projectionArchive: { extension: ".sunderprojection" },
      runtimeTarget: { kind: "worker", protocol: "sunder.worker.v1" },
      supportedRids: readQuotedArray(packageFormatPath, "Rids"),
      executables: ["Sunder.App", "Sunder.Host.Supervisor", "Sunder.Runtime.Host", "sunder"],
      payloadPolicy: {
        pdb: "included for authored managed payload files; Host-owned framework PDBs are excluded",
        xmlDocumentation: "excluded from public developer NuGet and runtime package payloads",
        evidence: {
          publicDeveloperNuget: sourceFingerprint("languages/csharp/dotnet/Directory.Build.props"),
          runtimePackage: sourceFingerprint(
            "languages/csharp/dotnet/packages/Sunder.Package.Build/build/Sunder.Package.Build.targets",
          ),
          releaseVerification: sourceFingerprint("scripts/release/verify-nuget-package-set.py"),
        },
      },
    },
    crossRepositoryBoundaries: {
      registryPublicOrigin: "Registry-owned; Core defines no Registry:PublicOrigin or Registry:Artifacts:PublicOrigin key",
      registryOperationMetadata: "Registry-server-owned; no Registry endpoint-name heuristic exists in Core",
      cloudFront: "not present in Core source or configuration",
    },
    phase2Deltas: [
      "Reset RPC descriptor semantics to one DescriptorVersion V1 selected by the unversioned DescriptorVersion constant.",
      "Added Sunder.Host.Contracts and Sunder.Package.Build to the existing .NET API baseline gate.",
      "Aligned @sunder/sdk/browser resourceProfile declarations with the injected bridge object.",
      "Replaced Runtime message-based RPC validation classification with structured validator metadata.",
      "Applied duplicate-property rejection to Stack manifest and content-index metadata.",
      "Added direct old/future schema rejection coverage.",
      "Versioned the local Stack index and replaced silent corruption fallback with preserved explicit rejection.",
      "Added explicit public/internal C# RPC binding accessibility with public compatibility defaults and fail-closed MSBuild validation.",
      "Excluded generated XML documentation companions from public developer NuGets and runtime package payloads while retaining authored managed PDBs.",
      "Removed the public Stack Worker registration convenience surface and the Sunder.Sdk.Stacks dependency on Sunder.Sdk.Worker.",
    ],
    allowedFutureDeltas: [
      "implementation-first-csharp-and-node-rpc-authoring",
      "protocol-project-package-and-authored-descriptor-retirement",
      "source-derived-signed-platform-contract-catalog",
    ],
    verificationGates: [
      "node scripts/compatibility/generate-core-compatibility-candidate.mjs --check",
      "dotnet test languages/csharp/dotnet/tests/Sunder.Sdk.PublicApi.Tests/Sunder.Sdk.PublicApi.Tests.csproj --no-restore",
      "dotnet test languages/csharp/dotnet/tests/Sunder.Registry.Contracts.Tests/Sunder.Registry.Contracts.Tests.csproj --no-restore",
      "dotnet test languages/csharp/dotnet/tests/Sunder.Cli.Tests/Sunder.Cli.Tests.csproj --no-restore",
      "dotnet test languages/csharp/dotnet/tests/Sunder.Package.Build.Tests/Sunder.Package.Build.Tests.csproj --no-restore",
      "dotnet test languages/csharp/dotnet/tests/Sunder.Package.Format.Tests/Sunder.Package.Format.Tests.csproj --no-restore",
      "dotnet test languages/csharp/dotnet/tests/Sunder.Package.Templates.Tests/Sunder.Package.Templates.Tests.csproj --no-restore",
      "python3 scripts/release/test_verify_nuget_package_set.py",
      "bash scripts/release/resolve-github-release.fixture.sh",
      "bash scripts/release/upload-github-release-assets.fixture.sh",
      "bash scripts/release/verify-release-promotion.fixture.sh",
      "bash scripts/release/verify-production-acceptance.fixture.sh",
      "node --test scripts/release/verify-release-promotion-workflows.test.mjs",
      "npm test --prefix languages/typescript/node",
      "dotnet build Sunder.Core.slnx --no-restore",
      "dotnet test Sunder.Core.slnx -c Release --no-restore --no-build",
    ],
  };
}

function collectPublicApiBaselines() {
  const directory = "languages/csharp/dotnet/tests/Sunder.Sdk.PublicApi.Tests/PublicApi";
  return listFiles(directory)
    .filter((path) => path.endsWith(".txt"))
    .map((path) => ({
      assembly: path.slice(path.lastIndexOf("/") + 1, -4),
      ...sourceFingerprint(path),
    }));
}

function baselineReference(baselines, assembly) {
  return baselines.find((item) => item.assembly === assembly)
    ?? fail(`Public API baseline is missing for ${assembly}.`);
}

function nugetArtifact(name, version, baselines) {
  const project = `languages/csharp/dotnet/packages/${name}/${name}.csproj`;
  if (!existsSync(resolve(repositoryRoot, project))) fail(`NuGet project is missing: ${project}`);
  const baseline = baselines.find((item) => item.assembly === name);
  return {
    packageId: name,
    version,
    artifact: `${name}.${version}.nupkg`,
    project,
    projectSha256: sourceFingerprint(project).sha256,
    publicApiBaseline: baseline?.path ?? null,
    publicApiSha256: baseline?.sha256 ?? null,
    packagingBehavior: name === "Sunder.Package.Templates"
      ? "packed dotnet-new topology"
      : name === "Sunder.Package.Build" ? "buildTransitive target plus tasks/net10.0" : "lib/ref contract assembly",
  };
}

function collectNpmSurfaces() {
  const packages = ["sdk", "package-tool", "create-sunder-package"];
  return packages.map((directoryName) => {
    const root = `languages/typescript/node/packages/${directoryName}`;
    const manifestPath = `${root}/package.json`;
    const manifest = JSON.parse(readText(manifestPath));
    const declarationInputs = listFiles(`${root}/src`)
      .filter((path) => path.endsWith(".ts"))
      .map(sourceFingerprint);
    const declarations = manifest.types === undefined
      ? []
      : listFiles(`${root}/dist`)
        .filter((path) => path.endsWith(".d.ts"))
        .map(sourceFingerprint);
    if (manifest.types !== undefined && declarations.length === 0) {
      fail(`No emitted declarations were found for ${manifest.name}.`);
    }
    return {
      name: manifest.name,
      version: manifest.version,
      artifact: `${manifest.name.replace("@", "").replace("/", "-")}-${manifest.version}.tgz`,
      main: manifest.main ?? null,
      types: manifest.types ?? null,
      exports: manifest.exports ?? null,
      bin: manifest.bin ?? null,
      engines: manifest.engines ?? null,
      dependencies: manifest.dependencies ?? {},
      manifest: sourceFingerprint(manifestPath),
      declarations,
      declarationSetSha256: sha256Text(
        declarations.map((item) => `${item.path}\0${item.sha256}`).join("\n"),
      ),
      declarationInputs,
      declarationInputSetSha256: sha256Text(
        declarationInputs.map((item) => `${item.path}\0${item.sha256}`).join("\n"),
      ),
    };
  });
}

function collectMsbuildSurface(baselines) {
  const path = "languages/csharp/dotnet/packages/Sunder.Package.Build/build/Sunder.Package.Build.targets";
  const text = readText(path);
  const propertyBlocks = [...text.matchAll(/<PropertyGroup\b[^>]*>([\s\S]*?)<\/PropertyGroup>/gu)]
    .map((match) => match[1]);
  const itemBlocks = [...text.matchAll(/<ItemGroup\b[^>]*>([\s\S]*?)<\/ItemGroup>/gu)]
    .map((match) => match[1]);
  const names = (blocks) => uniqueSorted(blocks.flatMap((block) =>
    [...block.matchAll(/<\/?(Sunder[A-Za-z0-9_]+)/gu)].map((match) => match[1])));
  const publicProperties = uniqueSorted([
    ...names(propertyBlocks),
    ...[...text.matchAll(/\$\((Sunder[A-Za-z0-9_]+)\)/gu)].map((match) => match[1]),
  ]);
  const publicItems = uniqueSorted([
    ...names(itemBlocks),
    ...[...text.matchAll(/@\((Sunder[A-Za-z0-9_]+)\)/gu)].map((match) => match[1]),
  ]);
  const tasks = uniqueSorted(
    [...text.matchAll(/<UsingTask\s+TaskName="([^"]+)"/gu)].map((match) => match[1]),
  );
  const targets = [...text.matchAll(/<Target\s+([\s\S]*?)>/gu)]
    .map((match) => parseXmlAttributes(match[1]))
    .filter((attributes) => attributes.Name !== undefined && !attributes.Name.startsWith("_"))
    .map((attributes) => ({
      name: attributes.Name,
      beforeTargets: attributes.BeforeTargets ?? null,
      afterTargets: attributes.AfterTargets ?? null,
      dependsOnTargets: attributes.DependsOnTargets ?? null,
    }))
    .sort((left, right) => compareOrdinal(left.name, right.name));
  const outputs = [...text.matchAll(/<Output\s+([\s\S]*?)\/>/gu)]
    .map((match) => parseXmlAttributes(match[1]))
    .map((attributes) => ({
      taskParameter: attributes.TaskParameter,
      property: attributes.PropertyName ?? null,
      item: attributes.ItemName ?? null,
    }))
    .filter((output) => output.property?.startsWith("Sunder") || output.item?.startsWith("Sunder"));
  return {
    targetsFile: sourceFingerprint(path),
    taskAssemblyApi: baselineReference(baselines, "Sunder.Package.Build"),
    taskAssemblyApiFidelity: sourceFingerprint(
      "languages/csharp/dotnet/tests/Sunder.Sdk.PublicApi.Tests/ApiFidelity/Sunder.Package.Build.txt",
    ),
    tasks,
    targets,
    properties: publicProperties,
    items: publicItems,
    itemMetadata: [
      {
        item: "SunderRpcBinding",
        metadata: "Accessibility",
        required: false,
        default: "Public",
        allowedValues: ["Public", "Internal"],
        comparison: "ordinal",
        scope: "all top-level generated C# declarations",
        invalidValuePolicy: "fail before replacing generated output",
        evidence: [
          "SunderRpcContractDescriptorTests.CSharpGenerator_AccessibilityModesAreDeterministicGoldenOutputsAndCompile",
          "GenerateSunderRpcBindingsTaskTests.Execute_AppliesExplicitAccessibility",
          "GenerateSunderRpcBindingsTaskTests.Execute_RejectsInvalidAccessibilityBeforeWritingOutput",
          "PackageTemplateTests.GeneratedPrimaryProject_BuildsCurrentRidAndPacksUniversalPackage",
        ],
      },
    ],
    bindingGeneratorSources: [
      sourceFingerprint(
        "languages/csharp/dotnet/packages/Sunder.Package.Build/Tasks/GenerateSunderRpcBindingsTask.cs",
      ),
      sourceFingerprint(
        "languages/csharp/dotnet/packages/Sunder.Sdk/Rpc/SunderRpcCSharpGenerator.cs",
      ),
      sourceFingerprint(
        "languages/csharp/dotnet/packages/Sunder.Sdk/Rpc/SunderRpcCSharpEmitter.cs",
      ),
      sourceFingerprint(
        "languages/csharp/dotnet/packages/Sunder.Sdk/Rpc/SunderRpcCSharpGeneratorModel.cs",
      ),
    ],
    outputs,
  };
}

function collectCliSurface() {
  const help = [];
  const pending = [[]];
  const observed = new Set();
  while (pending.length > 0) {
    const path = pending.shift();
    const key = path.join(" ");
    if (observed.has(key)) continue;
    observed.add(key);
    const result = runCli([...path, "--help"]);
    if (result.status !== 0) fail(`CLI help failed for '${key || "<root>"}': ${result.stderr}`);
    const normalized = normalizeText(result.stdout);
    const children = parseHelpCommands(normalized);
    help.push({
      path: key,
      helpSha256: sha256Text(normalized),
      helpUtf8Bytes: Buffer.byteLength(normalized, "utf8"),
      subcommands: children,
    });
    for (const child of children) pending.push([...path, child]);
  }
  help.sort((left, right) => compareOrdinal(left.path, right.path));

  const helpJson = runCli(["--help", "--json"]);
  if (helpJson.status !== 0) fail(`CLI JSON help failed: ${helpJson.stderr}`);
  const helpEnvelope = JSON.parse(helpJson.stdout);
  const invalidJson = runCli(["compatibility-unknown-command", "--json"]);
  if (invalidJson.status === 0) fail("CLI unknown-command probe unexpectedly succeeded.");
  const invalidEnvelope = JSON.parse(invalidJson.stdout);
  return {
    assembly: "sunder.dll",
    commands: help,
    jsonEnvelope: {
      schemaVersion: helpEnvelope.schemaVersion,
      propertyOrder: Object.keys(helpEnvelope),
      shape: jsonShape(helpEnvelope),
      usageFailureShape: jsonShape(invalidEnvelope),
    },
    exitCodes: parseCliExitCodes(),
    source: [
      sourceFingerprint("languages/csharp/dotnet/host/Sunder.Cli/CliCommandParser.cs"),
      sourceFingerprint("languages/csharp/dotnet/host/Sunder.Cli/CliCommandParser.OrdinaryUser.cs"),
      sourceFingerprint("languages/csharp/dotnet/host/Sunder.Cli/CliCommandParser.Symbols.cs"),
      sourceFingerprint("languages/csharp/dotnet/host/Sunder.Cli/CliOutput.cs"),
      sourceFingerprint("languages/csharp/dotnet/host/Sunder.Cli/CliErrors.cs"),
    ],
  };
}

function collectRuntimeEndpoints() {
  const directory = "languages/csharp/dotnet/host/Sunder.Runtime.Host/Endpoints";
  const endpoints = [];
  for (const path of listFiles(directory).filter((item) => item.endsWith("Endpoints.cs"))) {
    const text = readText(path);
    const base = path.endsWith("RuntimeHandshakeEndpoints.cs") ? "" : "/api/v1";
    const groups = new Map([["endpoints", ""]]);
    for (const match of text.matchAll(/var\s+(\w+)\s*=\s*endpoints\.MapGroup\(\s*"([^"]+)"\s*\)/gu)) {
      groups.set(match[1], match[2]);
    }
    for (const match of text.matchAll(/(\w+)\.Map(Get|Post|Put|Delete|Patch)\(\s*"([^"]+)"/gu)) {
      if (!groups.has(match[1])) continue;
      endpoints.push({
        method: match[2].toUpperCase(),
        path: joinRoute(base, groups.get(match[1]), match[3]),
        auth: "bearer",
        rateLimit: "none",
        metadataSource: path,
      });
    }
  }
  endpoints.push({
    method: "*",
    path: "/api/{**path}",
    auth: "bearer",
    rateLimit: "none",
    metadataSource: "languages/csharp/dotnet/host/Sunder.Runtime.Host/Program.cs",
  });
  return sortEndpoints(endpoints);
}

function runtimeHttpMetadataSources() {
  return uniqueSorted([
    "languages/csharp/dotnet/host/Sunder.Runtime.Host/Program.cs",
    "languages/csharp/dotnet/host/Sunder.Runtime.Host/RuntimeBearerAuthenticationMiddleware.cs",
    "languages/csharp/dotnet/host/Sunder.Runtime.Host/RuntimeProblemDetailsMiddleware.cs",
    ...listFiles("languages/csharp/dotnet/host/Sunder.Runtime.Host/Endpoints")
      .filter((path) => path.endsWith(".cs")),
  ]).map(sourceFingerprint);
}

function collectHostEndpoints() {
  const path = "languages/csharp/dotnet/host/Sunder.Host.Supervisor/Program.cs";
  const text = readText(path);
  const endpoints = [...text.matchAll(/app\.Map(Get|Post|Put|Delete|Patch)\(\s*"([^"]+)"/gu)]
    .map((match) => ({
      method: match[1].toUpperCase(),
      path: match[2],
      auth: "bearer",
      rateLimit: "none",
      metadataSource: path,
    }));
  for (const match of text.matchAll(/app\.MapMethods\(\s*"([^"]+)"\s*,\s*([^,]+),/gu)) {
    const methods = match[2].includes("forwardedMethods")
      ? ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"]
      : [...match[2].matchAll(/"([A-Z]+)"/gu)].map((item) => item[1]);
    for (const method of methods) {
      endpoints.push({ method, path: match[1], auth: "bearer", rateLimit: "none", metadataSource: path });
    }
  }
  return sortEndpoints(endpoints);
}

function hostHttpMetadataSources() {
  return productionSourceFiles("languages/csharp/dotnet/host/Sunder.Host.Supervisor")
    .filter((path) => path.endsWith(".cs"))
    .map(sourceFingerprint);
}

function collectRegistryClientEndpoints() {
  const appClient = "languages/csharp/dotnet/host/Sunder.App/Services/RegistryApiClient.cs";
  const cliPackages = "languages/csharp/dotnet/host/Sunder.Cli/RegistryClient.Packages.cs";
  const cliPublish = "languages/csharp/dotnet/host/Sunder.Cli/RegistryClient.Publish.cs";
  const cliStacks = "languages/csharp/dotnet/host/Sunder.Cli/RegistryClient.Stacks.cs";
  const runtimeAuth = "languages/csharp/dotnet/host/Sunder.Runtime.Host/Services/RegistryAuthApiClient.cs";
  const runtimeOperations = "languages/csharp/dotnet/host/Sunder.Runtime.Host/Services/RegistryAuthenticatedOperations.cs";
  const runtimePlan = "languages/csharp/dotnet/host/Sunder.Runtime.Host/Services/RegistryPackagePlanResolver.cs";
  // Route literals live in public contracts; keep the client compatibility inventory independently explicit.
  const endpoints = [
    { client: "App", method: "GET", path: "/api/v1/packages", queryExpression: "skip={skip}&take={take}&sort={FormatSort(sort)}", metadataSource: appClient },
    { client: "App", method: "GET", path: "/api/v1/packages/{packageId}", queryExpression: null, metadataSource: appClient },
    { client: "App", method: "GET", path: "/api/v1/packages/{packageId}/versions/{version}", queryExpression: null, metadataSource: appClient },
    { client: "App", method: "GET", path: "/api/v1/stacks", queryExpression: "skip={skip}&take={take}&sort={FormatSort(sort)}", metadataSource: appClient },
    { client: "App", method: "GET", path: "/api/v1/stacks/{stackId}", queryExpression: null, metadataSource: appClient },
    { client: "CLI", method: "POST", path: "/api/v1/dev/packages/publish/local", queryExpression: null, metadataSource: cliPackages },
    { client: "CLI", method: "POST", path: "/api/v1/dev/stacks/publish/local", queryExpression: null, metadataSource: cliStacks },
    { client: "CLI", method: "GET", path: "/api/v1/packages", queryExpression: "skip={skip}&take={take}", metadataSource: cliPackages },
    { client: "CLI", method: "POST", path: "/api/v1/packages/publish", queryExpression: null, metadataSource: cliPublish },
    { client: "CLI", method: "GET", path: "/api/v1/packages/{packageId}", queryExpression: null, metadataSource: cliPackages },
    { client: "CLI", method: "GET", path: "/api/v1/packages/{packageId}/dist-tags", queryExpression: null, metadataSource: cliPackages },
    { client: "CLI", method: "GET", path: "/api/v1/packages/{packageId}/versions/{version}", queryExpression: null, metadataSource: cliPackages },
    { client: "CLI", method: "GET", path: "/api/v1/stacks", queryExpression: "skip={skip}&take={take}", metadataSource: cliStacks },
    { client: "CLI", method: "POST", path: "/api/v1/stacks/publish", queryExpression: null, metadataSource: cliPublish },
    { client: "CLI", method: "GET", path: "/api/v1/stacks/{stackId}", queryExpression: null, metadataSource: cliStacks },
    { client: "Runtime", method: "DELETE", path: "/api/v1/cli-auth/token", queryExpression: null, metadataSource: runtimeAuth },
    { client: "Runtime", method: "POST", path: "/api/v1/cli-auth/token", queryExpression: null, metadataSource: runtimeAuth },
    { client: "Runtime", method: "GET", path: "/api/v1/me", queryExpression: null, metadataSource: runtimeAuth },
    { client: "Runtime", method: "POST", path: "/api/v1/packages/publish", queryExpression: null, metadataSource: runtimeOperations },
    { client: "Runtime", method: "POST", path: "/api/v1/packages/resolve-package-changes", queryExpression: null, metadataSource: runtimePlan },
    { client: "Runtime", method: "DELETE", path: "/api/v1/packages/{packageId}/dist-tags/{tag}", queryExpression: null, metadataSource: runtimeOperations },
    { client: "Runtime", method: "PUT", path: "/api/v1/packages/{packageId}/dist-tags/{tag}", queryExpression: null, metadataSource: runtimeOperations },
    { client: "Runtime", method: "PUT", path: "/api/v1/packages/{packageId}/versions/{version}/deprecation", queryExpression: null, metadataSource: runtimeOperations },
    { client: "Runtime", method: "PUT", path: "/api/v1/packages/{packageId}/versions/{version}/yank", queryExpression: null, metadataSource: runtimeOperations },
    { client: "Runtime", method: "DELETE", path: "/api/v1/packages/{resourceId}/star", queryExpression: null, metadataSource: runtimeOperations },
    { client: "Runtime", method: "PUT", path: "/api/v1/packages/{resourceId}/star", queryExpression: null, metadataSource: runtimeOperations },
    { client: "Runtime", method: "POST", path: "/api/v1/stacks/publish", queryExpression: null, metadataSource: runtimeOperations },
    { client: "Runtime", method: "DELETE", path: "/api/v1/stacks/{resourceId}/star", queryExpression: null, metadataSource: runtimeOperations },
    { client: "Runtime", method: "PUT", path: "/api/v1/stacks/{resourceId}/star", queryExpression: null, metadataSource: runtimeOperations },
    { client: "Runtime", method: "DELETE", path: "/api/v1/stacks/{stackId}", queryExpression: null, metadataSource: runtimeOperations },
  ];
  const identities = new Set(endpoints.map((endpoint) =>
    `${endpoint.client}\0${endpoint.method}\0${endpoint.path}\0${endpoint.queryExpression}`));
  if (endpoints.length !== 30 || identities.size !== endpoints.length) {
    fail(`Expected 30 distinct Registry client endpoints, found ${identities.size} of ${endpoints.length}.`);
  }
  return endpoints;
}

function registryClientMetadataSources() {
  return uniqueSorted([
    ...listFiles("languages/csharp/dotnet/host/Sunder.Cli")
      .filter((path) => /Registry(?:Client|Transport)[A-Za-z.]*\.cs$/u.test(path)),
    "languages/csharp/dotnet/host/Sunder.App/Services/BoundedHttpContentReader.cs",
    "languages/csharp/dotnet/host/Sunder.App/Services/RegistryApiClient.cs",
    ...listFiles("languages/csharp/dotnet/host/Sunder.Runtime.Host/Services")
      .filter((path) => /\/Registry[A-Za-z]+\.cs$/u.test(path)),
  ]).map(sourceFingerprint);
}

function collectCliErrors() {
  const path = "languages/csharp/dotnet/host/Sunder.Cli/CliErrors.cs";
  const text = readText(path);
  return {
    exitCodes: parseCliExitCodes(),
    codes: uniqueSorted([...text.matchAll(/"((?:cli|runtime|registry)\.[a-z0-9._-]+)"/gu)].map((match) => match[1])),
    mappingSource: sourceFingerprint(path),
  };
}

function parseCliExitCodes() {
  const text = readText("languages/csharp/dotnet/host/Sunder.Cli/CliErrors.cs");
  const values = {};
  for (const match of text.matchAll(/public const int (\w+) = ([^;]+);/gu)) {
    const raw = match[2].trim();
    values[match[1]] = /^\d+$/u.test(raw) ? Number(raw) : values[raw];
  }
  return Object.fromEntries(Object.entries(values).sort(([left], [right]) => compareOrdinal(left, right)));
}

function collectConfiguration() {
  const settingFiles = listFiles("languages/csharp/dotnet/host")
    .filter((path) => /\/appsettings(?:\.[A-Za-z]+)?\.json$/u.test(path));
  const settings = settingFiles.map((path) => ({
    path,
    keys: flattenJsonKeys(JSON.parse(readText(path))),
    sha256: sourceFingerprint(path).sha256,
  }));
  const environmentSources = [
    ...productionSourceFiles("languages/csharp/dotnet/host"),
    ...productionSourceFiles("languages/csharp/dotnet/packages"),
    ...productionSourceFiles("languages/csharp/dotnet/libraries"),
    ...productionSourceFiles("languages/typescript/node/packages", true),
    ...listFiles("scripts/install"),
    ...listFiles("scripts/release").filter((path) => !/(?:\.fixture|\.test|test_)/u.test(path)),
  ];
  const environment = new Map();
  for (const path of uniqueSorted(environmentSources)) {
    if (!isTextSource(path)) continue;
    const names = uniqueSorted(readText(path).match(/SUNDER_[A-Z0-9_]+/gu) ?? []);
    for (const name of names) {
      if (!environment.has(name)) environment.set(name, []);
      environment.get(name).push(path);
    }
  }
  return {
    settings,
    environment: [...environment.entries()]
      .sort(([left], [right]) => compareOrdinal(left, right))
      .map(([name, sources]) => ({ name, sources: uniqueSorted(sources) })),
    precedence: {
      appEnvironmentName: ["SUNDER_ENVIRONMENT", "DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT", "Debug:Development"],
      appRegistryApi: ["SUNDER_REGISTRY_API_URL", "appsettings.<environment>.json", "appsettings.json"],
      appRuntime: ["command line", "SUNDER_RUNTIME_URL", "managed Host connection"],
      cliEndpoints: ["command line", "SUNDER_* URL", "appsettings.<environment>.json", "appsettings.json", "built-in Runtime default"],
    },
  };
}

function collectPersistence() {
  const runtimeLocalState = "languages/csharp/dotnet/host/Sunder.Runtime.LocalState/RuntimeLocalState.cs";
  const appLocalState = "languages/csharp/dotnet/host/Sunder.App/Services/AppLocalState.cs";
  const runtimeConnection = "languages/csharp/dotnet/host/Sunder.Runtime.LocalState/RuntimeConnectionInfoStore.cs";
  const resourcePolicy = "languages/csharp/dotnet/host/Sunder.Runtime.LocalState/RuntimeResourcePolicy.cs";
  const installedPackages = "languages/csharp/dotnet/host/Sunder.Runtime.Host/Services/InstalledPackageStore.cs";
  const rpcPermissions = "languages/csharp/dotnet/host/Sunder.Runtime.Host/Services/RuntimeRpcPermissionStore.cs";
  const packageStoreRecovery = "languages/csharp/dotnet/host/Sunder.Runtime.Host/Services/PackageStoreRecovery.cs";
  const packageStoreTransactions = "languages/csharp/dotnet/host/Sunder.Runtime.Host/Services/PackageStoreTransactionManager.cs";
  const packageState = "languages/csharp/dotnet/host/Sunder.Runtime.Host/Infrastructure/Storage/PackageStateSerializer.cs";
  const packageSecrets = "languages/csharp/dotnet/host/Sunder.Runtime.Host/Infrastructure/Storage/PackageSecretsSerializer.cs";
  const packageStorageMigration = "languages/csharp/dotnet/host/Sunder.Runtime.Host/Infrastructure/Storage/PackageStorageMigrationJournal.cs";
  const masterKeyProtection = "languages/csharp/dotnet/host/Sunder.Runtime.Host/Infrastructure/Storage/MasterKeyProtection.cs";
  const masterKeyStore = "languages/csharp/dotnet/host/Sunder.Runtime.Host/Infrastructure/Storage/MasterKeyStore.cs";
  const atomicDocument = "languages/csharp/dotnet/host/Sunder.Runtime.Host/Infrastructure/Storage/AtomicFileDocument.cs";
  const localStacks = "languages/csharp/dotnet/host/Sunder.App/Services/LocalStackLibraryService.cs";
  const shellState = "languages/csharp/dotnet/host/Sunder.App/Models/ShellState.cs";
  const shellStateService = "languages/csharp/dotnet/host/Sunder.App/Services/ShellStateService.cs";
  const hostLifecycle = "languages/csharp/dotnet/host/Sunder.Host.Supervisor/HostLifecycleStore.cs";
  const hostIdentity = "languages/csharp/dotnet/host/Sunder.Host.Supervisor/HostIdentityStore.cs";
  const hostPayload = "languages/csharp/dotnet/host/Sunder.App/Services/UserHostPayloadStore.cs";

  const document = (id, relativePaths, versions, readFailurePolicy, sources) => ({
    id,
    relativePaths,
    versions,
    readFailurePolicy,
    sources: sources.map(sourceFingerprint),
  });

  return {
    stateRoots: [
      {
        id: "runtime-v1",
        identity: "Sunder/runtime/v1",
        schemaVersion: readIntegerConstant(runtimeLocalState, "SchemaVersion"),
        incompatibleRootPolicy: "reject; do not infer or migrate legacy state",
        source: sourceFingerprint(runtimeLocalState),
      },
      {
        id: "app-v1",
        identity: "Sunder/app/v1",
        schemaVersion: readIntegerConstant(appLocalState, "SchemaVersion"),
        incompatibleRootPolicy: "reject; do not infer or migrate legacy state",
        source: sourceFingerprint(appLocalState),
      },
    ],
    documents: [
      document(
        "runtime-and-host-connection",
        ["runtime/v1/connection.json", "host/connection/host.json"],
        { format: readIntegerConstant(runtimeConnection, "FormatVersion") },
        "invalid, unprotected, or unreadable credentials are unavailable; authenticated callers fail closed",
        [runtimeConnection, "languages/csharp/dotnet/host/Sunder.Host.Client/HostConnectionInfoStore.cs"],
      ),
      document(
        "runtime-resource-policy",
        ["app/v1/runtime-resource-policy.json", "Runtime launch payload"],
        {
          settings: readIntegerConstant(resourcePolicy, "SettingsFormatVersion"),
          effectiveProfile: readIntegerConstant(resourcePolicy, "EffectiveProfileFormatVersion"),
          launchPayload: readIntegerConstant(resourcePolicy, "LaunchPayloadVersion"),
        },
        "strict closed-shape parse; reject malformed, old, future, or hash-mismatched data",
        [resourcePolicy, "languages/csharp/dotnet/host/Sunder.App/Services/AppRuntimeResourcePolicySettingsService.cs"],
      ),
      document(
        "installed-package-catalog",
        ["runtime/v1/catalog/installed-packages.json"],
        { schema: readIntegerConstant(installedPackages, "CurrentSchemaVersion") },
        "reject malformed, old, future, incomplete, or inconsistent catalogs",
        [installedPackages, "languages/csharp/dotnet/host/Sunder.Runtime.Host/Services/InstalledPackageModels.cs"],
      ),
      document(
        "runtime-rpc-permissions",
        ["runtime/v1/catalog/rpc-permissions.json"],
        { schema: readMatchedInteger(rpcPermissions, /state\?\.SchemaVersion != (\d+)/u, "RPC permission schema") },
        "reject malformed, old, future, duplicate, or pending persisted permissions",
        [rpcPermissions],
      ),
      document(
        "package-store-transaction",
        ["runtime/v1/transactions/<transaction>/journal.json"],
        { schema: readIntegerConstant(packageStoreTransactions, "JournalSchemaVersion") },
        "fail closed during recovery when journal identity, phase, paths, or catalogs are invalid",
        [packageStoreRecovery, packageStoreTransactions],
      ),
      document(
        "package-state-and-settings",
        ["runtime/v1/package-data/<package>/data/state.json", "runtime/v1/package-data/<package>/data/settings.json"],
        { document: readIntegerConstant(packageState, "Version") },
        "reject legacy dictionaries, old/future formats, failure markers, invalid revisions, and invalid values",
        [packageState, atomicDocument],
      ),
      document(
        "package-secrets-and-registry-credentials",
        ["runtime/v1/package-data/<package>/data/secrets.json", "runtime/v1/credentials/registry/credentials.enc.json"],
        {
          document: readIntegerConstant(packageSecrets, "DocumentVersion"),
          cipher: readIntegerConstant(packageSecrets, "CipherVersion"),
          masterKeyProtection: readIntegerConstant(masterKeyProtection, "CurrentVersion"),
          masterKeyDocument: readIntegerConstant(masterKeyStore, "KeyDocumentVersion"),
          failureMarker: readIntegerConstant(atomicDocument, "CurrentVersion"),
        },
        "fail closed; legacy plaintext is unsupported; authenticated ciphertext failures are quarantined or represented by an atomic failure marker",
        [packageSecrets, masterKeyProtection, masterKeyStore, atomicDocument, "languages/csharp/dotnet/host/Sunder.Runtime.Host/Services/RegistryCredentialStore.cs"],
      ),
      document(
        "package-storage-migration-journal",
        ["runtime/v1/package-data/<package>/data/storage-migrations.json"],
        { document: readIntegerConstant(packageStorageMigration, "CurrentVersion") },
        "reject malformed, old/future, duplicate, or identity-mismatched completion records",
        [packageStorageMigration],
      ),
      document(
        "local-stack-index",
        ["app/v1/stacks/local-stacks.json"],
        { schema: readIntegerConstant(localStacks, "CurrentSchemaVersion") },
        "reject and preserve malformed, old, or future indexes; never silently present corruption as an empty library",
        [localStacks],
      ),
      document(
        "app-shell-state",
        ["app/v1/shell-state.json"],
        { layout: readIntegerConstant(shellState, "CurrentLayoutVersion") },
        "reset incompatible layout fields while preserving theme and preferred Runtime URL; malformed state uses defaults and is logged",
        [shellState, shellStateService],
      ),
      document(
        "app-notifications",
        ["app/v1/notifications.json"],
        null,
        "quarantine malformed state and recreate an empty bounded notification history",
        ["languages/csharp/dotnet/host/Sunder.App/Services/NotificationCenterService.cs"],
      ),
      document(
        "app-update-settings",
        ["app/v1/update-settings.json"],
        null,
        "quarantine malformed settings and recreate defaults",
        ["languages/csharp/dotnet/host/Sunder.App/Services/AppUpdateSettingsService.cs"],
      ),
      document(
        "host-lifecycle",
        ["host/runtime/v1/lifecycle.json"],
        { format: readIntegerConstant(hostLifecycle, "FormatVersion") },
        "reject malformed, old/future, or semantically invalid lifecycle state",
        [hostLifecycle],
      ),
      document(
        "host-identity",
        ["host/identity.json"],
        { document: readMatchedInteger(hostIdentity, /HostIdentityDocument\((\d+),/u, "Host identity version") },
        "reject malformed, old/future, empty, or invalid Host identity",
        [hostIdentity],
      ),
      document(
        "user-host-payload",
        ["host/payloads/current.json", "host/payloads/<payload>/.sunder-host-payload.json"],
        { descriptor: readIntegerConstant(hostPayload, "DescriptorVersion") },
        "verify descriptor identity and complete payload hash; stage or repair current payload atomically",
        [hostPayload, "languages/csharp/dotnet/host/Sunder.Host.Supervisor/HostDeploymentIdentityVerifier.cs"],
      ),
    ],
    externalOwnership: {
      registryDatabase: "Sunder Registry repository",
      agentSqliteAndDocuments: "Sunder Agent package repository",
    },
  };
}

function collectTelemetry() {
  const files = productionSourceFiles("languages/csharp/dotnet");
  const telemetryMatches = [];
  const loggingEvents = [];
  for (const path of files.filter((item) => item.endsWith(".cs"))) {
    const text = readText(path);
    if (/ActivitySource|System\.Diagnostics\.Metrics|CreateCounter|CreateHistogram|new Meter\(/u.test(text)) {
      telemetryMatches.push(path);
    }
    for (const match of text.matchAll(/new EventId\((\d+),\s*"([^"]+)"\)/gu)) {
      loggingEvents.push({ id: Number(match[1]), name: match[2], source: path });
    }
  }
  return {
    instruments: [],
    activityNames: [],
    operationValues: [],
    tags: [],
    instrumentationSourcesDetected: uniqueSorted(telemetryMatches),
    loggingEvents: loggingEvents.sort((left, right) => left.id - right.id),
    classification: "Core currently emits structured logs but defines no OpenTelemetry instrument surface.",
  };
}

function collectTemplates() {
  const dotnetRoot = "languages/csharp/dotnet/packages/Sunder.Package.Templates/templates/sunder-package";
  const dotnetDefinitionPath = `${dotnetRoot}/.template.config/template.json`;
  const dotnetDefinition = JSON.parse(readText(dotnetDefinitionPath));
  const nodeRoots = [
    "languages/typescript/node/packages/create-sunder-package/template",
    "languages/typescript/node/packages/create-sunder-package/template-react-node",
  ];
  return {
    dotnet: {
      root: dotnetRoot,
      files: topologyFiles(dotnetRoot),
      template: {
        identity: dotnetDefinition.identity,
        shortName: dotnetDefinition.shortName,
        parameters: Object.entries(dotnetDefinition.symbols)
          .filter(([, symbol]) => symbol.type === "parameter")
          .map(([name, symbol]) => ({
            name,
            datatype: symbol.datatype,
            defaultValue: symbol.defaultValue ?? null,
            isRequired: symbol.isRequired ?? false,
            isEnabled: symbol.isEnabled ?? null,
          })),
      },
      roles: {
        runtime: "canonical unsuffixed framework-dependent Worker V1 project with a managed module entry point",
        app: "optional role-isolated Avalonia App project",
        protocol: "package-local non-package contract project",
      },
      roleMetadataSources: [
        sourceFingerprint(`${dotnetRoot}/Sunder.Package.Template/Sunder.Package.Template.csproj`),
        sourceFingerprint(`${dotnetRoot}/Sunder.Package.Template/PackageMetadata.no-host.cs`),
        sourceFingerprint(`${dotnetRoot}/Sunder.Package.Template/PackageMetadata.host.cs`),
      ],
      packedBehaviorGate: "Sunder.Package.Templates.Tests.PackageTemplateTests",
    },
    node: nodeRoots.map((root) => ({
      root,
      files: topologyFiles(root),
      projectMetadata: JSON.parse(readText(`${root}/sunder.package.json`)),
      packedBehaviorGate: "create-sunder-package test plus root templates:typecheck",
    })),
  };
}

function topologyFiles(root) {
  return listFiles(root)
    .filter((path) => !path.split("/").some((part) =>
      part === ".sunder" || part === "dist" || part === "node_modules" || part.startsWith(".sunder-")))
    .map(sourceFingerprint);
}

function collectStringCodes(directory, pattern, excluded = new Set()) {
  const values = new Set();
  for (const path of productionSourceFiles(directory).filter((item) => item.endsWith(".cs"))) {
    for (const match of readText(path).matchAll(/"([a-z][a-z0-9._-]+)"/gu)) {
      if (pattern.test(match[1]) && !excluded.has(match[1])) values.add(match[1]);
      pattern.lastIndex = 0;
    }
  }
  return [...values].sort(compareOrdinal);
}

function constantsFromBaseline(baselines, assembly, typeName) {
  const baseline = baselineReference(baselines, assembly);
  const lines = readText(baseline.path).split("\n");
  const start = lines.findIndex((line) => line === `type sealed class Sunder.${assembly.split(".")[1]}.Contracts.${typeName}`);
  if (start < 0) return { baseline: baseline.path, values: {} };
  const values = {};
  for (const line of lines.slice(start + 1)) {
    if (line.startsWith("type ")) break;
    const match = line.match(/^  field static (?:System\.Int32|System\.String) (\w+) = (.+)$/u);
    if (!match) continue;
    values[match[1]] = match[2].startsWith('"') ? JSON.parse(match[2]) : Number(match[2]);
  }
  return { baseline: baseline.path, values };
}

function productionSourceFiles(directory, nodeSourcesOnly = false) {
  return listFiles(directory).filter((path) => {
    const parts = path.split("/");
    if (parts.some((part) => ["bin", "obj", "node_modules", "dist", "dist-test", "tests", "test", "fixtures"].includes(part))) {
      return false;
    }
    return !nodeSourcesOnly || parts.includes("src") || path.endsWith("package.json");
  });
}

function sourceFingerprint(path) {
  const absolute = resolve(repositoryRoot, path);
  const bytes = readFileSync(absolute);
  const textual = !bytes.includes(0) && isTextSource(path);
  const content = textual ? Buffer.from(normalizeText(bytes.toString("utf8")), "utf8") : bytes;
  return {
    path: path.replaceAll(sep, "/"),
    sha256: sha256(content),
    bytes: content.length,
  };
}

function listFiles(directory) {
  const absolute = resolve(repositoryRoot, directory);
  if (!existsSync(absolute)) return [];
  const result = [];
  for (const entry of readdirSync(absolute, { withFileTypes: true }).sort((left, right) => compareOrdinal(left.name, right.name))) {
    if (["bin", "obj", "node_modules", "dist", "dist-test", ".git"].includes(entry.name)) continue;
    const child = resolve(absolute, entry.name);
    if (entry.isDirectory()) {
      result.push(...listFiles(relativePath(child)));
    } else if (entry.isFile()) {
      result.push(relativePath(child));
    }
  }
  return result.sort(compareOrdinal);
}

function readText(path) {
  return normalizeText(readFileSync(resolve(repositoryRoot, path), "utf8"));
}

function readIntegerConstant(path, name) {
  const text = readText(path);
  const direct = text.match(new RegExp(`(?:const\\s+int|readonly\\s+number)\\s+${name}\\s*=\\s*(\\d+)`, "u"));
  if (direct) return Number(direct[1]);
  const alias = text.match(new RegExp(`const\\s+int\\s+${name}\\s*=\\s*([A-Za-z0-9_]+)`, "u"));
  if (alias) return readIntegerConstant(path, alias[1]);
  fail(`Integer constant ${name} was not found in ${path}.`);
}

function readMatchedInteger(path, pattern, label) {
  return Number(matchRequired(readText(path), pattern, `${label} in ${path}`));
}

function readStringConstant(path, name) {
  return matchRequired(
    readText(path),
    new RegExp(`const\\s+string\\s+${name}\\s*=\\s*"([^"]+)"`, "u"),
    `${name} in ${path}`,
  );
}

function readTypeLiteral(path, property) {
  return Number(matchRequired(
    readText(path),
    new RegExp(`readonly\\s+${property}:\\s*(\\d+)`, "u"),
    `${property} type literal in ${path}`,
  ));
}

function readQuotedArray(path, variable) {
  const text = readText(path);
  const match = text.match(new RegExp(`${variable}[\\s\\S]*?\\[([^\\]]+)\\]`, "u"));
  if (!match) fail(`Array ${variable} was not found in ${path}.`);
  return [...match[1].matchAll(/"([^"]+)"/gu)].map((item) => item[1]);
}

function flattenJsonKeys(value, prefix = "") {
  const result = [];
  for (const key of Object.keys(value).sort(compareOrdinal)) {
    const path = prefix === "" ? key : `${prefix}:${key}`;
    if (value[key] !== null && typeof value[key] === "object" && !Array.isArray(value[key])) {
      result.push(...flattenJsonKeys(value[key], path));
    } else {
      result.push({ key: path, value: value[key] });
    }
  }
  return result;
}

function parseXmlAttributes(text) {
  return Object.fromEntries([...text.matchAll(/([A-Za-z][A-Za-z0-9]*)="([^"]*)"/gu)]
    .map((match) => [match[1], match[2]]));
}

function buildCandidateInputs() {
  runRequired(
    "dotnet",
    ["build", cliProjectRelativePath, "-c", "Release", "--no-restore", "-p:GeneratePackageOnBuild=false"],
    "CLI build",
  );
  runRequired(
    process.platform === "win32" ? "npm.cmd" : "npm",
    ["run", "build", "--prefix", nodeWorkspaceRelativePath],
    "TypeScript declaration build",
  );
}

function runRequired(command, args, label) {
  const result = spawnSync(command, args, {
    cwd: repositoryRoot,
    encoding: "utf8",
    env: {
      ...process.env,
      DOTNET_CLI_UI_LANGUAGE: "en-US",
      DOTNET_NOLOGO: "1",
      NO_COLOR: "1",
      LANG: "C",
    },
  });
  if (result.error) fail(`${label} could not start: ${result.error.message}`);
  if (result.status !== 0) {
    fail(`${label} failed. Restore .NET and npm dependencies first.\n${result.stdout ?? ""}${result.stderr ?? ""}`);
  }
}

function runCli(args) {
  const result = spawnSync("dotnet", [cliAssembly, ...args], {
    cwd: repositoryRoot,
    encoding: "utf8",
    env: {
      ...process.env,
      DOTNET_CLI_UI_LANGUAGE: "en-US",
      DOTNET_NOLOGO: "1",
      NO_COLOR: "1",
      LANG: "C",
    },
  });
  if (result.error) fail(`Could not execute CLI: ${result.error.message}`);
  return {
    status: result.status,
    stdout: normalizeText(result.stdout ?? ""),
    stderr: normalizeText(result.stderr ?? ""),
  };
}

function parseHelpCommands(help) {
  const lines = help.split("\n");
  const start = lines.indexOf("Commands:");
  if (start < 0) return [];
  const commands = [];
  for (const line of lines.slice(start + 1)) {
    if (line.trim() === "") continue;
    const match = line.match(/^\s*([^\s<]+)(?:\s+<[^>]+>)*\s{2,}/u);
    if (match) commands.push(match[1]);
  }
  return uniqueSorted(commands);
}

function jsonShape(value) {
  if (value === null) return { type: "null" };
  if (Array.isArray(value)) {
    return { type: "array", itemTypes: uniqueSorted(value.map((item) => jsonType(item))) };
  }
  if (typeof value === "object") {
    return {
      type: "object",
      properties: Object.keys(value).map((name) => ({ name, shape: jsonShape(value[name]) })),
    };
  }
  return { type: typeof value };
}

function jsonType(value) {
  return value === null ? "null" : Array.isArray(value) ? "array" : typeof value;
}

function sortEndpoints(endpoints) {
  return endpoints.sort((left, right) => compareOrdinal(`${left.path} ${left.method}`, `${right.path} ${right.method}`));
}

function joinRoute(...parts) {
  return `/${parts.filter((part) => part !== undefined && part !== null && part !== "")
    .map((part) => part.replace(/^\/+|\/+$/gu, ""))
    .filter(Boolean)
    .join("/")}`;
}

function latestTag(tags, prefix) {
  return tags.filter((tag) => tag.startsWith(prefix)).sort(compareVersions).at(-1) ?? null;
}

function compareVersions(left, right) {
  const parse = (value) => value.slice(value.indexOf("v") + 1).split(".").map(Number);
  const leftParts = parse(left);
  const rightParts = parse(right);
  for (let index = 0; index < Math.max(leftParts.length, rightParts.length); index++) {
    const difference = (leftParts[index] ?? 0) - (rightParts[index] ?? 0);
    if (difference !== 0) return difference;
  }
  return compareOrdinal(left, right);
}

function gitLines(args) {
  const result = spawnSync("git", args, { cwd: repositoryRoot, encoding: "utf8" });
  if (result.status !== 0) fail(`git ${args.join(" ")} failed: ${result.stderr}`);
  return normalizeText(result.stdout).split("\n").filter(Boolean);
}

function relativePath(path) {
  return relative(repositoryRoot, path).split(sep).join("/");
}

function isTextSource(path) {
  return new Set([
    ".cs", ".csproj", ".json", ".md", ".mjs", ".js", ".ts", ".tsx", ".props", ".targets",
    ".sh", ".ps1", ".yml", ".yaml", ".xml", ".axaml", ".html", ".css", ".txt",
  ]).has(extname(path).toLowerCase()) || path.endsWith("LICENSE");
}

function normalizeText(value) {
  return value.replaceAll("\r\n", "\n").replaceAll("\r", "\n");
}

function sha256(value) {
  return createHash("sha256").update(value).digest("hex");
}

function sha256Text(value) {
  return sha256(Buffer.from(normalizeText(value), "utf8"));
}

function uniqueSorted(values) {
  return [...new Set(values)].sort(compareOrdinal);
}

function matchRequired(text, pattern, label) {
  const match = text.match(pattern);
  return match?.[1] ?? fail(`Could not read ${label}.`);
}

function compareOrdinal(left, right) {
  return left < right ? -1 : left > right ? 1 : 0;
}

function fail(message) {
  throw new Error(message);
}
