using System.Text.Json;
using Sunder.App.Services;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.App.Tests;

public sealed class ManifestProvenanceJsonTests
{
    [Theory]
    [InlineData("\"packageArtifactImports\":null", "Explicit JSON null")]
    [InlineData("\"packageApiImports\":null", "Explicit JSON null")]
    [InlineData("\"providerImports\":null", "Explicit JSON null")]
    [InlineData("\"platformImports\":null", "Explicit JSON null")]
    [InlineData("\"contractBundles\":[{\"source\":\"owned\",\"authorityKind\":\"package\",\"authorityId\":\"example.owner\",\"selectedAuthorityRelease\":null}]", "Explicit JSON null")]
    [InlineData("\"contractBundles\":[{\"source\":\"owned\",\"authorityKind\":\"package\",\"authorityId\":\"example.owner\",\"selectedAuthorityContentIdentity\":null}]", "Explicit JSON null")]
    [InlineData("\"dependsOn\":[{\"presence\":null}]", "Explicit JSON null")]
    [InlineData("\"packageApiImports\":[],\"packageApiImports\":[]", "Duplicate JSON property")]
    [InlineData("\"contractBundles\":[{\"source\":\"owned\",\"authorityKind\":\"package\",\"authorityId\":\"other.owner\",\"authorityId\":\"example.owner\"}]", "Duplicate JSON property")]
    public void ProjectionReader_RejectsInvalidJsonBeforeAdmission(string metadata, string expectedError)
    {
        using var temporary = TemporaryDirectoryScope.Create("sunder-app-manifest-provenance-json");
        temporary.WriteAllText(SunderPackageFormat.ManifestPath, $$"""{"id":"example.owner",{{metadata}}}""");

        var exception = Assert.Throws<InvalidDataException>(() => AppPackageManifestReader.Read(temporary.RootPath));

        var jsonError = Assert.IsType<JsonException>(exception.InnerException);
        Assert.Contains(expectedError, jsonError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectionReader_AllowsAbsentProvenanceAndLongstandingNulls()
    {
        using var temporary = TemporaryDirectoryScope.Create("sunder-app-manifest-provenance-json");
        temporary.WriteAllText(SunderPackageFormat.ManifestPath, """
            {"id":"example.owner","icon":null,"contractBundles":[
              {"source":"owned","authorityKind":"package","authorityId":"example.owner"}]}
            """);

        var manifest = AppPackageManifestReader.Read(temporary.RootPath);

        Assert.Null(manifest.PackageArtifactImports);
        Assert.Null(manifest.PackageApiImports);
        Assert.Null(manifest.ProviderImports);
        Assert.Null(manifest.PlatformImports);
        Assert.Null(Assert.Single(manifest.ContractBundles!)!.SelectedAuthorityRelease);
    }
}
