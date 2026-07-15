using Microsoft.Extensions.DependencyInjection;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Abstractions;
using Xunit;
using SdkSettingsFieldKind = Sunder.Sdk.Settings.PackageSettingsFieldKind;

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

    [Fact]
    public async Task PerKeyOperations_RouteSecretSchemaFieldsToSecretStorage()
    {
        var settings = new TestPackageSettings();
        var package = CreateLoadedPackage(CreateSchema(), settings);
        var service = new PackageSettingsService();

        await service.SetValueAsync(package, "apiKey", "secret-value");

        Assert.Equal("secret-value", await package.SecretsStore.GetSecretAsync("apiKey"));
        Assert.Null(await settings.GetStoredValueAsync("apiKey"));
        var response = await service.GetValueAsync(package, "apiKey");
        Assert.True(response.IsStored);
        Assert.Null(response.StoredValue);
        Assert.Null(response.EffectiveValue);

        await service.DeleteValueAsync(package, "apiKey");
        Assert.Null(await package.SecretsStore.GetSecretAsync("apiKey"));
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

    [Fact]
    public void SettingsSchemaProjection_UsesHostOwnedPackageMetadata()
    {
        var projected = PackageProtocolMapper.ToProtocolSettingsSchema(
            "host.package",
            "Host Package",
            CreateSdkSchema());

        Assert.NotNull(projected);
        Assert.Equal("host.package", projected.PackageId);
        Assert.Equal("Host Package", projected.PackageDisplayName);
    }

    [Fact]
    public void RuntimeContributionRegistry_RejectsMultipleSettingsSchemas()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var registry = new RuntimePackageContributionRegistry(
            services,
            new RuntimePackageExtensionCatalog(),
            "test.package");
        registry.RegisterSettingsSchema(CreateSdkSchema());

        var exception = Assert.Throws<InvalidOperationException>(
            () => registry.RegisterSettingsSchema(CreateSdkSchema()));

        Assert.Contains("more than one settings schema", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalSettingsField_RejectsInvalidKindAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Sunder.Sdk.Settings.PackageSettingsField(
            "invalid",
            "Invalid",
            (SdkSettingsFieldKind)int.MaxValue));
    }

    [Fact]
    public void CanonicalSettingsSchema_RejectsDuplicateIdsSecretDefaultsAndInvalidFields()
    {
        var text = new Sunder.Sdk.Settings.PackageSettingsField(
            "duplicate",
            "Text",
            SdkSettingsFieldKind.Text);
        Assert.Throws<ArgumentException>(() => new Sunder.Sdk.Settings.PackageSettingsSchema(
            null,
            [
                new Sunder.Sdk.Settings.PackageSettingsSection("first", "First", null, [text]),
                new Sunder.Sdk.Settings.PackageSettingsSection("second", "Second", null, [text]),
            ]));
        Assert.Throws<ArgumentException>(() => new Sunder.Sdk.Settings.PackageSettingsField(
            "secret",
            "Secret",
            SdkSettingsFieldKind.Secret,
            defaultValue: "must-not-persist"));
        Assert.Throws<ArgumentException>(() => new Sunder.Sdk.Settings.PackageSettingsField(
            "boolean",
            "Boolean",
            SdkSettingsFieldKind.Boolean,
            defaultValue: "sometimes"));
        Assert.Throws<ArgumentException>(() => new Sunder.Sdk.Settings.PackageSettingsField(
            "select",
            "Select",
            SdkSettingsFieldKind.Select,
            options: []));
    }

    [Fact]
    public async Task BulkSettings_PatchPreservesOmittedValuesWhileReplaceRemovesThem()
    {
        var settings = new TestPackageSettings(new Dictionary<string, string>
        {
            ["endpoint"] = "existing",
            ["model"] = "small",
        });
        var package = CreateLoadedPackage(CreateSchema(), settings);
        var service = new PackageSettingsService();

        await service.SaveValuesAsync(
            package,
            new UpdatePackageSettingsRequest(
                new Dictionary<string, string?> { ["enabled"] = "true" },
                PackageSettingsUpdateMode.Patch));

        Assert.Equal("existing", await settings.GetStoredValueAsync("endpoint"));
        Assert.Equal("small", await settings.GetStoredValueAsync("model"));

        await service.SaveValuesAsync(
            package,
            new UpdatePackageSettingsRequest(
                new Dictionary<string, string?> { ["enabled"] = "false" },
                PackageSettingsUpdateMode.Replace));

        Assert.Null(await settings.GetStoredValueAsync("endpoint"));
        Assert.Null(await settings.GetStoredValueAsync("model"));
        Assert.Equal("false", await settings.GetStoredValueAsync("enabled"));
    }

    [Fact]
    public async Task RequiredSecret_MustExistForReplaceAndCannotBeExplicitlyDeleted()
    {
        var package = CreateLoadedPackage(
            CreateSchema(requiredSecret: true),
            new TestPackageSettings(),
            requiredSecret: true);
        var service = new PackageSettingsService();

        await Assert.ThrowsAsync<RuntimeValidationException>(() => service.SaveValuesAsync(
            package,
            new UpdatePackageSettingsRequest(new Dictionary<string, string?>())));

        await package.SecretsStore.SetSecretAsync("apiKey", "existing-secret");
        await service.SaveValuesAsync(
            package,
            new UpdatePackageSettingsRequest(new Dictionary<string, string?>()));
        Assert.Equal("existing-secret", await package.SecretsStore.GetSecretAsync("apiKey"));

        await Assert.ThrowsAsync<RuntimeValidationException>(() => service.SaveValuesAsync(
            package,
            new UpdatePackageSettingsRequest(
                new Dictionary<string, string?> { ["apiKey"] = null },
                PackageSettingsUpdateMode.Patch)));
        await Assert.ThrowsAsync<RuntimeValidationException>(() =>
            service.DeleteValueAsync(package, "apiKey"));
        Assert.Equal("existing-secret", await package.SecretsStore.GetSecretAsync("apiKey"));
    }

    [Fact]
    public async Task BulkSettings_SecretCommitFailureRestoresPreviousSettingsDocument()
    {
        var root = CreateTempDirectory();
        var settings = new TestPackageSettings(new Dictionary<string, string> { ["endpoint"] = "old" });
        var secrets = new JsonPackageSecretsStore(
            Path.Combine(root, "secrets.json"),
            new SecretCommitFaultFileSystem(),
            null,
            new RestrictedFileMasterKeyProtection());
        var package = CreateLoadedPackage(CreateSchema(), settings, secretsStore: secrets);

        await Assert.ThrowsAsync<IOException>(() => new PackageSettingsService().SaveValuesAsync(
            package,
            new UpdatePackageSettingsRequest(new Dictionary<string, string?>
            {
                ["endpoint"] = "new",
                ["apiKey"] = "secret",
            })));

        Assert.Equal("old", await settings.GetStoredValueAsync("endpoint"));
        Assert.Null(await secrets.GetSecretAsync("apiKey"));
    }

    private static ActiveLoadedPackage CreateLoadedPackage(
        PackageSettingsSchemaDescriptor? schema,
        IPackageSettings settings,
        bool requiredSecret = false,
        JsonPackageSecretsStore? secretsStore = null)
    {
        var root = CreateTempDirectory();
        var assemblyPath = typeof(PackageSettingsService).Assembly.Location;
        return new ActiveLoadedPackage(
            new ActivePackageDescriptor("test.package", "Test Package", "1.0.0", PackageHostRoles.Runtime, null, true, PackageReadinessState.Ready, []),
            new RuntimePackageSource("test.package", PackageSourceKind.Dev, root),
            schema,
            new JsonPackageKeyValueStore(Path.Combine(root, "state.json")),
            secretsStore ?? new JsonPackageSecretsStore(Path.Combine(root, "secrets.json")),
            null,
            new Dictionary<string, IPackageCallbackHandler>(StringComparer.OrdinalIgnoreCase),
            [],
            new ServiceCollection().BuildServiceProvider(),
            new RuntimePackageLoadContext(
                "test.package",
                assemblyPath,
                new RuntimeSharedAssemblyRegistry([Path.GetDirectoryName(assemblyPath)!])),
            settings)
        {
            CanonicalSettingsSchema = CreateCanonicalSchema(requiredSecret),
        };
    }

    private static PackageSettingsSchemaDescriptor CreateSchema(bool requiredSecret = false)
        => new(
            "test.package",
            "Test Package",
            null,
            [new PackageSettingsSectionDescriptor(
                "general",
                "General",
                null,
                [
                    Field("endpoint", PackageSettingsFieldKind.Text, defaultValue: "https://default.test"),
                    Field("enabled", PackageSettingsFieldKind.Boolean),
                    Field("model", PackageSettingsFieldKind.Select, options: [new("small", "Small")]),
                    Field("apiKey", PackageSettingsFieldKind.Secret, isRequired: requiredSecret),
                ])]);

    private static PackageSettingsFieldDescriptor Field(
        string key,
        PackageSettingsFieldKind kind,
        string? defaultValue = null,
        bool isRequired = false,
        IReadOnlyList<PackageSettingsOptionDescriptor>? options = null)
        => new(key, key, kind, null, isRequired, null, defaultValue, options ?? []);

    private static Sunder.Sdk.Settings.PackageSettingsSchema CreateCanonicalSchema(bool requiredSecret = false)
        => new(
            null,
            [new Sunder.Sdk.Settings.PackageSettingsSection(
                "general",
                "General",
                null,
                [
                    new Sunder.Sdk.Settings.PackageSettingsField("endpoint", "Endpoint", SdkSettingsFieldKind.Text, defaultValue: "https://default.test"),
                    new Sunder.Sdk.Settings.PackageSettingsField("enabled", "Enabled", SdkSettingsFieldKind.Boolean),
                    new Sunder.Sdk.Settings.PackageSettingsField(
                        "model",
                        "Model",
                        SdkSettingsFieldKind.Select,
                        options: [new Sunder.Sdk.Settings.PackageSettingsOption("small", "Small")]),
                    new Sunder.Sdk.Settings.PackageSettingsField("apiKey", "API key", SdkSettingsFieldKind.Secret, isRequired: requiredSecret),
                ])]);

    private static Sunder.Sdk.Settings.PackageSettingsSchema CreateSdkSchema()
        => new(
            null,
            [new Sunder.Sdk.Settings.PackageSettingsSection(
                "general",
                "General",
                null,
                [
                    new Sunder.Sdk.Settings.PackageSettingsField("endpoint", "Endpoint", SdkSettingsFieldKind.Text, defaultValue: "https://default.test"),
                    new Sunder.Sdk.Settings.PackageSettingsField("apiKey", "API key", SdkSettingsFieldKind.Secret),
                ])]);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class TestPackageSettings(IReadOnlyDictionary<string, string>? initial = null) : IPackageSettingsDocument
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

        public Task ReplaceValuesAsync(
            IReadOnlyDictionary<string, string> values,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.Clear();
            foreach (var pair in values)
            {
                _values[pair.Key] = pair.Value;
            }
            return Task.CompletedTask;
        }
    }

    private sealed class SecretCommitFaultFileSystem : AtomicFileSystem
    {
        internal override void BeforeCommit(StorageCommitPhase phase, string destinationPath)
        {
            if (phase == StorageCommitPhase.SecretDocument)
            {
                throw new IOException("Injected secret document commit failure.");
            }
        }
    }
}
