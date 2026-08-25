import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { mkdtemp, mkdir, readFile, rename, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { verifyNodePackageArtifacts } from "./verify-node-package-artifacts.mjs";

const VERSION = "1.2.3";
const RANGE = ">=1.2.0 <1.3.0";
const VITE = "8.2.0";
const packageDefinitions = [
  { name: "@sunder/sdk", archive: `sunder-sdk-${VERSION}.tgz` },
  { name: "@sunder/package-tool", archive: `sunder-package-tool-${VERSION}.tgz` },
  { name: "create-sunder-package", archive: `create-sunder-package-${VERSION}.tgz` },
];

const hash = (algorithm, bytes, encoding) => createHash(algorithm).update(bytes).digest(encoding);

async function createFixture() {
  const root = await mkdtemp(join(tmpdir(), "sunder-node-artifacts-"));
  const artifacts = join(root, "artifacts");
  await mkdir(artifacts);
  const report = [];
  for (const item of packageDefinitions) {
    const staging = join(root, item.archive.replace(/\.tgz$/u, ""), "package");
    await mkdir(staging, { recursive: true });
    const manifest = {
      name: item.name,
      version: VERSION,
      license: "MIT",
      repository: { url: "git+https://github.com/Younics/sunder-core.git" },
      publishConfig: { access: "public", provenance: true },
    };
    if (item.name === "@sunder/package-tool") manifest.dependencies = { "@sunder/sdk": RANGE, vite: VITE };
    if (item.name === "create-sunder-package") manifest.dependencies = { "@sunder/sdk": RANGE };
    await writeFile(join(staging, "package.json"), `${JSON.stringify(manifest)}\n`);
    for (const file of ["LICENSE", "NOTICE", "README.md"]) await writeFile(join(staging, file), `${file} fixture\n`);
    if (item.name === "create-sunder-package") {
      for (const template of ["template", "template-react-node"]) {
        const templateDirectory = join(staging, template);
        await mkdir(templateDirectory);
        await writeFile(join(templateDirectory, "package.json"), `${JSON.stringify({
          dependencies: { "@sunder/sdk": RANGE },
          devDependencies: {
            "@sunder/package-tool": RANGE,
            ...(template === "template-react-node" ? { vite: `^${VITE}` } : {}),
          },
        })}\n`);
      }
    }
    const archive = join(artifacts, item.archive);
    execFileSync("tar", ["-czf", archive, "-C", join(staging, ".."), "package"]);
    const bytes = await readFile(archive);
    report.push({
      name: item.name,
      version: VERSION,
      filename: item.archive,
      size: bytes.length,
      shasum: hash("sha1", bytes, "hex"),
      integrity: `sha512-${hash("sha512", bytes, "base64")}`,
    });
  }
  const reportPath = join(root, "npm-pack.json");
  await writeFile(reportPath, `${JSON.stringify(report)}\n`);
  return { root, artifacts, reportPath };
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
    });
    assert.deepEqual(result.map((item) => item.name), packageDefinitions.map((item) => item.name));
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
      publishConfig: { access: "public", provenance: true },
    })}\n`);
    for (const file of ["LICENSE", "NOTICE", "README.md"]) await writeFile(join(staging, file), `${file}\n`);
    execFileSync("tar", ["-czf", join(fixture.artifacts, `sunder-sdk-${VERSION}.tgz`), "-C", join(staging, ".."), "package"]);

    await assert.rejects(
      verifyNodePackageArtifacts({
        version: VERSION,
        npmVersionRange: RANGE,
        viteVersion: VITE,
        artifactDirectory: fixture.artifacts,
        packReportPath: fixture.reportPath,
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
      join(fixture.artifacts, `sunder-sdk-${VERSION}.tgz`),
      join(fixture.artifacts, "nested", `sunder-sdk-${VERSION}.tgz`),
    );

    await assert.rejects(
      verifyNodePackageArtifacts({
        version: VERSION,
        npmVersionRange: RANGE,
        viteVersion: VITE,
        artifactDirectory: fixture.artifacts,
        packReportPath: fixture.reportPath,
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
      }),
      /strict SemVer/u,
    );
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});
