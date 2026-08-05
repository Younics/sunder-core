using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Sdk.Storage;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageStorageServicesTests
{
    private const string ChildLockPathEnvironmentVariable = "SUNDER_STORAGE_TEST_CHILD_LOCK";
    private const string ChildReadyPathEnvironmentVariable = "SUNDER_STORAGE_TEST_CHILD_READY";
    private const string ChildReleasePathEnvironmentVariable = "SUNDER_STORAGE_TEST_CHILD_RELEASE";

    [Fact]
    public void LocalPackageFileStore_ResolvePath_RejectsParentTraversal()
    {
        var store = new LocalPackageFileStore(CreateTempDirectory());

        var exception = Assert.Throws<ArgumentException>(() => store.ResolvePath("../outside.txt"));

        Assert.Contains("traversal", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalPackageFileStore_ResolvePath_RejectsAbsolutePath()
    {
        var store = new LocalPackageFileStore(CreateTempDirectory());
        var absolutePath = Path.Combine(Path.GetTempPath(), "outside.txt");

        var exception = Assert.Throws<ArgumentException>(() => store.ResolvePath(absolutePath));

        Assert.Contains("must be relative", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalPackageFileStore_ResolvePath_CombinesRelativeSegments()
    {
        var root = CreateTempDirectory();
        var store = new LocalPackageFileStore(root);

        var path = store.ResolvePath("folder/file.txt");

        Assert.Equal(Path.Combine(root, "folder", "file.txt"), path);
    }

    [Fact]
    public async Task LocalPackageFileStore_StreamWriteAtomicallyReplacesAndOpensContent()
    {
        var root = CreateTempDirectory();
        var store = new LocalPackageFileStore(root);
        await store.WriteAsync("nested/value.bin", new byte[] { 1, 2, 3 });
        await using var replacement = new MemoryStream([4, 5, 6, 7], writable: false);

        await store.WriteAsync("nested/value.bin", replacement);
        await using var opened = await store.OpenReadAsync("nested/value.bin");

        Assert.NotNull(opened);
        using var copied = new MemoryStream();
        await opened.CopyToAsync(copied);
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, copied.ToArray());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "nested"), ".sunder-*.tmp"));
    }

    [Fact]
    public async Task LocalPackageFileStore_CancelledStreamWritePreservesPriorFileAndCleansTemp()
    {
        var root = CreateTempDirectory();
        var store = new LocalPackageFileStore(root);
        await store.WriteAsync("value.bin", new byte[] { 1, 2, 3 });
        using var cancellation = new CancellationTokenSource();
        await using var replacement = new CancelAfterFirstReadStream(
            Enumerable.Repeat((byte)9, 128 * 1024).ToArray(),
            cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.WriteAsync("value.bin", replacement, cancellation.Token));

        Assert.Equal(new byte[] { 1, 2, 3 }, await store.ReadAsync("value.bin"));
        Assert.Empty(Directory.EnumerateFiles(root, ".sunder-*.tmp"));
    }

    [Fact]
    public async Task LocalPackageFileStore_PreCancelledWriteDoesNotCreateTargetOrTemp()
    {
        var root = CreateTempDirectory();
        var store = new LocalPackageFileStore(root);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.WriteAsync("value.bin", new byte[] { 1 }, cancellation.Token));

        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task JsonPackageKeyValueStore_RejectsLegacyDictionaryWithoutMutation()
    {
        var statePath = Path.Combine(CreateTempDirectory(), "state.json");
        File.WriteAllText(statePath, """
            {"format":"legacy-format-value","version":"legacy-version-value","legacy":"preserved"}
            """);
        var original = File.ReadAllBytes(statePath);
        var store = new JsonPackageKeyValueStore(statePath);

        await Assert.ThrowsAsync<PackageStorageNotSupportedException>(() => store.GetValueAsync("format"));
        await Assert.ThrowsAsync<PackageStorageNotSupportedException>(() => store.SetValueAsync("new", "value"));
        Assert.Equal(original, File.ReadAllBytes(statePath));
    }

    [Fact]
    public async Task JsonPackageKeyValueStore_SupportsSetReplaceDeleteAndList()
    {
        var statePath = Path.Combine(CreateTempDirectory(), "state.json");
        var store = new JsonPackageKeyValueStore(statePath);

        await store.SetValueAsync("group.first", "one");
        await store.SetValueAsync("other", "two");
        await store.SetValueAsync("group.first", "replaced");

        Assert.Equal("replaced", await store.GetValueAsync("group.first"));
        Assert.True(await store.ContainsKeyAsync("other"));
        Assert.Equal(["group.first"], await store.ListKeysAsync("group."));

        await store.DeleteValueAsync("other");

        Assert.False(await store.ContainsKeyAsync("other"));
        Assert.Equal(["group.first"], await store.ListKeysAsync());
    }

    [Fact]
    public async Task JsonPackageKeyValueStore_TwoInstancesMergeConcurrentWrites()
    {
        const int writeCount = 40;
        var statePath = Path.Combine(CreateTempDirectory(), "state.json");
        var stores = new[]
        {
            new JsonPackageKeyValueStore(statePath),
            new JsonPackageKeyValueStore(statePath),
        };

        await Task.WhenAll(Enumerable.Range(0, writeCount).Select(index => Task.Run(
            () => stores[index % stores.Length].SetValueAsync($"key-{index}", $"value-{index}"))));

        var keys = await stores[0].ListKeysAsync();
        Assert.Equal(writeCount, keys.Count);
        for (var index = 0; index < writeCount; index++)
        {
            Assert.Equal($"value-{index}", await stores[1].GetValueAsync($"key-{index}"));
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(statePath));
        Assert.Equal(writeCount, document.RootElement.GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task JsonPackageKeyValueStore_MigratesKnownLegacyKeysRetainsBackupAndIsIdempotent()
    {
        const string bindingId = "workspace:/caf\u00E9 with spaces/";
        var directory = CreateTempDirectory();
        var statePath = Path.Combine(directory, "state.json");
        var legacyWorkspaceKey = $"workspace-bindings:{bindingId}:config";
        var original = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "sunder.package-state",
            version = 1,
            revision = 7,
            values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [legacyWorkspaceKey] = "workspace-value",
                ["docker.images:v1"] = "image-value",
                ["stable"] = "stable-value",
            },
        });
        await File.WriteAllBytesAsync(statePath, original);
        var store = new JsonPackageKeyValueStore(statePath);
        PackageStorageKeyMigration[] migrations =
        [
            PackageStorageKeyMigration.OpaqueId(
                "workspace-bindings:",
                ":config",
                "workspace-bindings.config",
                2),
            PackageStorageKeyMigration.Exact("docker.images:v1", "docker.images.v1"),
        ];

        await store.MigrateKeysAsync(migrations);

        var workspaceKey = PackageStorageKeyFactory.Create("workspace-bindings.config", 2, bindingId);
        Assert.Equal("workspace-value", await store.GetValueAsync(workspaceKey));
        Assert.Equal("image-value", await store.GetValueAsync("docker.images.v1"));
        Assert.Equal("stable-value", await store.GetValueAsync("stable"));
        var backupPath = Assert.Single(Directory.GetFiles(directory, "state.json.migration-backup.*"));
        Assert.Equal(original, await File.ReadAllBytesAsync(backupPath));
        var committed = await File.ReadAllBytesAsync(statePath);
        using (var document = JsonDocument.Parse(committed))
        {
            Assert.Equal(8, document.RootElement.GetProperty("revision").GetInt64());
            Assert.False(document.RootElement.GetProperty("values").TryGetProperty(legacyWorkspaceKey, out _));
            Assert.False(document.RootElement.GetProperty("values").TryGetProperty("docker.images:v1", out _));
        }

        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            new JsonPackageKeyValueStore(statePath).MigrateKeysAsync(migrations)));

        Assert.Equal(committed, await File.ReadAllBytesAsync(statePath));
        Assert.Single(Directory.GetFiles(directory, "state.json.migration-backup.*"));
    }

    [Fact]
    public async Task JsonPackageKeyValueStore_JsonIdentityMigrationUsesOnlyCaseEquivalentPayloadIdentity()
    {
        var directory = CreateTempDirectory();
        var statePath = Path.Combine(directory, "state.json");
        const string payload = "{\"ServerId\":\"server-one\",\"Name\":\"one\"}";
        const string mismatchedPayload = "{\"ServerId\":\"unrelated\",\"Name\":\"other\"}";
        var original = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "sunder.package-state",
            version = 1,
            revision = 2,
            values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mcp.servers.SERVER-ONE"] = payload,
                ["mcp.servers.source-id"] = mismatchedPayload,
            },
        });
        await File.WriteAllBytesAsync(statePath, original);
        var migration = PackageStorageKeyMigration.OpaqueIdWithJsonIdentity(
            "mcp.servers.",
            string.Empty,
            "mcp.catalog.server",
            1,
            "serverId");
        var destination = PackageStorageKeyFactory.Create("mcp.catalog.server", 1, "server-one");

        var store = new JsonPackageKeyValueStore(statePath);
        await store.MigrateKeysAsync([migration]);

        Assert.Equal(payload, await store.GetValueAsync(destination));
        Assert.Null(await store.GetValueAsync(PackageStorageKeyFactory.Create(
            "mcp.catalog.server",
            1,
            "SERVER-ONE")));
        Assert.Equal(
            mismatchedPayload,
            await store.GetValueAsync(PackageStorageKeyFactory.Create(
                "mcp.catalog.server",
                1,
                "source-id")));
        Assert.Null(await store.GetValueAsync(PackageStorageKeyFactory.Create(
            "mcp.catalog.server",
            1,
            "unrelated")));
    }

    [Fact]
    public async Task JsonPackageKeyValueStore_JsonIdentityMigrationRejectsCaseCollisionAtomically()
    {
        var directory = CreateTempDirectory();
        var statePath = Path.Combine(directory, "state.json");
        var original = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "sunder.package-state",
            version = 1,
            revision = 2,
            values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mcp.servers.Server-One"] = "{\"ServerId\":\"server-one\",\"Name\":\"first\"}",
                ["mcp.servers.SERVER-ONE"] = "{\"ServerId\":\"server-one\",\"Name\":\"second\"}",
            },
        });
        await File.WriteAllBytesAsync(statePath, original);
        var migration = PackageStorageKeyMigration.OpaqueIdWithJsonIdentity(
            "mcp.servers.",
            string.Empty,
            "mcp.catalog.server",
            1,
            "serverId");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new JsonPackageKeyValueStore(statePath).MigrateKeysAsync([migration]));

        Assert.Contains("collision", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await File.ReadAllBytesAsync(statePath));
        Assert.Empty(Directory.GetFiles(directory, "state.json.migration-backup.*"));
    }

    [Fact]
    public async Task JsonPackageKeyValueStore_CaseInsensitiveJsonIdentityRejectsCaseVariantPayloadsAtomically()
    {
        var directory = CreateTempDirectory();
        var statePath = Path.Combine(directory, "state.json");
        var original = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "sunder.package-state",
            version = 1,
            revision = 2,
            values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mcp.servers.server-one"] = "{\"ServerId\":\"server-one\",\"Name\":\"first\"}",
                ["mcp.servers.SERVER-ONE"] = "{\"ServerId\":\"SERVER-ONE\",\"Name\":\"second\"}",
            },
        });
        await File.WriteAllBytesAsync(statePath, original);
        var migration = PackageStorageKeyMigration.OpaqueIdWithCaseInsensitiveJsonIdentity(
            "mcp.servers.",
            string.Empty,
            "mcp.catalog.server",
            1,
            "serverId");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new JsonPackageKeyValueStore(statePath).MigrateKeysAsync([migration]));

        Assert.Contains("collision", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await File.ReadAllBytesAsync(statePath));
        Assert.Empty(Directory.GetFiles(directory, "state.json.migration-backup.*"));
    }

    [Fact]
    public async Task JsonPackageKeyValueStore_MigrationRejectsNonidenticalCollisionWithoutMutation()
    {
        const string bindingId = "binding";
        var directory = CreateTempDirectory();
        var statePath = Path.Combine(directory, "state.json");
        var destination = PackageStorageKeyFactory.Create("workspace-bindings.config", 2, bindingId);
        var original = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "sunder.package-state",
            version = 1,
            revision = 3,
            values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [$"workspace-bindings:{bindingId}:config"] = "legacy",
                [destination] = "different",
            },
        });
        await File.WriteAllBytesAsync(statePath, original);
        var migration = PackageStorageKeyMigration.OpaqueId(
            "workspace-bindings:",
            ":config",
            "workspace-bindings.config",
            2);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new JsonPackageKeyValueStore(statePath).MigrateKeysAsync([migration]));

        Assert.Contains("nonidentical", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await File.ReadAllBytesAsync(statePath));
        Assert.Empty(Directory.GetFiles(directory, "state.json.migration-backup.*"));
    }

    [Fact]
    public async Task JsonPackageKeyValueStore_MigrationRejectsMultipleMatchingRulesWithoutMutation()
    {
        const string legacyKey = "legacy:binding";
        var directory = CreateTempDirectory();
        var statePath = Path.Combine(directory, "state.json");
        var destination = PackageStorageKeyFactory.Create("bindings.config", 1, "binding");
        var original = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "sunder.package-state",
            version = 1,
            revision = 2,
            values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [legacyKey] = "value",
            },
        });
        await File.WriteAllBytesAsync(statePath, original);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new JsonPackageKeyValueStore(statePath).MigrateKeysAsync(
            [
                PackageStorageKeyMigration.Exact(legacyKey, destination),
                PackageStorageKeyMigration.OpaqueId("legacy:", string.Empty, "bindings.config", 1),
            ]));

        Assert.Contains("multiple migration rules", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await File.ReadAllBytesAsync(statePath));
        Assert.Empty(Directory.GetFiles(directory, "state.json.migration-backup.*"));
    }

    [Fact]
    public async Task JsonPackageKeyValueStore_MigrationLeavesUnknownInvalidKeysFailClosed()
    {
        var directory = CreateTempDirectory();
        var statePath = Path.Combine(directory, "state.json");
        var original = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "sunder.package-state",
            version = 1,
            revision = 1,
            values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["workspace-bindings:known:config"] = "known",
                ["unknown:invalid"] = "must-not-reset",
            },
        });
        await File.WriteAllBytesAsync(statePath, original);
        var migration = PackageStorageKeyMigration.OpaqueId(
            "workspace-bindings:",
            ":config",
            "workspace-bindings.config",
            2);

        var exception = await Assert.ThrowsAsync<PackageStorageRecoveryRequiredException>(() =>
            new JsonPackageKeyValueStore(statePath).MigrateKeysAsync([migration]));

        Assert.Equal(original, await File.ReadAllBytesAsync(exception.QuarantinePath));
        AssertFailureMarker(statePath, exception.QuarantinePath);
        Assert.Empty(Directory.GetFiles(directory, "state.json.migration-backup.*"));
    }

    [Fact]
    public void PackageStorageKeyMigrationEngine_DynamicCleanupPrefersCanonicalAndExistingValues()
    {
        var firstDestination = PackageStorageKeyFactory.Create("mcp.secret.header", 1, "first");
        var secondDestination = PackageStorageKeyFactory.Create("mcp.secret.header", 1, "second");
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mcp.servers.live.v3.headers.X-Token"] = "canonical",
            ["mcp.servers.LIVE.v3.headers.x-token"] = "conflicting-case-remnant",
            ["mcp.servers.live.v2.headers.X-Token"] = "old-version",
            ["mcp.servers.live.v3.headers.Second"] = "stale-second",
            [secondDestination] = "current-second",
        };
        var migration = PackageStorageKeyMigration.DynamicCleanup(key => key switch
        {
            "mcp.servers.live.v3.headers.X-Token" =>
                PackageStorageKeyMigrationAction.Rewrite(firstDestination, precedence: 1),
            _ when key.Equals("mcp.servers.live.v3.headers.X-Token", StringComparison.OrdinalIgnoreCase) =>
                PackageStorageKeyMigrationAction.Rewrite(firstDestination),
            _ when key.Equals("mcp.servers.live.v3.headers.Second", StringComparison.OrdinalIgnoreCase) =>
                PackageStorageKeyMigrationAction.Rewrite(secondDestination),
            _ when key.StartsWith("mcp.servers.", StringComparison.OrdinalIgnoreCase) =>
                PackageStorageKeyMigrationAction.Delete,
            _ => PackageStorageKeyMigrationAction.NoMatch,
        });

        var result = PackageStorageKeyMigrationEngine.Apply(values, [migration]);

        Assert.True(result.Changed);
        Assert.Equal("canonical", result.Values[firstDestination]);
        Assert.Equal("current-second", result.Values[secondDestination]);
        Assert.Equal(2, result.Values.Count);
    }

    [Fact]
    public async Task JsonPackageKeyValueStore_MigrationRecoversMarkerCreatedOnlyByKnownLegacyKey()
    {
        const string bindingId = "imported:/\u65E5\u672C\u8A9E workspace";
        var directory = CreateTempDirectory();
        var statePath = Path.Combine(directory, "state.json");
        var legacyKey = $"workspace-bindings:{bindingId}:config";
        var original = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "sunder.package-state",
            version = 1,
            revision = 4,
            values = new Dictionary<string, string> { [legacyKey] = "preserved" },
        });
        await File.WriteAllBytesAsync(statePath, original);
        var store = new JsonPackageKeyValueStore(statePath);
        var failure = await Assert.ThrowsAsync<PackageStorageRecoveryRequiredException>(() =>
            store.GetValueAsync("valid"));
        var migration = PackageStorageKeyMigration.OpaqueId(
            "workspace-bindings:",
            ":config",
            "workspace-bindings.config",
            2);

        await store.MigrateKeysAsync([migration]);

        Assert.Equal(
            "preserved",
            await store.GetValueAsync(PackageStorageKeyFactory.Create("workspace-bindings.config", 2, bindingId)));
        Assert.Equal(original, await File.ReadAllBytesAsync(failure.QuarantinePath));
        Assert.True(File.Exists(failure.QuarantinePath));
        Assert.Empty(Directory.GetFiles(directory, "state.json.migration-backup.*"));
    }

    [Fact]
    public async Task JsonPackageKeyValueStore_UnchangedSetAndReplaceDoNotAdvanceRevisionOrRewriteDocument()
    {
        var statePath = Path.Combine(CreateTempDirectory(), "state.json");
        var store = new JsonPackageKeyValueStore(statePath);
        await store.SetValueAsync("key", "value");
        var committed = File.ReadAllBytes(statePath);

        await store.SetValueAsync("key", "value");
        await store.ReplaceValuesAsync(new Dictionary<string, string> { ["key"] = "value" });

        Assert.Equal(committed, File.ReadAllBytes(statePath));
        using var document = JsonDocument.Parse(committed);
        Assert.Equal(1, document.RootElement.GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task JsonPackageKeyValueStore_ChildProcessLockBlocksOtherProcesses()
    {
        var tempDirectory = CreateTempDirectory();
        var statePath = Path.Combine(tempDirectory, "state.json");
        var readyPath = Path.Combine(tempDirectory, "ready");
        var releasePath = Path.Combine(tempDirectory, "release");
        using var child = StartLockChildProcess($"{statePath}.lock", readyPath, releasePath);

        try
        {
            await WaitForFileAsync(readyPath, child, TimeSpan.FromSeconds(20));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            var store = new JsonPackageKeyValueStore(statePath);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => store.SetValueAsync("blocked", "value", cancellation.Token));
        }
        finally
        {
            File.WriteAllText(releasePath, "release");
            Assert.True(child.WaitForExit(20_000), "The storage-lock child process did not exit.");
        }

        Assert.Equal(0, child.ExitCode);
    }

    [Fact]
    public void JsonPackageKeyValueStore_ChildProcessLockHolder()
    {
        var lockPath = Environment.GetEnvironmentVariable(ChildLockPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(lockPath))
        {
            return;
        }

        var readyPath = Environment.GetEnvironmentVariable(ChildReadyPathEnvironmentVariable)!;
        var releasePath = Environment.GetEnvironmentVariable(ChildReleasePathEnvironmentVariable)!;
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        using var lockHandle = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        File.WriteAllText(readyPath, "ready");

        Assert.True(
            SpinWait.SpinUntil(() => File.Exists(releasePath), TimeSpan.FromSeconds(20)),
            "The parent process did not release the storage lock child.");
    }

    [Fact]
    public async Task JsonPackageKeyValueStore_FailedTempWriteRetainsPriorDocument()
    {
        var statePath = Path.Combine(CreateTempDirectory(), "state.json");
        var store = new JsonPackageKeyValueStore(statePath);
        await store.SetValueAsync("stable", "prior-value");
        var priorDocument = File.ReadAllBytes(statePath);
        var faultingStore = new JsonPackageKeyValueStore(statePath, new TruncatingWriteFileSystem());

        await Assert.ThrowsAsync<IOException>(() => faultingStore.SetValueAsync("new", "not-committed"));

        Assert.Equal(priorDocument, File.ReadAllBytes(statePath));
        Assert.Equal("prior-value", await store.GetValueAsync("stable"));
        Assert.Null(await store.GetValueAsync("new"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(statePath)!, "state.json.tmp.*"));
    }

    [Fact]
    public async Task JsonPackageKeyValueStore_SymbolicLinkDocumentIsRejectedWithoutMutatingTarget()
    {
        var tempDirectory = CreateTempDirectory();
        var outsidePath = Path.Combine(CreateTempDirectory(), "outside.json");
        var statePath = Path.Combine(tempDirectory, "state.json");
        await new JsonPackageKeyValueStore(outsidePath).SetValueAsync("stable", "outside");
        try
        {
            File.CreateSymbolicLink(statePath, outsidePath);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            return;
        }
        var original = File.ReadAllBytes(outsidePath);

        await Assert.ThrowsAsync<IOException>(
            () => new JsonPackageKeyValueStore(statePath).SetValueAsync("new", "value"));

        Assert.Equal(original, File.ReadAllBytes(outsidePath));
        Assert.Equal("outside", await new JsonPackageKeyValueStore(outsidePath).GetValueAsync("stable"));
    }

    [Fact]
    public async Task JsonPackageKeyValueStore_CorruptionMarkerFailsClosedUntilExplicitReset()
    {
        var tempDirectory = CreateTempDirectory();
        var statePath = Path.Combine(tempDirectory, "state.json");
        File.WriteAllText(statePath, "not-json");
        var store = new JsonPackageKeyValueStore(statePath);

        var first = await Assert.ThrowsAsync<PackageStorageRecoveryRequiredException>(() => store.GetValueAsync("missing"));

        Assert.True(File.Exists(statePath));
        Assert.Equal(first.QuarantinePath, Assert.Single(Directory.GetFiles(tempDirectory, "state.json.corrupt.*")));
        AssertFailureMarker(statePath, first.QuarantinePath);

        var failures = await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
        {
            try
            {
                if (index % 2 == 0)
                {
                    _ = await new JsonPackageKeyValueStore(statePath).GetValueAsync("missing");
                }
                else
                {
                    await new JsonPackageKeyValueStore(statePath).SetValueAsync("new", "value");
                }

                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }));

        Assert.All(failures, failure => Assert.IsType<PackageStorageRecoveryRequiredException>(failure));
        Assert.Single(Directory.GetFiles(tempDirectory, "state.json.corrupt.*"));

        store.ResetAfterFailure();
        await store.SetValueAsync("recovered", "value");
        Assert.Equal("value", await store.GetValueAsync("recovered"));
    }

    [Fact]
    public async Task JsonPackageKeyValueStore_UnsupportedEnvelopePreservesCanonicalBytes()
    {
        var tempDirectory = CreateTempDirectory();
        var statePath = Path.Combine(tempDirectory, "state.json");
        var original = """
            {"format":"sunder.package-state","version":2,"revision":10,"values":{"key":"value"}}
            """u8.ToArray();
        File.WriteAllBytes(statePath, original);
        var store = new JsonPackageKeyValueStore(statePath);

        var exception = await Assert.ThrowsAsync<PackageStorageNotSupportedException>(() => store.GetValueAsync("key"));

        Assert.Contains("not supported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, File.ReadAllBytes(statePath));
        Assert.Empty(Directory.GetFiles(tempDirectory, "state.json.corrupt.*"));
    }

    [Fact]
    public async Task JsonPackageKeyValueStore_ValidatesNullsAndRevisionOverflowBeforeMutation()
    {
        var statePath = Path.Combine(CreateTempDirectory(), "state.json");
        var original = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "sunder.package-state",
            version = 1,
            revision = long.MaxValue,
            values = new Dictionary<string, string> { ["key"] = "value" },
        });
        File.WriteAllBytes(statePath, original);
        var store = new JsonPackageKeyValueStore(statePath);

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.GetValueAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SetValueAsync(null!, "value"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SetValueAsync("key", null!));
        Assert.Equal("value", await store.GetValueAsync("key"));
        await Assert.ThrowsAsync<OverflowException>(() => store.SetValueAsync("new", "value"));
        await Assert.ThrowsAsync<OverflowException>(() => store.DeleteValueAsync("key"));
        Assert.Equal(original, File.ReadAllBytes(statePath));
    }

    [Fact]
    public async Task JsonPackageKeyValueStore_DoesNotReportCancellationAfterCommit()
    {
        using var cancellation = new CancellationTokenSource();
        var statePath = Path.Combine(CreateTempDirectory(), "state.json");
        var fileSystem = new CancelAfterReplaceFileSystem(cancellation);
        var store = new JsonPackageKeyValueStore(statePath, fileSystem);

        await store.SetValueAsync("committed", "value", cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal("value", await new JsonPackageKeyValueStore(statePath).GetValueAsync("committed"));
    }

    [Fact]
    public async Task JsonPackageSecretsStore_RejectsLegacyPlaintextWithoutMutation()
    {
        const string secret = "legacy-super-secret-value";
        var secretsPath = Path.Combine(CreateTempDirectory(), "secrets.json");
        File.WriteAllText(secretsPath, $$"""
            {"format":"legacy-format","version":"legacy-version","first":"{{secret}}"}
            """);
        var original = File.ReadAllBytes(secretsPath);
        var store = CreateSecretsStore(secretsPath);

        await Assert.ThrowsAsync<PackageStorageNotSupportedException>(() => store.GetSecretAsync("first"));
        Assert.Equal(original, File.ReadAllBytes(secretsPath));
        Assert.False(File.Exists($"{secretsPath}.key"));
    }

    [Fact]
    public async Task JsonPackageSecretsStore_LegacyPlaintextRemainsRejectedAcrossInstances()
    {
        const string secret = "legacy-retry-secret-value";
        var secretsPath = Path.Combine(CreateTempDirectory(), "secrets.json");
        var original = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            ["apiKey"] = secret,
        });
        File.WriteAllBytes(secretsPath, original);
        var firstStore = CreateSecretsStore(secretsPath);
        await Assert.ThrowsAsync<PackageStorageNotSupportedException>(() => firstStore.SetSecretAsync("new", "value"));
        Assert.Equal(original, File.ReadAllBytes(secretsPath));
        Assert.False(File.Exists($"{secretsPath}.key"));
        await Assert.ThrowsAsync<PackageStorageNotSupportedException>(
            () => CreateSecretsStore(secretsPath).GetSecretAsync("apiKey"));
    }

    [Fact]
    public async Task JsonPackageSecretsStore_SupportsSetReplaceDeleteAndListAcrossInstances()
    {
        var secretsPath = Path.Combine(CreateTempDirectory(), "secrets.json");
        var firstStore = CreateSecretsStore(secretsPath);
        var secondStore = CreateSecretsStore(secretsPath);

        await firstStore.SetSecretAsync("z-key", "one");
        await secondStore.SetSecretAsync("a-key", "two");
        await firstStore.SetSecretAsync("z-key", "replaced");

        Assert.Equal("replaced", await secondStore.GetSecretAsync("z-key"));
        Assert.Equal(["a-key", "z-key"], await firstStore.ListKeysAsync());

        await secondStore.DeleteSecretAsync("a-key");

        Assert.Null(await firstStore.GetSecretAsync("a-key"));
        Assert.Equal(["z-key"], await secondStore.ListKeysAsync());
    }

    [Fact]
    public async Task JsonPackageSecretsStore_TwoInstancesMergeConcurrentWrites()
    {
        const int writeCount = 24;
        var secretsPath = Path.Combine(CreateTempDirectory(), "secrets.json");
        var stores = new[]
        {
            CreateSecretsStore(secretsPath),
            CreateSecretsStore(secretsPath),
        };

        await Task.WhenAll(Enumerable.Range(0, writeCount).Select(index => Task.Run(
            () => stores[index % stores.Length].SetSecretAsync($"key-{index}", $"secret-{index}"))));

        Assert.Equal(writeCount, (await stores[0].ListKeysAsync()).Count);
        for (var index = 0; index < writeCount; index++)
        {
            Assert.Equal($"secret-{index}", await stores[1].GetSecretAsync($"key-{index}"));
        }
    }

    [Fact]
    public async Task JsonPackageSecretsStore_UnchangedSetAndReplaceDoNotAdvanceRevisionOrRewriteDocument()
    {
        var secretsPath = Path.Combine(CreateTempDirectory(), "secrets.json");
        var store = CreateSecretsStore(secretsPath);
        await store.SetSecretAsync("key", "value");
        var committed = File.ReadAllBytes(secretsPath);

        await store.SetSecretAsync("key", "value");
        await store.ReplaceValuesAsync(new Dictionary<string, string> { ["key"] = "value" });

        Assert.Equal(committed, File.ReadAllBytes(secretsPath));
    }

    [Fact]
    public async Task JsonPackageSecretsStore_MigrationRecoversKnownInvalidEncryptedKey()
    {
        var directory = CreateTempDirectory();
        var secretsPath = Path.Combine(directory, "secrets.json");
        var permissiveStore = new JsonPackageSecretsStore(
            secretsPath,
            null,
            null,
            new RestrictedFileMasterKeyProtection(),
            enforcePackageKeyValidation: false);
        await permissiveStore.SetSecretAsync("mcp.servers:imported/token", "preserved-secret");
        var strictStore = CreateSecretsStore(secretsPath);
        var failure = await Assert.ThrowsAsync<PackageStorageRecoveryRequiredException>(() =>
            strictStore.GetSecretAsync("valid"));
        var destination = PackageStorageKeyFactory.Create("mcp.oauth.tokens", 1, "imported/token");

        await strictStore.MigrateKeysAsync(
        [
            PackageStorageKeyMigration.Exact("mcp.servers:imported/token", destination),
        ]);

        Assert.Equal("preserved-secret", await strictStore.GetSecretAsync(destination));
        Assert.True(File.Exists(failure.QuarantinePath));
        Assert.Empty(Directory.GetFiles(directory, "secrets.json.migration-backup.*"));
    }

    [Fact]
    public async Task JsonPackageSecretsStore_DynamicCleanupRecoversRecognizedOrphanWithoutReadingItsValue()
    {
        const string orphanKey = "mcp.servers.deleted/id.v7.headers.X%2FToken";
        var directory = CreateTempDirectory();
        var secretsPath = Path.Combine(directory, "secrets.json");
        var permissiveStore = new JsonPackageSecretsStore(
            secretsPath,
            null,
            null,
            new RestrictedFileMasterKeyProtection(),
            enforcePackageKeyValidation: false);
        await permissiveStore.SetSecretAsync(orphanKey, "orphan-secret");
        var strictStore = CreateSecretsStore(secretsPath);
        var failure = await Assert.ThrowsAsync<PackageStorageRecoveryRequiredException>(() =>
            strictStore.GetSecretAsync("valid"));

        await strictStore.MigrateKeysAsync(
        [
            PackageStorageKeyMigration.DynamicCleanup(key =>
                string.Equals(key, orphanKey, StringComparison.Ordinal)
                    ? PackageStorageKeyMigrationAction.Delete
                    : PackageStorageKeyMigrationAction.NoMatch),
        ]);

        Assert.Empty(await strictStore.ListKeysAsync());
        Assert.True(File.Exists(failure.QuarantinePath));
        Assert.Empty(Directory.GetFiles(directory, "secrets.json.migration-backup.*"));
    }

    [Fact]
    public async Task JsonPackageSecretsStore_DynamicCleanupLeavesUnknownInvalidKeyQuarantinedAtomically()
    {
        const string recognizedKey = "mcp.servers.deleted/id.apiKey";
        const string unknownKey = "mcp.servers.deleted/id.unknown:shape";
        var directory = CreateTempDirectory();
        var secretsPath = Path.Combine(directory, "secrets.json");
        var permissiveStore = new JsonPackageSecretsStore(
            secretsPath,
            null,
            null,
            new RestrictedFileMasterKeyProtection(),
            enforcePackageKeyValidation: false);
        await permissiveStore.SetSecretAsync(recognizedKey, "recognized-secret");
        await permissiveStore.SetSecretAsync(unknownKey, "unknown-secret");
        var strictStore = CreateSecretsStore(secretsPath);

        var failure = await Assert.ThrowsAsync<PackageStorageRecoveryRequiredException>(() =>
            strictStore.MigrateKeysAsync(
            [
                PackageStorageKeyMigration.DynamicCleanup(key =>
                    string.Equals(key, recognizedKey, StringComparison.Ordinal)
                        ? PackageStorageKeyMigrationAction.Delete
                        : PackageStorageKeyMigrationAction.NoMatch),
            ]));

        AssertFailureMarker(secretsPath, failure.QuarantinePath, "aes-gcm-master-key");
        var recognizedDestination = PackageStorageKeyFactory.Create("mcp.secret.api-key", 1, "deleted/id");
        var unknownDestination = PackageStorageKeyFactory.Create("mcp.secret.recovered", 1, "unknown");
        await strictStore.MigrateKeysAsync(
        [
            PackageStorageKeyMigration.DynamicCleanup(key => key switch
            {
                recognizedKey => PackageStorageKeyMigrationAction.Rewrite(recognizedDestination),
                unknownKey => PackageStorageKeyMigrationAction.Rewrite(unknownDestination),
                _ => PackageStorageKeyMigrationAction.NoMatch,
            }),
        ]);
        Assert.Equal("recognized-secret", await strictStore.GetSecretAsync(recognizedDestination));
        Assert.Equal("unknown-secret", await strictStore.GetSecretAsync(unknownDestination));
        Assert.Empty(Directory.GetFiles(directory, "secrets.json.migration-backup.*"));
    }

    [Fact]
    public async Task JsonPackageSecretsStore_MigratesEncryptedLegacyKeyRetainsCiphertextBackupAndIsIdempotent()
    {
        const string legacyKey = "mcp.servers.imported/id.apiKey";
        const string secret = "preserved-secret-value";
        var directory = CreateTempDirectory();
        var secretsPath = Path.Combine(directory, "secrets.json");
        var permissiveStore = new JsonPackageSecretsStore(
            secretsPath,
            null,
            null,
            new RestrictedFileMasterKeyProtection(),
            enforcePackageKeyValidation: false);
        await permissiveStore.SetSecretAsync(legacyKey, secret);
        var original = await File.ReadAllBytesAsync(secretsPath);
        var destination = PackageStorageKeyFactory.Create("mcp.secret.api-key", 1, "imported/id");
        var migration = PackageStorageKeyMigration.Exact(legacyKey, destination);
        var strictStore = CreateSecretsStore(secretsPath);

        await strictStore.MigrateKeysAsync([migration]);

        Assert.Equal(secret, await strictStore.GetSecretAsync(destination));
        var backupPath = Assert.Single(Directory.GetFiles(directory, "secrets.json.migration-backup.*"));
        Assert.Equal(original, await File.ReadAllBytesAsync(backupPath));
        Assert.DoesNotContain(secret, await File.ReadAllTextAsync(backupPath), StringComparison.Ordinal);
        var committed = await File.ReadAllBytesAsync(secretsPath);

        await strictStore.MigrateKeysAsync([migration]);

        Assert.Equal(committed, await File.ReadAllBytesAsync(secretsPath));
        Assert.Single(Directory.GetFiles(directory, "secrets.json.migration-backup.*"));
    }

    [Fact]
    public async Task JsonPackageSecretsStore_EncryptsSensitivePlaintextQuarantineAndFailsClosedRepeatedly()
    {
        const string malformedSecret = "malformed-plaintext-super-secret";
        var tempDirectory = CreateTempDirectory();
        var secretsPath = Path.Combine(tempDirectory, "secrets.json");
        File.WriteAllText(secretsPath, malformedSecret);
        var store = CreateSecretsStore(secretsPath);

        var first = await Assert.ThrowsAsync<PackageStorageRecoveryRequiredException>(() => store.GetSecretAsync("missing"));

        AssertFailureMarker(secretsPath, first.QuarantinePath, "aes-gcm-master-key");
        var quarantine = File.ReadAllText(first.QuarantinePath);
        Assert.DoesNotContain(malformedSecret, quarantine, StringComparison.Ordinal);
        using (var document = JsonDocument.Parse(quarantine))
        {
            Assert.Equal(
                "sunder.package-secrets-quarantine-encrypted",
                document.RootElement.GetProperty("format").GetString());
        }

        await Assert.ThrowsAsync<PackageStorageRecoveryRequiredException>(() => store.GetSecretAsync("missing"));
        await Assert.ThrowsAsync<PackageStorageRecoveryRequiredException>(() => store.SetSecretAsync("new", "secret"));
        await Assert.ThrowsAsync<PackageStorageRecoveryRequiredException>(() => CreateSecretsStore(secretsPath).ListKeysAsync());
        var concurrentFailures = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(async () =>
        {
            try
            {
                var concurrentStore = CreateSecretsStore(secretsPath);
                if (index % 2 == 0)
                {
                    _ = await concurrentStore.GetSecretAsync("missing");
                }
                else
                {
                    await concurrentStore.DeleteSecretAsync("missing");
                }

                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        })));
        Assert.All(
            concurrentFailures,
            failure => Assert.IsType<PackageStorageRecoveryRequiredException>(failure));
        Assert.Single(Directory.GetFiles(tempDirectory, "secrets.json.corrupt.*"));
        AssertPrivateFileModeWhenSupported(first.QuarantinePath);

        store.ResetAfterFailure();
        await store.SetSecretAsync("recovered", "new-secret");
        Assert.Equal("new-secret", await store.GetSecretAsync("recovered"));
    }

    [Fact]
    public async Task JsonPackageSecretsStore_RetainsRestrictedRawQuarantineWhenKeyUnavailable()
    {
        const string malformedSecret = "malformed-plaintext-key-unavailable";
        var secretsPath = Path.Combine(CreateTempDirectory(), "secrets.json");
        File.WriteAllText(secretsPath, malformedSecret);
        var store = new JsonPackageSecretsStore(
            secretsPath,
            null,
            null,
            new AlwaysUnavailableMasterKeyProtection());

        var exception = await Assert.ThrowsAsync<PackageStorageRecoveryRequiredException>(() => store.GetSecretAsync("missing"));

        AssertFailureMarker(secretsPath, exception.QuarantinePath, "restricted-raw");
        Assert.Equal(malformedSecret, File.ReadAllText(exception.QuarantinePath));
        AssertPrivateFileModeWhenSupported(exception.QuarantinePath);
    }

    [Fact]
    public async Task JsonPackageSecretsStore_AuthenticatedTamperCreatesDurableRecoveryMarker()
    {
        var tempDirectory = CreateTempDirectory();
        var secretsPath = Path.Combine(tempDirectory, "secrets.json");
        var store = CreateSecretsStore(secretsPath);
        await store.SetSecretAsync("key", "secret-value");
        var tamperedDocument = JsonNode.Parse(File.ReadAllText(secretsPath))!;
        var ciphertext = Convert.FromBase64String(tamperedDocument["ciphertext"]!.GetValue<string>());
        ciphertext[0] ^= 0x80;
        tamperedDocument["ciphertext"] = Convert.ToBase64String(ciphertext);
        var tamperedBytes = JsonSerializer.SerializeToUtf8Bytes(tamperedDocument);
        File.WriteAllBytes(secretsPath, tamperedBytes);

        var exception = await Assert.ThrowsAsync<PackageStorageRecoveryRequiredException>(() => store.GetSecretAsync("key"));

        AssertFailureMarker(secretsPath, exception.QuarantinePath, "aes-gcm-master-key");
        Assert.NotEqual(tamperedBytes, File.ReadAllBytes(exception.QuarantinePath));
        await Assert.ThrowsAsync<PackageStorageRecoveryRequiredException>(() => store.GetSecretAsync("key"));
    }

    [Fact]
    public async Task JsonPackageSecretsStore_MissingKeyIsRecoverableAndPreservesCiphertext()
    {
        var tempDirectory = CreateTempDirectory();
        var secretsPath = Path.Combine(tempDirectory, "secrets.json");
        var store = CreateSecretsStore(secretsPath);
        await store.SetSecretAsync("key", "secret-value");
        var canonicalBytes = File.ReadAllBytes(secretsPath);
        File.Delete($"{secretsPath}.key");

        var exception = await Assert.ThrowsAsync<PackageStorageKeyUnavailableException>(() => store.GetSecretAsync("key"));

        Assert.Contains("missing", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(canonicalBytes, File.ReadAllBytes(secretsPath));
        Assert.Empty(Directory.GetFiles(tempDirectory, "secrets.json.corrupt.*"));
    }

    [Fact]
    public async Task JsonPackageSecretsStore_KeyProviderFailurePreservesKeyAndCiphertext()
    {
        var tempDirectory = CreateTempDirectory();
        var secretsPath = Path.Combine(tempDirectory, "secrets.json");
        await CreateSecretsStore(secretsPath).SetSecretAsync("key", "secret-value");
        var canonicalBytes = File.ReadAllBytes(secretsPath);
        var keyBytes = File.ReadAllBytes($"{secretsPath}.key");
        var unavailableStore = new JsonPackageSecretsStore(
            secretsPath,
            null,
            null,
            new AlwaysUnavailableMasterKeyProtection());

        await Assert.ThrowsAsync<PackageStorageKeyUnavailableException>(() => unavailableStore.GetSecretAsync("key"));

        Assert.Equal(canonicalBytes, File.ReadAllBytes(secretsPath));
        Assert.Equal(keyBytes, File.ReadAllBytes($"{secretsPath}.key"));
        Assert.Empty(Directory.GetFiles(tempDirectory, "*.corrupt.*"));
    }

    [Fact]
    public async Task JsonPackageSecretsStore_WrongWellFormedKeyIsAuthenticatedCorruption()
    {
        var tempDirectory = CreateTempDirectory();
        var secretsPath = Path.Combine(tempDirectory, "secrets.json");
        var otherSecretsPath = Path.Combine(tempDirectory, "other-secrets.json");
        var store = CreateSecretsStore(secretsPath);
        await store.SetSecretAsync("key", "secret-value");
        await CreateSecretsStore(otherSecretsPath).SetSecretAsync("other", "other-value");
        File.Copy($"{otherSecretsPath}.key", $"{secretsPath}.key", overwrite: true);

        var exception = await Assert.ThrowsAsync<PackageStorageRecoveryRequiredException>(() => store.GetSecretAsync("key"));

        AssertFailureMarker(secretsPath, exception.QuarantinePath, "aes-gcm-master-key");
        Assert.True(File.Exists($"{secretsPath}.key"));
    }

    [Fact]
    public async Task JsonPackageSecretsStore_UnsupportedDocumentsAndKeySchemesAreNonMutating()
    {
        var tempDirectory = CreateTempDirectory();
        var unsupportedPath = Path.Combine(tempDirectory, "unsupported.json");
        var unsupportedBytes = """
            {"format":"sunder.package-secrets-encrypted","version":2,"protection":{}}
            """u8.ToArray();
        File.WriteAllBytes(unsupportedPath, unsupportedBytes);

        await Assert.ThrowsAsync<PackageStorageNotSupportedException>(
            () => CreateSecretsStore(unsupportedPath).GetSecretAsync("missing"));
        Assert.Equal(unsupportedBytes, File.ReadAllBytes(unsupportedPath));
        Assert.False(File.Exists($"{unsupportedPath}.key"));

        var cipherSchemePath = Path.Combine(tempDirectory, "cipher-scheme.json");
        await CreateSecretsStore(cipherSchemePath).SetSecretAsync("key", "secret-value");
        var cipherSchemeDocument = JsonNode.Parse(File.ReadAllText(cipherSchemePath))!;
        cipherSchemeDocument["protection"]!["scheme"] = "future-aead";
        File.WriteAllText(cipherSchemePath, cipherSchemeDocument.ToJsonString());
        var unsupportedCipherBytes = File.ReadAllBytes(cipherSchemePath);
        var preservedCipherKey = File.ReadAllBytes($"{cipherSchemePath}.key");

        await Assert.ThrowsAsync<PackageStorageNotSupportedException>(
            () => CreateSecretsStore(cipherSchemePath).GetSecretAsync("key"));
        Assert.Equal(unsupportedCipherBytes, File.ReadAllBytes(cipherSchemePath));
        Assert.Equal(preservedCipherKey, File.ReadAllBytes($"{cipherSchemePath}.key"));

        var secretsPath = Path.Combine(tempDirectory, "secrets.json");
        await CreateSecretsStore(secretsPath).SetSecretAsync("key", "secret-value");
        var canonicalBytes = File.ReadAllBytes(secretsPath);
        var keyDocument = JsonNode.Parse(File.ReadAllText($"{secretsPath}.key"))!;
        keyDocument["protection"]!["scheme"] = "future-key-provider";
        File.WriteAllText($"{secretsPath}.key", keyDocument.ToJsonString());
        var unsupportedKeyBytes = File.ReadAllBytes($"{secretsPath}.key");

        await Assert.ThrowsAsync<PackageStorageNotSupportedException>(
            () => CreateSecretsStore(secretsPath).GetSecretAsync("key"));
        Assert.Equal(canonicalBytes, File.ReadAllBytes(secretsPath));
        Assert.Equal(unsupportedKeyBytes, File.ReadAllBytes($"{secretsPath}.key"));
        Assert.Empty(Directory.GetFiles(tempDirectory, "*.corrupt.*"));
    }

    [Fact]
    public async Task JsonPackageSecretsStore_MalformedProtectionMetadataFailsClosed()
    {
        var tempDirectory = CreateTempDirectory();
        var secretsPath = Path.Combine(tempDirectory, "secrets.json");
        File.WriteAllText(secretsPath, """
            {"format":"sunder.package-secrets-encrypted","version":1,"protection":{"scheme":"aes-gcm","version":1}}
            """);

        var exception = await Assert.ThrowsAsync<PackageStorageRecoveryRequiredException>(
            () => CreateSecretsStore(secretsPath).GetSecretAsync("key"));

        AssertFailureMarker(secretsPath, exception.QuarantinePath, "aes-gcm-master-key");
    }

    [Fact]
    public async Task JsonPackageSecretsStore_StructurallyCorruptKeyFailsClosedWithoutMutatingCiphertext()
    {
        var tempDirectory = CreateTempDirectory();
        var secretsPath = Path.Combine(tempDirectory, "secrets.json");
        var store = CreateSecretsStore(secretsPath);
        await store.SetSecretAsync("key", "secret-value");
        var canonicalBytes = File.ReadAllBytes(secretsPath);
        File.WriteAllText($"{secretsPath}.key", "not-a-key-document");

        var first = await Assert.ThrowsAsync<PackageStorageRecoveryRequiredException>(() => store.GetSecretAsync("key"));

        Assert.Equal(canonicalBytes, File.ReadAllBytes(secretsPath));
        AssertFailureMarker($"{secretsPath}.key", first.QuarantinePath, "restricted-raw");
        await Assert.ThrowsAsync<PackageStorageRecoveryRequiredException>(() => store.GetSecretAsync("key"));
    }

    [Fact]
    public async Task JsonPackageSecretsStore_ValidatesNullsAndRevisionOverflowBeforeMutation()
    {
        var secretsPath = Path.Combine(CreateTempDirectory(), "secrets.json");
        var cipher = new PlaintextTestCipher();
        var store = CreateSecretsStore(secretsPath, cipher: cipher);
        await store.SetSecretAsync("key", "value");
        var outer = JsonNode.Parse(File.ReadAllText(secretsPath))!;
        var plaintext = JsonNode.Parse(Convert.FromBase64String(outer["ciphertext"]!.GetValue<string>()))!;
        plaintext["revision"] = long.MaxValue;
        outer["ciphertext"] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(plaintext));
        File.WriteAllText(secretsPath, outer.ToJsonString());
        var overflowDocument = File.ReadAllBytes(secretsPath);

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.GetSecretAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SetSecretAsync(null!, "value"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SetSecretAsync("key", null!));
        Assert.Equal("value", await store.GetSecretAsync("key"));
        await Assert.ThrowsAsync<OverflowException>(() => store.SetSecretAsync("new", "value"));
        await Assert.ThrowsAsync<OverflowException>(() => store.DeleteSecretAsync("key"));
        Assert.Equal(overflowDocument, File.ReadAllBytes(secretsPath));
    }

    [Fact]
    public async Task JsonPackageSecretsStore_KeyAndSecretCommitFaultsRespectCommitOrdering()
    {
        var keyFaultPath = Path.Combine(CreateTempDirectory(), "secrets.json");
        var keyFaultFileSystem = new PhaseFaultFileSystem(StorageCommitPhase.MasterKey);
        var keyFaultStore = CreateSecretsStore(keyFaultPath, keyFaultFileSystem);

        await Assert.ThrowsAsync<IOException>(() => keyFaultStore.SetSecretAsync("key", "value"));
        Assert.False(File.Exists($"{keyFaultPath}.key"));
        Assert.False(File.Exists(keyFaultPath));

        var secretFaultPath = Path.Combine(CreateTempDirectory(), "secrets.json");
        var secretFaultFileSystem = new PhaseFaultFileSystem(StorageCommitPhase.SecretDocument);
        var secretFaultStore = CreateSecretsStore(secretFaultPath, secretFaultFileSystem);

        await Assert.ThrowsAsync<IOException>(() => secretFaultStore.SetSecretAsync("key", "value"));
        Assert.True(File.Exists($"{secretFaultPath}.key"));
        Assert.False(File.Exists(secretFaultPath));
        Assert.Equal(
            [StorageCommitPhase.MasterKey, StorageCommitPhase.SecretDocument],
            secretFaultFileSystem.AttemptedPhases);

        await CreateSecretsStore(secretFaultPath).SetSecretAsync("key", "value");
        Assert.Equal("value", await CreateSecretsStore(secretFaultPath).GetSecretAsync("key"));
    }

    [Fact]
    public void PlatformMasterKeyProtection_SelectsPreferredProvidersAndExplicitFallback()
    {
        var key = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
        var keychain = new FakeExternalMasterKeyStore(MasterKeyProtectionSchemes.MacOsKeychain, available: true);
        var secretService = new FakeExternalMasterKeyStore(
            MasterKeyProtectionSchemes.LinuxSecretService,
            available: true);
        var dpapi = new FakeWindowsDataProtection();

        var macProtection = new PlatformMasterKeyProtection(
            StoragePlatform.MacOs,
            keychain,
            secretService,
            dpapi);
        var macKey = macProtection.Protect(key);
        Assert.Equal(MasterKeyProtectionSchemes.MacOsKeychain, macKey.Scheme);
        Assert.Equal(key, macProtection.Unprotect(macKey.Scheme, macKey.Version, macKey.Payload));

        var linuxProtection = new PlatformMasterKeyProtection(
            StoragePlatform.Linux,
            keychain,
            secretService,
            dpapi);
        var linuxKey = linuxProtection.Protect(key);
        Assert.Equal(MasterKeyProtectionSchemes.LinuxSecretService, linuxKey.Scheme);

        var headlessProtection = new PlatformMasterKeyProtection(
            StoragePlatform.Linux,
            keychain,
            new FakeExternalMasterKeyStore(MasterKeyProtectionSchemes.LinuxSecretService, available: false),
            dpapi);
        var fallbackKey = headlessProtection.Protect(key);
        Assert.Equal(MasterKeyProtectionSchemes.RestrictedFile, fallbackKey.Scheme);
        Assert.Equal(key, fallbackKey.Payload);

        var windowsProtection = new PlatformMasterKeyProtection(
            StoragePlatform.Windows,
            keychain,
            secretService,
            dpapi);
        var windowsKey = windowsProtection.Protect(key);
        Assert.Equal(MasterKeyProtectionSchemes.Dpapi, windowsKey.Scheme);
        Assert.True(windowsProtection.ShouldReprotect(MasterKeyProtectionSchemes.RestrictedFile));
    }

    [Fact]
    public void WindowsDataProtection_CurrentUserRoundTripsWhenRunningOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var entropy = RandomNumberGenerator.GetBytes(32);
        var protection = new WindowsDataProtection();
        var protectedKey = protection.Protect(key, entropy);
        try
        {
            var restored = protection.Unprotect(protectedKey, entropy);
            try
            {
                Assert.Equal(key, restored);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(restored);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(entropy);
            CryptographicOperations.ZeroMemory(protectedKey);
        }
    }

    [Fact]
    public void PlatformMasterKeyProtection_NewProviderFailuresFallBackButExistingLoadsFailClosed()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var throwingStore = new FakeExternalMasterKeyStore(
            MasterKeyProtectionSchemes.LinuxSecretService,
            available: true)
        {
            ThrowOnStore = true,
        };
        var linuxProtection = new PlatformMasterKeyProtection(
            StoragePlatform.Linux,
            null,
            throwingStore,
            null);

        var fallback = linuxProtection.Protect(key);

        Assert.Equal(MasterKeyProtectionSchemes.RestrictedFile, fallback.Scheme);
        Assert.Equal(key, fallback.Payload);
        Assert.Throws<PackageStorageKeyUnavailableException>(() => linuxProtection.Unprotect(
            MasterKeyProtectionSchemes.LinuxSecretService,
            MasterKeyProtectionSchemes.CurrentVersion,
            Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"))));

        var failingDpapi = new FakeWindowsDataProtection { ThrowOnProtect = true, ThrowOnUnprotect = true };
        var windowsProtection = new PlatformMasterKeyProtection(
            StoragePlatform.Windows,
            null,
            null,
            failingDpapi);

        Assert.Equal(MasterKeyProtectionSchemes.RestrictedFile, windowsProtection.Protect(key).Scheme);
        Assert.Throws<PackageStorageKeyUnavailableException>(() => windowsProtection.Unprotect(
            MasterKeyProtectionSchemes.Dpapi,
            MasterKeyProtectionSchemes.CurrentVersion,
            [1, 2, 3]));
    }

    [Fact]
    public void PlatformMasterKeyProtection_RejectsNonUtf8AndNonGuidExternalReferences()
    {
        var store = new FakeExternalMasterKeyStore(
            MasterKeyProtectionSchemes.LinuxSecretService,
            available: true);
        var protection = new PlatformMasterKeyProtection(StoragePlatform.Linux, null, store, null);

        Assert.Throws<PackageStorageKeyUnavailableException>(() => protection.Unprotect(
            MasterKeyProtectionSchemes.LinuxSecretService,
            MasterKeyProtectionSchemes.CurrentVersion,
            [0xFF]));
        Assert.Throws<PackageStorageKeyUnavailableException>(() => protection.Unprotect(
            MasterKeyProtectionSchemes.LinuxSecretService,
            MasterKeyProtectionSchemes.CurrentVersion,
            "not-a-guid"u8.ToArray()));
        Assert.Equal(0, store.LoadCount);
    }

    [Fact]
    public async Task JsonPackageSecretsStore_ProviderProbeAndReprotectionFailureKeepRestrictedKeyReadable()
    {
        var secretsPath = Path.Combine(CreateTempDirectory(), "secrets.json");
        await CreateSecretsStore(secretsPath).SetSecretAsync("key", "value");
        var keyDocument = File.ReadAllBytes($"{secretsPath}.key");
        var protection = new PlatformMasterKeyProtection(
            StoragePlatform.Windows,
            null,
            null,
            new FakeWindowsDataProtection { ThrowOnProtect = true });

        var store = new JsonPackageSecretsStore(secretsPath, null, null, protection);

        Assert.Equal("value", await store.GetSecretAsync("key"));
        Assert.Equal(keyDocument, File.ReadAllBytes($"{secretsPath}.key"));

        var throwingAvailability = new FakeExternalMasterKeyStore(
            MasterKeyProtectionSchemes.LinuxSecretService,
            available: true)
        {
            ThrowOnAvailability = true,
        };
        var probeStore = new JsonPackageSecretsStore(
            secretsPath,
            null,
            null,
            new PlatformMasterKeyProtection(StoragePlatform.Linux, null, throwingAvailability, null));
        Assert.Equal("value", await probeStore.GetSecretAsync("key"));
        Assert.Equal(keyDocument, File.ReadAllBytes($"{secretsPath}.key"));
    }

    [Fact]
    public async Task JsonPackageSecretsStore_OuterReprotectionLockFailureDoesNotFailReadableSecret()
    {
        var secretsPath = Path.Combine(CreateTempDirectory(), "secrets.json");
        await CreateSecretsStore(secretsPath).SetSecretAsync("key", "value");
        var keyDocument = File.ReadAllBytes($"{secretsPath}.key");
        var fileSystem = new FailSecondMasterKeyLockFileSystem();
        var protection = new PlatformMasterKeyProtection(
            StoragePlatform.Windows,
            null,
            null,
            new FakeWindowsDataProtection());
        var store = new JsonPackageSecretsStore(secretsPath, fileSystem, null, protection);

        Assert.Equal("value", await store.GetSecretAsync("key"));
        Assert.Equal(2, fileSystem.MasterKeyLockAttempts);
        Assert.Equal(keyDocument, File.ReadAllBytes($"{secretsPath}.key"));
    }

    [Fact]
    public async Task JsonPackageSecretsStore_WrongLengthExternalKeyIsUnavailableWithoutQuarantine()
    {
        var tempDirectory = CreateTempDirectory();
        var secretsPath = Path.Combine(tempDirectory, "secrets.json");
        var externalStore = new FakeExternalMasterKeyStore(
            MasterKeyProtectionSchemes.MacOsKeychain,
            available: true);
        var protection = new PlatformMasterKeyProtection(StoragePlatform.MacOs, externalStore, null, null);
        var store = new JsonPackageSecretsStore(secretsPath, null, null, protection);
        await store.SetSecretAsync("key", "value");
        var keyDocument = File.ReadAllBytes($"{secretsPath}.key");
        var secretDocument = File.ReadAllBytes(secretsPath);
        externalStore.LoadOverride = new byte[31];

        await Assert.ThrowsAsync<PackageStorageKeyUnavailableException>(() => store.GetSecretAsync("key"));

        Assert.Equal(keyDocument, File.ReadAllBytes($"{secretsPath}.key"));
        Assert.Equal(secretDocument, File.ReadAllBytes(secretsPath));
        Assert.Empty(Directory.GetFiles(tempDirectory, "*.corrupt.*"));
    }

    [Fact]
    public async Task JsonPackageSecretsStore_RollsBackExternalItemWhenMasterKeyCommitFails()
    {
        var secretsPath = Path.Combine(CreateTempDirectory(), "secrets.json");
        var externalStore = new FakeExternalMasterKeyStore(
            MasterKeyProtectionSchemes.MacOsKeychain,
            available: true);
        var protection = new PlatformMasterKeyProtection(StoragePlatform.MacOs, externalStore, null, null);
        var store = new JsonPackageSecretsStore(
            secretsPath,
            new PhaseFaultFileSystem(StorageCommitPhase.MasterKey),
            null,
            protection);

        await Assert.ThrowsAsync<IOException>(() => store.SetSecretAsync("key", "value"));

        Assert.Equal(1, externalStore.StoreCount);
        Assert.Equal(1, externalStore.DeleteCount);
        Assert.Empty(externalStore.Identifiers);
        Assert.False(File.Exists($"{secretsPath}.key"));
    }

    [Fact]
    public async Task JsonPackageSecretsStore_DoesNotDeleteExternalItemAfterUncertainPostReplaceFailure()
    {
        var secretsPath = Path.Combine(CreateTempDirectory(), "secrets.json");
        var externalStore = new FakeExternalMasterKeyStore(
            MasterKeyProtectionSchemes.MacOsKeychain,
            available: true);
        var protection = new PlatformMasterKeyProtection(StoragePlatform.MacOs, externalStore, null, null);
        var store = new JsonPackageSecretsStore(
            secretsPath,
            new OneShotSyncFailureFileSystem(),
            null,
            protection);

        await Assert.ThrowsAsync<IOException>(() => store.SetSecretAsync("key", "value"));

        Assert.True(File.Exists($"{secretsPath}.key"));
        Assert.Equal(0, externalStore.DeleteCount);
        var recovered = new JsonPackageSecretsStore(secretsPath, null, null, protection);
        await recovered.SetSecretAsync("key", "value");
        Assert.Equal("value", await recovered.GetSecretAsync("key"));
    }

    [Fact]
    public void MasterKeyStore_CleansSupersededExternalReferenceAfterSuccessfulReprotection()
    {
        var keyPath = Path.Combine(CreateTempDirectory(), "secrets.json.key");
        var protection = new RotatingMasterKeyProtection();
        var keyStore = new MasterKeyStore(keyPath, new AtomicFileSystem(), protection);
        var first = keyStore.GetOrCreate();
        try
        {
            protection.EnableReprotection = true;
            keyStore.Reprotect(first.Key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(first.Key);
        }

        Assert.Equal(["reference-1"], protection.DeletedReferences);
        using var document = JsonDocument.Parse(File.ReadAllBytes(keyPath));
        Assert.Equal(
            "reference-2",
            Encoding.UTF8.GetString(Convert.FromBase64String(document.RootElement.GetProperty("payload").GetString()!)));
    }

    [Fact]
    public void MacOsKeychainMasterKeyStore_PassesPasswordAfterWAndNeverUsesPromptInput()
    {
        var firstKey = RandomNumberGenerator.GetBytes(32);
        var updatedKey = RandomNumberGenerator.GetBytes(32);
        var runner = new FakeCredentialCommandRunner([
            new CommandResult(0, string.Empty, string.Empty),
            new CommandResult(0, Convert.ToBase64String(firstKey) + Environment.NewLine, string.Empty),
            new CommandResult(0, string.Empty, string.Empty),
            new CommandResult(0, Convert.ToBase64String(updatedKey) + Environment.NewLine, string.Empty),
            new CommandResult(0, string.Empty, string.Empty),
        ]);
        var store = new MacOsKeychainMasterKeyStore(
            runner,
            "/usr/bin/security",
            _ => true,
            isMacOs: true);
        var identifier = Guid.NewGuid().ToString("N");

        Assert.True(store.TryStore(identifier, firstKey));
        Assert.Equal(firstKey, store.Load(identifier));
        Assert.True(store.TryStore(identifier, updatedKey));
        Assert.Equal(updatedKey, store.Load(identifier));
        Assert.True(store.TryDelete(identifier));

        Assert.All(runner.Invocations, invocation =>
        {
            Assert.Equal("/usr/bin/security", invocation.FileName);
            Assert.Null(invocation.StandardInput);
            Assert.Equal(TimeSpan.FromSeconds(5), invocation.Timeout);
            var serviceOption = Assert.Single(
                invocation.Arguments.Select((argument, index) => (argument, index)),
                item => item.argument == "-s");
            Assert.Equal("io.sunder.runtime.v1.package-storage.master-key", invocation.Arguments[serviceOption.index + 1]);
        });
        var addInvocations = runner.Invocations
            .Where(invocation => invocation.Arguments[0] == "add-generic-password")
            .ToArray();
        Assert.Equal(2, addInvocations.Length);
        Assert.All(addInvocations, invocation =>
        {
            Assert.Contains("-U", invocation.Arguments);
            var passwordOption = Assert.Single(
                invocation.Arguments.Select((argument, index) => (argument, index)),
                item => item.argument == "-w");
            Assert.True(passwordOption.index + 1 < invocation.Arguments.Count);
            Assert.False(string.IsNullOrWhiteSpace(invocation.Arguments[passwordOption.index + 1]));
            Assert.Equal(invocation.Arguments.Count - 2, passwordOption.index);
        });
    }

    [Fact]
    public void PlatformMasterKeyProtection_MacOsCommandFailureFallsBackWhileLookupFailsClosed()
    {
        var identifier = Guid.NewGuid().ToString("N");
        var runner = new FakeCredentialCommandRunner([
            new CommandResult(0, "login.keychain-db", string.Empty),
            new CommandResult(1, string.Empty, "store failed"),
            new CommandResult(0, "login.keychain-db", string.Empty),
            new CommandResult(1, string.Empty, "lookup failed"),
        ]);
        var keychain = new MacOsKeychainMasterKeyStore(runner, "/usr/bin/security", _ => true, isMacOs: true);
        var protection = new PlatformMasterKeyProtection(StoragePlatform.MacOs, keychain, null, null);

        Assert.Equal(
            MasterKeyProtectionSchemes.RestrictedFile,
            protection.Protect(RandomNumberGenerator.GetBytes(32)).Scheme);
        Assert.Throws<PackageStorageKeyUnavailableException>(() => protection.Unprotect(
            MasterKeyProtectionSchemes.MacOsKeychain,
            MasterKeyProtectionSchemes.CurrentVersion,
            Encoding.UTF8.GetBytes(identifier)));
    }

    [Fact]
    public void CommandRunner_DrainsBothStreamsAndWritesStandardInput()
    {
        var command = CreateShellCommand(
            "read line; i=0; while [ $i -lt 3000 ]; do printf 'o'; printf 'e' >&2; i=$((i+1)); done; printf -- \":$line\"",
            "set /p line=& for /L %i in (1,1,3000) do @(<nul set /p =o& <nul set /p =e 1>&2)& <nul set /p =:%line%");

        var result = CommandRunner.Run(command.FileName, command.Arguments, "input-value\n");

        Assert.Equal(0, result.ExitCode);
        Assert.EndsWith(":input-value", result.StandardOutput.TrimEnd(), StringComparison.Ordinal);
        Assert.Equal(3000, result.StandardError.TrimEnd().Length);
    }

    [Fact]
    public void CommandRunner_TimesOutKillsWaitsAndReturnsPromptly()
    {
        var command = CreateShellCommand("sleep 30", "ping -n 31 127.0.0.1 >nul");
        var stopwatch = Stopwatch.StartNew();

        Assert.Throws<PackageStorageKeyUnavailableException>(() => CommandRunner.Run(
            command.FileName,
            command.Arguments,
            null,
            TimeSpan.FromMilliseconds(150)));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Timeout took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void CommandRunner_NullInputIsClosedAndCannotReadTerminal()
    {
        var command = CreateShellCommand(
            "if read line; then printf unexpected; else printf closed; fi",
            "set /p line= && (<nul set /p =unexpected) || (<nul set /p =closed)");

        var result = CommandRunner.Run(
            command.FileName,
            command.Arguments,
            null,
            TimeSpan.FromSeconds(2));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("closed", result.StandardOutput.TrimEnd());
    }

    [MacOsExternalKeyStoreFact]
    public void MacOsKeychain_OptInIntegration_AddUpdateLoadDelete()
    {
        var store = new MacOsKeychainMasterKeyStore();
        Assert.True(store.IsAvailable);
        var identifier = Guid.NewGuid().ToString("N");
        var first = RandomNumberGenerator.GetBytes(32);
        var second = RandomNumberGenerator.GetBytes(32);
        try
        {
            Assert.True(store.TryStore(identifier, first));
            Assert.Equal(first, store.Load(identifier));
            Assert.True(store.TryStore(identifier, second));
            Assert.Equal(second, store.Load(identifier));
        }
        finally
        {
            Assert.True(store.TryDelete(identifier));
        }
    }

    [LinuxExternalKeyStoreFact]
    public void LinuxSecretService_OptInIntegration_StoreLoadDelete()
    {
        var store = new LinuxSecretServiceMasterKeyStore();
        Assert.True(store.IsAvailable);
        var identifier = Guid.NewGuid().ToString("N");
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            Assert.True(store.TryStore(identifier, key));
            Assert.Equal(key, store.Load(identifier));
        }
        finally
        {
            Assert.True(store.TryDelete(identifier));
        }
    }

    [Fact]
    public async Task JsonPackageSecretsStore_WindowsReprotectsRestrictedFileKeyBeforeFutureWrites()
    {
        var secretsPath = Path.Combine(CreateTempDirectory(), "secrets.json");
        await CreateSecretsStore(secretsPath).SetSecretAsync("key", "value");
        Assert.Equal(MasterKeyProtectionSchemes.RestrictedFile, ReadKeyScheme($"{secretsPath}.key"));
        var windowsProtection = new PlatformMasterKeyProtection(
            StoragePlatform.Windows,
            null,
            null,
            new FakeWindowsDataProtection());
        var windowsStore = new JsonPackageSecretsStore(secretsPath, null, null, windowsProtection);

        Assert.Equal("value", await windowsStore.GetSecretAsync("key"));
        Assert.Equal(MasterKeyProtectionSchemes.Dpapi, ReadKeyScheme($"{secretsPath}.key"));

        await windowsStore.SetSecretAsync("second", "other-value");
        using var document = JsonDocument.Parse(File.ReadAllBytes(secretsPath));
        Assert.Equal(
            MasterKeyProtectionSchemes.Dpapi,
            document.RootElement.GetProperty("protection").GetProperty("keyScheme").GetString());
    }

    [Fact]
    public async Task JsonPackageSecretsStore_UsesPrivateDirectoryFileLockTempAndQuarantinePermissions()
    {
        var tempDirectory = CreateTempDirectory();
        var secretsPath = Path.Combine(tempDirectory, "secrets.json");
        var store = CreateSecretsStore(secretsPath);
        await store.SetSecretAsync("key", "value");

        AssertPrivateDirectoryModeWhenSupported(tempDirectory);
        AssertPrivateFileModeWhenSupported(secretsPath);
        AssertPrivateFileModeWhenSupported($"{secretsPath}.lock");
        AssertPrivateFileModeWhenSupported($"{secretsPath}.key");
        AssertPrivateFileModeWhenSupported($"{secretsPath}.key.lock");
        Assert.Empty(Directory.GetFiles(tempDirectory, "*.tmp.*"));

        File.WriteAllText(secretsPath, "malformed-secret");
        var exception = await Assert.ThrowsAsync<PackageStorageRecoveryRequiredException>(() => store.GetSecretAsync("key"));
        AssertPrivateFileModeWhenSupported(exception.QuarantinePath);

        if (OperatingSystem.IsWindows())
        {
            AssertCurrentUserOnlyWindowsAcl(tempDirectory, directory: true);
            AssertCurrentUserOnlyWindowsAcl(exception.QuarantinePath, directory: false);
        }
    }

    private static JsonPackageSecretsStore CreateSecretsStore(
        string path,
        AtomicFileSystem? fileSystem = null,
        ISecretCipher? cipher = null) =>
        new(path, fileSystem, cipher, new RestrictedFileMasterKeyProtection());

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ReadKeyScheme(string keyPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(keyPath));
        return document.RootElement.GetProperty("protection").GetProperty("scheme").GetString()!;
    }

    private static ShellCommand CreateShellCommand(string unixCommand, string windowsCommand) => OperatingSystem.IsWindows()
        ? new ShellCommand("cmd.exe", ["/d", "/s", "/c", windowsCommand])
        : new ShellCommand("/bin/sh", ["-c", unixCommand]);

    private static void AssertFailureMarker(
        string canonicalPath,
        string quarantinePath,
        string? quarantineProtection = null)
    {
        using var marker = JsonDocument.Parse(File.ReadAllBytes(canonicalPath));
        Assert.Equal(StorageFailureMarker.FormatName, marker.RootElement.GetProperty("format").GetString());
        Assert.Equal(Path.GetFileName(quarantinePath), marker.RootElement.GetProperty("quarantineFile").GetString());
        if (quarantineProtection is not null)
        {
            Assert.Equal(
                quarantineProtection,
                marker.RootElement.GetProperty("quarantineProtection").GetString());
        }
    }

    private static Process StartLockChildProcess(string lockPath, string readyPath, string releasePath)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("vstest");
        startInfo.ArgumentList.Add(typeof(PackageStorageServicesTests).Assembly.Location);
        startInfo.ArgumentList.Add(
            "--Tests:Sunder.Runtime.Host.Tests.PackageStorageServicesTests.JsonPackageKeyValueStore_ChildProcessLockHolder");
        startInfo.ArgumentList.Add("--Logger:console;Verbosity=minimal");
        startInfo.Environment[ChildLockPathEnvironmentVariable] = lockPath;
        startInfo.Environment[ChildReadyPathEnvironmentVariable] = readyPath;
        startInfo.Environment[ChildReleasePathEnvironmentVariable] = releasePath;
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start storage-lock child process.");
    }

    private static async Task WaitForFileAsync(string path, Process child, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!File.Exists(path) && DateTimeOffset.UtcNow < deadline && !child.HasExited)
        {
            await Task.Delay(25);
        }

        if (!File.Exists(path))
        {
            var output = await child.StandardOutput.ReadToEndAsync();
            var error = await child.StandardError.ReadToEndAsync();
            throw new Xunit.Sdk.XunitException(
                $"Storage-lock child did not become ready. Exit={child.ExitCode}; Output={output}; Error={error}");
        }
    }

    private static void AssertPrivateFileModeWhenSupported(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    private static void AssertPrivateDirectoryModeWhenSupported(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(path));
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AssertCurrentUserOnlyWindowsAcl(string path, bool directory)
    {
        var currentUser = WindowsIdentity.GetCurrent().User!;
        FileSystemSecurity security = directory
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        var accessRules = rules.Cast<FileSystemAccessRule>().ToArray();
        Assert.NotEmpty(accessRules);
        Assert.All(accessRules, rule =>
        {
            Assert.Equal(currentUser, rule.IdentityReference);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.False(rule.IsInherited);
        });
    }

    private sealed class TruncatingWriteFileSystem : AtomicFileSystem
    {
        internal override void WriteTempFile(string path, byte[] contents, bool sensitive)
        {
            base.WriteTempFile(path, contents[..Math.Max(1, contents.Length / 2)], sensitive);
            throw new IOException("Injected failure after a truncated temp write.");
        }
    }

    private sealed class CancelAfterReplaceFileSystem(CancellationTokenSource cancellation) : AtomicFileSystem
    {
        internal override void ReplaceFile(string sourcePath, string destinationPath)
        {
            base.ReplaceFile(sourcePath, destinationPath);
            cancellation.Cancel();
        }
    }

    private sealed class PhaseFaultFileSystem(StorageCommitPhase faultPhase) : AtomicFileSystem
    {
        private bool _faulted;

        internal List<StorageCommitPhase> AttemptedPhases { get; } = [];

        internal override void BeforeCommit(StorageCommitPhase phase, string destinationPath)
        {
            AttemptedPhases.Add(phase);
            if (!_faulted && phase == faultPhase)
            {
                _faulted = true;
                throw new IOException($"Injected {phase} commit failure.");
            }
        }
    }

    private sealed class OneShotSyncFailureFileSystem : AtomicFileSystem
    {
        private bool _failed;

        internal override void SyncDirectory(string path)
        {
            if (!_failed)
            {
                _failed = true;
                throw new IOException("Injected directory synchronization failure after replacement.");
            }

            base.SyncDirectory(path);
        }
    }

    private sealed class FailSecondMasterKeyLockFileSystem : AtomicFileSystem
    {
        internal int MasterKeyLockAttempts { get; private set; }

        internal override IDisposable OpenExclusiveLock(string path, bool sensitive)
        {
            if (path.EndsWith(".key.lock", StringComparison.Ordinal))
            {
                MasterKeyLockAttempts++;
                if (MasterKeyLockAttempts == 2)
                {
                    throw new UnauthorizedAccessException("Injected outer reprotection lock failure.");
                }
            }

            return base.OpenExclusiveLock(path, sensitive);
        }
    }

    private sealed class PlaintextTestCipher : ISecretCipher
    {
        public SecretCiphertext Encrypt(byte[] key, byte[] plaintext, byte[] associatedData) =>
            new(new byte[AesGcmSecretCipher.NonceSize], plaintext.ToArray(), new byte[AesGcmSecretCipher.TagSize]);

        public byte[] Decrypt(
            byte[] key,
            byte[] nonce,
            byte[] ciphertext,
            byte[] tag,
            byte[] associatedData) => ciphertext.ToArray();
    }

    private sealed class AlwaysUnavailableMasterKeyProtection : IMasterKeyProtection
    {
        public ProtectedMasterKey Protect(byte[] masterKey) =>
            throw new PackageStorageKeyUnavailableException("Injected key-provider unavailability.");

        public byte[] Unprotect(string scheme, int version, byte[] payload) =>
            throw new PackageStorageKeyUnavailableException("Injected key-provider unavailability.");

        public bool ShouldReprotect(string scheme) => false;

        public bool TryDelete(string scheme, int version, byte[] payload) => false;
    }

    private sealed class FakeExternalMasterKeyStore(string scheme, bool available) : IExternalMasterKeyStore
    {
        private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

        public string Scheme { get; } = scheme;

        public bool ThrowOnAvailability { get; init; }

        public bool ThrowOnStore { get; init; }

        public byte[]? LoadOverride { get; set; }

        public int StoreCount { get; private set; }

        public int LoadCount { get; private set; }

        public int DeleteCount { get; private set; }

        public IReadOnlyCollection<string> Identifiers => _keys.Keys;

        public bool IsAvailable => ThrowOnAvailability
            ? throw new IOException("Injected provider availability failure.")
            : available;

        public bool TryStore(string identifier, byte[] key)
        {
            StoreCount++;
            if (ThrowOnStore)
            {
                throw new IOException("Injected external store failure.");
            }

            if (!IsAvailable)
            {
                return false;
            }

            _keys[identifier] = key.ToArray();
            return true;
        }

        public byte[] Load(string identifier)
        {
            LoadCount++;
            if (LoadOverride is not null)
            {
                return LoadOverride.ToArray();
            }

            return _keys.TryGetValue(identifier, out var key)
                ? key.ToArray()
                : throw new PackageStorageKeyUnavailableException("The fake external key is unavailable.");
        }

        public bool TryDelete(string identifier)
        {
            DeleteCount++;
            return _keys.Remove(identifier);
        }
    }

    private sealed class FakeWindowsDataProtection : IWindowsDataProtection
    {
        public bool ThrowOnProtect { get; init; }

        public bool ThrowOnUnprotect { get; init; }

        public byte[] Protect(byte[] key, byte[] entropy) => ThrowOnProtect
            ? throw new CryptographicException("Injected DPAPI protect failure.")
            : key.Select(value => (byte)(value ^ 0xA5)).ToArray();

        public byte[] Unprotect(byte[] protectedKey, byte[] entropy) => ThrowOnUnprotect
            ? throw new CryptographicException("Injected DPAPI unprotect failure.")
            : protectedKey.Select(value => (byte)(value ^ 0xA5)).ToArray();
    }

    private sealed class RotatingMasterKeyProtection : IMasterKeyProtection
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        private int _reference;

        public bool EnableReprotection { get; set; }

        public List<string> DeletedReferences { get; } = [];

        public ProtectedMasterKey Protect(byte[] masterKey)
        {
            masterKey.CopyTo(_key, 0);
            var reference = $"reference-{++_reference}";
            return new ProtectedMasterKey(
                MasterKeyProtectionSchemes.MacOsKeychain,
                MasterKeyProtectionSchemes.CurrentVersion,
                Encoding.UTF8.GetBytes(reference));
        }

        public byte[] Unprotect(string scheme, int version, byte[] payload) => _key.ToArray();

        public bool ShouldReprotect(string scheme) => EnableReprotection;

        public bool TryDelete(string scheme, int version, byte[] payload)
        {
            DeletedReferences.Add(Encoding.UTF8.GetString(payload));
            return true;
        }
    }

    private sealed class FakeCredentialCommandRunner(IEnumerable<CommandResult> results) : ICredentialCommandRunner
    {
        private readonly Queue<CommandResult> _results = new(results);

        public List<CommandInvocation> Invocations { get; } = [];

        public CommandResult Run(
            string fileName,
            IReadOnlyList<string> arguments,
            string? standardInput,
            TimeSpan timeout)
        {
            Invocations.Add(new CommandInvocation(fileName, arguments.ToArray(), standardInput, timeout));
            return _results.Count > 0
                ? _results.Dequeue()
                : throw new InvalidOperationException("No fake command result was configured.");
        }
    }

    private sealed record CommandInvocation(
        string FileName,
        IReadOnlyList<string> Arguments,
        string? StandardInput,
        TimeSpan Timeout);

    private sealed class CancelAfterFirstReadStream(
        byte[] contents,
        CancellationTokenSource cancellation) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => contents.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (_position >= contents.Length)
            {
                return 0;
            }

            var count = Math.Min(buffer.Length, contents.Length - _position);
            contents.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            cancellation.Cancel();
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class MacOsExternalKeyStoreFactAttribute : FactAttribute
    {
        public MacOsExternalKeyStoreFactAttribute()
        {
            if (!OperatingSystem.IsMacOS()
                || !string.Equals(
                    Environment.GetEnvironmentVariable("SUNDER_RUN_EXTERNAL_KEYSTORE_TESTS"),
                    "1",
                    StringComparison.Ordinal))
            {
                Skip = "Set SUNDER_RUN_EXTERNAL_KEYSTORE_TESTS=1 on macOS to permit Keychain mutation.";
            }
        }
    }

    private sealed class LinuxExternalKeyStoreFactAttribute : FactAttribute
    {
        public LinuxExternalKeyStoreFactAttribute()
        {
            if (!OperatingSystem.IsLinux()
                || !string.Equals(
                    Environment.GetEnvironmentVariable("SUNDER_RUN_EXTERNAL_KEYSTORE_TESTS"),
                    "1",
                    StringComparison.Ordinal))
            {
                Skip = "Set SUNDER_RUN_EXTERNAL_KEYSTORE_TESTS=1 on Linux to permit Secret Service mutation.";
            }
        }
    }

    private sealed record ShellCommand(string FileName, IReadOnlyList<string> Arguments);
}
