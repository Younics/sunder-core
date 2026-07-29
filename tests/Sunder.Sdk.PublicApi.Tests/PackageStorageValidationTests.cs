using Sunder.Sdk.Runtime;
using Sunder.Sdk.Settings;
using Sunder.Sdk.Storage;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class PackageStorageValidationTests
{
    [Fact]
    public void Limits_AreTheV1PackageStorageBounds()
    {
        Assert.Equal(256, PackageStorageValidation.MaximumKeyLength);
        Assert.Equal(1024 * 1024, PackageStorageValidation.MaximumValueUtf8Bytes);
        Assert.Equal(1024, PackageStorageValidation.MaximumRelativePathLength);
        Assert.Equal(255, PackageStorageValidation.MaximumPathSegmentLength);
        Assert.Equal(16 * 1024 * 1024, PackageStorageValidation.MaximumFileBytes);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("Feature.Enabled_1")]
    [InlineData("a-b.c_d")]
    public void Keys_AcceptPortableAsciiTokens(string key)
        => Assert.True(PackageStorageValidation.IsValidKey(key));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("with space")]
    [InlineData("path/key")]
    [InlineData("caf\u00E9")]
    public void Keys_RejectNonPortableTokens(string? key)
        => Assert.False(PackageStorageValidation.IsValidKey(key));

    [Fact]
    public void Keys_EnforceTheCharacterLimit()
    {
        Assert.True(PackageStorageValidation.IsValidKey(
            new string('a', PackageStorageValidation.MaximumKeyLength)));
        Assert.False(PackageStorageValidation.IsValidKey(
            new string('a', PackageStorageValidation.MaximumKeyLength + 1)));
    }

    public static TheoryData<string> OpaqueIds => new()
    {
        "contains:colon",
        "unicode-\u65E5\u672C\u8A9E-\u00E9",
        "contains whitespace\tand newline\n",
        "path/with\\separators",
        new string('x', PackageStorageValidation.MaximumKeyLength + 200),
    };

    [Theory]
    [MemberData(nameof(OpaqueIds))]
    public void KeyFactory_HashesArbitraryOpaqueIdsIntoPortableBoundedKeys(string opaqueId)
    {
        var key = PackageStorageKeyFactory.Create("workspace-bindings.config", 2, opaqueId);
        var repeated = PackageStorageKeyFactory.Create("workspace-bindings.config", 2, opaqueId);
        var hash = key[(key.LastIndexOf('.') + 1)..];

        Assert.Equal(repeated, key);
        Assert.StartsWith("workspace-bindings.config.v2.", key, StringComparison.Ordinal);
        Assert.Equal(64, hash.Length);
        Assert.All(hash, character => Assert.True(char.IsAsciiDigit(character) || character is >= 'a' and <= 'f'));
        Assert.True(PackageStorageValidation.IsValidKey(key));
        Assert.True(key.Length <= PackageStorageValidation.MaximumKeyLength);
    }

    [Fact]
    public void KeyFactory_RejectsInvalidInputsAndSeparatesVersionsAndOpaqueIds()
    {
        var first = PackageStorageKeyFactory.Create("catalog.item", 1, "opaque");

        Assert.Equal(
            "catalog.item.v1.ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            PackageStorageKeyFactory.Create("catalog.item", 1, "abc"));
        Assert.NotEqual(first, PackageStorageKeyFactory.Create("catalog.item", 2, "opaque"));
        Assert.NotEqual(first, PackageStorageKeyFactory.Create("catalog.item", 1, "Opaque"));
        Assert.NotEqual(
            PackageStorageKeyFactory.Create("catalog.item", 1, "\u00E9"),
            PackageStorageKeyFactory.Create("catalog.item", 1, "e\u0301"));
        Assert.True(PackageStorageValidation.IsValidKey(
            PackageStorageKeyFactory.Create(new string('p', 188), 1, "opaque")));
        Assert.Throws<ArgumentException>(() => PackageStorageKeyFactory.Create("bad:prefix", 1, "opaque"));
        Assert.Throws<ArgumentOutOfRangeException>(() => PackageStorageKeyFactory.Create("catalog.item", 0, "opaque"));
        Assert.Throws<ArgumentException>(() => PackageStorageKeyFactory.Create("catalog.item", 1, string.Empty));
        Assert.Throws<ArgumentException>(() => PackageStorageKeyFactory.Create("catalog.item", 1, "\uD800"));
        Assert.Throws<ArgumentException>(() => PackageStorageKeyFactory.Create(new string('p', 189), 1, "opaque"));
    }

    [Fact]
    public void KeyMigration_MapsExactAndOpaqueLegacyKeysOrdinally()
    {
        var exact = PackageStorageKeyMigration.Exact("docker.images:v1", "docker.images.v1");
        var opaque = PackageStorageKeyMigration.OpaqueId(
            "workspace-bindings:",
            ":config",
            "workspace-bindings.config",
            2);

        Assert.True(exact.TryGetDestinationKey("docker.images:v1", out var imageKey));
        Assert.Equal("docker.images.v1", imageKey);
        Assert.False(exact.TryGetDestinationKey("Docker.images:v1", out _));
        Assert.True(opaque.TryGetDestinationKey("workspace-bindings:a/b:\u65E5:config", out var workspaceKey));
        Assert.Equal(
            PackageStorageKeyFactory.Create("workspace-bindings.config", 2, "a/b:\u65E5"),
            workspaceKey);
        Assert.False(opaque.TryGetDestinationKey("workspace-bindings::config", out _));
    }

    [Fact]
    public void KeyMigration_CaseInsensitiveJsonIdentityConvergesCaseVariants()
    {
        var migration = PackageStorageKeyMigration.OpaqueIdWithCaseInsensitiveJsonIdentity(
            "mcp.servers.",
            string.Empty,
            "mcp.catalog.server",
            1,
            "serverId");

        Assert.True(migration.TryGetDestinationKey(
            "mcp.servers.server-one",
            "{\"ServerId\":\"server-one\"}",
            out var lowerDestination));
        Assert.True(migration.TryGetDestinationKey(
            "mcp.servers.SERVER-ONE",
            "{\"ServerId\":\"SERVER-ONE\"}",
            out var upperDestination));
        Assert.Equal(lowerDestination, upperDestination);
        Assert.Equal(
            PackageStorageKeyFactory.Create("mcp.catalog.server", 1, "SERVER-ONE"),
            lowerDestination);
    }

    [Fact]
    public void KeyMigration_DynamicCleanupExposesOnlyKeyBasedActions()
    {
        var destination = PackageStorageKeyFactory.Create("mcp.secret.header", 1, "server");
        var migration = PackageStorageKeyMigration.DynamicCleanup(key => key switch
        {
            "legacy.current" => PackageStorageKeyMigrationAction.Rewrite(destination, precedence: 2),
            "legacy.orphan" => PackageStorageKeyMigrationAction.Delete,
            _ => PackageStorageKeyMigrationAction.NoMatch,
        });

        var rewrite = migration.Resolve("legacy.current", "secret-value-is-not-forwarded");

        Assert.True(migration.IsDynamicCleanup);
        Assert.Equal(PackageStorageKeyMigrationActionKind.Rewrite, rewrite.Kind);
        Assert.Equal(destination, rewrite.DestinationKey);
        Assert.Equal(2, rewrite.Precedence);
        Assert.Equal(PackageStorageKeyMigrationActionKind.Delete, migration.Resolve("legacy.orphan").Kind);
        Assert.Equal(PackageStorageKeyMigrationActionKind.NoMatch, migration.Resolve("unknown").Kind);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PackageStorageKeyMigrationAction.Rewrite(destination, precedence: -1));
    }

    [Fact]
    public void RuntimeInvocationException_ExposesOnlyValidatedDiagnosticMetadata()
    {
        var exception = new PackageRuntimeInvocationException(
            "runtime.v1.unavailable",
            isTransient: true,
            statusCode: 503,
            correlationId: "correlation-42");

        Assert.Equal("runtime.v1.unavailable", exception.Code);
        Assert.True(exception.IsTransient);
        Assert.Equal(503, exception.StatusCode);
        Assert.Equal("correlation-42", exception.CorrelationId);
        Assert.Null(exception.InnerException);
        Assert.Throws<ArgumentException>(() => new PackageRuntimeInvocationException("Runtime BAD", false));
        Assert.Throws<ArgumentException>(() => new PackageRuntimeInvocationException("runtime.v1.bad", false, correlationId: "unsafe id"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PackageRuntimeInvocationException("runtime.v1.bad", false, statusCode: 99));
    }

    [Fact]
    public void Values_EnforceStrictUtf8ByteCountRatherThanCharacterCount()
    {
        var exact = new string('\u00E9', PackageStorageValidation.MaximumValueUtf8Bytes / 2);

        Assert.True(PackageStorageValidation.IsValidValue(string.Empty));
        Assert.True(PackageStorageValidation.IsValidValue(exact));
        Assert.False(PackageStorageValidation.IsValidValue(exact + "a"));
        Assert.False(PackageStorageValidation.IsValidValue("\uD800"));
        Assert.False(PackageStorageValidation.IsValidValue(null));
    }

    [Theory]
    [InlineData("file.db")]
    [InlineData("folder/file.db")]
    [InlineData("folder/a file.txt")]
    public void RelativePaths_AcceptPortablePaths(string path)
        => Assert.True(PackageStorageValidation.IsValidRelativePath(path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/rooted")]
    [InlineData("folder\\file.db")]
    [InlineData("folder//file.db")]
    [InlineData("folder/./file.db")]
    [InlineData("folder/../file.db")]
    [InlineData("folder/file.")]
    [InlineData("folder/file ")]
    [InlineData("folder/file?.db")]
    [InlineData("folder/caf\u00E9.db")]
    [InlineData("CON")]
    [InlineData("aux.txt")]
    public void RelativePaths_RejectPlatformDependentOrUnsafePaths(string? path)
        => Assert.False(PackageStorageValidation.IsValidRelativePath(path));

    [Fact]
    public void RelativePaths_EnforcePathAndSegmentLimits()
    {
        Assert.True(PackageStorageValidation.IsValidRelativePath(
            new string('a', PackageStorageValidation.MaximumPathSegmentLength)));
        Assert.False(PackageStorageValidation.IsValidRelativePath(
            new string('a', PackageStorageValidation.MaximumPathSegmentLength + 1)));

        var exactPath = string.Join('/', Enumerable.Repeat(new string('a', 204), 5));
        Assert.Equal(PackageStorageValidation.MaximumRelativePathLength, exactPath.Length);
        Assert.True(PackageStorageValidation.IsValidRelativePath(exactPath));
        Assert.False(PackageStorageValidation.IsValidRelativePath(exactPath + "a"));
    }

    [Fact]
    public void FileLengths_EnforceTheByteLimit()
    {
        Assert.True(PackageStorageValidation.IsValidFileLength(0));
        Assert.True(PackageStorageValidation.IsValidFileLength(PackageStorageValidation.MaximumFileBytes));
        Assert.False(PackageStorageValidation.IsValidFileLength(-1));
        Assert.False(PackageStorageValidation.IsValidFileLength(PackageStorageValidation.MaximumFileBytes + 1L));
    }

    [Fact]
    public void SettingsSchema_UsesPortableKeysAndUtf8ValueBounds()
    {
        var oversized = new string('\u00E9', PackageStorageValidation.MaximumValueUtf8Bytes / 2 + 1);

        Assert.Throws<ArgumentException>(() => new PackageSettingsField(
            "caf\u00E9",
            "Invalid key",
            PackageSettingsFieldKind.Text));
        Assert.Throws<ArgumentException>(() => new PackageSettingsSection(
            "caf\u00E9",
            "Invalid section",
            null,
            [new PackageSettingsField("valid", "Valid", PackageSettingsFieldKind.Text)]));
        Assert.Throws<ArgumentException>(() => new PackageSettingsField(
            "value",
            "Oversized default",
            PackageSettingsFieldKind.Text,
            defaultValue: oversized));
        Assert.Throws<ArgumentException>(() => new PackageSettingsOption(oversized, "Oversized option"));
    }
}
