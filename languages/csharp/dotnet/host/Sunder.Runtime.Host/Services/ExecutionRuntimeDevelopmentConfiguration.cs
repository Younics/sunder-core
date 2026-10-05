using System.Globalization;

namespace Sunder.Runtime.Host.Services;

internal sealed record ExecutionRuntimeDevelopmentConfiguration(
    ExecutionRuntimeHostTrustService? Trust,
    bool AllowInsecureLoopbackRegistry)
{
    public const string ReleaseKeyIdVariable = "SUNDER_RUNTIME_DEVELOPMENT_RELEASE_KEY_ID";
    public const string ReleasePublicKeyVariable = "SUNDER_RUNTIME_DEVELOPMENT_RELEASE_PUBLIC_KEY_BASE64";
    public const string TrustGenerationVariable = "SUNDER_RUNTIME_DEVELOPMENT_TRUST_GENERATION";
    public const string AllowInsecureLoopbackRegistryVariable =
        "SUNDER_RUNTIME_DEVELOPMENT_ALLOW_INSECURE_LOOPBACK_REGISTRY";

    private const long MaximumSafeJsonInteger = 9_007_199_254_740_991;

    public static ExecutionRuntimeDevelopmentConfiguration ReadAndClear(bool isDevelopment)
        => ReadAndClear(
            isDevelopment,
            Environment.GetEnvironmentVariable,
            static variable => Environment.SetEnvironmentVariable(variable, null));

    internal static ExecutionRuntimeDevelopmentConfiguration ReadAndClear(
        bool isDevelopment,
        Func<string, string?> read,
        Action<string> clear)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(clear);
        var keyId = read(ReleaseKeyIdVariable);
        var publicKeyBase64 = read(ReleasePublicKeyVariable);
        var trustGenerationValue = read(TrustGenerationVariable);
        var allowHttpValue = read(AllowInsecureLoopbackRegistryVariable);
        clear(ReleaseKeyIdVariable);
        clear(ReleasePublicKeyVariable);
        clear(TrustGenerationVariable);
        clear(AllowInsecureLoopbackRegistryVariable);

        if (keyId is null
            && publicKeyBase64 is null
            && trustGenerationValue is null
            && allowHttpValue is null)
        {
            return new ExecutionRuntimeDevelopmentConfiguration(null, false);
        }
        if (!isDevelopment)
        {
            throw new InvalidOperationException(
                "Development execution-runtime trust cannot be configured outside the Development environment.");
        }

        var trustGeneration = 1L;
        if (string.IsNullOrWhiteSpace(keyId)
            || string.IsNullOrWhiteSpace(publicKeyBase64)
            || trustGenerationValue is not null
            && !long.TryParse(
                trustGenerationValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out trustGeneration)
            || trustGeneration is <= 0 or > MaximumSafeJsonInteger)
        {
            throw new InvalidOperationException(
                "Development execution-runtime trust requires one release key id, public key, and positive safe-integer trust generation.");
        }
        if (allowHttpValue is not null && !bool.TryParse(allowHttpValue, out _))
        {
            throw new InvalidOperationException(
                "The Development execution-runtime loopback transport setting must be true or false.");
        }

        byte[] publicKey;
        try
        {
            publicKey = Convert.FromBase64String(publicKeyBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "The Development execution-runtime release public key is not valid base64.",
                exception);
        }
        var trust = new ExecutionRuntimeHostTrustService(
            RuntimeRegistryContractMapper.RuntimeTrustProfile.ProfileId,
            trustGeneration,
            developmentTrustEnabled: true,
            productionKeys: [],
            developmentKeys:
            [
                new ExecutionRuntimeHostTrustKey(
                    keyId,
                    publicKey,
                    trustGeneration),
            ]);
        return new ExecutionRuntimeDevelopmentConfiguration(
            trust,
            bool.TryParse(allowHttpValue, out var allowHttp) && allowHttp);
    }
}
