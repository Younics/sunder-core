using Microsoft.Extensions.DependencyInjection;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageSettingsServiceTests
{
    [Fact]
    public async Task GetValuesAsync_ReturnsOnlyStoredSchemaSettingsAndSecretPresence()
    {
        var settings = new TestPackageSettings(new Dictionary<string, string> { ["endpoint"] = "https://example.test" });
        var package = CreateLoadedPackage(CreateSchema(), settings);
        await package.StateStore.SetValueAsync("operational", "opaque-state");
        await package.SecretsStore.SetSecretAsync("apiKey", "secret-value");

        var response = await new PackageSettingsService().GetValuesAsync(package);

        Assert.Equal("https://example.test", response.StoredValues["endpoint"]);
        Assert.DoesNotContain("operational", response.StoredValues.Keys);
        Assert.Equal(["apiKey"], response.StoredSecretKeys);
    }

    [Fact]
    public async Task SaveValuesAsync_WritesSettingsAndSecretsWithoutChangingState()
    {
        var settings = new TestPackageSettings(new Dictionary<string, string> { ["endpoint"] = "old" });
        var package = CreateLoadedPackage(CreateSchema(), settings);
        await package.StateStore.SetValueAsync("operational", "preserved");

        await new PackageSettingsService().SaveValuesAsync(
            package,
            new UpdatePackageSettingsRequest(new Dictionary<string, string?>
            {
                ["model"] = "small",
                ["apiKey"] = "new-secret",
            }));

        Assert.Null(await settings.GetStoredValueAsync("endpoint"));
        Assert.Equal("small", await settings.GetStoredValueAsync("model"));
        Assert.Equal("new-secret", await package.SecretsStore.GetSecretAsync("apiKey"));
        Assert.Equal("preserved", await package.StateStore.GetValueAsync("operational"));
    }

    [Theory]
    [InlineData("unknown", "value")]
    [InlineData("enabled", "sometimes")]
    [InlineData("model", "undeclared")]
    public async Task SaveValuesAsync_RejectsUnknownOrInvalidValues(string key, string value)
    {
        var package = CreateLoadedPackage(CreateSchema(), new TestPackageSettings());

        await Assert.ThrowsAsync<RuntimeValidationException>(() =>
            new PackageSettingsService().SaveValuesAsync(
                package,
                new UpdatePackageSettingsRequest(new Dictionary<string, string?> { [key] = value })));

        Assert.Empty(await package.StateStore.ListKeysAsync());
    }

    [Fact]
    public async Task PackageSettings_DistinguishesStoredValueFromSchemaDefaultAndUsesSettingsFile()
    {
        var root = CreateTempDirectory();
        var settingsPath = Path.Combine(root, "settings.json");
        var settings = new PackageSettings(new JsonPackageKeyValueStore(settingsPath))
        {
            Schema = CreateSdkSchema(),
        };

        Assert.Null(await settings.GetStoredValueAsync("endpoint"));
        Assert.Equal("https://default.test", await settings.GetValueAsync("endpoint"));

        await settings.SetValueAsync("endpoint", "https://stored.test");

        Assert.Equal("https://stored.test", await settings.GetStoredValueAsync("endpoint"));
        Assert.True(File.Exists(settingsPath));
        Assert.False(File.Exists(Path.Combine(root, "state.json")));
        await Assert.ThrowsAsync<ArgumentException>(() => settings.SetValueAsync("apiKey", "secret"));
    }

    private static ActiveLoadedPackage CreateLoadedPackage(
        PackageConfigurationSchemaDescriptor? schema,
        IPackageSettings settings)
    {
        var root = CreateTempDirectory();
        var assemblyPath = typeof(PackageSettingsService).Assembly.Location;
        return new ActiveLoadedPackage(
            new ActivePackageDescriptor("test.package", "Test Package", "1.0.0", null, true, PackageReadinessState.Ready, []),
            new RuntimePackageSource("test.package", PackageSourceKind.Dev, root),
            schema,
            new JsonPackageKeyValueStore(Path.Combine(root, "state.json")),
            new JsonPackageSecretsStore(Path.Combine(root, "secrets.json")),
            null,
            new Dictionary<string, IPackageCallbackHandler>(StringComparer.OrdinalIgnoreCase),
            [],
            new ServiceCollection().BuildServiceProvider(),
            new RuntimePackageLoadContext(
                "test.package",
                assemblyPath,
                new RuntimeSharedAssemblyRegistry([Path.GetDirectoryName(assemblyPath)!])),
            settings);
    }

    private static PackageConfigurationSchemaDescriptor CreateSchema()
        => new(
            "test.package",
            "Test Package",
            null,
            [new PackageConfigurationSectionDescriptor(
                "general",
                "General",
                null,
                [
                    Field("endpoint", PackageConfigurationFieldKind.Text, defaultValue: "https://default.test"),
                    Field("enabled", PackageConfigurationFieldKind.Boolean),
                    Field("model", PackageConfigurationFieldKind.Select, options: [new("small", "Small")]),
                    Field("apiKey", PackageConfigurationFieldKind.Secret),
                ])]);

    private static PackageConfigurationFieldDescriptor Field(
        string key,
        PackageConfigurationFieldKind kind,
        string? defaultValue = null,
        IReadOnlyList<PackageConfigurationOptionDescriptor>? options = null)
        => new(key, key, kind, null, false, null, defaultValue, options ?? []);

    private static Sunder.Sdk.Configuration.PackageConfigurationSchema CreateSdkSchema()
        => new(
            "test.package",
            "Test Package",
            null,
            [new Sunder.Sdk.Configuration.PackageConfigurationSection(
                "general",
                "General",
                null,
                [
                    new Sunder.Sdk.Configuration.PackageConfigurationField("endpoint", "Endpoint", Sunder.Sdk.Configuration.PackageConfigurationFieldKind.Text, DefaultValue: "https://default.test"),
                    new Sunder.Sdk.Configuration.PackageConfigurationField("apiKey", "API key", Sunder.Sdk.Configuration.PackageConfigurationFieldKind.Secret),
                ])]);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class TestPackageSettings(IReadOnlyDictionary<string, string>? initial = null) : IPackageSettings
    {
        private readonly Dictionary<string, string> _values = new(initial ?? new Dictionary<string, string>(), StringComparer.Ordinal);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
            => GetStoredValueAsync(key, cancellationToken);

        public Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
