using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Package.Projections;
using Sunder.Runtime.Client;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

[Collection(RuntimeProcessIntegrationCollection.Name)]
public sealed class RuntimeResetAcceptanceTests
{
    private const string PackageId = "startup.counted.package";
    private const string PackageVersion = "1.0.0";
    private const string SecretKey = "reset.acceptance.secret.v1";

    [Fact]
    public async Task Reset_removes_execution_runtime_state_and_preserves_data_credentials_and_user_files()
    {
        var root = CreateTempDirectory();
        var runtimeRoot = Path.Combine(root, "runtime");
        RuntimeLocalState.EnsureInitialized(runtimeRoot);
        var paths = new RuntimePackagePaths(runtimeRoot);
        var packageDataSentinel = Path.Combine(paths.PackageDataRootPath, PackageId, "data", "settings-secrets-workspace.db");
        var credentialsSentinel = Path.Combine(paths.RegistryCredentialRootPath, "credentials.enc.json");
        var userFile = Path.Combine(root, "workspace", "user-file.txt");
        WriteSentinel(packageDataSentinel, "package-data");
        WriteSentinel(credentialsSentinel, "credentials");
        Directory.CreateDirectory(paths.CatalogRootPath);
        File.WriteAllText(paths.RpcPermissionFilePath, "capability-decisions");
        WriteSentinel(userFile, "user-file");
        CreateExecutionRuntimeDerivedState(paths);
        var policySentinel = Path.Combine(paths.RootPath, RuntimeV1StateDescriptor.PackagePolicyDirectory, new string('a', 64) + ".json");
        WriteSentinel(policySentinel, "disposable-policy");
        var supervisionSentinel = Path.Combine(paths.RootPath, RuntimeV1StateDescriptor.AppPolicySessionsDirectory, new string('b', 32) + ".json");
        WriteSentinel(supervisionSentinel, "retained-loaded-process-evidence");

        var previousStateRoot = Environment.GetEnvironmentVariable("SUNDER_RUNTIME_STATE_ROOT");
        RuntimeV1ResetResult reset;
        try
        {
            Environment.SetEnvironmentVariable("SUNDER_RUNTIME_STATE_ROOT", runtimeRoot);
            reset = await RuntimeV1StateReset.ResetAsync(TimeSpan.Zero);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SUNDER_RUNTIME_STATE_ROOT", previousStateRoot);
        }

        Assert.True(reset.Success);
        var runtimeReset = Assert.Single(reset.Categories, category => category.Category == "execution-runtime-derived-state");
        Assert.Equal(
            [
                RuntimeV1StateDescriptor.ExecutionRuntimeArtifactsDirectory,
                RuntimeV1StateDescriptor.ExecutionRuntimeRootsDirectory,
                RuntimeV1StateDescriptor.ExecutionRuntimeReceiptsDirectory,
                RuntimeV1StateDescriptor.ExecutionRuntimeHealthDirectory,
                RuntimeV1StateDescriptor.ExecutionRuntimeStagingDirectory,
                RuntimeV1StateDescriptor.ExecutionRuntimeTransactionsDirectory,
                RuntimeV1StateDescriptor.ExecutionRuntimeQuarantineDirectory,
                RuntimeV1StateDescriptor.ExecutionRuntimeLeasesDirectory,
                RuntimeV1StateDescriptor.ExecutionRuntimeTombstonesDirectory,
            ],
            runtimeReset.RelativePaths);
        Assert.False(Directory.EnumerateFileSystemEntries(paths.ExecutionRuntimeStoreRootPath).Any());
        Assert.False(File.Exists(policySentinel));
        Assert.Equal("retained-loaded-process-evidence", File.ReadAllText(supervisionSentinel));
        Assert.Equal("package-data", File.ReadAllText(packageDataSentinel));
        Assert.Equal("credentials", File.ReadAllText(credentialsSentinel));
        Assert.Equal("capability-decisions", File.ReadAllText(paths.RpcPermissionFilePath));
        Assert.Equal("user-file", File.ReadAllText(userFile));
    }

