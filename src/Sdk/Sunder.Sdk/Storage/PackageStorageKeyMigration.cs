using System.Text.Json;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Storage;

/// <summary>Describes one package-owned legacy physical-key migration or cleanup rule.</summary>
[SunderSdkCapability(SunderSdkCapabilities.StorageV1)]
[SunderSdkCapability(SunderSdkCapabilities.StorageKeyMigrationV1)]
public sealed class PackageStorageKeyMigration
{
    private readonly Func<string, PackageStorageKeyMigrationAction>? _cleanupResolver;
    private readonly string? _destinationKey;
    private readonly string? _destinationPrefix;
    private readonly int _destinationVersion;
    private readonly string? _jsonIdentityProperty;
    private readonly bool _canonicalizeIdentityCase;

    private PackageStorageKeyMigration(
        string legacyPrefix,
        string legacySuffix,
        string? destinationKey,
        string? destinationPrefix,
        int destinationVersion,
        string? jsonIdentityProperty = null,
        bool canonicalizeIdentityCase = false,
        Func<string, PackageStorageKeyMigrationAction>? cleanupResolver = null)
    {
        LegacyPrefix = legacyPrefix;
        LegacySuffix = legacySuffix;
        _destinationKey = destinationKey;
        _destinationPrefix = destinationPrefix;
        _destinationVersion = destinationVersion;
        _jsonIdentityProperty = jsonIdentityProperty;
        _canonicalizeIdentityCase = canonicalizeIdentityCase;
        _cleanupResolver = cleanupResolver;
    }

    /// <summary>Gets the exact legacy key or the prefix before an opaque identifier.</summary>
    public string LegacyPrefix { get; }

    /// <summary>Gets the suffix after an opaque identifier, or an empty string for an exact migration.</summary>
    public string LegacySuffix { get; }

    /// <summary>Gets whether this rule performs collision-tolerant dynamic cleanup.</summary>
    public bool IsDynamicCleanup => _cleanupResolver is not null;

    /// <summary>
    /// Creates a key-only cleanup rule for recognized crash remnants whose complete physical shape must be parsed
    /// dynamically.
    /// </summary>
    /// <remarks>
    /// The resolver never receives stored values. Existing portable destinations win over remnants. If multiple
    /// equally preferred remnants contain different values for an absent destination, all conflicting remnants are
    /// removed and no destination is created. Returning <see cref="PackageStorageKeyMigrationAction.NoMatch"/> keeps
    /// the key outside this rule, so unknown invalid keys retain fail-closed handling.
    /// </remarks>
    public static PackageStorageKeyMigration DynamicCleanup(
        Func<string, PackageStorageKeyMigrationAction> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        return new PackageStorageKeyMigration(
            string.Empty,
            string.Empty,
            null,
            null,
            0,
            cleanupResolver: resolver);
    }

    /// <summary>Resolves one physical key without inspecting a stored value.</summary>
    public PackageStorageKeyMigrationAction Resolve(string legacyKey)
    {
        ArgumentNullException.ThrowIfNull(legacyKey);
        if (_cleanupResolver is not null)
        {
            return ResolveCleanup(legacyKey);
        }
        return TryGetDestinationKey(legacyKey, out var destinationKey)
            ? PackageStorageKeyMigrationAction.Rewrite(destinationKey)
            : PackageStorageKeyMigrationAction.NoMatch;
    }

    /// <summary>Resolves one physical key using its stored value when the rule supports JSON identity recovery.</summary>
    public PackageStorageKeyMigrationAction Resolve(string legacyKey, string value)
    {
        ArgumentNullException.ThrowIfNull(legacyKey);
        ArgumentNullException.ThrowIfNull(value);
        if (_cleanupResolver is not null)
        {
            return ResolveCleanup(legacyKey);
        }
        return TryGetDestinationKey(legacyKey, value, out var destinationKey)
            ? PackageStorageKeyMigrationAction.Rewrite(destinationKey)
            : PackageStorageKeyMigrationAction.NoMatch;
    }

