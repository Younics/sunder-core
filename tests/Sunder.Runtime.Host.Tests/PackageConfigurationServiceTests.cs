using Microsoft.Extensions.DependencyInjection;
using Sunder.Protocol;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Storage;
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
        loadedPackage.SecretsStore.SetSecret("apiKey", "secret-value");
        var service = new PackageConfigurationService();

        var response = await service.GetConfigurationValuesAsync(loadedPackage);

        Assert.Equal("test.package", response.PackageId);
        Assert.Equal("https://example.test", response.Values["endpoint"]);
        Assert.DoesNotContain("apiKey", response.Values.Keys);
        Assert.Equal(["apiKey"], response.StoredSecretKeys);
    }

    [Fact]
    public async Task SaveConfigurationValuesAsync_WritesAllowedValuesAndStoresSecretsSeparately()
    {
        var loadedPackage = CreateLoadedPackage(CreateConfigurationSchema());
        await loadedPackage.StateStore.SetValueAsync("endpoint", "https://old.example.test");
        await loadedPackage.StateStore.SetValueAsync("apiKey", "state-secret");
        loadedPackage.SecretsStore.SetSecret("apiKey", "old-secret");
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
        Assert.Equal("new-secret", loadedPackage.SecretsStore.GetSecret("apiKey"));
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

    private static ActiveLoadedPackage CreateLoadedPackage(PackageConfigurationSchemaDescriptor? configurationSchema)
    {
        var tempDirectory = CreateTempDirectory();
        var assemblyPath = typeof(PackageConfigurationService).Assembly.Location;

        return new ActiveLoadedPackage(
            new ActivePackageDescriptor("test.package", "Test Package", "1.0.0", Icon: null, IsEnabled: true, PackageReadinessState.Ready, Views: []),
            new PackageSourceDescriptor("test.package", PackageSourceKind.Dev, tempDirectory),
            configurationSchema,
            new JsonPackageKeyValueStore(Path.Combine(tempDirectory, "state.json")),
            new JsonPackageSecretsStore(Path.Combine(tempDirectory, "secrets.json")),
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
}
