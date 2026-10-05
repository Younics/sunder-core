using Sunder.Package.Format;

namespace Sunder.Runtime.Host.Services;

internal sealed class ExecutionRuntimeHostTrustKey
{
    public ExecutionRuntimeHostTrustKey(
        string keyId,
        ReadOnlySpan<byte> publicKey,
        long introducedInGeneration,
        long? retiredAfterGeneration = null)
    {
        ExecutionRuntimeStoreLayout.ValidateSegment(keyId, nameof(keyId));
        if (publicKey.Length != SunderExecutionRuntimeFormat.Ed25519PublicKeyBytes)
        {
            throw new ArgumentException(
                $"An Ed25519 public key must contain exactly {SunderExecutionRuntimeFormat.Ed25519PublicKeyBytes} bytes.",
                nameof(publicKey));
        }
        if (introducedInGeneration <= 0
            || retiredAfterGeneration is not null && retiredAfterGeneration < introducedInGeneration)
        {
            throw new ArgumentOutOfRangeException(nameof(introducedInGeneration));
        }

        KeyId = keyId;
        PublicKey = publicKey.ToArray();
        IntroducedInGeneration = introducedInGeneration;
        RetiredAfterGeneration = retiredAfterGeneration;
    }

    public string KeyId { get; }
    public ReadOnlyMemory<byte> PublicKey { get; }
    public long IntroducedInGeneration { get; }
    public long? RetiredAfterGeneration { get; }
}

internal sealed class ExecutionRuntimeHostTrustService
{
    private readonly IReadOnlyDictionary<string, ExecutionRuntimeHostTrustKey> _productionKeys;
    private readonly IReadOnlyDictionary<string, ExecutionRuntimeHostTrustKey> _developmentKeys;

    public ExecutionRuntimeHostTrustService(
        string profileId,
        long generation,
        bool developmentTrustEnabled,
        IEnumerable<ExecutionRuntimeHostTrustKey> productionKeys,
        IEnumerable<ExecutionRuntimeHostTrustKey>? developmentKeys = null)
    {
        ExecutionRuntimeStoreLayout.ValidateSegment(profileId, nameof(profileId));
        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        ProfileId = profileId;
        Generation = generation;
        DevelopmentTrustEnabled = developmentTrustEnabled;
        _productionKeys = CreateKeySet(productionKeys, nameof(productionKeys));
        _developmentKeys = CreateKeySet(developmentKeys ?? [], nameof(developmentKeys));
        var duplicate = _productionKeys.Keys.Intersect(_developmentKeys.Keys, StringComparer.Ordinal).FirstOrDefault();
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Trust key id '{duplicate}' occurs in both production and development trust sets.");
        }
        var overlappingMaterial = _productionKeys.Values.FirstOrDefault(productionKey =>
            _developmentKeys.Values.Any(developmentKey =>
                productionKey.PublicKey.Span.SequenceEqual(developmentKey.PublicKey.Span)));
        if (overlappingMaterial is not null)
        {
            throw new ArgumentException(
                $"Trust key material for '{overlappingMaterial.KeyId}' occurs in both production and development trust sets.");
        }
        if (!developmentTrustEnabled && _developmentKeys.Count > 0)
        {
            throw new ArgumentException(
                "Development keys cannot be supplied when development trust is disabled.",
                nameof(developmentKeys));
        }
    }

    public string ProfileId { get; }
    public long Generation { get; }
    public bool DevelopmentTrustEnabled { get; }

    internal ExecutionRuntimeTrustEnvironment Classify(
        string signingKeyId,
        long minimumHostTrustGeneration)
    {
        ValidateGeneration(minimumHostTrustGeneration);
        var hasProductionKey = _productionKeys.TryGetValue(signingKeyId, out var productionKey);
        var hasDevelopmentKey = _developmentKeys.TryGetValue(signingKeyId, out var developmentKey);
        if (hasProductionKey == hasDevelopmentKey)
        {
            throw new ExecutionRuntimeTrustException(hasProductionKey
                ? $"Execution runtime signing key '{signingKeyId}' occurs in multiple Host trust environments."
                : $"Execution runtime signing key '{signingKeyId}' is not trusted by this Host profile.");
        }
        if (hasProductionKey)
        {
            EnsureActive(productionKey!);
            return ExecutionRuntimeTrustEnvironment.Production;
        }
        if (!DevelopmentTrustEnabled)
        {
            throw new ExecutionRuntimeTrustException(
                "Development execution runtime trust is disabled for this Host profile.");
        }

        EnsureActive(developmentKey!);
        return ExecutionRuntimeTrustEnvironment.Development;
    }

    public SunderExecutionRuntimeTrustedKey Resolve(
        string keyId,
        ExecutionRuntimeTrustEnvironment environment,
        long minimumHostTrustGeneration)
    {
        ValidateGeneration(minimumHostTrustGeneration);

        var keys = environment switch
        {
            ExecutionRuntimeTrustEnvironment.Production => _productionKeys,
            ExecutionRuntimeTrustEnvironment.Development when DevelopmentTrustEnabled => _developmentKeys,
            ExecutionRuntimeTrustEnvironment.Development => throw new ExecutionRuntimeTrustException(
                "Development execution runtime trust is disabled for this Host profile."),
            _ => throw new ExecutionRuntimeTrustException("The execution runtime trust environment is invalid."),
        };
        if (!keys.TryGetValue(keyId, out var key))
        {
            throw new ExecutionRuntimeTrustException(
                $"Execution runtime signing key '{keyId}' is not trusted in the {environment.ToString().ToLowerInvariant()} trust set.");
        }
        EnsureActive(key);

        return new SunderExecutionRuntimeTrustedKey(key.KeyId, key.PublicKey);
    }

    private void ValidateGeneration(long minimumHostTrustGeneration)
    {
        if (minimumHostTrustGeneration <= 0)
        {
            throw new ExecutionRuntimeTrustException(
                "The execution runtime plan has an invalid minimum Host trust generation.");
        }
        if (Generation < minimumHostTrustGeneration)
        {
            throw new ExecutionRuntimeTrustException(
                $"Host trust generation {Generation} does not satisfy required generation {minimumHostTrustGeneration}.");
        }
    }

    private void EnsureActive(ExecutionRuntimeHostTrustKey key)
    {
        if (Generation < key.IntroducedInGeneration
            || key.RetiredAfterGeneration is { } retired && Generation > retired)
        {
            throw new ExecutionRuntimeTrustException(
                $"Execution runtime signing key '{key.KeyId}' is not active at Host trust generation {Generation}.");
        }
    }

    private static IReadOnlyDictionary<string, ExecutionRuntimeHostTrustKey> CreateKeySet(
        IEnumerable<ExecutionRuntimeHostTrustKey> keys,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(keys, parameterName);
        var result = new Dictionary<string, ExecutionRuntimeHostTrustKey>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            ArgumentNullException.ThrowIfNull(key, parameterName);
            if (!result.TryAdd(key.KeyId, key))
            {
                throw new ArgumentException($"Duplicate trust key id '{key.KeyId}'.", parameterName);
            }
        }
        return result;
    }
}

internal sealed class ExecutionRuntimeEd25519SignatureVerifier
    : ISunderExecutionRuntimeArchiveSignatureVerifier
{
    public bool Verify(
        ReadOnlySpan<byte> signatureInput,
        ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> trustedPublicKey)
        => ExecutionRuntimeEd25519.Verify(signatureInput, signature, trustedPublicKey);
}
