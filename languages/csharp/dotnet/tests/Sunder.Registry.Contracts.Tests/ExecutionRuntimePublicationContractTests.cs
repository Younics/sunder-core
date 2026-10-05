using System.Text.Json;
using Sunder.Registry.Contracts;
using Xunit;

namespace Sunder.Registry.Contracts.Tests;

public sealed class ExecutionRuntimePublicationContractTests
{
    [Fact]
    public void LocalArtifactRequest_RoundTripsSharedWebContractShape()
    {
        var expected = new RegistryPublishLocalExecutionRuntimeArtifactRequest(
            "/tmp/sunder.runtime.dotnet.sunderruntime",
            "publication-1",
            new string('a', 64),
            42);
        var json = JsonSerializer.Serialize(expected, JsonOptions);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            ["runtimePath", "idempotencyKey", "expectedArtifactSha256", "expectedArtifactSize"],
            document.RootElement.EnumerateObject().Select(static property => property.Name));
        Assert.Equal(
            expected,
            JsonSerializer.Deserialize<RegistryPublishLocalExecutionRuntimeArtifactRequest>(json, JsonOptions));
    }

    [Fact]
    public void PublicationLookupRequest_BindsReleaseArtifactAndTrustExpectations()
    {
        var release = new RegistryStageExecutionRuntimeReleaseRequest(
            "sunder.runtime.dotnet",
            "10.0.1",
            2,
            "sunder.dotnet-worker-module.v1",
            "sunder.worker.v1",
            2,
            "sunder-release-2026-01",
            [
                new("linux-arm64", "sunder-native-ci-2026-01"),
                new("linux-x64", "sunder-native-ci-2026-01"),
                new("osx-arm64", "sunder-native-ci-2026-01"),
                new("osx-x64", "sunder-native-ci-2026-01"),
                new("win-arm64", "sunder-native-ci-2026-01"),
                new("win-x64", "sunder-native-ci-2026-01"),
            ],
            ["linux-arm64", "linux-x64", "osx-arm64", "osx-x64", "win-arm64", "win-x64"]);
        var expected = new RegistryLookupExecutionRuntimePublicationRequest(
            release,
            [new RegistryExecutionRuntimePublicationArtifactExpectation(
                "linux-x64",
                "runtime-publication-linux-x64",
                new string('a', 64),
                42,
                new string('b', 64),
                new string('c', 64),
                "sunder-release-2026-01",
                new string('d', 64),
                "sunder-native-ci-2026-01")]);
        var json = JsonSerializer.Serialize(expected, JsonOptions);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(["release", "expectedArtifacts"], document.RootElement.EnumerateObject().Select(static property => property.Name));
        Assert.Equal(
            [
                "rid", "idempotencyKey", "artifactSha256", "artifactSize", "manifestSha256",
                "contentIdentity", "signingKeyId", "nativeAttestationSha256", "nativeAttestationKeyId",
            ],
            document.RootElement.GetProperty("expectedArtifacts")[0]
                .EnumerateObject()
                .Select(static property => property.Name));
        Assert.Equivalent(
            expected,
            JsonSerializer.Deserialize<RegistryLookupExecutionRuntimePublicationRequest>(json, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
