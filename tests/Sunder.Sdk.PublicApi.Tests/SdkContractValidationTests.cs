using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Settings;
using Sunder.Sdk.Stacks;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class SdkContractValidationTests
{
    [Fact]
    public void PackageSettingsSchema_RejectsDuplicateFieldKeysAcrossSections()
    {
        var field = new PackageSettingsField("enabled", "Enabled", PackageSettingsFieldKind.Boolean);

        var exception = Assert.Throws<ArgumentException>(() => new PackageSettingsSchema(
            null,
            [
                new PackageSettingsSection("general", "General", null, [field]),
                new PackageSettingsSection("advanced", "Advanced", null, [field]),
            ]));

        Assert.Contains("declared more than once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageSettingsField_RejectsSecretDefault()
        => Assert.Throws<ArgumentException>(() => new PackageSettingsField(
            "api-key",
            "API key",
            PackageSettingsFieldKind.Secret,
            defaultValue: "must-not-ship"));

    [Fact]
    public void StackSelectionHelpers_DefaultMissingDetailOverrideAndHonorItemSelection()
    {
        var selected = new StackExportRequest(
            ["profile"],
            [new StackExportItemSelection("profile", [new StackExportDetailSelection("model", IsSelected: false)])]);

        Assert.True(selected.IsItemSelected("PROFILE"));
        Assert.False(selected.IsDetailSelected("profile", "model"));
        Assert.True(selected.IsDetailSelected("profile", "instructions"));
        Assert.False(selected.IsDetailSelected("other", "instructions"));
        Assert.Equal("fallback", selected.GetDetailValue("profile", "model", "fallback"));
    }

    [Fact]
    public async Task PackageFileStore_DefaultStreamMembersRoundTrip()
    {
        IPackageFileStore store = new MemoryPackageFileStore();
        await using var source = new MemoryStream([1, 2, 3]);

        await store.WriteAsync("data.bin", source);
        await using var result = await store.OpenReadAsync("data.bin");

        Assert.NotNull(result);
        using var copy = new MemoryStream();
        await result.CopyToAsync(copy);
        Assert.Equal([1, 2, 3], copy.ToArray());
    }

    private sealed class MemoryPackageFileStore : IPackageFileStore
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        public Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
            => Task.FromResult(_files.TryGetValue(relativePath, out var value) ? value.ToArray() : null);

        public Task WriteAsync(
            string relativePath,
            ReadOnlyMemory<byte> contents,
            CancellationToken cancellationToken = default)
        {
            _files[relativePath] = contents.ToArray();
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            _files.Remove(relativePath);
            return Task.CompletedTask;
        }
    }
}
