import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import test from "node:test";
import ts from "typescript";

interface IdentifierKeywordFixture {
  readonly schemaVersion: number;
  readonly csharpLanguageVersion: string;
  readonly typescriptVersion: string;
  readonly ecmascriptTarget: string;
  readonly csharpReservedKeywords: readonly string[];
  readonly csharpContextualKeywords: readonly string[];
  readonly ecmascriptStrictModuleBindingRestrictions: readonly string[];
}

const fixtureSha256 = "58c9633ef5f46744b039387de575b0fbfe447940a44b655ee04d56b7ed1c0241";
const fixtureProperties = [
  "schemaVersion",
  "csharpLanguageVersion",
  "typescriptVersion",
  "ecmascriptTarget",
  "csharpReservedKeywords",
  "csharpContextualKeywords",
  "ecmascriptStrictModuleBindingRestrictions",
] as const;

const fixturePath = resolve(
  __dirname,
  "../../../../../../../specs/rpc/v1/conformance/identifier-keywords.json",
);

test("identifier keyword fixture matches the pinned TypeScript strict-module grammar", async () => {
  const source = await readFile(fixturePath, "utf8");
  assert.equal(createHash("sha256").update(source.replace(/\r\n?/gu, "\n")).digest("hex"), fixtureSha256);
  assertClosedFixtureShape(source);
  const fixture = JSON.parse(source) as IdentifierKeywordFixture;
  assert.deepEqual(Object.keys(fixture), fixtureProperties);

  assert.equal(fixture.schemaVersion, 1);
  assert.equal(fixture.csharpLanguageVersion, "14.0");
  assert.equal(fixture.typescriptVersion, "6.0.3");
  assert.equal(ts.version, fixture.typescriptVersion);
  assert.equal(fixture.ecmascriptTarget, "ES2023");

  assertCanonicalSet(fixture.csharpReservedKeywords);
  assertCanonicalSet(fixture.csharpContextualKeywords);
  assertCanonicalSet(fixture.ecmascriptStrictModuleBindingRestrictions);

  for (const word of fixture.ecmascriptStrictModuleBindingRestrictions) {
    assert.notEqual(bindingDiagnostics(word, ts.ScriptKind.TS).length, 0, `${word} must be rejected as a TypeScript strict-module binding`);
    assert.notEqual(bindingDiagnostics(word, ts.ScriptKind.JS).length, 0, `${word} must be rejected as a JavaScript strict-module binding`);
    assert.deepEqual(bindingDiagnostics(`_${word}`, ts.ScriptKind.TS), [], `_${word} must be a valid escaped TypeScript binding`);
    assert.deepEqual(bindingDiagnostics(`_${word}`, ts.ScriptKind.JS), [], `_${word} must be a valid escaped JavaScript binding`);
  }

  for (const word of ["async", "from", "get", "of", "set", "type"]) {
    assert.deepEqual(bindingDiagnostics(word, ts.ScriptKind.TS), [], `${word} must remain an adjacent valid TypeScript binding`);
    assert.deepEqual(bindingDiagnostics(word, ts.ScriptKind.JS), [], `${word} must remain an adjacent valid JavaScript binding`);
  }
});

function bindingDiagnostics(identifier: string, scriptKind: ts.ScriptKind.TS | ts.ScriptKind.JS): readonly number[] {
  const sourcePath = `/sunder-identifier-keyword.${scriptKind === ts.ScriptKind.TS ? "ts" : "js"}`;
  const source = `export const ${identifier} = 1;`;
  const options: ts.CompilerOptions = {
    allowJs: scriptKind === ts.ScriptKind.JS,
    alwaysStrict: true,
    module: ts.ModuleKind.ESNext,
    noEmit: true,
    noLib: true,
    strict: true,
    target: ts.ScriptTarget.ES2023,
  };
  const fallback = ts.createCompilerHost(options);
  const host: ts.CompilerHost = {
    ...fallback,
    fileExists: (path) => path === sourcePath,
    readFile: (path) => path === sourcePath ? source : undefined,
    getSourceFile: (path, languageVersion) => path === sourcePath
      ? ts.createSourceFile(path, source, languageVersion, true, scriptKind)
      : undefined,
    writeFile: () => undefined,
  };
  const program = ts.createProgram([sourcePath], options, host);
  return ts.getPreEmitDiagnostics(program)
    .filter((diagnostic) => diagnostic.file?.fileName === sourcePath)
    .map((diagnostic) => diagnostic.code);
}

function assertClosedFixtureShape(source: string): void {
  const parsed = ts.parseJsonText(fixturePath, source);
  const statement = assertSingle(parsed.statements);
  assert.ok(ts.isExpressionStatement(statement));
  assert.ok(ts.isObjectLiteralExpression(statement.expression));
  const names = statement.expression.properties.map((property) => {
    assert.ok(ts.isPropertyAssignment(property));
    assert.ok(ts.isStringLiteral(property.name));
    return property.name.text;
  });
  assert.deepEqual(names, fixtureProperties);
}

function assertSingle<T>(values: readonly T[]): T {
  assert.equal(values.length, 1);
  return values[0]!;
}

function assertCanonicalSet(values: readonly string[]): void {
  assert.notEqual(values.length, 0);
  assert.deepEqual(values, [...values].sort());
  assert.equal(new Set(values).size, values.length);
  for (const value of values) assert.match(value, /^[a-z]+$/u);
}
