using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Storage;

/// <summary>Creates deterministic portable physical keys for opaque package-owned identifiers.</summary>
[SunderSdkCapability(SunderSdkCapabilities.StorageV1)]
[SunderSdkCapability(SunderSdkCapabilities.StorageKeyMigrationV1)]
public static class PackageStorageKeyFactory
{
    private const int Sha256HexLength = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Creates a key in the form <c>{prefix}.v{version}.{lowercase-sha256}</c>, hashing the exact UTF-8 bytes of
    /// <paramref name="opaqueId"/> without changing that domain identifier.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The prefix or opaque identifier is empty or invalid, or the resulting key would exceed the storage bound.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> is less than one.</exception>
    public static string Create(string prefix, int version, string opaqueId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentNullException.ThrowIfNull(opaqueId);
        if (!PackageStorageValidation.IsValidKey(prefix))
        {
            throw new ArgumentException("Package storage key prefixes must be portable ASCII tokens.", nameof(prefix));
        }
        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Package storage key versions must be positive.");
        }
        if (opaqueId.Length == 0)
        {
            throw new ArgumentException("Opaque package storage identifiers must not be empty.", nameof(opaqueId));
        }

        var versionedPrefix = string.Concat(
            prefix,
            ".v",
            version.ToString(CultureInfo.InvariantCulture),
            ".");
        if (versionedPrefix.Length + Sha256HexLength > PackageStorageValidation.MaximumKeyLength)
        {
            throw new ArgumentException(
                $"The versioned package storage key prefix leaves insufficient room within the {PackageStorageValidation.MaximumKeyLength}-character key limit.",
                nameof(prefix));
        }

        byte[] opaqueBytes;
        try
        {
            opaqueBytes = StrictUtf8.GetBytes(opaqueId);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("Opaque package storage identifiers must contain valid Unicode.", nameof(opaqueId), exception);
        }

        return versionedPrefix + Convert.ToHexString(SHA256.HashData(opaqueBytes)).ToLowerInvariant();
    }
}
