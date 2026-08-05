using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Storage;

/// <summary>Describes how a dynamic cleanup rule handles one physical package storage key.</summary>
[SunderSdkCapability(SunderSdkCapabilities.StorageV1)]
[SunderSdkCapability(SunderSdkCapabilities.StorageKeyMigrationV1)]
public sealed class PackageStorageKeyMigrationAction
{
    private PackageStorageKeyMigrationAction(
        PackageStorageKeyMigrationActionKind kind,
        string? destinationKey,
        int precedence)
    {
        Kind = kind;
        DestinationKey = destinationKey;
        Precedence = precedence;
    }

    /// <summary>Gets an action that leaves an unrecognized key outside this rule.</summary>
    public static PackageStorageKeyMigrationAction NoMatch { get; } = new(
        PackageStorageKeyMigrationActionKind.NoMatch,
        null,
        0);

    /// <summary>Gets an action that removes a recognized obsolete key.</summary>
    public static PackageStorageKeyMigrationAction Delete { get; } = new(
        PackageStorageKeyMigrationActionKind.Delete,
        null,
        0);

    /// <summary>Creates an action that rewrites a recognized key to a portable destination.</summary>
    /// <param name="destinationKey">The portable physical destination key.</param>
    /// <param name="precedence">
    /// The non-negative precedence used when multiple cleanup remnants target one absent destination. The highest
    /// precedence wins; equally preferred nonidentical values are all removed rather than selected.
    /// </param>
    public static PackageStorageKeyMigrationAction Rewrite(string destinationKey, int precedence = 0)
    {
        if (!PackageStorageValidation.IsValidKey(destinationKey))
        {
            throw new ArgumentException("Migration destination keys must be portable package storage keys.", nameof(destinationKey));
        }
        if (precedence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(precedence), "Migration precedence must not be negative.");
        }

        return new PackageStorageKeyMigrationAction(
            PackageStorageKeyMigrationActionKind.Rewrite,
            destinationKey,
            precedence);
    }

    /// <summary>Gets the operation selected for the physical key.</summary>
    public PackageStorageKeyMigrationActionKind Kind { get; }

    /// <summary>Gets the portable destination for <see cref="PackageStorageKeyMigrationActionKind.Rewrite"/>.</summary>
    public string? DestinationKey { get; }

    /// <summary>Gets this remnant's precedence when a cleanup destination is absent.</summary>
    public int Precedence { get; }
}

/// <summary>Identifies the operation selected by a package storage migration rule.</summary>
[SunderSdkCapability(SunderSdkCapabilities.StorageV1)]
[SunderSdkCapability(SunderSdkCapabilities.StorageKeyMigrationV1)]
public enum PackageStorageKeyMigrationActionKind
{
    /// <summary>The rule does not recognize the physical key.</summary>
    NoMatch,
    /// <summary>The rule recognizes and removes an obsolete physical key.</summary>
    Delete,
    /// <summary>The rule recognizes and rewrites a physical key.</summary>
    Rewrite,
}
