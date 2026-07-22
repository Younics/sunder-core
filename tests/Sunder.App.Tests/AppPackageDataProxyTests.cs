using System.Net;
using Sunder.App.Services;
using Sunder.Runtime.Client;
using Sunder.Sdk.Storage;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPackageDataProxyTests
{
    [Fact]
    public async Task DataAndSettingsProxies_RejectInputsBeforeRuntimeTransport()
    {
        var requestCount = 0;
        using var handler = new DelegateHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var client = new RuntimePackageDataClient(
            () => new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5123/"), "test-token"),
            handler);
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        var state = new AppRuntimePackageStateStore("test.package", client, publication);
        var settings = new AppRuntimePackageSettings("test.package", client, publication);
        var secrets = new AppRuntimePackageSecrets("test.package", client, publication);
        var files = new AppRuntimePackageFileStore("test.package", client, publication);
        var oversizedValue = new string(
            '\u00E9',
            PackageStorageValidation.MaximumValueUtf8Bytes / 2 + 1);

        await Assert.ThrowsAsync<ArgumentException>(() => state.GetValueAsync("bad key"));
        await Assert.ThrowsAsync<ArgumentException>(() => state.SetValueAsync("valid", oversizedValue));
        await Assert.ThrowsAsync<ArgumentException>(() => state.ListKeysAsync("bad prefix"));
        await Assert.ThrowsAsync<ArgumentException>(() => settings.GetStoredValueAsync("caf\u00E9"));
        await Assert.ThrowsAsync<ArgumentException>(() => settings.SetValueAsync("valid", oversizedValue));
        await Assert.ThrowsAsync<ArgumentException>(() => secrets.GetSecretAsync("bad/key"));
        await Assert.ThrowsAsync<ArgumentException>(() => secrets.SetSecretAsync("valid", oversizedValue));
        await Assert.ThrowsAsync<ArgumentException>(() => files.ReadAsync("folder\\file.bin"));
        await Assert.ThrowsAsync<ArgumentException>(() => files.WriteAsync(
            "valid.bin",
            new byte[PackageStorageValidation.MaximumFileBytes + 1]));

        Assert.Equal(0, requestCount);
    }

    [Theory]
    [InlineData("key", true)]
    [InlineData("Feature.Enabled_1", true)]
    [InlineData("bad key", false)]
    [InlineData("caf\u00E9", false)]
    public void AppKeyGuard_MatchesTheSdkContract(string key, bool expected)
    {
        Assert.Equal(expected, PackageStorageValidation.IsValidKey(key));
        if (expected)
        {
            AppPackageStorageGuards.Key(key, "key");
        }
        else
        {
            Assert.Throws<ArgumentException>(() => AppPackageStorageGuards.Key(key, "key"));
        }
    }

    [Theory]
    [InlineData("folder/file.bin", true)]
    [InlineData("folder\\file.bin", false)]
    [InlineData("../file.bin", false)]
    [InlineData("CON", false)]
    public void AppPathGuard_MatchesTheSdkContract(string path, bool expected)
    {
        Assert.Equal(expected, PackageStorageValidation.IsValidRelativePath(path));
        if (expected)
        {
            AppPackageStorageGuards.RelativePath(path, "relativePath");
        }
        else
        {
            Assert.Throws<ArgumentException>(() => AppPackageStorageGuards.RelativePath(path, "relativePath"));
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(send(request));
        }
    }
}
