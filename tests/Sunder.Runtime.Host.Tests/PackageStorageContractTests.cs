using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Sdk.Storage;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageStorageContractTests
{
    [Fact]
    public async Task LocalStateAndSecretStores_RejectInvalidInputsBeforePersistence()
    {
        var root = CreateTempDirectory();
        var statePath = Path.Combine(root, "state.json");
        var secretsPath = Path.Combine(root, "secrets.json");
        var state = new JsonPackageKeyValueStore(statePath);
        var secrets = CreateSecretsStore(secretsPath);

        await Assert.ThrowsAsync<ArgumentException>(() => state.SetValueAsync("bad key", "value"));
        await Assert.ThrowsAsync<ArgumentException>(() => state.GetValueAsync("caf\u00E9"));
        await Assert.ThrowsAsync<ArgumentException>(() => state.ListKeysAsync("bad prefix"));
        await Assert.ThrowsAsync<ArgumentException>(() => secrets.SetSecretAsync("bad/key", "value"));
        await Assert.ThrowsAsync<ArgumentException>(() => secrets.GetSecretAsync("caf\u00E9"));

        Assert.False(File.Exists(statePath));
        Assert.False(File.Exists(secretsPath));
        Assert.False(File.Exists($"{secretsPath}.key"));
    }

    [Fact]
    public async Task LocalStateAndSecretStores_UseUtf8ValueLimits()
    {
        var root = CreateTempDirectory();
        var state = new JsonPackageKeyValueStore(Path.Combine(root, "state.json"));
        var secrets = CreateSecretsStore(Path.Combine(root, "secrets.json"));
        var exact = new string('\u00E9', PackageStorageValidation.MaximumValueUtf8Bytes / 2);
        var oversized = exact + "a";

        await state.SetValueAsync("value", exact);
        await secrets.SetSecretAsync("value", exact);

        await Assert.ThrowsAsync<ArgumentException>(() => state.SetValueAsync("value", oversized));
        await Assert.ThrowsAsync<ArgumentException>(() => secrets.SetSecretAsync("value", oversized));
        Assert.Equal(exact, await state.GetValueAsync("value"));
        Assert.Equal(exact, await secrets.GetSecretAsync("value"));
    }

    [Fact]
    public async Task LocalFileStore_OversizedStreamLeavesInputOpenAndPriorFileUnchanged()
    {
        var root = CreateTempDirectory();
        var store = new LocalPackageFileStore(root);
        await store.WriteAsync("value.bin", new byte[] { 1, 2, 3 });
        using var source = new MemoryStream(
            new byte[PackageStorageValidation.MaximumFileBytes + 1],
            writable: false);

        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync("value.bin", source));

        Assert.True(source.CanRead);
        Assert.Equal(new byte[] { 1, 2, 3 }, await store.ReadAsync("value.bin"));
        Assert.Empty(Directory.EnumerateFiles(root, ".sunder-*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LocalFileStore_RejectsOversizedStoredFileWithoutReadingIt()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "oversized.bin");
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(PackageStorageValidation.MaximumFileBytes + 1L);
        }
        var store = new LocalPackageFileStore(root);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync("oversized.bin"));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await store.OpenReadAsync("oversized.bin"));
    }

    [Fact]
    public async Task LocalFileStore_RejectsNonPortablePathsAndSymbolicLinkTraversal()
    {
        var root = CreateTempDirectory();
        var outside = CreateTempDirectory();
        var store = new LocalPackageFileStore(root);

        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync("folder\\file.bin", new byte[] { 1 }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync("CON", new byte[] { 1 }));

        var linkPath = Path.Combine(root, "link");
        try
        {
            Directory.CreateSymbolicLink(linkPath, outside);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            return;
        }

        var exception = Assert.Throws<ArgumentException>(() => store.ResolvePath("link/file.bin"));
        Assert.Contains("symbolic links", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(outside, "file.bin")));
    }

    [Fact]
    public async Task PersistedStateOutsideTheSdkContractFailsClosed()
    {
        var root = CreateTempDirectory();
        var statePath = Path.Combine(root, "state.json");
        await File.WriteAllTextAsync(statePath, """
            {"format":"sunder.package-state","version":1,"revision":1,"values":{"bad key":"value"}}
            """);
        var store = new JsonPackageKeyValueStore(statePath);

        await Assert.ThrowsAsync<PackageStorageRecoveryRequiredException>(() => store.GetValueAsync("valid"));

        Assert.Single(Directory.EnumerateFiles(root, "state.json.corrupt.*"));
    }

    private static JsonPackageSecretsStore CreateSecretsStore(string path)
        => new(path, null, null, new RestrictedFileMasterKeyProtection());

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-storage-contract-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