    /// <summary>Creates a rule that rewrites one exact legacy key to one portable destination key.</summary>
    public static PackageStorageKeyMigration Exact(string legacyKey, string destinationKey)
    {
        ValidateLegacyPart(legacyKey, nameof(legacyKey), allowEmpty: false);
        if (!PackageStorageValidation.IsValidKey(destinationKey))
        {
            throw new ArgumentException("Migration destination keys must be portable package storage keys.", nameof(destinationKey));
        }
        if (string.Equals(legacyKey, destinationKey, StringComparison.Ordinal))
        {
            throw new ArgumentException("A package storage key migration must change the physical key.", nameof(destinationKey));
        }

        return new PackageStorageKeyMigration(legacyKey, string.Empty, destinationKey, null, 0);
    }

    /// <summary>
    /// Creates a rule that extracts the non-empty opaque identifier between a legacy prefix and suffix and rewrites
    /// it with <see cref="PackageStorageKeyFactory"/>.
    /// </summary>
    public static PackageStorageKeyMigration OpaqueId(
        string legacyPrefix,
        string legacySuffix,
        string destinationPrefix,
        int destinationVersion)
    {
        ValidateLegacyPart(legacyPrefix, nameof(legacyPrefix), allowEmpty: false);
        ValidateLegacyPart(legacySuffix, nameof(legacySuffix), allowEmpty: true);
        _ = PackageStorageKeyFactory.Create(destinationPrefix, destinationVersion, "validation");
        return new PackageStorageKeyMigration(
            legacyPrefix,
            legacySuffix,
            null,
            destinationPrefix,
            destinationVersion);
    }

    /// <summary>
    /// Creates an opaque-identifier rule that preserves the exact casing of a case-equivalent top-level JSON string
    /// identity from the stored value. Missing, malformed, ambiguous, or non-case-equivalent identities fall back to
    /// the identifier encoded in the legacy key.
    /// </summary>
    public static PackageStorageKeyMigration OpaqueIdWithJsonIdentity(
        string legacyPrefix,
        string legacySuffix,
        string destinationPrefix,
        int destinationVersion,
        string identityPropertyName)
        => CreateJsonIdentityMigration(
            legacyPrefix,
            legacySuffix,
            destinationPrefix,
            destinationVersion,
            identityPropertyName,
            canonicalizeIdentityCase: false);

    /// <summary>
    /// Creates a JSON-identity rule that canonicalizes the case of the resulting opaque identifier so
    /// case-equivalent legacy identities converge on one destination key.
    /// </summary>
    /// <remarks>
    /// The JSON identity is accepted only when it is case-equivalent to the identifier in the legacy key.
    /// The selected identifier is canonicalized with invariant uppercase before its destination key is created.
    /// </remarks>
    public static PackageStorageKeyMigration OpaqueIdWithCaseInsensitiveJsonIdentity(
        string legacyPrefix,
        string legacySuffix,
        string destinationPrefix,
        int destinationVersion,
        string identityPropertyName)
        => CreateJsonIdentityMigration(
            legacyPrefix,
            legacySuffix,
            destinationPrefix,
            destinationVersion,
            identityPropertyName,
            canonicalizeIdentityCase: true);

    private static PackageStorageKeyMigration CreateJsonIdentityMigration(
        string legacyPrefix,
        string legacySuffix,
        string destinationPrefix,
        int destinationVersion,
        string identityPropertyName,
        bool canonicalizeIdentityCase)
    {
        ValidateLegacyPart(legacyPrefix, nameof(legacyPrefix), allowEmpty: false);
        ValidateLegacyPart(legacySuffix, nameof(legacySuffix), allowEmpty: true);
        ArgumentException.ThrowIfNullOrWhiteSpace(identityPropertyName);
        if (!PackageStorageValidation.IsValidValue(identityPropertyName))
        {
            throw new ArgumentException(
                "A migration JSON identity property must contain bounded valid Unicode.",
                nameof(identityPropertyName));
        }
        _ = PackageStorageKeyFactory.Create(destinationPrefix, destinationVersion, "validation");
        return new PackageStorageKeyMigration(
            legacyPrefix,
            legacySuffix,
            null,
            destinationPrefix,
            destinationVersion,
            identityPropertyName,
            canonicalizeIdentityCase);
    }

