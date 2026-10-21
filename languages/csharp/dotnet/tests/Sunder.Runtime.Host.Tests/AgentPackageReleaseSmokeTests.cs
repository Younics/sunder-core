using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sunder.Core.Tests;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

[Collection(RuntimeProcessIntegrationCollection.Name)]
public sealed class AgentPackageReleaseSmokeTests
{
    private const string AgentPackageId = "younics.ai.agent";
    private const string SemanticMemoryPackageId = "younics.ai.agent.memory.semantic";
    private const string ShellPackageId = "younics.ai.agent.tools.shell";
    private static readonly string[] ShellProviderIds =
    [
        "shell.permissions",
        "shell.prompt.context",
        "shell.tools",
    ];

    [ExternalArtifactFact("SUNDER_REQUIRE_AGENT_PACKAGE_RELEASE", "SUNDER_AGENT_PACKAGE_ARCHIVE_DIR")]
    [Trait("Category", "ExternalArtifact")]
    public async Task ReleaseArchives_InstallActivateUnloadReloadAndReinstallAsOneFamily()
    {
        var archiveDirectory = Environment.GetEnvironmentVariable("SUNDER_AGENT_PACKAGE_ARCHIVE_DIR");
        Assert.False(string.IsNullOrWhiteSpace(archiveDirectory), "SUNDER_AGENT_PACKAGE_ARCHIVE_DIR is required by the selected artifact lane.");

        var archives = Directory.GetFiles(Path.GetFullPath(archiveDirectory!), "*.sunderpkg")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var baseline = AgentCurrentBaseline.Load();
        Assert.Equal(baseline.Packages.Select(package => package.PackageId).Order(StringComparer.Ordinal),
            archives.Select(baseline.VerifyArchive).Order(StringComparer.Ordinal));

        var root = Path.Combine(Path.GetTempPath(), "sunder-agent-release-smoke", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(root);
        var store = new InstalledPackageStore(paths);
        var logger = new ReleaseSmokeLogger();
        var host = new RuntimePackageSessionTestHost(
            logger,
            store,
            new SunderPackageArchiveInstaller(paths),
            lifecyclePolicy: new RuntimeLifecyclePolicyOptions
            {
                PackageWorkerStartupTimeout = TimeSpan.FromMinutes(2),
                PackageRuntimeGenerationActivationTimeout = TimeSpan.FromMinutes(2),
            });
        var observedWorkerProcessIds = new HashSet<int>();

        try
        {
            await host.InitializeAsync();
            logger.Clear();
            var install = await host.ApplyPackageStoreMutationsAsync(archives
                .Select(path => new PackageStoreMutation(
                    PackageStoreMutationKind.Install,
                    ArchiveFilePath: path,
                    CapabilityConsent: ReadSensitiveCapabilityConsent(path)))
                .ToArray());
            Assert.True(install.Success, logger.Describe("Install family", install.Errors));

            var installed = await host.GetInstalledPackagesAsync();
            Assert.Equal(archives.Length, installed.Count);
            VerifyReleasedSqliteBytes(archives, installed, paths);
            logger.Clear();
            var activation = await host.LoadInstalledPackagesAsync();
            Assert.True(activation.Success, logger.Describe("Activate family", activation.Errors));
            Assert.Equal(archives.Length, host.GetActivePackages().Count);
            var initialWorkerProcessIds = GetWorkerProcessIds(host);
            Assert.Equal(archives.Length, initialWorkerProcessIds.Length);
            observedWorkerProcessIds.UnionWith(initialWorkerProcessIds);
            Assert.Equal(baseline.AppPackageIds, host.GetActivePackageUiSnapshots().Select(snapshot => snapshot.PackageId).Order(StringComparer.Ordinal));
            baseline.VerifyProviders(host.RpcCatalog.GetSnapshot().Providers);

            var agentCatalogProvider = Assert.Single(
                host.RpcCatalog.GetSnapshot().Providers,
                provider => provider.PackageId == AgentPackageId
                            && provider.ProviderId == "agent.runtime.catalog");
            Assert.Equal("younics.ai.agent.runtime.catalog", agentCatalogProvider.ContractId);
            Assert.Equal("1.0.0", agentCatalogProvider.ContractVersion);
            Assert.Equal(SunderRpcProviderState.Active, agentCatalogProvider.State);

            var shell = Assert.Single(host.GetActivePackages(), package => package.PackageId == ShellPackageId);
            Assert.Equal(PackageReadinessState.Ready, shell.Readiness);
            var shellProviders = host.RpcCatalog.GetSnapshot().Providers
                .Where(provider => provider.PackageId == ShellPackageId)
                .ToArray();
            Assert.Equal(ShellProviderIds, shellProviders.Select(provider => provider.ProviderId));
            Assert.All(shellProviders, provider => Assert.Equal(SunderRpcProviderState.Active, provider.State));

            logger.Clear();
            var unload = await host.ApplyPackageStoreMutationsAsync(installed
                .OrderByDescending(static package => package.PackageId, StringComparer.Ordinal)
                .Select(package => new PackageStoreMutation(PackageStoreMutationKind.Disable, package.PackageId))
                .ToArray());
            Assert.True(unload.Success, logger.Describe("Unload family", unload.Errors));
            Assert.Empty(host.GetActivePackages());
            await AssertProcessesStoppedAsync(initialWorkerProcessIds);

            logger.Clear();
            var reload = await host.ApplyPackageStoreMutationsAsync(installed
                .OrderBy(static package => package.PackageId, StringComparer.Ordinal)
                .Select(package => new PackageStoreMutation(PackageStoreMutationKind.Enable, package.PackageId))
                .ToArray());
            Assert.True(reload.Success, logger.Describe("Reload family", reload.Errors));
            Assert.Equal(archives.Length, host.GetActivePackages().Count);
            var reloadedWorkerProcessIds = GetWorkerProcessIds(host);
            Assert.Equal(archives.Length, reloadedWorkerProcessIds.Length);
            observedWorkerProcessIds.UnionWith(reloadedWorkerProcessIds);

            baseline.VerifyProviders(host.RpcCatalog.GetSnapshot().Providers);
            await ExportAppSnapshotAsync(host, baseline);

            logger.Clear();
            var reinstall = await host.ApplyPackageStoreMutationsAsync(installed
                .OrderBy(static package => package.PackageId, StringComparer.Ordinal)
                .Select(package => new PackageStoreMutation(
                    PackageStoreMutationKind.Upgrade,
                    package.PackageId,
                    Assert.Single(archives, path => string.Equals(
                        Path.GetFileName(path),
                        $"{package.PackageId}.{package.Version}.sunderpkg",
                        StringComparison.OrdinalIgnoreCase)),
                    Reinstall: true))
                .ToArray());
            Assert.True(reinstall.Success, logger.Describe("Reinstall family", reinstall.Errors));
            Assert.Equal(archives.Length, host.GetActivePackages().Count);
            await AssertProcessesStoppedAsync(reloadedWorkerProcessIds);
            var reinstalledWorkerProcessIds = GetWorkerProcessIds(host);
            Assert.Equal(archives.Length, reinstalledWorkerProcessIds.Length);
            observedWorkerProcessIds.UnionWith(reinstalledWorkerProcessIds);
            baseline.VerifyProviders(host.RpcCatalog.GetSnapshot().Providers);
        }
        finally
        {
            observedWorkerProcessIds.UnionWith(GetWorkerProcessIds(host));
            await host.ShutdownAsync();
            await AssertProcessesStoppedAsync(observedWorkerProcessIds);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            Assert.False(Directory.Exists(root));
        }
    }

    private static PackageCapabilityConsent? ReadSensitiveCapabilityConsent(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var manifest = ReadArchiveJson<SunderPackageManifest>(archive, SunderPackageFormat.ManifestPath);
        var capabilities = PackageSensitiveCapabilityPolicy.GetRequiredCapabilities(manifest);
        if (capabilities.Count == 0) return null;

        var contentIndex = ReadArchiveJson<SunderPackageContentIndex>(archive, SunderPackageFormat.ContentIndexPath);
        return new PackageCapabilityConsent(
            manifest.Id ?? throw new InvalidDataException("Package manifest id is missing."),
            manifest.Version ?? throw new InvalidDataException("Package manifest version is missing."),
            PackageSensitiveCapabilityPolicy.GetManifestSha256(contentIndex),
            capabilities);
    }

    private static T ReadArchiveJson<T>(ZipArchive archive, string path) where T : class
    {
        var entry = archive.GetEntry(path)
            ?? throw new InvalidDataException($"Package archive is missing {path}.");
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<T>(stream)
            ?? throw new InvalidDataException($"Package archive contains invalid {path}.");
    }

    private static void VerifyReleasedSqliteBytes(
        IReadOnlyList<string> archives,
        IReadOnlyList<InstalledPackageDescriptor> installed,
        RuntimePackagePaths paths)
    {
        var semanticArchivePath = Assert.Single(archives, path => string.Equals(
            Path.GetFileNameWithoutExtension(path),
            $"{SemanticMemoryPackageId}.{installed.Single(package => package.PackageId == SemanticMemoryPackageId).Version}",
            StringComparison.OrdinalIgnoreCase));
        var expectedArchiveEntries = SunderPackageFormat.SupportedRuntimeIdentifiers
            .Select(rid => $"payload/runtime/{rid}/lib/{GetSqliteLibraryName(rid)}")
            .Append("payload/runtime/shared/lib/SQLitePCLRaw.provider.e_sqlite3.dll")
            .Order(StringComparer.Ordinal)
            .ToArray();
        using (var archive = ZipFile.OpenRead(semanticArchivePath))
        {
            var actual = archive.Entries
                .Select(static entry => entry.FullName)
                .Where(static path => path.EndsWith("e_sqlite3.dll", StringComparison.Ordinal)
                                      || path.EndsWith("libe_sqlite3.dylib", StringComparison.Ordinal)
                                      || path.EndsWith("libe_sqlite3.so", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(expectedArchiveEntries, actual);
            Assert.All(
                expectedArchiveEntries,
                path => Assert.True(archive.GetEntry(path)?.Length > 0, $"SQLite release entry was empty: {path}"));
        }

        var semanticPackage = installed.Single(package => package.PackageId == SemanticMemoryPackageId);
        var currentRid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        var installRoot = paths.GetInstalledPackagePath(semanticPackage.PackageId, semanticPackage.Version);
        var providerPath = Path.Combine(
            installRoot,
            "payload", "runtime", "shared", "lib", "SQLitePCLRaw.provider.e_sqlite3.dll");
        var nativePath = Path.Combine(
            installRoot,
            "payload", "runtime", currentRid, "lib", GetSqliteLibraryName(currentRid));
        Assert.True(new FileInfo(providerPath).Length > 0, $"SQLite managed provider was missing: {providerPath}");
        Assert.True(new FileInfo(nativePath).Length > 0, $"SQLite native library was missing: {nativePath}");
        var handle = NativeLibrary.Load(nativePath);
        try
        {
            Assert.True(NativeLibrary.TryGetExport(handle, "sqlite3_libversion", out _));
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    private static string GetSqliteLibraryName(string rid)
        => rid.StartsWith("win-", StringComparison.Ordinal)
            ? "e_sqlite3.dll"
            : rid.StartsWith("osx-", StringComparison.Ordinal)
                ? "libe_sqlite3.dylib"
                : "libe_sqlite3.so";

    private static int[] GetWorkerProcessIds(RuntimePackageSessionTestHost host)
        => host.ActiveSession.LoadedPackageMap.Values
            .Select(static package => package.Worker)
            .OfType<ProcessRuntimeWorker>()
            .Select(WorkerReleaseMetrics.GetProcess)
            .Select(static process => process.Id)
            .Order()
            .ToArray();

    private static async Task AssertProcessesStoppedAsync(IEnumerable<int> processIds)
    {
        foreach (var processId in processIds.Distinct())
        {
            var timeout = Stopwatch.StartNew();
            while (IsProcessAlive(processId) && timeout.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(50);
            }
            Assert.False(IsProcessAlive(processId), $"Agent release worker process {processId} survived retirement.");
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task ExportAppSnapshotAsync(RuntimePackageSessionTestHost host, AgentCurrentBaseline baseline)
    {
        var outputDirectory = Environment.GetEnvironmentVariable("SUNDER_AGENT_UI_SNAPSHOT_DIR");
        if (string.IsNullOrWhiteSpace(outputDirectory)) return;

        Directory.CreateDirectory(outputDirectory);
        var snapshot = Assert.Single(
            host.GetActivePackageUiSnapshots(),
            candidate => candidate.PackageId == AgentPackageId);
        var package = Assert.Single(
            host.GetActivePackages(),
            candidate => candidate.PackageId == snapshot.PackageId);
        await using var lease = host.AcquireCurrentUiSnapshot(snapshot.SnapshotId);
        Assert.NotNull(lease);
        Assert.Equal(snapshot.ContentHash, lease.ContentHash);

        var snapshotPath = Path.Combine(outputDirectory, "younics.ai.agent.snapshot");
        await using (var destination = new FileStream(snapshotPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await lease.Stream.CopyToAsync(destination);
        }

        var metadataPath = Path.Combine(outputDirectory, "metadata.json");
        await File.WriteAllTextAsync(
            metadataPath,
            JsonSerializer.Serialize(
                new AgentAppSnapshotMetadata(package, snapshot, baseline.InventorySha256, baseline.ContractsSha256),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private sealed record AgentAppSnapshotMetadata(
        ActivePackageDescriptor Package,
        PackageUiSnapshotDescriptor Snapshot,
        string InventorySha256,
        string ContractsSha256);

    private sealed class ReleaseSmokeLogger : ILogger<RuntimePackageSessionTestHost>
    {
        private readonly object _gate = new();
        private readonly List<string> _diagnostics = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (logLevel < LogLevel.Warning
                && exception is null
                && !message.StartsWith("Process package ", StringComparison.Ordinal)) return;
            lock (_gate)
            {
                var errorCode = exception?.GetType().GetProperty("SqliteErrorCode")?.GetValue(exception);
                var extendedErrorCode = exception?.GetType().GetProperty("SqliteExtendedErrorCode")?.GetValue(exception);
                var sqliteDetails = errorCode is null && extendedErrorCode is null
                    ? null
                    : $"SQLite result code: {errorCode}; extended result code: {extendedErrorCode}";
                var diagnostic = string.Join(
                    Environment.NewLine,
                    new[] { message, sqliteDetails, exception?.ToString() }
                        .Where(static value => value is not null));
                if (_diagnostics.Count == 200) _diagnostics.RemoveAt(0);
                _diagnostics.Add(diagnostic);
            }
        }

        public void Clear()
        {
            lock (_gate) _diagnostics.Clear();
        }

        public string Describe(string operation, IReadOnlyList<string> errors)
        {
            lock (_gate)
            {
                return string.Join(
                    Environment.NewLine,
                    new[] { operation }.Concat(errors).Concat(_diagnostics)
                        .Where(static value => value is not null));
            }
        }
    }
}
