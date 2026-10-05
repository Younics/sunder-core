using System.Globalization;

namespace Sunder.ExecutionRuntime.Build;

internal static class DevelopmentExecutionRuntimePublicConfiguration
{
    public const string HostReleaseKeyIdVariable = "SUNDER_RUNTIME_DEVELOPMENT_RELEASE_KEY_ID";
    public const string HostReleasePublicKeyVariable = "SUNDER_RUNTIME_DEVELOPMENT_RELEASE_PUBLIC_KEY_BASE64";
    public const string HostTrustGenerationVariable = "SUNDER_RUNTIME_DEVELOPMENT_TRUST_GENERATION";
    public const string HostAllowInsecureLoopbackRegistryVariable =
        "SUNDER_RUNTIME_DEVELOPMENT_ALLOW_INSECURE_LOOPBACK_REGISTRY";

    public static IReadOnlyDictionary<string, string> CreateRegistryEnvironment(
        DevelopmentExecutionRuntimeTrust trust)
    {
        ArgumentNullException.ThrowIfNull(trust);
        var generation = trust.Generation.ToString(CultureInfo.InvariantCulture);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"Registry__ExecutionRuntimes__Development__ReleaseKeys__{trust.ReleaseKey.KeyId}__PublicKeyBase64"] =
                trust.ReleaseKey.PublicKeyBase64,
            [$"Registry__ExecutionRuntimes__Development__ReleaseKeys__{trust.ReleaseKey.KeyId}__MinimumHostTrustGeneration"] =
                generation,
            [$"Registry__ExecutionRuntimes__Development__NativeAttestationKeys__{trust.NativeAttestationKey.KeyId}__PublicKeyBase64"] =
                trust.NativeAttestationKey.PublicKeyBase64,
            [$"Registry__ExecutionRuntimes__Development__NativeAttestationKeys__{trust.NativeAttestationKey.KeyId}__MinimumHostTrustGeneration"] =
                "0",
            ["Registry__ExecutionRuntimes__MinimumSupportedHostTrustGeneration"] = generation,
            ["Registry__ExecutionRuntimes__TrustPolicyGeneration"] = generation,
        };
    }

    public static IReadOnlyDictionary<string, string> CreateHostEnvironment(
        DevelopmentExecutionRuntimeTrust trust,
        bool allowInsecureLoopbackRegistry)
    {
        ArgumentNullException.ThrowIfNull(trust);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [HostReleaseKeyIdVariable] = trust.ReleaseKey.KeyId,
            [HostReleasePublicKeyVariable] = trust.ReleaseKey.PublicKeyBase64,
            [HostTrustGenerationVariable] = trust.Generation.ToString(CultureInfo.InvariantCulture),
            [HostAllowInsecureLoopbackRegistryVariable] = allowInsecureLoopbackRegistry.ToString().ToLowerInvariant(),
        };
    }
}
