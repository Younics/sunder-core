using System.Text.Json;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class ManifestProvenanceJsonTests
{
    [Theory]
    [InlineData("\"packageArtifactImports\":null", "Explicit JSON null")]
    [InlineData("\"packageApiImports\":null", "Explicit JSON null")]
    [InlineData("\"providerImports\":null", "Explicit JSON null")]
    [InlineData("\"platformImports\":null", "Explicit JSON null")]
    [InlineData("\"contractBundles\":[{\"source\":\"owned\",\"authorityKind\":\"package\",\"authorityId\":\"example.owner\",\"selectedAuthorityContentIdentity\":null}]", "Explicit JSON null")]
    [InlineData("\"contractBundles\":[{\"source\":\"owned\",\"authorityKind\":\"package\",\"authorityId\":\"example.owner\",\"selectedAuthorityRelease\":null}]", "Explicit JSON null")]
    [InlineData("\"dependsOn\":[{\"presence\":null}]", "Explicit JSON null")]
    [InlineData("\"providerImports\":[],\"providerImports\":[]", "Duplicate JSON property")]
    [InlineData("\"contractBundles\":[{\"source\":\"owned\",\"authorityKind\":\"package\",\"authorityId\":\"other.owner\",\"authorityId\":\"example.owner\"}]", "Duplicate JSON property")]
    [InlineData("\"icon\":null,\"contractBundles\":[{\"source\":\"owned\",\"authorityKind\":\"package\",\"authorityId\":\"example.owner\"}]", null)]
    public async Task TrustedInstalledReader_EnforcesJsonRulesEvenWithMatchingReceipt(string metadata, string? expectedError)
    {
        using var temporary = TemporaryDirectoryScope.Create("sunder-host-manifest-provenance-json");
        var manifestPath = temporary.WriteAllText(SunderPackageFormat.ManifestPath, $$"""
            {"archiveFormatVersion":1,"manifestVersion":1,"id":"example.owner","name":"Example Owner",
             "version":"1.0.0","targets":[],{{metadata}}}
            """);
        CanonicalPackageTestBuilder.WriteContentIndex(temporary.RootPath);
        var contentIndex = JsonSerializer.Deserialize<SunderPackageContentIndex>(
            File.ReadAllText(temporary.GetPath(SunderPackageFormat.ContentIndexPath)))!;
        // Bypass archive validation to exercise the trusted receipt reader with the original JSON bytes.
        var installed = new InstalledPackageRecord(
            "example.owner", "Example Owner", null, "1.0.0", null,
            temporary.RootPath, manifestPath,
            PackageSessionPreparer.ComputeContentIdentity(temporary.RootPath),
            contentIndex.Files!.Select(entry => new InstalledPackageContentRecord(
                entry!.Path!, entry.Sha256!, entry.Size)).ToArray(),
            [], true, DateTimeOffset.UtcNow, InstalledPackageProvenanceRecord.Unknown, []);
        var errors = new List<string>();

        var activation = await new PackageSessionPreparer("win-x64").ReadInstalledActivationStateAsync(
            installed, temporary.RootPath, errors, CancellationToken.None);

        if (expectedError is null)
        {
            Assert.NotNull(activation);
            Assert.Empty(errors);
        }
        else
        {
            Assert.Null(activation);
            Assert.Contains("metadata receipt is invalid: " + expectedError, Assert.Single(errors), StringComparison.Ordinal);
        }
    }
}
