import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { dirname, join } from "node:path";
import { writeFile } from "node:fs/promises";
import test from "node:test";
import { createPackedServiceTools } from "./packed-service-tools";
import { serviceApiFixture } from "./service-api-fixtures";

test("packed TS and genuine JS Models, Services and natural resources have Node-free browser declaration and runtime closures", {
  skip: process.env.SUNDER_TEST_PACKED_SERVICES !== "1", timeout: 600_000,
}, async context => {
  const packed = createPackedServiceTools();
  try {
    const tools = await packed.get();
    const installed = createRequire(tools.creator);
    const toolRoot = dirname(installed.resolve("@sunder/package-tool"));
    const ts: typeof import("typescript") = installed("typescript");
    const esbuild: typeof import("esbuild") = installed("esbuild");
    assert.equal(typeof installed("@sunder/sdk").contentFromFile, "function");
    const browserSdk = installed("@sunder/sdk/browser");
    assert.equal(typeof browserSdk.SunderContent.fromBytes, "function");
    assert.equal(typeof browserSdk.ServiceReference, "function");
    assert.equal(Object.hasOwn(browserSdk, "contentFromFile"), false);

    for (const language of ["ts", "js"] as const) await context.test(language, async () => {
      const fixture = await serviceApiFixture(toolRoot, language);
      try {
        const model = language === "ts"
          ? 'type PackedResource = {body: import("@sunder/sdk").SunderContent; target: import("@sunder/sdk").ServiceReference<typeof otherMessages>};'
          : '/** @typedef {{body: import("@sunder/sdk").SunderContent, target: import("@sunder/sdk").ServiceReference<typeof otherMessages>}} PackedResource */';
        await writeFile(join(fixture.root, `src/services.${language}`), fixture.source + `
${model}
export const assets = defineService({
  /** @param {PackedResource} value @returns {PackedResource} */
  roundTrip(value${language === "ts" ? ": PackedResource" : ""}) { return value; }
}, {runtimeUse:{}});
`);
        const project = await fixture.load();
        await fixture.generate(project);
        const consumer = join(fixture.root, `browser.${language}`);
        const resourceType = 'import("#sunder/api").SelfModels.PackedResource';
        const referenceType = 'import("@sunder/sdk/browser").ServiceReference<typeof Services.Self.otherMessages>';
        const source = `import {Self,Services} from "#sunder/api";
import {createBrowserContext,SunderContent} from "@sunder/sdk/browser";
export {Services};
export const localContent = () => SunderContent.fromBytes(new Uint8Array([1,2,3]));
/** @param {${resourceType}} value @param {${referenceType}} reference */
export async function inspect(value${language === "ts" ? `: ${resourceType}` : ""},reference${language === "ts" ? `: ${referenceType}` : ""}) {
  const read = await value.body.openRead();
  await reference.ensureCurrent();
  await reference.Service.send("typed");
  const signal = reference.retirementSignal;
  await value.body.dispose();
  return {read,signal};
}
export function connect() {
  const owner = createBrowserContext(Self);
  return {owner,service:owner.api(Self).assets,token:Services.Self.assets};
}
`;
        await writeFile(consumer, source);
        const program = ts.createProgram([consumer], {
          target: ts.ScriptTarget.ES2023, module: ts.ModuleKind.ESNext,
          moduleResolution: ts.ModuleResolutionKind.Bundler,
          strict: true, allowJs: true, checkJs: true, noEmit: true, skipLibCheck: false, types: [],
          paths: {
             "@sunder/sdk/browser": [installed.resolve("@sunder/sdk/browser").replace(/\.js$/u, ".d.ts")],
             "@sunder/sdk/generated": [installed.resolve("@sunder/sdk/generated").replace(/\.js$/u, ".d.ts")],
            "@sunder/sdk/core": [installed.resolve("@sunder/sdk/core").replace(/\.js$/u, ".d.ts")],
          },
        });
        assert.deepEqual(ts.getPreEmitDiagnostics(program).map(diagnostic =>
          `${diagnostic.file?.fileName}: ${ts.flattenDiagnosticMessageText(diagnostic.messageText, "\n")}`), []);
        for (const file of program.getSourceFiles()) {
          const path = file.fileName.replaceAll("\\", "/");
          assert.doesNotMatch(path, /\/@types\/node\/|\/sdk\/dist\/(?:index|worker|worker-types|content-node)\.d\.ts$/u, path);
          if (path.includes("/.sunder/generated/rpc/")) assert.ok(file.isDeclarationFile, path);
          assert.notEqual(path, join(fixture.root, `src/services.${language}`));
        }
        const bundled = await esbuild.build({
          entryPoints: [consumer], bundle: true, platform: "browser", format: "cjs", write: false, metafile: true,
          alias: {"@sunder/sdk/browser": installed.resolve("@sunder/sdk/browser"), "@sunder/sdk/generated": installed.resolve("@sunder/sdk/generated")},
        });
        for (const path of Object.keys(bundled.metafile!.inputs))
          assert.doesNotMatch(path, /node:|\/(?:content-node|worker|worker-types)\.js$|\/src\/services\.[jt]s$/u, path);
        assert.doesNotMatch(bundled.outputFiles[0]!.text, /node:fs|SelfModels|runWorker/u);
        const module = {exports: {} as {Services: {Self: {assets: unknown}}; localContent(): {length: number; dispose(): Promise<void>}}};
        new Function("module", "exports", bundled.outputFiles[0]!.text)(module, module.exports);
        assert.ok(module.exports.Services.Self.assets);
        const content = module.exports.localContent();
        assert.equal(content.length, 3);
        await content.dispose();
      } finally { await fixture.dispose(); }
    });
  } finally { await packed.dispose(); }
});
