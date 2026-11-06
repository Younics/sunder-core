import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { mkdtemp, mkdir, readFile, rename, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import test from "node:test";
import { verifyNodePackageArtifacts } from "../../release/verify-node-package-artifacts.mjs";

const VERSION = "1.2.3";
const RANGE = ">=1.2.0 <1.3.0";
const VITE = "8.2.0";
const NODE_ENGINE = ">=24.18.1 <25.0.0";
const commonEntries = ["package.json", "README.md", "LICENSE", "NOTICE"];
const packageDefinitions = [
  { name: "@younics/sunder-sdk", archive: `younics-sunder-sdk-${VERSION}.tgz`, maximumBytes: 1024 * 1024, requiredEntries: ["dist/index.js"] },
  { name: "@younics/sunder-package-tool", archive: `younics-sunder-package-tool-${VERSION}.tgz`, maximumBytes: 1024 * 1024, requiredEntries: ["dist/cli.js"] },
  {
    name: "@younics/create-sunder-package",
    archive: `younics-create-sunder-package-${VERSION}.tgz`,
    maximumBytes: 1024 * 1024,
    requiredEntries: ["dist/cli.js", "template/package.json", "template-react-node/package.json"],
  },
];

const hash = (algorithm, bytes, encoding) => createHash(algorithm).update(bytes).digest(encoding);
const expectedEntries = (item) => [...commonEntries, ...item.requiredEntries];
const writeArchive = (archive, staging, entries) => execFileSync(
  "tar",
  ["-czf", archive, "-C", join(staging, ".."), ...entries.map((entry) => `package/${entry}`)],
);

async function createFixture() {
  const root = await mkdtemp(join(tmpdir(), "sunder-node-artifacts-"));
  const artifacts = join(root, "artifacts");
  await mkdir(artifacts);
  const report = [];
  const stagingByName = new Map();
  for (const item of packageDefinitions) {
    const staging = join(root, item.archive.replace(/\.tgz$/u, ""), "package");
    stagingByName.set(item.name, staging);
    await mkdir(staging, { recursive: true });
    const manifest = {
      name: item.name,
      version: VERSION,
      license: "MIT",
      repository: { url: "git+https://github.com/Younics/sunder-core.git" },
      engines: { node: NODE_ENGINE },
      publishConfig: { access: "public", provenance: true },
    };
    if (item.name === "@younics/sunder-package-tool") manifest.dependencies = { "@younics/sunder-sdk": RANGE, vite: VITE };
    if (item.name === "@younics/create-sunder-package") manifest.dependencies = { "@younics/sunder-sdk": RANGE };
    for (const entry of item.requiredEntries) {
      const path = join(staging, entry);
      await mkdir(dirname(path), { recursive: true });
      await writeFile(path, `${entry} fixture\n`);
    }
    await writeFile(join(staging, "package.json"), `${JSON.stringify(manifest)}\n`);
    for (const file of ["LICENSE", "NOTICE", "README.md"]) await writeFile(join(staging, file), `${file} fixture\n`);
    if (item.name === "@younics/create-sunder-package") {
      for (const template of ["template", "template-react-node"]) {
        const templateDirectory = join(staging, template);
        await mkdir(templateDirectory, { recursive: true });
        await writeFile(join(templateDirectory, "package.json"), `${JSON.stringify({
          engines: { node: NODE_ENGINE },
          dependencies: { "@younics/sunder-sdk": RANGE },
          devDependencies: {
            "@younics/sunder-package-tool": RANGE,
            ...(template === "template-react-node" ? { vite: `^${VITE}` } : {}),
          },
        })}\n`);
      }
    }
    const archive = join(artifacts, item.archive);
    writeArchive(archive, staging, expectedEntries(item));
    const bytes = await readFile(archive);
    report.push({
      name: item.name,
      version: VERSION,
      filename: item.archive,
      size: bytes.length,
      shasum: hash("sha1", bytes, "hex"),
      integrity: `sha512-${hash("sha512", bytes, "base64")}`,
      files: expectedEntries(item)
        .map((path) => ({ path })),
    });
  }
  const reportPath = join(root, "npm-pack.json");
  await writeFile(reportPath, `${JSON.stringify(report)}\n`);
  const inventoryPath = join(root, "core-public-packages.json");
  await writeFile(inventoryPath, `${JSON.stringify({
    schemaVersion: 1,
    npm: packageDefinitions.map(({ name, maximumBytes, requiredEntries }) => ({ name, maximumBytes, requiredEntries })),
  })}\n`);
  return { root, artifacts, reportPath, inventoryPath, stagingByName };
}

test("verifies exact npm paths, identities, versions, contents, and npm pack digests", async () => {
  const fixture = await createFixture();
  try {
    const result = await verifyNodePackageArtifacts({
      version: VERSION,
      npmVersionRange: RANGE,
      viteVersion: VITE,
      artifactDirectory: fixture.artifacts,
      packReportPath: fixture.reportPath,
      packageInventoryPath: fixture.inventoryPath,
    });
    assert.deepEqual(result.map((item) => item.name), packageDefinitions.map((item) => item.name));
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});

test("selected npm verification rejects skipped archives without weakening the default full set", async () => {
  const fixture = await createFixture();
  try {
    const options = { version: VERSION, npmVersionRange: RANGE, viteVersion: VITE,
      artifactDirectory: fixture.artifacts, packReportPath: fixture.reportPath, packageInventoryPath: fixture.inventoryPath };
    const packageNames = ["@younics/sunder-package-tool"];
    await assert.rejects(verifyNodePackageArtifacts({ ...options, packageNames }), /artifact paths/u);
    for (const item of packageDefinitions.filter((item) => !packageNames.includes(item.name))) {
      await rm(join(fixture.artifacts, item.archive));
    }
    const report = JSON.parse(await readFile(fixture.reportPath, "utf8")).filter((item) => packageNames.includes(item.name));
    await writeFile(fixture.reportPath, JSON.stringify(report));
    const result = await verifyNodePackageArtifacts({ ...options, packageNames });
    assert.deepEqual(result.map((item) => item.name), packageNames);
    await assert.rejects(verifyNodePackageArtifacts(options), /artifact paths/u);
    for (const invalid of [[], ["@other/package"], [...packageNames, ...packageNames]]) {
      await assert.rejects(verifyNodePackageArtifacts({ ...options, packageNames: invalid }), /public package selection/u);
    }
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});

test("rejects an expected filename containing another package identity", async () => {
  const fixture = await createFixture();
  try {
    const staging = join(fixture.root, "wrong", "package");
    await mkdir(staging, { recursive: true });
    await writeFile(join(staging, "package.json"), `${JSON.stringify({
      name: "@other/sdk",
      version: VERSION,
      license: "MIT",
      repository: { url: "git+https://github.com/Younics/sunder-core.git" },
      engines: { node: NODE_ENGINE },
      publishConfig: { access: "public", provenance: true },
    })}\n`);
    for (const file of ["LICENSE", "NOTICE", "README.md"]) await writeFile(join(staging, file), `${file}\n`);
    writeArchive(join(fixture.artifacts, `younics-sunder-sdk-${VERSION}.tgz`), staging, commonEntries);

    await assert.rejects(
      verifyNodePackageArtifacts({
        version: VERSION,
        npmVersionRange: RANGE,
        viteVersion: VITE,
        artifactDirectory: fixture.artifacts,
        packReportPath: fixture.reportPath,
        packageInventoryPath: fixture.inventoryPath,
      }),
      /package identity/u,
    );
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});

test("rejects a coordinated tarball outside its exact top-level path", async () => {
  const fixture = await createFixture();
  try {
    await mkdir(join(fixture.artifacts, "nested"));
    await rename(
      join(fixture.artifacts, `younics-sunder-sdk-${VERSION}.tgz`),
      join(fixture.artifacts, "nested", `younics-sunder-sdk-${VERSION}.tgz`),
    );

    await assert.rejects(
      verifyNodePackageArtifacts({
        version: VERSION,
        npmVersionRange: RANGE,
        viteVersion: VITE,
        artifactDirectory: fixture.artifacts,
        packReportPath: fixture.reportPath,
        packageInventoryPath: fixture.inventoryPath,
      }),
      /artifact paths/u,
    );
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});

test("rejects a numeric prerelease identifier with a leading zero", async () => {
  const fixture = await createFixture();
  try {
    await assert.rejects(
      verifyNodePackageArtifacts({
        version: "1.2.3-01",
        npmVersionRange: RANGE,
        viteVersion: VITE,
        artifactDirectory: fixture.artifacts,
        packReportPath: fixture.reportPath,
        packageInventoryPath: fixture.inventoryPath,
      }),
      /strict SemVer/u,
    );
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});

test("rejects an unexpected final npm pack report entry", async () => {
  const fixture = await createFixture();
  try {
    const report = JSON.parse(await readFile(fixture.reportPath, "utf8"));
    report[0].files.push({ path: "dist/unexpected.js" });
    await writeFile(fixture.reportPath, `${JSON.stringify(report)}\n`);

    await assert.rejects(
      verifyNodePackageArtifacts({
        version: VERSION,
        npmVersionRange: RANGE,
        viteVersion: VITE,
        artifactDirectory: fixture.artifacts,
        packReportPath: fixture.reportPath,
        packageInventoryPath: fixture.inventoryPath,
      }),
      /entry closure drifted/u,
    );
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});

test("rejects an unexpected final archive entry independently of the npm pack report", async () => {
  const fixture = await createFixture();
  try {
    const item = packageDefinitions[0];
    const staging = fixture.stagingByName.get(item.name);
    await writeFile(join(staging, "dist", "unexpected.js"), "unexpected\n");
    writeArchive(
      join(fixture.artifacts, item.archive),
      staging,
      [...expectedEntries(item), "dist/unexpected.js"],
    );

    await assert.rejects(
      verifyNodePackageArtifacts({
        version: VERSION,
        npmVersionRange: RANGE,
        viteVersion: VITE,
        artifactDirectory: fixture.artifacts,
        packReportPath: fixture.reportPath,
        packageInventoryPath: fixture.inventoryPath,
      }),
      /archive entry closure drifted/u,
    );
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});

test("rejects non-regular final archive members", async () => {
  const fixture = await createFixture();
  try {
    const item = packageDefinitions[0];
    const staging = fixture.stagingByName.get(item.name);
    await mkdir(join(staging, "unexpected-directory"));
    writeArchive(
      join(fixture.artifacts, item.archive),
      staging,
      [...expectedEntries(item), "unexpected-directory"],
    );

    await assert.rejects(
      verifyNodePackageArtifacts({
        version: VERSION,
        npmVersionRange: RANGE,
        viteVersion: VITE,
        artifactDirectory: fixture.artifacts,
        packReportPath: fixture.reportPath,
        packageInventoryPath: fixture.inventoryPath,
      }),
      /non-regular member/u,
    );
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});

test("rejects a final archive above its inventoried byte bound", async () => {
  const fixture = await createFixture();
  try {
    const inventory = JSON.parse(await readFile(fixture.inventoryPath, "utf8"));
    inventory.npm[0].maximumBytes = 1;
    await writeFile(fixture.inventoryPath, `${JSON.stringify(inventory)}\n`);

    await assert.rejects(
      verifyNodePackageArtifacts({
        version: VERSION,
        npmVersionRange: RANGE,
        viteVersion: VITE,
        artifactDirectory: fixture.artifacts,
        packReportPath: fixture.reportPath,
        packageInventoryPath: fixture.inventoryPath,
      }),
      /exceeds 1 bytes/u,
    );
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});

test("rejects a packed manifest with the wrong Node engine", async () => {
  const fixture = await createFixture();
  try {
    const item = packageDefinitions[0];
    const staging = fixture.stagingByName.get(item.name);
    const manifestPath = join(staging, "package.json");
    const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
    manifest.engines.node = ">=22";
    await writeFile(manifestPath, `${JSON.stringify(manifest)}\n`);
    writeArchive(join(fixture.artifacts, item.archive), staging, expectedEntries(item));

    await assert.rejects(
      verifyNodePackageArtifacts({
        version: VERSION,
        npmVersionRange: RANGE,
        viteVersion: VITE,
        artifactDirectory: fixture.artifacts,
        packReportPath: fixture.reportPath,
        packageInventoryPath: fixture.inventoryPath,
      }),
      /must require Node/u,
    );
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});

test("rejects a packed template manifest with the wrong Node engine", async () => {
  const fixture = await createFixture();
  try {
    const item = packageDefinitions[2];
    const staging = fixture.stagingByName.get(item.name);
    const manifestPath = join(staging, "template", "package.json");
    const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
    manifest.engines.node = ">=22";
    await writeFile(manifestPath, `${JSON.stringify(manifest)}\n`);
    writeArchive(join(fixture.artifacts, item.archive), staging, expectedEntries(item));

    await assert.rejects(
      verifyNodePackageArtifacts({
        version: VERSION,
        npmVersionRange: RANGE,
        viteVersion: VITE,
        artifactDirectory: fixture.artifacts,
        packReportPath: fixture.reportPath,
        packageInventoryPath: fixture.inventoryPath,
      }),
      /template must require Node/u,
    );
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});
