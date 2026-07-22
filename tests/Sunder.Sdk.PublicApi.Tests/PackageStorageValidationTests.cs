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
