using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.App.Services;
using Sunder.Package.Projections;
using Sunder.Runtime.Client;
using Xunit;

namespace Sunder.App.Tests;

public sealed class LocalStateV1Tests
{
    [Fact]
    public void AppStateRoot_UsesExplicitOverride()
    {
        var configuredRoot = Path.Combine(Path.GetTempPath(), "sunder-app-state-override");

        Assert.Equal(Path.GetFullPath(configuredRoot), AppLocalState.ResolveRootPath(configuredRoot));
    }

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
        var markerPath = Path.Combine(runtimeRoot, "catalog", "installed-packages.json");
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        File.WriteAllText(markerPath, "keep");

        Assert.Throws<InvalidDataException>(() => RuntimeLocalState.EnsureInitialized(runtimeRoot));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => RuntimeV1StateReset.ResetAsync(TimeSpan.Zero, runtimeRoot));
        Assert.Equal("keep", File.ReadAllText(markerPath));
    }

    [Fact]
    public async Task Reset_deletes_only_derived_v1_paths_preserves_user_state_and_is_idempotent()
    {
        var root = CreateTempDirectory();
        var runtimeRoot = Path.Combine(root, "runtime", "v1");
        var legacyRoot = Path.Combine(root, "runtime", "packages");
        const string stageId = "11111111111111111111111111111111";
        const string orphanId = "22222222222222222222222222222222";
        const string transferId = "33333333333333333333333333333333";
        RuntimeLocalState.EnsureInitialized(runtimeRoot);
        Directory.CreateDirectory(Path.Combine(runtimeRoot, "catalog"));
        File.WriteAllText(Path.Combine(runtimeRoot, "catalog", "installed-packages.json"), "state");
        File.WriteAllText(Path.Combine(runtimeRoot, "catalog", "rpc-permissions.json"), "capability-decisions");
        Directory.CreateDirectory(Path.Combine(runtimeRoot, "installed", "demo", "1.0.0"));
        File.WriteAllText(Path.Combine(runtimeRoot, "installed", "demo", "1.0.0", "payload"), "installed");
        var staging = Path.Combine(runtimeRoot, "staging", stageId);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "derived"), "derived");
        var transaction = Path.Combine(runtimeRoot, "transactions", stageId);
        Directory.CreateDirectory(Path.Combine(transaction, "backups", "0000"));
        File.WriteAllText(Path.Combine(transaction, "journal.json"), "journal");
        File.WriteAllText(Path.Combine(transaction, "backups", "0000", "payload"), "backup");
        var tombstone = Path.Combine(runtimeRoot, "tombstones", $"{stageId}-0000");
        Directory.CreateDirectory(tombstone);
        File.WriteAllText(Path.Combine(tombstone, "payload"), "tombstone");
        var orphan = Path.Combine(runtimeRoot, "tombstones", $"orphan-{orphanId}");
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "payload"), "orphan");
        var transfer = Path.Combine(runtimeRoot, "transfers", $"{transferId}.sunderpkg");
        Directory.CreateDirectory(Path.GetDirectoryName(transfer)!);
        File.WriteAllText(transfer, "transfer");
        var cacheRevision = Path.Combine(runtimeRoot, "cache", "package-ui-snapshots", "r2");
        Directory.CreateDirectory(Path.Combine(cacheRevision, "objects"));
        File.WriteAllText(Path.Combine(cacheRevision, "objects", "snapshot"), "cache");
        var packageFiles = Path.Combine(runtimeRoot, "package-data", "demo", "files");
        Directory.CreateDirectory(packageFiles);
        File.WriteAllText(Path.Combine(packageFiles, "ordinary.key"), "package-owned-data");
        var packageData = Path.Combine(runtimeRoot, "package-data", "demo", "data");
        Directory.CreateDirectory(packageData);
        File.WriteAllText(Path.Combine(packageData, "settings.json"), "settings");
        File.WriteAllText(Path.Combine(packageData, "secrets.json"), "secrets");
        var packageWorkspace = Path.Combine(runtimeRoot, "package-data", "demo", "workspace");
        Directory.CreateDirectory(packageWorkspace);
        File.WriteAllText(Path.Combine(packageWorkspace, "workspace.db"), "workspace");
        var packageLogs = Path.Combine(runtimeRoot, "package-data", "demo", "logs");
        Directory.CreateDirectory(packageLogs);
        File.WriteAllText(Path.Combine(packageLogs, "runtime.log"), "evidence");
        var credentials = Path.Combine(runtimeRoot, "credentials", "registry");
        Directory.CreateDirectory(credentials);
        File.WriteAllText(Path.Combine(credentials, "credentials.enc.json"), "credentials");
        File.WriteAllText(Path.Combine(runtimeRoot, "connection.json"), "connection");
        Directory.CreateDirectory(legacyRoot);
        File.WriteAllText(Path.Combine(legacyRoot, "installed-packages.json"), "legacy");

        var first = await RuntimeV1StateReset.ResetAsync(TimeSpan.Zero, runtimeRoot);
        var second = await RuntimeV1StateReset.ResetAsync(TimeSpan.Zero, runtimeRoot);

        Assert.True(first.Success);
        Assert.True(second.Success);
        var resetPaths = first.Categories.SelectMany(category => category.RelativePaths ?? []).ToArray();
        Assert.Contains("installed/demo/1.0.0", resetPaths);
        Assert.Contains($"staging/{stageId}", resetPaths);
        Assert.Contains($"transactions/{stageId}", resetPaths);
        Assert.Contains("cache/package-ui-snapshots/r2", resetPaths);
        Assert.DoesNotContain("installed", resetPaths);
        Assert.DoesNotContain("transactions", resetPaths);
        Assert.True(Directory.Exists(runtimeRoot));
        Assert.False(File.Exists(Path.Combine(runtimeRoot, "catalog", "installed-packages.json")));
        Assert.False(Directory.Exists(Path.Combine(runtimeRoot, "installed", "demo", "1.0.0")));
        Assert.False(Directory.Exists(staging));
        Assert.False(File.Exists(Path.Combine(transaction, "journal.json")));
        Assert.False(Directory.Exists(Path.Combine(transaction, "backups")));
        Assert.False(Directory.Exists(tombstone));
        Assert.False(Directory.Exists(orphan));
        Assert.False(File.Exists(transfer));
        Assert.False(Directory.Exists(cacheRevision));
        Assert.False(File.Exists(Path.Combine(runtimeRoot, "connection.json")));
        Assert.Equal("capability-decisions", File.ReadAllText(Path.Combine(runtimeRoot, "catalog", "rpc-permissions.json")));
        Assert.Equal("package-owned-data", File.ReadAllText(Path.Combine(packageFiles, "ordinary.key")));
        Assert.Equal("settings", File.ReadAllText(Path.Combine(packageData, "settings.json")));
        Assert.Equal("secrets", File.ReadAllText(Path.Combine(packageData, "secrets.json")));
        Assert.Equal("workspace", File.ReadAllText(Path.Combine(packageWorkspace, "workspace.db")));
        Assert.Equal("evidence", File.ReadAllText(Path.Combine(packageLogs, "runtime.log")));
        Assert.Equal("credentials", File.ReadAllText(Path.Combine(credentials, "credentials.enc.json")));
        Assert.NotNull(first.ReceiptRelativePath);
        Assert.NotNull(second.ReceiptRelativePath);
        Assert.True(File.Exists(Path.Combine(runtimeRoot, first.ReceiptRelativePath!)));
        Assert.True(File.Exists(Path.Combine(runtimeRoot, second.ReceiptRelativePath!)));
        AssertCompletedReceipt(runtimeRoot, first.ReceiptRelativePath!);
        AssertCompletedReceipt(runtimeRoot, second.ReceiptRelativePath!);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(runtimeRoot, "reset-receipts"), "*.tmp"));
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(legacyRoot, "installed-packages.json")));
    }

    [Fact]
    public async Task Startup_cache_reset_deletes_only_reviewed_cache_revisions()
    {
        var runtimeRoot = Path.Combine(CreateTempDirectory(), "runtime", "v1");
        RuntimeLocalState.EnsureInitialized(runtimeRoot);
        var installed = Path.Combine(runtimeRoot, "installed", "demo", "1.0.0", "payload");
        var packageData = Path.Combine(runtimeRoot, "package-data", "demo", "data", "settings.json");
        var executionRuntime = Path.Combine(runtimeRoot, "execution-runtimes", "roots", "sha256", "keep");
        var executionRuntimeHealth = Path.Combine(runtimeRoot, "execution-runtimes", "health", "runtime.json");
        var credential = Path.Combine(runtimeRoot, "credentials", "registry", "credentials.enc.json");
        var transfer = Path.Combine(runtimeRoot, "transfers", $"{Guid.NewGuid():N}.sunderpkg");
        foreach (var path in new[] { installed, packageData, executionRuntime, executionRuntimeHealth, credential, transfer })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "keep");
        }
        Directory.CreateDirectory(Path.Combine(runtimeRoot, "catalog"));
        File.WriteAllText(Path.Combine(runtimeRoot, "catalog", "installed-packages.json"), "keep");
        File.WriteAllText(Path.Combine(runtimeRoot, "connection.json"), "keep");
        var snapshotRevision = Path.Combine(runtimeRoot, "cache", "package-ui-snapshots", "r2");
        Directory.CreateDirectory(Path.Combine(snapshotRevision, "objects"));
        File.WriteAllText(Path.Combine(snapshotRevision, "objects", "snapshot"), "cache");
        var sourceRoot = Path.Combine(CreateTempDirectory(), "projection-source");
        Directory.CreateDirectory(sourceRoot);
        var sourceFile = Path.Combine(sourceRoot, "app.bin");
        File.WriteAllText(sourceFile, "app");
        var sourceBytes = await File.ReadAllBytesAsync(sourceFile);
        var projectionCache = new PackageProjectionCache(runtimeRoot);
        await using (var projection = await projectionCache.GetOrBuildAndLeaseAsync(
                         new PackageProjectionRequest(
                             sourceRoot,
                             "demo",
                             Convert.ToHexStringLower(SHA256.HashData("source"u8)),
                             PackageProjectionRole.App,
                             PackageProjectionCache.DefaultRulesIdentity,
                             ["app/test"],
                             [new PackageProjectionSourceFile(
                                 "app.bin",
                                 "app.bin",
                                 Convert.ToHexStringLower(SHA256.HashData(sourceBytes)),
                                 sourceBytes.LongLength)],
                             ["app.bin"]),
                         new PackageProjectionOwner("test", Guid.NewGuid(), "demo", "generation")))
        {
            Assert.True(Directory.Exists(projection.RootPath));
        }

        var first = await RuntimeV1StateReset.ResetStartupCacheAsync(TimeSpan.Zero, runtimeRoot);
        var second = await RuntimeV1StateReset.ResetStartupCacheAsync(TimeSpan.Zero, runtimeRoot);

        Assert.True(first.Success);
        Assert.True(second.Success);
        var category = Assert.Single(first.Categories);
        Assert.Equal("startup-cache", category.Category);
        var resetPaths = Assert.IsAssignableFrom<IReadOnlyList<string>>(category.RelativePaths);
        Assert.Contains("cache/package-ui-snapshots/r2", resetPaths);
        Assert.Contains("cache/package-projections/r1", resetPaths);
        Assert.Contains("execution-runtimes/health", resetPaths);
        Assert.Equal("already-empty", Assert.Single(second.Categories).Status);
        Assert.False(Directory.Exists(snapshotRevision));
        Assert.False(Directory.Exists(projectionCache.RootPath));
        var coordinationPath = Path.Combine(
            runtimeRoot,
            RuntimeV1StateDescriptor.PackageProjectionCacheCoordinationFile.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(coordinationPath));
        Assert.DoesNotContain(
            RuntimeV1StateDescriptor.PackageProjectionCacheCoordinationFile,
            resetPaths);
        Assert.All(
            new[] { installed, packageData, executionRuntime, credential, transfer },
            path => Assert.True(File.Exists(path)));
        Assert.False(File.Exists(executionRuntimeHealth));
        Assert.True(File.Exists(Path.Combine(runtimeRoot, "catalog", "installed-packages.json")));
        Assert.True(File.Exists(Path.Combine(runtimeRoot, "connection.json")));
        AssertCompletedReceipt(runtimeRoot, first.ReceiptRelativePath!);
    }

    [Fact]
    public async Task Startup_cache_reset_refuses_a_live_projection_lease()
    {
        var runtimeRoot = Path.Combine(CreateTempDirectory(), "runtime", "v1");
        var sourceRoot = Path.Combine(CreateTempDirectory(), "projection-source");
        RuntimeLocalState.EnsureInitialized(runtimeRoot);
        Directory.CreateDirectory(sourceRoot);
        var sourceFile = Path.Combine(sourceRoot, "app.bin");
        File.WriteAllText(sourceFile, "app");
        var sourceBytes = await File.ReadAllBytesAsync(sourceFile);
        var projectionCache = new PackageProjectionCache(runtimeRoot);
        await using var projection = await projectionCache.GetOrBuildAndLeaseAsync(
            new PackageProjectionRequest(
                sourceRoot,
                "demo",
                Convert.ToHexStringLower(SHA256.HashData("source"u8)),
                PackageProjectionRole.App,
                PackageProjectionCache.DefaultRulesIdentity,
                ["app/test"],
                [new PackageProjectionSourceFile(
                    "app.bin",
                    "app.bin",
                    Convert.ToHexStringLower(SHA256.HashData(sourceBytes)),
                    sourceBytes.LongLength)],
                ["app.bin"]),
            new PackageProjectionOwner("test", Guid.NewGuid(), "demo", "generation"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RuntimeV1StateReset.ResetStartupCacheAsync(TimeSpan.Zero, runtimeRoot));

        Assert.Contains("live App or Runtime lease", exception.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(projection.RootPath));
    }

    [Fact]
    public async Task Reset_refuses_unknown_root_entries_before_deleting_known_state()
    {
        var runtimeRoot = Path.Combine(CreateTempDirectory(), "runtime", "v1");
        RuntimeLocalState.EnsureInitialized(runtimeRoot);
        Directory.CreateDirectory(Path.Combine(runtimeRoot, "catalog"));
        var catalogPath = Path.Combine(runtimeRoot, "catalog", "installed-packages.json");
        File.WriteAllText(catalogPath, "state");
        var staleSchemaTemp = Path.Combine(runtimeRoot, ".schema.json.interrupted.tmp");
        File.WriteAllText(staleSchemaTemp, "stale");
        var unknownPath = Path.Combine(runtimeRoot, "unknown-user-data");
        File.WriteAllText(unknownPath, "keep");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => RuntimeV1StateReset.ResetAsync(TimeSpan.Zero, runtimeRoot));

        Assert.Contains("unclassified", exception.Message, StringComparison.Ordinal);
        Assert.Equal("state", File.ReadAllText(catalogPath));
        Assert.Equal("keep", File.ReadAllText(unknownPath));
        Assert.Equal("stale", File.ReadAllText(staleSchemaTemp));
        Assert.False(File.Exists(Path.Combine(runtimeRoot, "runtime.lock")));
        Assert.False(Directory.Exists(Path.Combine(runtimeRoot, "reset-receipts")));
    }

    [Fact]
    public async Task Reset_refuses_noncanonical_root_entry_casing_without_mutation()
    {
        var runtimeRoot = Path.Combine(CreateTempDirectory(), "runtime", "v1");
        RuntimeLocalState.EnsureInitialized(runtimeRoot);
        var noncanonicalPath = Path.Combine(runtimeRoot, "CACHE");
        Directory.CreateDirectory(noncanonicalPath);
        File.WriteAllText(Path.Combine(noncanonicalPath, "keep"), "keep");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => RuntimeV1StateReset.ResetAsync(TimeSpan.Zero, runtimeRoot));

        Assert.Equal("keep", File.ReadAllText(Path.Combine(noncanonicalPath, "keep")));
        Assert.False(File.Exists(Path.Combine(runtimeRoot, RuntimeLocalState.LeaseFileName)));
        Assert.False(Directory.Exists(Path.Combine(runtimeRoot, RuntimeV1StateDescriptor.ResetReceiptDirectory)));
    }

    [Theory]
    [InlineData("catalog/unreviewed.json", false)]
    [InlineData("installed/not-valid!/1.0.0", true)]
    [InlineData("installed/demo/not-a-version", true)]
    [InlineData("staging/not-a-guid", true)]
    [InlineData("transactions/11111111111111111111111111111111/notes.txt", false)]
    [InlineData("tombstones/not-a-tombstone", true)]
    [InlineData("transfers/unrecognized.bin", false)]
    [InlineData("cache/unreviewed", true)]
    [InlineData("cache/package-ui-snapshots/r999", true)]
    [InlineData("cache/package-projections/r999", true)]
    public async Task Reset_refuses_unknown_nested_names_without_mutation(string relativePath, bool directory)
    {
        var runtimeRoot = Path.Combine(CreateTempDirectory(), "runtime", "v1");
        RuntimeLocalState.EnsureInitialized(runtimeRoot);
        Directory.CreateDirectory(Path.Combine(runtimeRoot, "catalog"));
        var catalogPath = Path.Combine(runtimeRoot, "catalog", "installed-packages.json");
        File.WriteAllText(catalogPath, "state");
        var unknownPath = Path.Combine(runtimeRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory ? unknownPath : Path.GetDirectoryName(unknownPath)!);
        if (!directory)
        {
            File.WriteAllText(unknownPath, "keep");
        }
        var staleSchemaTemp = Path.Combine(runtimeRoot, ".schema.json.interrupted.tmp");
        File.WriteAllText(staleSchemaTemp, "stale");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => RuntimeV1StateReset.ResetAsync(TimeSpan.Zero, runtimeRoot));

        Assert.Equal("state", File.ReadAllText(catalogPath));
        Assert.True(Path.Exists(unknownPath));
        Assert.Equal("stale", File.ReadAllText(staleSchemaTemp));
        Assert.False(File.Exists(Path.Combine(runtimeRoot, "runtime.lock")));
        Assert.False(Directory.Exists(Path.Combine(runtimeRoot, "reset-receipts")));
    }

    [Fact]
    public async Task Reset_refuses_root_reparse_point_without_mutating_target()
    {
        var root = CreateTempDirectory();
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "runtime-link");
        RuntimeLocalState.EnsureInitialized(target);
        var staleSchemaTemp = Path.Combine(target, ".schema.json.interrupted.tmp");
        File.WriteAllText(staleSchemaTemp, "stale");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception linkException) when (linkException is UnauthorizedAccessException or IOException)
        {
            return;
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => RuntimeV1StateReset.ResetAsync(TimeSpan.Zero, link));

        Assert.Contains("symbolic link or reparse-point", exception.Message, StringComparison.Ordinal);
        Assert.Equal("stale", File.ReadAllText(staleSchemaTemp));
        Assert.False(File.Exists(Path.Combine(target, "runtime.lock")));
        Assert.False(Directory.Exists(Path.Combine(target, "reset-receipts")));
    }

    [Fact]
    public async Task Reset_refuses_existing_root_beneath_reparse_ancestor_without_mutation()
    {
        var root = CreateTempDirectory();
        var targetAncestor = Path.Combine(root, "target");
        var targetRoot = Path.Combine(targetAncestor, "runtime", "v1");
        var linkAncestor = Path.Combine(root, "runtime-link");
        RuntimeLocalState.EnsureInitialized(targetRoot);
        var staleSchemaTemp = Path.Combine(targetRoot, ".schema.json.interrupted.tmp");
        File.WriteAllText(staleSchemaTemp, "stale");
        if (!TryCreateDirectorySymbolicLink(linkAncestor, targetAncestor))
        {
            return;
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => RuntimeV1StateReset.ResetAsync(
                TimeSpan.Zero,
                Path.Combine(linkAncestor, "runtime", "v1")));

        Assert.Contains(linkAncestor, exception.Message, StringComparison.Ordinal);
        Assert.Equal("stale", File.ReadAllText(staleSchemaTemp));
        Assert.False(File.Exists(Path.Combine(targetRoot, "runtime.lock")));
        Assert.False(Directory.Exists(Path.Combine(targetRoot, "reset-receipts")));
    }

    [Fact]
    public async Task Reset_refuses_nonexistent_root_beneath_reparse_ancestor_without_creation()
    {
        var root = CreateTempDirectory();
        var targetAncestor = Path.Combine(root, "target");
        var linkAncestor = Path.Combine(root, "runtime-link");
        Directory.CreateDirectory(targetAncestor);
        if (!TryCreateDirectorySymbolicLink(linkAncestor, targetAncestor))
        {
            return;
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => RuntimeV1StateReset.ResetAsync(
                TimeSpan.Zero,
                Path.Combine(linkAncestor, "runtime", "v1")));

        Assert.Contains(linkAncestor, exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(targetAncestor));
    }

    [Fact]
    public async Task Reset_allows_trusted_macos_var_alias()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var tempPath = Path.GetFullPath(Path.GetTempPath());
        var varTempPath = tempPath.StartsWith("/private/var/", StringComparison.Ordinal)
            ? "/var/" + tempPath[13..]
            : tempPath;
        if (!varTempPath.StartsWith("/var/", StringComparison.Ordinal))
        {
            return;
        }

        var runtimeRoot = Path.Combine(varTempPath, "SunderLocalStateV1AliasTests", Guid.NewGuid().ToString("N"));
        try
        {
            RuntimeLocalState.EnsureInitialized(runtimeRoot);

            var result = await RuntimeV1StateReset.ResetAsync(TimeSpan.Zero, runtimeRoot);

            Assert.True(result.Success);
        }
        finally
        {
            if (Directory.Exists(runtimeRoot))
            {
                Directory.Delete(runtimeRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Reset_refuses_reparse_root_entry_without_mutating_target()
    {
        var root = CreateTempDirectory();
        var runtimeRoot = Path.Combine(root, "runtime", "v1");
        var outside = Path.Combine(root, "outside");
        RuntimeLocalState.EnsureInitialized(runtimeRoot);
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "keep.txt");
        File.WriteAllText(marker, "keep");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(runtimeRoot, "installed"), outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return;
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            () => RuntimeV1StateReset.ResetAsync(TimeSpan.Zero, runtimeRoot));

        Assert.Equal("keep", File.ReadAllText(marker));
        Assert.False(File.Exists(Path.Combine(runtimeRoot, "runtime.lock")));
        Assert.False(Directory.Exists(Path.Combine(runtimeRoot, "reset-receipts")));
    }

    [Fact]
    public async Task Reset_refuses_nested_reparse_point_without_mutating_target()
    {
        var root = CreateTempDirectory();
        var runtimeRoot = Path.Combine(root, "runtime", "v1");
        var outside = Path.Combine(root, "outside");
        var installedVersion = Path.Combine(runtimeRoot, "installed", "demo", "1.0.0");
        RuntimeLocalState.EnsureInitialized(runtimeRoot);
        Directory.CreateDirectory(installedVersion);
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "keep.txt");
        File.WriteAllText(marker, "keep");
        if (!TryCreateDirectorySymbolicLink(Path.Combine(installedVersion, "linked-payload"), outside))
        {
            return;
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            () => RuntimeV1StateReset.ResetAsync(TimeSpan.Zero, runtimeRoot));

        Assert.Equal("keep", File.ReadAllText(marker));
        Assert.True(Directory.Exists(installedVersion));
        Assert.False(File.Exists(Path.Combine(runtimeRoot, RuntimeLocalState.LeaseFileName)));
        Assert.False(Directory.Exists(Path.Combine(runtimeRoot, RuntimeV1StateDescriptor.ResetReceiptDirectory)));
    }

    [Fact]
    public async Task Reset_initializes_existing_empty_root_and_writes_completed_receipt()
    {
        var runtimeRoot = Path.Combine(CreateTempDirectory(), "runtime", "v1");
        Directory.CreateDirectory(runtimeRoot);

        var result = await RuntimeV1StateReset.ResetAsync(TimeSpan.Zero, runtimeRoot);

        Assert.True(result.Success);
        Assert.All(result.Categories, category => Assert.Equal("already-empty", category.Status));
        AssertSchema(Path.Combine(runtimeRoot, "schema.json"), "sunder.runtime.local-state");
        Assert.NotNull(result.ReceiptRelativePath);
        AssertCompletedReceipt(runtimeRoot, result.ReceiptRelativePath!);
        RuntimeLocalState.Validate(runtimeRoot);
    }

    [Fact]
    public async Task Reset_initializes_nonexistent_root_and_writes_completed_receipt()
    {
        var runtimeRoot = Path.Combine(CreateTempDirectory(), "runtime", "v1");
        Assert.False(Path.Exists(runtimeRoot));

        var result = await RuntimeV1StateReset.ResetAsync(TimeSpan.Zero, runtimeRoot);

        Assert.True(result.Success);
        AssertSchema(Path.Combine(runtimeRoot, "schema.json"), "sunder.runtime.local-state");
        Assert.NotNull(result.ReceiptRelativePath);
        AssertCompletedReceipt(runtimeRoot, result.ReceiptRelativePath!);
        RuntimeLocalState.Validate(runtimeRoot);
    }

    [Fact]
    public async Task Reset_deletes_reviewed_durable_json_crash_temporaries()
    {
        const string transactionId = "11111111111111111111111111111111";
        const string catalogTempName = "installed-packages.json.tmp-22222222222222222222222222222222";
        const string journalTempName = "journal.json.tmp-33333333333333333333333333333333";
        const string schemaTempName = ".schema.json.interrupted.tmp";
        var runtimeRoot = Path.Combine(CreateTempDirectory(), "runtime", "v1");
        RuntimeLocalState.EnsureInitialized(runtimeRoot);
        var catalogTemp = Path.Combine(runtimeRoot, "catalog", catalogTempName);
        Directory.CreateDirectory(Path.GetDirectoryName(catalogTemp)!);
        File.WriteAllText(catalogTemp, "catalog-temp");
        var journalTemp = Path.Combine(runtimeRoot, "transactions", transactionId, journalTempName);
        Directory.CreateDirectory(Path.GetDirectoryName(journalTemp)!);
        File.WriteAllText(journalTemp, "journal-temp");
        var schemaTemp = Path.Combine(runtimeRoot, schemaTempName);
        File.WriteAllText(schemaTemp, "schema-temp");

        var result = await RuntimeV1StateReset.ResetAsync(TimeSpan.Zero, runtimeRoot);

        var resetPaths = result.Categories.SelectMany(category => category.RelativePaths ?? []).ToArray();
        Assert.True(result.Success);
        Assert.Contains($"catalog/{catalogTempName}", resetPaths);
        Assert.Contains($"transactions/{transactionId}", resetPaths);
        Assert.Contains(schemaTempName, resetPaths);
        Assert.False(File.Exists(catalogTemp));
        Assert.False(File.Exists(journalTemp));
        Assert.False(File.Exists(schemaTemp));
        Assert.False(Directory.Exists(Path.GetDirectoryName(journalTemp)));
    }

    [Fact]
    public async Task Reset_refuses_rpc_permission_crash_temporary_without_mutation()
    {
        const string temporaryName = "rpc-permissions.json.tmp-11111111111111111111111111111111";
        var runtimeRoot = Path.Combine(CreateTempDirectory(), "runtime", "v1");
        RuntimeLocalState.EnsureInitialized(runtimeRoot);
        var temporaryPath = Path.Combine(runtimeRoot, "catalog", temporaryName);
        Directory.CreateDirectory(Path.GetDirectoryName(temporaryPath)!);
        File.WriteAllText(temporaryPath, "keep");
        var staleSchemaTemp = Path.Combine(runtimeRoot, ".schema.json.interrupted.tmp");
        File.WriteAllText(staleSchemaTemp, "stale");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => RuntimeV1StateReset.ResetAsync(TimeSpan.Zero, runtimeRoot));

        Assert.Equal("keep", File.ReadAllText(temporaryPath));
        Assert.Equal("stale", File.ReadAllText(staleSchemaTemp));
        Assert.False(File.Exists(Path.Combine(runtimeRoot, "runtime.lock")));
        Assert.False(Directory.Exists(Path.Combine(runtimeRoot, "reset-receipts")));
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
    public async Task Reset_pre_cancelled_does_not_create_runtime_root()
    {
        var runtimeRoot = Path.Combine(CreateTempDirectory(), "missing", "runtime", "v1");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RuntimeV1StateReset.ResetAsync(TimeSpan.Zero, runtimeRoot, cancellation.Token));

        Assert.False(Directory.Exists(runtimeRoot));
    }

    [Fact]
    public async Task Reset_preserves_credentials_without_interpreting_key_files()
    {
        var runtimeRoot = Path.Combine(CreateTempDirectory(), "runtime", "v1");
        RuntimeLocalState.EnsureInitialized(runtimeRoot);
        var credentials = Path.Combine(runtimeRoot, "credentials");
        Directory.CreateDirectory(credentials);
        var registryCredentials = Path.Combine(credentials, "registry");
        Directory.CreateDirectory(registryCredentials);
        var keyPath = Path.Combine(registryCredentials, "credentials.enc.json.key");
        File.WriteAllText(keyPath, "not-json");

        var result = await RuntimeV1StateReset.ResetAsync(TimeSpan.Zero, runtimeRoot);

        Assert.True(result.Success);
        Assert.Equal("not-json", File.ReadAllText(keyPath));
    }

    [Fact]
    public async Task Reset_refuses_hard_linked_deletion_file_before_receipt()
    {
        var runtimeRoot = Path.Combine(CreateTempDirectory(), "runtime", "v1");
        RuntimeLocalState.EnsureInitialized(runtimeRoot);
        var preservedPath = Path.Combine(runtimeRoot, "package-data", "demo", "files", "keep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(preservedPath)!);
        File.WriteAllText(preservedPath, "keep");
        var transferPath = Path.Combine(
            runtimeRoot,
            "transfers",
            $"{Guid.NewGuid():N}.download");
        Directory.CreateDirectory(Path.GetDirectoryName(transferPath)!);
        if (!TryCreateHardLink(preservedPath, transferPath))
        {
            throw new IOException(
                "Could not create the hard-link reset fixture.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        UnixFileMode? preservedMode = null;
        if (!OperatingSystem.IsWindows())
        {
            preservedMode = File.GetUnixFileMode(preservedPath);
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            () => RuntimeV1StateReset.ResetAsync(TimeSpan.Zero, runtimeRoot));

        Assert.Equal("keep", File.ReadAllText(preservedPath));
        Assert.Equal("keep", File.ReadAllText(transferPath));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(preservedMode, File.GetUnixFileMode(preservedPath));
        }
        Assert.False(Directory.Exists(Path.Combine(runtimeRoot, RuntimeV1StateDescriptor.ResetReceiptDirectory)));
    }

    [Fact]
    public void Runtime_owned_mutation_refuses_a_different_mount_identity()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var root = CreateTempDirectory();

        Assert.Throws<InvalidDataException>(() =>
            RuntimeOwnedFileSystem.ValidateMutableDirectory(root, "/dev"));
    }

    [Fact]
    public async Task Reset_terminalizes_receipt_when_post_start_work_fails()
    {
        var runtimeRoot = Path.Combine(CreateTempDirectory(), "runtime", "v1");
        RuntimeLocalState.EnsureInitialized(runtimeRoot);

        await Assert.ThrowsAsync<IOException>(() => RuntimeV1StateReset.ResetAsync(
            TimeSpan.Zero,
            runtimeRoot,
            afterStarted: () => throw new IOException("Injected post-start failure.")));

        var receiptPath = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(runtimeRoot, RuntimeV1StateDescriptor.ResetReceiptDirectory),
            "*.json"));
        using var receipt = JsonDocument.Parse(File.ReadAllText(receiptPath));
        Assert.Equal("partial", receipt.RootElement.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, receipt.RootElement.GetProperty("completedAtUtc").ValueKind);
        Assert.All(
            receipt.RootElement.GetProperty("categories").EnumerateArray(),
            category => Assert.Equal("partial", category.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task Reset_refuses_replaced_bound_root_after_started_receipt()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runtimeRoot = Path.Combine(CreateTempDirectory(), "runtime", "v1");
        var movedRoot = runtimeRoot + "-moved";
        RuntimeLocalState.EnsureInitialized(runtimeRoot);
        var expectedIdentity = RuntimeOwnedFileSystem.GetMutableDirectoryIdentity(runtimeRoot);
        var replacementTransfer = Path.Combine(runtimeRoot, "transfers", $"{Guid.NewGuid():N}.download");

        await Assert.ThrowsAsync<AggregateException>(() => RuntimeV1StateReset.ResetAsync(
            TimeSpan.Zero,
            runtimeRoot,
            afterStarted: () =>
            {
                Directory.Move(runtimeRoot, movedRoot);
                RuntimeLocalState.EnsureInitialized(runtimeRoot);
                Directory.CreateDirectory(Path.GetDirectoryName(replacementTransfer)!);
                File.WriteAllText(replacementTransfer, "replacement");
            },
            expectedRootIdentity: expectedIdentity));

        Assert.Equal("replacement", File.ReadAllText(replacementTransfer));
        var startedReceipt = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(movedRoot, RuntimeV1StateDescriptor.ResetReceiptDirectory),
            "*.json"));
        using var receipt = JsonDocument.Parse(File.ReadAllText(startedReceipt));
        Assert.Equal("started", receipt.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Reset_refuses_replaced_planned_subtree_after_started_receipt()
    {
        var runtimeRoot = Path.Combine(CreateTempDirectory(), "runtime", "v1");
        RuntimeLocalState.EnsureInitialized(runtimeRoot);
        var versionRoot = Path.Combine(runtimeRoot, "installed", "demo", "1.0.0");
        Directory.CreateDirectory(versionRoot);
        File.WriteAllText(Path.Combine(versionRoot, "delete.txt"), "delete");
        var preservedRoot = Path.Combine(runtimeRoot, "package-data", "demo", "preserved");
        Directory.CreateDirectory(preservedRoot);
        var preservedMarker = Path.Combine(preservedRoot, "keep.txt");
        File.WriteAllText(preservedMarker, "keep");
        var movedVersionRoot = Path.Combine(runtimeRoot, "package-data", "demo", "original-version");

        await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeV1StateReset.ResetAsync(
            TimeSpan.Zero,
            runtimeRoot,
            afterStarted: () =>
            {
                Directory.Move(versionRoot, movedVersionRoot);
                Directory.Move(preservedRoot, versionRoot);
            }));

        Assert.Equal("keep", File.ReadAllText(Path.Combine(versionRoot, "keep.txt")));
        Assert.Equal("delete", File.ReadAllText(Path.Combine(movedVersionRoot, "delete.txt")));
        var receiptPath = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(runtimeRoot, RuntimeV1StateDescriptor.ResetReceiptDirectory),
            "*.json"));
        using var receipt = JsonDocument.Parse(File.ReadAllText(receiptPath));
        Assert.Equal("partial", receipt.RootElement.GetProperty("status").GetString());
    }

    private static void AssertSchema(string path, string api)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("Sunder", document.RootElement.GetProperty("product").GetString());
        Assert.Equal(api, document.RootElement.GetProperty("api").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    private static void AssertCompletedReceipt(string runtimeRoot, string relativePath)
    {
        using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(runtimeRoot, relativePath)));
        Assert.Equal("sunder.runtime-local-reset-receipt", receipt.RootElement.GetProperty("format").GetString());
        Assert.Equal("completed", receipt.RootElement.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, receipt.RootElement.GetProperty("completedAtUtc").ValueKind);
        Assert.Contains(
            "package-data",
            receipt.RootElement.GetProperty("preservedPaths").EnumerateArray().Select(item => item.GetString()));
    }

    private static bool TryCreateDirectorySymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static bool TryCreateHardLink(string existingPath, string newPath)
        => OperatingSystem.IsWindows()
            ? CreateHardLinkWindows(newPath, existingPath, 0)
            : CreateHardLinkUnix(existingPath, newPath) == 0;

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkUnix(string existingPath, string newPath);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(
        string fileName,
        string existingFileName,
        nint securityAttributes);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            ResolvePhysicalDirectory(Path.GetTempPath()),
            "SunderLocalStateV1Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ResolvePhysicalDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)!;
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            current = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
        }

        return current;
    }
}
