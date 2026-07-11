using Sunder.Runtime.Client;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RuntimeConnectionInfoStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsConnectionUsingPrivateStorage()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-connection-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(rootPath, "runtime", "connection-v1.json");
        var connection = new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5275/"), RuntimeBearerToken.Create());
        try
        {
            RuntimeConnectionInfoStore.Save(connection, path);

            var loaded = RuntimeConnectionInfoStore.Load(path);

            Assert.NotNull(loaded);
            Assert.Equal(connection.RuntimeUrl, loaded.RuntimeUrl);
            Assert.True(System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(connection.BearerToken),
                System.Text.Encoding.UTF8.GetBytes(loaded.BearerToken)));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(Path.GetDirectoryName(path)!));
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(path));
            }
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public void LoadFor_WhenUrlDoesNotMatch_FailsClosed()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-connection-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(rootPath, "connection-v1.json");
        try
        {
            RuntimeConnectionInfoStore.Save(
                new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5275/"), RuntimeBearerToken.Create()),
                path);

            Assert.Throws<InvalidOperationException>(
                () => RuntimeConnectionInfoStore.LoadFor(new Uri("http://127.0.0.1:5276/"), path));
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public void Load_WhenUnixFileIsAccessibleByOtherUsers_FailsClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-connection-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(rootPath, "connection-v1.json");
        try
        {
            RuntimeConnectionInfoStore.Save(
                new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5275/"), RuntimeBearerToken.Create()),
                path);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

            Assert.Null(RuntimeConnectionInfoStore.Load(path));
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }
}
