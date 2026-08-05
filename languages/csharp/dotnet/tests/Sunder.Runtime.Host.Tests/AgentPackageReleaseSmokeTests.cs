using Microsoft.Extensions.Logging;
using System.Text.Json;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class AgentPackageReleaseSmokeTests
{
    private const string AgentPackageId = "sunder.package.agent";
    private const string ShellPackageId = "sunder.package.agent.tools.shell";
    private static readonly string[] ShellProviderIds =
    [
        "shell.permissions",
        "shell.prompt.context",
        "shell.tools",
    ];

    [Fact]
    public async Task ReleaseArchives_InstallActivateUnloadReloadAndReinstallAsOneFamily()
    {
        var archiveDirectory = Environment.GetEnvironmentVariable("SUNDER_AGENT_PACKAGE_ARCHIVE_DIR");
        if (string.IsNullOrWhiteSpace(archiveDirectory))
        {
            return;
        }

        var archives = Directory.GetFiles(Path.GetFullPath(archiveDirectory), "*.sunderpkg")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(15, archives.Length);

        var root = Path.Combine(Path.GetTempPath(), "sunder-agent-release-smoke", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(root);
        var store = new InstalledPackageStore(paths);
        var logger = new ReleaseSmokeLogger();
        var host = new RuntimePackageSessionTestHost(
            logger,
            store,
            new SunderPackageArchiveInstaller(paths));

        try
        {
            await host.InitializeAsync();
            foreach (var archive in archives)
            {
                logger.Clear();
                var result = await host.InstallPackageFromRuntimePathAsync(archive);
                Assert.True(result.Success, logger.Describe(
                    Path.GetFileName(archive),
                    result.Errors));
            }

            var installed = await host.GetInstalledPackagesAsync();
            Assert.Equal(archives.Length, installed.Count);
            logger.Clear();
            var activation = await host.LoadInstalledPackagesAsync();
            Assert.True(activation.Success, logger.Describe("Activate family", activation.Errors));
            Assert.Equal(archives.Length, host.GetActivePackages().Count);
            Assert.Contains(host.GetActivePackageUiSnapshots(), snapshot => snapshot.PackageId == AgentPackageId);

            var catalogPayload = await host.RuntimeOperations.InvokeAsync(
                AgentPackageId,
                "agent.catalog.v1",
                "{}"u8.ToArray(),
                CancellationToken.None);
            using (var catalog = JsonDocument.Parse(catalogPayload))
            {
                var localTools = catalog.RootElement.GetProperty("localTools").EnumerateArray().ToArray();
                Assert.Contains(
                    localTools,
                    tool => tool.GetProperty("descriptor").GetProperty("toolId").GetString() == "shell");
            }

            var shell = Assert.Single(host.GetActivePackages(), package => package.PackageId == ShellPackageId);
            Assert.Equal(PackageReadinessState.Ready, shell.Readiness);
            var shellProviders = host.RpcCatalog.GetSnapshot().Providers
                .Where(provider => provider.PackageId == ShellPackageId)
                .ToArray();
            Assert.Equal(ShellProviderIds, shellProviders.Select(provider => provider.ProviderId));
            Assert.All(shellProviders, provider => Assert.Equal(SunderRpcProviderState.Active, provider.State));

            foreach (var package in installed.OrderByDescending(static package => package.PackageId, StringComparer.Ordinal))
            {
                logger.Clear();
                var result = await host.UnloadPackageSessionAsync(package.PackageId, PackageSourceKind.Installed);
                Assert.True(result.Success, logger.Describe($"Unload {package.PackageId}", result.Errors));
            }
            Assert.Empty(host.GetActivePackages());

            foreach (var package in installed.OrderBy(static package => package.PackageId, StringComparer.Ordinal))
            {
                logger.Clear();
                var result = await host.LoadPackageSessionAsync(new PackageSessionLoadRequest(PackageSourceKind.Installed, package.PackageId));
                Assert.True(result.Success, logger.Describe($"Reload {package.PackageId}", result.Errors));
            }
            Assert.Equal(archives.Length, host.GetActivePackages().Count);

            await ExportAppSnapshotAsync(host);

            foreach (var package in installed.OrderBy(static package => package.PackageId, StringComparer.Ordinal))
            {
                var expectedFileName = $"{package.PackageId}.{package.Version}.sunderpkg";
                var archive = Assert.Single(archives, path => string.Equals(Path.GetFileName(path), expectedFileName, StringComparison.OrdinalIgnoreCase));
                logger.Clear();
                var result = await host.ReinstallPackageFromRuntimePathAsync(package.PackageId, archive);
                Assert.True(result.Success, logger.Describe($"Reinstall {package.PackageId}", result.Errors));
            }
            Assert.Equal(archives.Length, host.GetActivePackages().Count);
        }
        finally
        {
            await host.ShutdownAsync();
            TryDelete(root);
        }
    }

    private static async Task ExportAppSnapshotAsync(RuntimePackageSessionTestHost host)
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

        var snapshotPath = Path.Combine(outputDirectory, "sunder.package.agent.snapshot");
        await using (var destination = new FileStream(snapshotPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await lease.Stream.CopyToAsync(destination);
        }

        var metadataPath = Path.Combine(outputDirectory, "metadata.json");
        await File.WriteAllTextAsync(
            metadataPath,
            JsonSerializer.Serialize(
                new AgentAppSnapshotMetadata(package, snapshot),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record AgentAppSnapshotMetadata(
        ActivePackageDescriptor Package,
        PackageUiSnapshotDescriptor Snapshot);

    private sealed class ReleaseSmokeLogger : ILogger<RuntimePackageSessionTestHost>
    {
        private readonly object _gate = new();
        private string? _activationFailure;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is null) return;
            var message = formatter(state, exception);
            if (!message.StartsWith("Failed to activate package ", StringComparison.Ordinal)) return;
            lock (_gate)
            {
                var errorCode = exception.GetType().GetProperty("SqliteErrorCode")?.GetValue(exception);
                var extendedErrorCode = exception.GetType().GetProperty("SqliteExtendedErrorCode")?.GetValue(exception);
                var sqliteDetails = errorCode is null && extendedErrorCode is null
                    ? null
                    : $"SQLite result code: {errorCode}; extended result code: {extendedErrorCode}";
                _activationFailure = string.Join(
                    Environment.NewLine,
                    new[] { message, sqliteDetails, exception.ToString() }
                        .Where(static value => value is not null));
            }
        }

        public void Clear()
        {
            lock (_gate) _activationFailure = null;
        }

        public string Describe(string operation, IReadOnlyList<string> errors)
        {
            lock (_gate)
            {
                return string.Join(
                    Environment.NewLine,
                    new[] { operation }.Concat(errors).Append(_activationFailure)
                        .Where(static value => value is not null));
            }
        }
    }
}
