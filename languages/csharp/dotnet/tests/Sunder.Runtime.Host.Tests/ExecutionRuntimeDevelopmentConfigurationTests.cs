using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class ExecutionRuntimeDevelopmentConfigurationTests
{
    [Fact]
    public void ReadAndClear_ProjectsExactDevelopmentTrustGenerationAndClearsInputs()
    {
        const string keyId = "sunder.runtime.development.release.0123456789abcdef01234567";
        var values = PublicConfiguration(keyId, generation: "4", allowHttp: "true");
        var cleared = new List<string>();

        var configured = ExecutionRuntimeDevelopmentConfiguration.ReadAndClear(
            isDevelopment: true,
            values.GetValueOrDefault,
            cleared.Add);

        Assert.NotNull(configured.Trust);
        Assert.Equal(4, configured.Trust.Generation);
        Assert.True(configured.Trust.DevelopmentTrustEnabled);
        Assert.True(configured.AllowInsecureLoopbackRegistry);
        Assert.Equal(
            ExecutionRuntimeTrustEnvironment.Development,
            configured.Trust.Classify(keyId, minimumHostTrustGeneration: 4));
        Assert.Equal(keyId, configured.Trust.Resolve(
            keyId,
            ExecutionRuntimeTrustEnvironment.Development,
            minimumHostTrustGeneration: 4).KeyId);
        Assert.Equal(
            PublicVariableNames.Order(StringComparer.Ordinal),
            cleared.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ReadAndClear_ExistingDevelopmentConfigurationDefaultsToGenerationOne()
    {
        const string keyId = "sunder.runtime.development.release.0123456789abcdef01234567";
        var values = PublicConfiguration(keyId, generation: null, allowHttp: null);

        var configured = ExecutionRuntimeDevelopmentConfiguration.ReadAndClear(
            isDevelopment: true,
            values.GetValueOrDefault,
            _ => { });

        Assert.Equal(1, configured.Trust?.Generation);
        Assert.False(configured.AllowInsecureLoopbackRegistry);
    }

    [Fact]
    public void ReadAndClear_RejectsDevelopmentTrustOutsideDevelopmentAfterClearingInputs()
    {
        var values = PublicConfiguration(
            "sunder.runtime.development.release.0123456789abcdef01234567",
            generation: "2",
            allowHttp: "false");
        var cleared = new List<string>();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ExecutionRuntimeDevelopmentConfiguration.ReadAndClear(
                isDevelopment: false,
                values.GetValueOrDefault,
                cleared.Add));

        Assert.Contains("outside the Development environment", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            PublicVariableNames.Order(StringComparer.Ordinal),
            cleared.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("9007199254740992")]
    [InlineData("not-a-generation")]
    public void ReadAndClear_RejectsInvalidTrustGeneration(string generation)
    {
        var values = PublicConfiguration(
            "sunder.runtime.development.release.0123456789abcdef01234567",
            generation,
            allowHttp: "true");

        Assert.Throws<InvalidOperationException>(() =>
            ExecutionRuntimeDevelopmentConfiguration.ReadAndClear(
                isDevelopment: true,
                values.GetValueOrDefault,
                _ => { }));
    }

    private static Dictionary<string, string?> PublicConfiguration(
        string keyId,
        string? generation,
        string? allowHttp)
        => new(StringComparer.Ordinal)
        {
            [ExecutionRuntimeDevelopmentConfiguration.ReleaseKeyIdVariable] = keyId,
            [ExecutionRuntimeDevelopmentConfiguration.ReleasePublicKeyVariable] =
                Convert.ToBase64String(new byte[32]),
            [ExecutionRuntimeDevelopmentConfiguration.TrustGenerationVariable] = generation,
            [ExecutionRuntimeDevelopmentConfiguration.AllowInsecureLoopbackRegistryVariable] = allowHttp,
        };

    private static readonly string[] PublicVariableNames =
    [
        ExecutionRuntimeDevelopmentConfiguration.ReleaseKeyIdVariable,
        ExecutionRuntimeDevelopmentConfiguration.ReleasePublicKeyVariable,
        ExecutionRuntimeDevelopmentConfiguration.TrustGenerationVariable,
        ExecutionRuntimeDevelopmentConfiguration.AllowInsecureLoopbackRegistryVariable,
    ];
}
