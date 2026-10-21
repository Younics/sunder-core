import assert from "node:assert/strict";
import { basename, resolve } from "node:path";
import test from "node:test";
import ts from "typescript";
import * as sdk from "../src/index";
import { defineService } from "../src/services";

test("current authoring exports and the test compilation graph exclude the retired DSL", () => {
  assert.equal(sdk.defineService, defineService);
  assert.equal(Object.hasOwn(sdk, "rpc"), false);
  const configPath = resolve(__dirname, "../../tsconfig.test.json");
  const config = ts.readConfigFile(configPath, ts.sys.readFile);
  assert.equal(config.error, undefined);
  const parsed = ts.parseJsonConfigFileContent(config.config, ts.sys, resolve(configPath, ".."));
  assert.deepEqual(parsed.errors, []);
  const program = ts.createProgram(parsed.fileNames, parsed.options);
  const sources = program.getSourceFiles().map(source => resolve(source.fileName));
  assert.equal(sources.some(path => basename(path) === "historical-authoring.ts" || path.endsWith(".txt")), false);
  assert.ok(sources.includes(resolve(__dirname, "../../src/services.ts")));
  assert.ok(sources.includes(resolve(__dirname, "../../test/services.test.ts")));
});
