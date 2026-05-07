using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Storage;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageStorageServicesTests
{
    [Fact]
    public void LocalPackageFileStore_GetPath_RejectsParentTraversal()
    {
        var store = new LocalPackageFileStore(CreateTempDirectory());

        var exception = Assert.Throws<InvalidOperationException>(() => store.GetPath("../outside.txt"));

        Assert.Contains("parent directory traversal", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalPackageFileStore_GetPath_RejectsAbsolutePath()
    {
        var store = new LocalPackageFileStore(CreateTempDirectory());
        var absolutePath = Path.Combine(Path.GetTempPath(), "outside.txt");

        var exception = Assert.Throws<InvalidOperationException>(() => store.GetPath(absolutePath));

        Assert.Contains("must be relative", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalPackageFileStore_GetPath_CombinesRelativeSegments()
    {
        var root = CreateTempDirectory();
        var store = new LocalPackageFileStore(root);

        var path = store.GetPath("folder/./file.txt");

        Assert.Equal(Path.Combine(root, "folder", "file.txt"), path);
    }

    [Fact]
    public void JsonPackageKeyValueStore_QuarantinesCorruptJson()
    {
        var tempDirectory = CreateTempDirectory();
        var statePath = Path.Combine(tempDirectory, "state.json");
        File.WriteAllText(statePath, "not-json");
        var store = new JsonPackageKeyValueStore(statePath);

        var value = store.GetValue("missing");

        Assert.Null(value);
        Assert.False(File.Exists(statePath));
        Assert.Single(Directory.GetFiles(tempDirectory, "state.json.corrupt.*"));
    }

    [Fact]
    public void JsonPackageSecretsStore_QuarantinesCorruptJson()
    {
        var tempDirectory = CreateTempDirectory();
        var secretsPath = Path.Combine(tempDirectory, "secrets.json");
        File.WriteAllText(secretsPath, "not-json");
        var store = new JsonPackageSecretsStore(secretsPath);

        var value = store.GetSecret("missing");

        Assert.Null(value);
        Assert.False(File.Exists(secretsPath));
        Assert.Single(Directory.GetFiles(tempDirectory, "secrets.json.corrupt.*"));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
