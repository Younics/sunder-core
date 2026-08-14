using System.Text.Json;
using Sunder.App.Services;
using Sunder.Runtime.Client;
using Xunit;

namespace Sunder.App.Tests;

public sealed class LocalStateV1Tests
{
    [Fact]
    public void Fresh_runtime_and_app_roots_initialize_schema_one_atomically()
    {
        var root = CreateTempDirectory();
        var runtimeRoot = Path.Combine(root, "runtime", "v1");
        var appRoot = Path.Combine(root, "app", "v1");

        RuntimeLocalState.EnsureInitialized(runtimeRoot);
        AppLocalState.EnsureInitialized(appRoot);

        AssertSchema(Path.Combine(runtimeRoot, "schema.json"), "sunder.runtime.local-state");
        AssertSchema(Path.Combine(appRoot, "schema.json"), "sunder.app.local-state");
        Assert.Empty(Directory.EnumerateFiles(runtimeRoot, "*.tmp"));
        Assert.Empty(Directory.EnumerateFiles(appRoot, "*.tmp"));
    }

    [Fact]
    public void Nonempty_roots_without_compatible_metadata_are_rejected_without_touching_legacy_data()
    {
        var root = CreateTempDirectory();
        var runtimeRoot = Path.Combine(root, "runtime", "v1");
        var appRoot = Path.Combine(root, "app", "v1");
        Directory.CreateDirectory(runtimeRoot);
        Directory.CreateDirectory(appRoot);
        File.WriteAllText(Path.Combine(runtimeRoot, "legacy.json"), "legacy-runtime");
        File.WriteAllText(Path.Combine(appRoot, "legacy.json"), "legacy-app");

        Assert.Throws<InvalidDataException>(() => RuntimeLocalState.EnsureInitialized(runtimeRoot));
        Assert.Throws<InvalidDataException>(() => AppLocalState.EnsureInitialized(appRoot));
        Assert.Equal("legacy-runtime", File.ReadAllText(Path.Combine(runtimeRoot, "legacy.json")));
        Assert.Equal("legacy-app", File.ReadAllText(Path.Combine(appRoot, "legacy.json")));
    }

    [Fact]
    public async Task Incompatible_metadata_blocks_startup_and_reset_without_deletion()
    {
        var runtimeRoot = Path.Combine(CreateTempDirectory(), "runtime", "v1");
        RuntimeLocalState.EnsureInitialized(runtimeRoot);
        var schemaPath = Path.Combine(runtimeRoot, "schema.json");
        File.WriteAllText(schemaPath, """
            {"product":"Sunder","api":"sunder.runtime.local-state","schemaVersion":2}
            """);
        var markerPath = Path.Combine(runtimeRoot, "marker.txt");
        File.WriteAllText(markerPath, "keep");

        Assert.Throws<InvalidDataException>(() => RuntimeLocalState.EnsureInitialized(runtimeRoot));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => RuntimeV1StateReset.ResetAsync(TimeSpan.Zero, runtimeRoot));
        Assert.Equal("keep", File.ReadAllText(markerPath));
    }

    [Fact]
    public async Task Reset_deletes_only_fixed_v1_paths_and_is_idempotent()
    {
        var root = CreateTempDirectory();
        var runtimeRoot = Path.Combine(root, "runtime", "v1");
        var legacyRoot = Path.Combine(root, "runtime", "packages");
        RuntimeLocalState.EnsureInitialized(runtimeRoot);
        Directory.CreateDirectory(Path.Combine(runtimeRoot, "catalog"));
        File.WriteAllText(Path.Combine(runtimeRoot, "catalog", "installed-packages.json"), "state");
        File.WriteAllText(Path.Combine(runtimeRoot, "stale-internal.tmp"), "stale");
        var packageFiles = Path.Combine(runtimeRoot, "package-data", "demo", "files");
        Directory.CreateDirectory(packageFiles);
        File.WriteAllText(Path.Combine(packageFiles, "ordinary.key"), "package-owned-data");
        Directory.CreateDirectory(legacyRoot);
        File.WriteAllText(Path.Combine(legacyRoot, "installed-packages.json"), "legacy");

        var first = await RuntimeV1StateReset.ResetAsync(TimeSpan.Zero, runtimeRoot);
        var second = await RuntimeV1StateReset.ResetAsync(TimeSpan.Zero, runtimeRoot);

        Assert.False(first.Success);
        Assert.False(second.Success);
        Assert.True(Directory.Exists(runtimeRoot));
        Assert.Equal("stale", File.ReadAllText(Path.Combine(runtimeRoot, "stale-internal.tmp")));
        Assert.Equal("partial", second.Categories.Single(category => category.Category == "runtime-v1-root").Status);
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(legacyRoot, "installed-packages.json")));
    }

    [Fact]
    public async Task Reset_rejects_locked_runtime_root()
    {
        var runtimeRoot = Path.Combine(CreateTempDirectory(), "runtime", "v1");
        RuntimeLocalState.EnsureInitialized(runtimeRoot);
        await using var lease = new FileStream(
            Path.Combine(runtimeRoot, RuntimeLocalState.LeaseFileName),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RuntimeV1StateReset.ResetAsync(TimeSpan.Zero, runtimeRoot));
        Assert.True(File.Exists(Path.Combine(runtimeRoot, "schema.json")));
    }

    [Fact]
    public async Task Partial_credential_cleanup_is_safe_and_retryable()
    {
        var runtimeRoot = Path.Combine(CreateTempDirectory(), "runtime", "v1");
        RuntimeLocalState.EnsureInitialized(runtimeRoot);
        var credentials = Path.Combine(runtimeRoot, "credentials");
        Directory.CreateDirectory(credentials);
        var registryCredentials = Path.Combine(credentials, "registry");
        Directory.CreateDirectory(registryCredentials);
        var keyPath = Path.Combine(registryCredentials, "credentials.enc.json.key");
        File.WriteAllText(keyPath, "not-json");

        var first = await RuntimeV1StateReset.ResetAsync(TimeSpan.Zero, runtimeRoot);
        File.Delete(keyPath);
        var retry = await RuntimeV1StateReset.ResetAsync(TimeSpan.Zero, runtimeRoot);

        Assert.False(first.Success);
        Assert.Equal("partial", Assert.Single(first.Categories, item => item.Category == "registry-credentials").Status);
        Assert.True(retry.Success);
    }

    private static void AssertSchema(string path, string api)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("Sunder", document.RootElement.GetProperty("product").GetString());
        Assert.Equal(api, document.RootElement.GetProperty("api").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "SunderLocalStateV1Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
