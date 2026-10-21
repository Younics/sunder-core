import assert from "node:assert/strict";
import test from "node:test";
import { comparePublicApi, parsePublicApi } from "./public-api-comparison.mjs";

const header = "# sunder-public-api-v2\nassembly Synthetic, Version=1.1.0.0, Culture=neutral, PublicKeyToken=null\n";
const baseline = header + `type public abstract class Sample.Base
  method protected .ctor()
  method protected virtual System.Int32 Read()
  property System.String Name { public get; protected set; }
type public interface Sample.IReader<out T>
  method public abstract virtual T Read()
`;

test("compatible additions preserve old API without pinning the complete current snapshot hash", () => {
  const candidate = baseline.replace("type public interface", "  method public System.Int32 Optional(System.Int32 value = 7)\ntype public interface")
    + "  method public virtual System.Int32 Default()\n"
    + "type public interface Sample.INew\n  method public abstract virtual System.Void RequiredOnlyForNewType()\n";
  assert.deepEqual(comparePublicApi(baseline, candidate), []);
  assert.notEqual(baseline, candidate);
});

test("removals and source or CLR shape mutations fail retained-surface comparison", () => {
  const mutations = [
    text => text.replace("  method protected .ctor()\n", ""),
    text => text.replace("protected virtual System.Int32 Read()", "public virtual System.Int32 Read()"),
    text => text.replace("protected set;", "private set;"),
    text => text.replace("<out T>", "<T>"),
    text => text.replace("Version=1.1.0.0", "Version=1.2.0.0"),
    text => text.replace("abstract class", "class"),
    text => text.replace("System.String Name", "System.String? Name"),
    text => text.replace("protected virtual", "protected final override"),
    text => text.replace("type public interface", 'attribute Sunder.Sdk.Compatibility.SunderSdkCapabilityAttribute("new-feature.v1")\ntype public interface'),
  ];
  for (const mutate of mutations) assert.notEqual(comparePublicApi(baseline, mutate(baseline)).length, 0);
});

test("new abstract static abstract accessor and required-member obligations are not additive compatibility", () => {
  const members = [
    "method public abstract virtual System.Void Mandatory()",
    "method public static abstract virtual System.Void Mandatory()",
    "property System.Int32 Value { public abstract virtual get; }",
    "event System.Action Changed { public abstract virtual add; public abstract virtual remove; }",
    "property required System.String Label { public get; public init; }",
    "field public required System.String Label",
  ];
  for (const member of members) {
    const errors = comparePublicApi(baseline, baseline + `  ${member}\n`);
    assert.ok(errors.some(error => error.includes("mandatory implementer/initializer")), member);
  }
});

test("attributes stay attached to the declaring type or member and defaults remain source contracts", () => {
  const old = header + `type public class Sample.Task
  attribute Microsoft.Build.Framework.RequiredAttribute
  property System.String Name { public get; public set; }
  method public System.Void Run(System.String mode = "safe")
`;
  assert.equal(comparePublicApi(old, old.replace("RequiredAttribute", "OutputAttribute")).length, 1);
  assert.equal(comparePublicApi(old, old.replace('mode = "safe"', 'mode = "other"')).length, 1);
});

test("directional nullability and accessor-scoped attributes remain part of the owning declaration", () => {
  const old = header + `type public class Sample.Api
  field public System.String Field [write: System.String?]
  attribute get Sunder.Sdk.Compatibility.SunderSdkCapabilityAttribute("storage.v1")
  property System.String Value { public get; public set; } [write: System.String?]
  attribute add Sunder.Sdk.Compatibility.SunderSdkCapabilityAttribute("logging.v1")
  event System.Action<System.String?>? Changed { public add; public remove; }
  property System.Collections.Generic.KeyValuePair<System.String, System.Collections.Generic.List<System.String?>?>? Nested { public get; public set; }
`;
  assert.deepEqual(comparePublicApi(old, old), []);
  const mutations = [
    text => text.replace("Field [write: System.String?]", "Field"),
    text => text.replace("{ public get; public set; } [write: System.String?]", "{ public get; public set; }"),
    text => text.replace("attribute get ", "attribute set "),
    text => text.replace("attribute add ", "attribute remove "),
    text => text.replace("System.Action<System.String?>?", "System.Action<System.String>?"),
    text => text.replace("System.Action<System.String?>?", "System.Action<System.String?>"),
    text => text.replace("List<System.String?>", "List<System.String>"),
  ];
  for (const mutate of mutations) assert.equal(comparePublicApi(old, mutate(old)).length, 1);

  const mandatory = old + '  attribute get Sunder.Sdk.Compatibility.SunderSdkCapabilityAttribute("new-feature.v1")\n'
    + "  property System.String Required { public abstract virtual get; public abstract virtual set; } [write: System.String?]\n";
  assert.ok(comparePublicApi(old, mandatory).some(error => error.includes("mandatory implementer/initializer")));
});

test("invalid legacy duplicate or orphaned API records fail closed", () => {
  for (const invalid of [baseline.replace("v2", "v1"), baseline + "bogus\n", baseline + "  attribute X\n",
    baseline + "type public abstract class Sample.Base\n", header, baseline + "  method public abstract virtual T Read()\n"]) {
    assert.throws(() => parsePublicApi(invalid));
  }
});