    /// <summary>Attempts to map a physical legacy key to its portable destination key.</summary>
    public bool TryGetDestinationKey(string legacyKey, out string destinationKey)
        => TryGetDestinationKey(legacyKey, value: null, inspectJsonIdentity: false, out destinationKey);

    /// <summary>Attempts to map a physical legacy key using both its opaque identifier and stored value.</summary>
    public bool TryGetDestinationKey(string legacyKey, string value, out string destinationKey)
    {
        ArgumentNullException.ThrowIfNull(value);
        return TryGetDestinationKey(legacyKey, value, inspectJsonIdentity: true, out destinationKey);
    }

    private bool TryGetDestinationKey(
        string legacyKey,
        string? value,
        bool inspectJsonIdentity,
        out string destinationKey)
    {
        ArgumentNullException.ThrowIfNull(legacyKey);
        if (_cleanupResolver is not null)
        {
            var action = ResolveCleanup(legacyKey);
            destinationKey = action.DestinationKey ?? string.Empty;
            return action.Kind == PackageStorageKeyMigrationActionKind.Rewrite;
        }
        if (_destinationKey is not null)
        {
            if (string.Equals(legacyKey, LegacyPrefix, StringComparison.Ordinal))
            {
                destinationKey = _destinationKey;
                return true;
            }

            destinationKey = string.Empty;
            return false;
        }

        if (!legacyKey.StartsWith(LegacyPrefix, StringComparison.Ordinal)
            || !legacyKey.EndsWith(LegacySuffix, StringComparison.Ordinal)
            || legacyKey.Length <= LegacyPrefix.Length + LegacySuffix.Length)
        {
            destinationKey = string.Empty;
            return false;
        }

        var opaqueLength = legacyKey.Length - LegacyPrefix.Length - LegacySuffix.Length;
        var opaqueId = legacyKey.Substring(LegacyPrefix.Length, opaqueLength);
        if (inspectJsonIdentity
            && _jsonIdentityProperty is not null
            && TryReadJsonIdentity(value!, _jsonIdentityProperty, out var payloadIdentity)
            && string.Equals(opaqueId, payloadIdentity, StringComparison.OrdinalIgnoreCase))
        {
            opaqueId = payloadIdentity;
        }
        if (_canonicalizeIdentityCase)
        {
            opaqueId = opaqueId.ToUpperInvariant();
        }
        destinationKey = PackageStorageKeyFactory.Create(
            _destinationPrefix!,
            _destinationVersion,
            opaqueId);
        return true;
    }

    private PackageStorageKeyMigrationAction ResolveCleanup(string legacyKey)
        => _cleanupResolver!(legacyKey)
           ?? throw new InvalidOperationException("A dynamic package storage cleanup resolver returned null.");

    private static bool TryReadJsonIdentity(
        string value,
        string identityPropertyName,
        out string identity)
    {
        identity = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var found = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, identityPropertyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (found || property.Value.ValueKind != JsonValueKind.String)
                {
                    return false;
                }
                identity = property.Value.GetString() ?? string.Empty;
                found = true;
            }
            return found && identity.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ValidateLegacyPart(string value, string parameterName, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if ((!allowEmpty && value.Length == 0) || !PackageStorageValidation.IsValidValue(value))
        {
            throw new ArgumentException("Legacy package storage key components must contain bounded valid Unicode.", parameterName);
        }
    }
}
