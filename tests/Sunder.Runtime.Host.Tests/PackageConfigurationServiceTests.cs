using Microsoft.Extensions.DependencyInjection;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageConfigurationServiceTests
{
    [Fact]
    public async Task GetConfigurationValuesAsync_ExcludesSecretValuesAndReportsStoredSecretKeys()
    {
        var loadedPackage = CreateLoadedPackage(CreateConfigurationSchema());
        await loadedPackage.StateStore.SetValueAsync("endpoint", "https://example.test");
        await loadedPackage.StateStore.SetValueAsync("apiKey", "state-secret");
        await loadedPackage.SecretsStore.SetSecretAsync("apiKey", "secret-value");
        var service = new PackageConfigurationService();

        var response = await service.GetConfigurationValuesAsync(loadedPackage);

        Assert.Equal("test.package", response.PackageId);
        Assert.Equal("https://example.test", response.Values["endpoint"]);
        Assert.DoesNotContain("apiKey", response.Values.Keys);
        Assert.Equal(["apiKey"], response.StoredSecretKeys);
    }

    [Fact]
    public async Task GetConfigurationValuesAsync_MigratesLegacyStateSecretBeforeDeletingStateCopy()
    {
        const string secret = "legacy-state-secret";
        var loadedPackage = CreateLoadedPackage(CreateConfigurationSchema());
        await loadedPackage.StateStore.SetValueAsync("apiKey", secret);
        var service = new PackageConfigurationService();

        var response = await service.GetConfigurationValuesAsync(loadedPackage);

        Assert.Null(await loadedPackage.StateStore.GetValueAsync("apiKey"));
        Assert.Equal(secret, await loadedPackage.SecretsStore.GetSecretAsync("apiKey"));
        Assert.Equal(["apiKey"], response.StoredSecretKeys);
        Assert.DoesNotContain(
            secret,
            File.ReadAllText(Path.Combine(loadedPackage.Source.Folder, "secrets.json")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetConfigurationValuesAsync_SecretWriteFailurePreservesLegacyStateAndRetries()
    {
        const string secret = "legacy-state-retry-secret";
        var fileSystem = new SecretDocumentFaultFileSystem();
        var loadedPackage = CreateLoadedPackage(CreateConfigurationSchema(), fileSystem);
        await loadedPackage.StateStore.SetValueAsync("apiKey", secret);
        var service = new PackageConfigurationService();

        var exception = await Assert.ThrowsAsync<IOException>(
            () => service.GetConfigurationValuesAsync(loadedPackage));

        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(secret, await loadedPackage.StateStore.GetValueAsync("apiKey"));
        Assert.False(File.Exists(Path.Combine(loadedPackage.Source.Folder, "secrets.json")));

        var response = await service.GetConfigurationValuesAsync(loadedPackage);

        Assert.Null(await loadedPackage.StateStore.GetValueAsync("apiKey"));
        Assert.Equal(secret, await loadedPackage.SecretsStore.GetSecretAsync("apiKey"));
        Assert.Equal(["apiKey"], response.StoredSecretKeys);
    }

    [Fact]
    public async Task SaveConfigurationValuesAsync_WritesAllowedValuesAndStoresSecretsSeparately()
    {
        var loadedPackage = CreateLoadedPackage(CreateConfigurationSchema());
        await loadedPackage.StateStore.SetValueAsync("endpoint", "https://old.example.test");
        await loadedPackage.StateStore.SetValueAsync("apiKey", "state-secret");
        await loadedPackage.SecretsStore.SetSecretAsync("apiKey", "old-secret");
        var service = new PackageConfigurationService();

        var saved = await service.SaveConfigurationValuesAsync(
            loadedPackage,
            new UpdatePackageConfigurationValuesRequest(new Dictionary<string, string?>
            {
                ["model"] = "gpt-test",
                ["apiKey"] = "new-secret",
                ["unknown"] = "ignored",
            }));

        Assert.True(saved);
        Assert.Null(await loadedPackage.StateStore.GetValueAsync("endpoint"));
        Assert.Equal("gpt-test", await loadedPackage.StateStore.GetValueAsync("model"));
        Assert.Null(await loadedPackage.StateStore.GetValueAsync("apiKey"));
        Assert.Null(await loadedPackage.StateStore.GetValueAsync("unknown"));
        Assert.Equal("new-secret", await loadedPackage.SecretsStore.GetSecretAsync("apiKey"));
    }

    [Fact]
    public async Task SaveConfigurationValuesAsync_ReturnsFalseWithoutSchema()
    {
        var loadedPackage = CreateLoadedPackage(configurationSchema: null);
        var service = new PackageConfigurationService();

        var saved = await service.SaveConfigurationValuesAsync(
            loadedPackage,
            new UpdatePackageConfigurationValuesRequest(new Dictionary<string, string?>
            {
                ["endpoint"] = "https://example.test",
            }));

        Assert.False(saved);
        Assert.Empty(await loadedPackage.StateStore.ListKeysAsync());
    }

    private static ActiveLoadedPackage CreateLoadedPackage(
        PackageConfigurationSchemaDescriptor? configurationSchema,
        AtomicFileSystem? secretsFileSystem = null)
    {
        var tempDirectory = CreateTempDirectory();
        var assemblyPath = typeof(PackageConfigurationService).Assembly.Location;

        return new ActiveLoadedPackage(
            new ActivePackageDescriptor("test.package", "Test Package", "1.0.0", Icon: null, IsEnabled: true, PackageReadinessState.Ready, Views: []),
            new RuntimePackageSource("test.package", PackageSourceKind.Dev, tempDirectory),
            configurationSchema,
            new JsonPackageKeyValueStore(Path.Combine(tempDirectory, "state.json")),
            new JsonPackageSecretsStore(
                Path.Combine(tempDirectory, "secrets.json"),
                secretsFileSystem,
                null,
                new RestrictedFileMasterKeyProtection()),
            AuthHandler: null,
            CallbackHandlers: new Dictionary<string, IPackageCallbackHandler>(StringComparer.OrdinalIgnoreCase),
            BackgroundServices: [],
            new ServiceCollection().BuildServiceProvider(),
            new RuntimePackageLoadContext(
                "test.package",
                assemblyPath,
                new RuntimeSharedAssemblyRegistry([Path.GetDirectoryName(assemblyPath)!])));
    }

    private static PackageConfigurationSchemaDescriptor CreateConfigurationSchema()
        => new(
            "test.package",
            "Test Package",
            Summary: null,
            Sections:
            [
                new PackageConfigurationSectionDescriptor(
                    "general",
                    "General",
                    Description: null,
                    Fields:
                    [
                        new PackageConfigurationFieldDescriptor(
                            "endpoint",
                            "Endpoint",
                            PackageConfigurationFieldKind.Text,
                            Description: null,
                            IsRequired: false,
                            Placeholder: null,
                            DefaultValue: null,
                            Options: []),
                        new PackageConfigurationFieldDescriptor(
                            "model",
                            "Model",
                            PackageConfigurationFieldKind.Text,
                            Description: null,
                            IsRequired: false,
                            Placeholder: null,
                            DefaultValue: null,
                            Options: []),
                        new PackageConfigurationFieldDescriptor(
                            "apiKey",
                            "API key",
                            PackageConfigurationFieldKind.Secret,
                            Description: null,
                            IsRequired: false,
                            Placeholder: null,
                            DefaultValue: null,
                            Options: []),
                    ])
            ]);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class SecretDocumentFaultFileSystem : AtomicFileSystem
    {
        private bool _faulted;

        internal override void BeforeCommit(StorageCommitPhase phase, string destinationPath)
        {
            if (!_faulted && phase == StorageCommitPhase.SecretDocument)
            {
                _faulted = true;
                throw new IOException("Injected secret document commit failure.");
            }
        }
    }
}