    [Fact]
    public async Task Reset_preserves_package_data_across_reinstall_and_reactivation()
    {
        var root = CreateTempDirectory();
        var runtimeRoot = Path.Combine(root, "runtime");
        var sourceRoot = Path.Combine(root, "source");
        RuntimeLocalState.EnsureInitialized(runtimeRoot);
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            sourceRoot,
            PackageId,
            PackageVersion,
            typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
            roles: [SunderPackageFormat.RuntimeHostRole],
            runtimeRequiredHostCapabilities:
            [
                "sdk-baseline-1-1.v1",
                "core.v1",
                "execution-runtime.v1",
                "rpc.v1",
                "worker-protocol.v1",
            ]);
        File.WriteAllText(Path.Combine(sourceRoot, "payload", "shared", "mode.txt"), "v1-package-data");
        CanonicalPackageTestBuilder.WriteContentIndex(sourceRoot);
        var archivePath = await CanonicalPackageTestBuilder.CreateArchiveAsync(sourceRoot);
        var paths = new RuntimePackagePaths(runtimeRoot);
        var packageDataPath = Path.Combine(paths.PackageDataRootPath, PackageId);
        RuntimePackageSessionTestHost? host = null;
        var hostShutdown = false;
        var packageDataCleanupSafe = false;
        var previousStateRoot = Environment.GetEnvironmentVariable("SUNDER_RUNTIME_STATE_ROOT");
        try
        {
            host = CreateHost(paths);
            await host.InitializeAsync();
            var firstInstall = await host.InstallPackageFromRuntimePathAsync(archivePath);
            AssertSuccessfulInstall(firstInstall);
            var firstLoaded = Assert.Single(host.ActiveSession.LoadedPackageMap).Value;
            var firstWorker = Assert.IsType<ProcessRuntimeWorker>(firstLoaded.Worker);
            using (await WaitForWorkerStatusAsync(firstWorker))
            {
            }
            var firstActivationId = firstLoaded.RuntimeActivationId;
            Assert.NotEqual(Guid.Empty, firstActivationId);

            Assert.True(await host.SetPackageStateAsync(PackageId, "reset.acceptance.data.v1", "data-sentinel"));
            Assert.True(await host.SetPackageSettingAsync(PackageId, "enabled", "false"));
            Assert.True(await host.SetPackageSecretAsync(PackageId, SecretKey, "secret-sentinel"));
            var workspacePath = firstWorker.WorkspaceRootPath;
            Assert.False(string.IsNullOrWhiteSpace(workspacePath));
            var workspaceSentinel = Path.Combine(workspacePath, "reset-acceptance.txt");
            File.WriteAllText(workspaceSentinel, "workspace-sentinel");
            await AssertSentinelsAsync(host, workspaceSentinel);
            Assert.Equal(
                ["1.0.0:configure", "1.0.0:register", "1.0.0:start"],
                File.ReadAllLines(Path.Combine(workspacePath, "activation-counters.txt")));

            await host.ShutdownAsync();
            hostShutdown = true;
            var beforeReset = SnapshotFiles(packageDataPath);
            var credentialsSentinel = Path.Combine(paths.RegistryCredentialRootPath, "preserved.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(credentialsSentinel)!);
            File.WriteAllText(credentialsSentinel, "credential-sentinel");
            Directory.CreateDirectory(paths.CatalogRootPath);
            var rpcPermissionSentinel = JsonSerializer.Serialize(new
            {
                SchemaVersion = 2,
                Revision = 0,
                Permissions = Array.Empty<object>(),
            });
            File.WriteAllText(paths.RpcPermissionFilePath, rpcPermissionSentinel);
            var userFile = Path.Combine(root, "user-file.txt");
            File.WriteAllText(userFile, "user-file-sentinel");
            CreateExecutionRuntimeDerivedState(paths);

            Environment.SetEnvironmentVariable("SUNDER_RUNTIME_STATE_ROOT", runtimeRoot);
            var reset = await RuntimeV1StateReset.ResetAsync(TimeSpan.Zero);

            Assert.True(reset.Success);
            Assert.Contains(
                reset.Categories.SelectMany(static category => category.RelativePaths ?? []),
                path => path == $"catalog/installed-packages.json");
            Assert.Contains(
                reset.Categories.SelectMany(static category => category.RelativePaths ?? []),
                path => path == $"installed/{PackageId}/{PackageVersion}");
            Assert.DoesNotContain(
                reset.Categories.SelectMany(static category => category.RelativePaths ?? []),
                path => path.StartsWith("package-data/", StringComparison.Ordinal));
            Assert.False(File.Exists(paths.StateFilePath));
            Assert.False(Directory.Exists(paths.GetInstalledPackagePath(PackageId, PackageVersion)));
            Assert.False(Directory.Exists(paths.ExecutionRuntimeArtifactRootPath));
            Assert.False(Directory.Exists(paths.ExecutionRuntimeRootsPath));
            Assert.False(Directory.Exists(paths.ExecutionRuntimeTransactionRootPath));
            Assert.False(Directory.Exists(paths.ExecutionRuntimeQuarantineRootPath));
            Assert.False(Directory.Exists(paths.ExecutionRuntimeLeaseRootPath));
            Assert.False(Directory.Exists(paths.ExecutionRuntimeTombstoneRootPath));
            Assert.Equal("credential-sentinel", File.ReadAllText(credentialsSentinel));
            Assert.Equal(rpcPermissionSentinel, File.ReadAllText(paths.RpcPermissionFilePath));
            Assert.Equal("user-file-sentinel", File.ReadAllText(userFile));
            Assert.False(string.IsNullOrWhiteSpace(reset.ReceiptRelativePath));
            Assert.True(File.Exists(Path.Combine(runtimeRoot, reset.ReceiptRelativePath)));
            AssertSnapshotsEqual(beforeReset, SnapshotFiles(packageDataPath));

            host = CreateHost(new RuntimePackagePaths(runtimeRoot));
            hostShutdown = false;
            await host.InitializeAsync();
            Assert.Empty(await host.GetInstalledPackagesAsync());
            var secondInstall = await host.InstallPackageFromRuntimePathAsync(archivePath);
            AssertSuccessfulInstall(secondInstall);
            var secondLoaded = Assert.Single(host.ActiveSession.LoadedPackageMap).Value;
            var secondWorker = Assert.IsType<ProcessRuntimeWorker>(secondLoaded.Worker);
            using var secondStatus = await WaitForWorkerStatusAsync(secondWorker);

            Assert.NotEqual(firstActivationId, secondLoaded.RuntimeActivationId);
            Assert.False(secondStatus.RootElement.GetProperty("storedWasNull").GetBoolean());
            Assert.Equal("false", secondStatus.RootElement.GetProperty("effective").GetString());
            Assert.Equal(workspacePath, secondWorker.WorkspaceRootPath);
            await AssertSentinelsAsync(host, workspaceSentinel);
            Assert.Equal(
                [
                    "1.0.0:configure", "1.0.0:register", "1.0.0:start",
                    "1.0.0:configure", "1.0.0:register", "1.0.0:start",
                ],
                File.ReadAllLines(Path.Combine(workspacePath, "activation-counters.txt")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SUNDER_RUNTIME_STATE_ROOT", previousStateRoot);
            try
            {
                if (host is not null && !hostShutdown) await host.ShutdownAsync();
                var projectionCache = new PackageProjectionCache(runtimeRoot);
                if (Directory.Exists(projectionCache.RootPath))
                {
                    await using var reset = await projectionCache.AcquireOfflineResetScopeAsync();
                    await reset.ValidateAsync();
                    await reset.PrepareForDeletionAsync();
                    Directory.Delete(projectionCache.RootPath, recursive: true);
                }
            }
            finally
            {
                try
                {
                    await CleanupAcceptanceSecretAsync(packageDataPath);
                    packageDataCleanupSafe = true;
                }
                finally
                {
                    try
                    {
                        if (File.Exists(archivePath)) File.Delete(archivePath);
                    }
                    finally
                    {
                        if (packageDataCleanupSafe && Directory.Exists(root))
                        {
                            Directory.Delete(root, recursive: true);
                        }
                    }
                }
            }
        }
    }

    private static RuntimePackageSessionTestHost CreateHost(RuntimePackagePaths paths)
        => new(
            NullLogger<RuntimePackageSessionTestHost>.Instance,
            new InstalledPackageStore(paths),
            new SunderPackageArchiveInstaller(paths),
            lifecyclePolicy: new RuntimeLifecyclePolicyOptions
            {
                PackageWorkerStartupTimeout = TimeSpan.FromSeconds(30),
                PackageRuntimeGenerationActivationTimeout = TimeSpan.FromSeconds(30),
            });

    private static void CreateExecutionRuntimeDerivedState(RuntimePackagePaths paths)
    {
        var hash = new string('a', 64);
        var executionRuntimeRoot = Path.Combine(paths.ExecutionRuntimeRootsPath, hash);
        var executionRuntimeLauncher = Path.Combine(executionRuntimeRoot, "dotnet");
        WriteSentinel(new ExecutionRuntimeStoreLayout(paths).GetArchivePath(hash));
        WriteSentinel(executionRuntimeLauncher);
        ExecutionRuntimeRootFileSystem.Seal(executionRuntimeRoot, executionRuntimeLauncher);
        WriteSentinel(Path.Combine(paths.ExecutionRuntimeReceiptRootPath, "runtime", "receipt.json"));
        WriteSentinel(Path.Combine(paths.ExecutionRuntimeHealthRootPath, $"{hash}.json"));
        WriteSentinel(Path.Combine(paths.ExecutionRuntimeStagingRootPath, Guid.NewGuid().ToString("N"), "content"));
        WriteSentinel(Path.Combine(paths.ExecutionRuntimeTransactionRootPath, Guid.NewGuid().ToString("N"), "journal.json"));
        WriteSentinel(Path.Combine(paths.ExecutionRuntimeQuarantineRootPath, Guid.NewGuid().ToString("N"), "content"));
        WriteSentinel(Path.Combine(paths.ExecutionRuntimeLeaseRootPath, $"{Guid.NewGuid():N}.json"));
        WriteSentinel(Path.Combine(paths.ExecutionRuntimeTombstoneRootPath, Guid.NewGuid().ToString("N"), "receipt.json"));
    }

    private static void WriteSentinel(string path, string content = "derived")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void AssertSuccessfulInstall(Sunder.Runtime.Contracts.PackageOperationResult result)
    {
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.True(result.StoreCommitted);
        Assert.True(result.RuntimeSessionApplied);
    }

    private static async Task AssertSentinelsAsync(
        RuntimePackageSessionTestHost host,
        string workspaceSentinel)
    {
        var state = await host.GetPackageStateAsync(PackageId, "reset.acceptance.data.v1");
        Assert.True(state is { Found: true, Value: "data-sentinel" });
        var setting = await host.GetPackageSettingAsync(PackageId, "enabled");
        Assert.True(setting is { IsStored: true, StoredValue: "false", EffectiveValue: "false" });
        var secret = await host.GetPackageSecretAsync(PackageId, SecretKey);
        Assert.True(secret is { Found: true, Value: "secret-sentinel" });
        Assert.Equal("workspace-sentinel", File.ReadAllText(workspaceSentinel));
    }

    private static async Task CleanupAcceptanceSecretAsync(string packageDataPath)
    {
        var secretsPath = Path.Combine(packageDataPath, "data", "secrets.json");
        try
        {
            if (!File.Exists(secretsPath)) return;

            var secrets = new JsonPackageSecretsStore(secretsPath);
            await secrets.DeleteSecretAsync(SecretKey);
            Assert.Null(await secrets.GetSecretAsync(SecretKey));
        }
        finally
        {
            DeleteExternalMasterKey($"{secretsPath}.key");
        }
    }

    private static void DeleteExternalMasterKey(string keyPath)
    {
        if (!File.Exists(keyPath)) return;

        byte[]? keyDocument = null;
        byte[]? payload = null;
        try
        {
            keyDocument = File.ReadAllBytes(keyPath);
            using var document = JsonDocument.Parse(keyDocument);
            var protection = document.RootElement.GetProperty("protection");
            var scheme = protection.GetProperty("scheme").GetString();
            if (scheme is not (MasterKeyProtectionSchemes.MacOsKeychain
                or MasterKeyProtectionSchemes.LinuxSecretService))
            {
                return;
            }

            var version = protection.GetProperty("version").GetInt32();
            payload = Convert.FromBase64String(document.RootElement.GetProperty("payload").GetString()!);
            var platformProtection = new PlatformMasterKeyProtection();
            var deleted = platformProtection.TryDelete(scheme, version, payload);
            PackageStorageKeyUnavailableException? lookupFailure = null;
            byte[]? unexpectedKey = null;
            try
            {
                unexpectedKey = platformProtection.Unprotect(scheme, version, payload);
            }
            catch (PackageStorageKeyUnavailableException exception)
            {
                lookupFailure = exception;
            }
            finally
            {
                if (unexpectedKey is not null) CryptographicOperations.ZeroMemory(unexpectedKey);
            }

            Assert.True(deleted, $"Failed to delete the acceptance fixture's '{scheme}' master-key entry.");
            Assert.NotNull(lookupFailure);
        }
        finally
        {
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
            if (keyDocument is not null) CryptographicOperations.ZeroMemory(keyDocument);
        }
    }

    private static async Task<JsonDocument> WaitForWorkerStatusAsync(ProcessRuntimeWorker worker)
    {
        var path = Path.Combine(worker.WorkerTemporaryPath, "v1-package-data.json");
        var signal = FileSignal.WaitForAsync(
            path,
            TimeSpan.FromSeconds(10),
            () => $"generationCompleted={worker.GenerationCompletion.IsCompleted}; temporaryRoot={worker.WorkerTemporaryPath}");
        var completed = await Task.WhenAny(signal, worker.GenerationCompletion);
        if (completed == worker.GenerationCompletion && !File.Exists(path))
        {
            await worker.GenerationCompletion;
            throw new InvalidOperationException("The Worker stopped before writing its package-data status.");
        }
        await signal;
        return JsonDocument.Parse(await File.ReadAllBytesAsync(path));
    }

    private static IReadOnlyDictionary<string, byte[]> SnapshotFiles(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path).Replace('\\', '/'),
                File.ReadAllBytes,
                StringComparer.Ordinal);

    private static void AssertSnapshotsEqual(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var path in expected.Keys) Assert.Equal(expected[path], actual[path]);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            ResolvePhysicalDirectory(Path.GetTempPath()),
            "SunderRuntimeResetAcceptanceTests",
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
