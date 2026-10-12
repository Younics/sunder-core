using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcAuthoringReceiptTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[\"runtime\",\"app\"]")]
    [InlineData("[\"App\"]")]
    [InlineData("[\"runtime\",\"runtime\"]")]
    [InlineData("[\"browser\"]")]
    public void ConsumerRolesRejectPresentInvalidIntentInsteadOfTreatingItAsLegacyAbsence(string value)
    {
        var document = LockDocument(); document["packageImports"]!["example.dependency"]!["consumerRoles"] = JsonNode.Parse(value);
        Assert.Contains("package-api.consumer-roles-invalid", Assert.Throws<InvalidDataException>(() => ReadLock(document)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConsumerRoleMigrationRefusesUnrepresentableRetainedNarrowActions()
    {
        var contractLock = ReadLock(); var imports = Prepare(contractLock);
        var receipt = JsonNode.Parse(Write(contractLock, Compile(imports), imports))!;
        var document = LockDocument(); document["packageImports"]!["example.dependency"]!.AsObject().Remove("consumerRoles");
        var path = _temporary.GetPath("narrow.lock.json"); var original = Serialize(document); File.WriteAllBytes(path, original);
        receipt["lockSha256"] = Sha256(original); receipt["uses"]![0]!["actions"] = new JsonArray("discover");
        var output = _temporary.CreateDirectory("obj", "sunder-rpc");
        File.WriteAllBytes(Path.Combine(output, SunderRpcAuthoringDocuments.AuthoringReceiptFileName), Serialize(receipt.AsObject()));
        var error = Assert.Throws<InvalidDataException>(() => SunderRpcConsumerRoleMigration.Execute(path, null, null, false, null, null, default));
        Assert.Contains("consumer-roles-migration-ambiguous", error.Message, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("app")]
    [InlineData("runtime")]
    [InlineData("app,runtime")]
    public void ConsumerRolesProjectIndependentlyAndKeepCompleteImportedAppEvidence(string roles)
    {
        var document = LockDocument();
        document["packageImports"]!["example.dependency"]!["consumerRoles"] = new JsonArray(roles.Split(',').Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        var contractLock = ReadLock(document); var imports = Prepare(contractLock); var compilation = Compile(imports);
        foreach (var role in new[] { "app", "runtime" })
        {
            var bytes = SunderRpcAuthoringDocuments.WriteAuthoringReceipt("1.0.0", Fingerprint, contractLock, PackageId, PackageVersion,
                compilation, imports, role, new HashSet<string>(StringComparer.Ordinal) { "app", "runtime" });
            var projection = SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(bytes, contractLock.Sha256, PackageId, PackageVersion);
            var selected = roles.Split(',').Contains(role);
            Assert.Equal(selected ? 1 : 0, projection.Manifest.UsesContracts!.Count);
            Assert.Equal(selected ? 1 : 0, projection.Manifest.PackageApiImports!.Count);
            if (selected)
            {
                Assert.Equal(role, Assert.Single(projection.Manifest.UsesContracts!)!.Role);
                Assert.Equal(role, Assert.Single(Assert.Single(projection.Manifest.PackageApiImports!)!.UseRefs!)!.Role);
                Assert.Single(projection.Manifest.PackageArtifactImports!); Assert.Single(projection.Manifest.ContractBundles!);
            }
            Assert.Empty(projection.Manifest.Provides!);
        }
        if (roles == "app") Assert.Contains("package-api.consumer-roles-unavailable", Assert.Throws<InvalidDataException>(() =>
            Write(contractLock, compilation, imports)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredWinsIsScopedToConsumerRoleAcrossOwnerAndReexport()
    {
        var document = LockDocument(); AddReexport(document, consumer: true, provider: false);
        document["packageImports"]!["example.dependency"]!["consumerRoles"] = new JsonArray("app");
        document["packageImports"]!["example.dependency"]!["requiredContracts"] = new JsonArray(ContractId);
        document["packageImports"]!["example.reexport"]!["consumerRoles"] = new JsonArray("runtime");
        document["packageImports"]!["example.reexport"]!["requiredContracts"] = new JsonArray();
        var contractLock = ReadLock(document); var imports = Prepare(contractLock); var compilation = Compile(imports);
        foreach (var role in new[] { "app", "runtime" })
        {
            var bytes = SunderRpcAuthoringDocuments.WriteAuthoringReceipt("1.0.0", Fingerprint, contractLock, PackageId, PackageVersion, compilation, imports, role,
                new HashSet<string>(StringComparer.Ordinal) { "app", "runtime" });
            var projection = SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(bytes, contractLock.Sha256, PackageId, PackageVersion);
            Assert.Equal(role == "app", Assert.Single(projection.Manifest.UsesContracts!)!.Required);
            Assert.Equal(role == "app" ? "example.dependency" : "example.reexport", Assert.Single(projection.Manifest.PackageApiImports!)!.PackageId);
        }
    }

    [Fact]
    public void ConsumerRoleMigrationIsExplicitConservativeAtomicAndNoOpStable()
    {
        var document = LockDocument(); document["packageImports"]!["example.dependency"]!.AsObject().Remove("consumerRoles");
        var path = _temporary.GetPath("migrate.lock.json"); var original = Serialize(document); File.WriteAllBytes(path, original);
        Assert.Contains("consumer-roles-migration-required", Assert.Throws<InvalidDataException>(() => SunderRpcAuthoringDocuments.ReadContractLock(path)).Message, StringComparison.Ordinal);
        Assert.Throws<IOException>(() => SunderRpcConsumerRoleMigration.Execute(path, null, null, false, (_, _, _) => throw new IOException("source changed"), null, default));
        Assert.Equal(original, File.ReadAllBytes(path));
        var migrated = SunderRpcConsumerRoleMigration.Execute(path, null, null, false, null, null, default);
        Assert.True(migrated.Changed); Assert.Equal(new[] { "runtime" }, migrated.ContractLock.PackageImports["example.dependency"].ConsumerRoles);
        var stamp = File.GetLastWriteTimeUtc(path); var bytes = File.ReadAllBytes(path);
        Assert.False(SunderRpcConsumerRoleMigration.Execute(path, null, null, false, null, null, default).Changed);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path)); Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.True(SunderRpcConsumerRoleMigration.Execute(path, "example.dependency", "app,runtime", false, null, null, default).Changed);
        Assert.Equal(new[] { "app", "runtime" }, SunderRpcAuthoringDocuments.ReadContractLock(path).PackageImports["example.dependency"].ConsumerRoles);
    }
}
