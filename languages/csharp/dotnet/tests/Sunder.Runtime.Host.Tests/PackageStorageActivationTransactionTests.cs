using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sunder.Core.TestSupport;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageStorageActivationTransactionTests
{
    [Fact]
    public async Task IntentWriteFailure_DoesNotMoveStorageOrCreateUnownedBackups()
    {
        using var fixture = new Fixture();

        await Assert.ThrowsAsync<IOException>(() => fixture.BeginAsync(new FailingFileSystem { FailIntent = true }));

        await fixture.AssertValuesAsync("baseline");
        Assert.Empty(fixture.Backups);
        Assert.Empty(fixture.Journals);
    }

    [UnixFact]
    public async Task FileCopyFailureAfterBothRenames_RestoresStorageAndRemovesThePendingJournal()
    {
        using var fixture = new Fixture();
        var unsupported = Path.Combine(fixture.PackageRoot, "files", "unsupported-fifo");
        Assert.Equal(0, MkFifo(unsupported, 0x180));

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.BeginAsync());

        await fixture.AssertValuesAsync("baseline");
        Assert.Empty(fixture.Backups);
        Assert.Empty(fixture.Journals);
        File.Delete(unsupported);
        await fixture.InitializeAsync();
    }

    [Fact]
    public async Task CommittedActivation_PartialCleanupFailureSurvivesColdInitializationWithoutRollback()
    {
        using var fixture = new Fixture();
        var fileSystem = new FailingFileSystem { FailCleanup = true };
        var storage = await fixture.BeginAsync(fileSystem);
        await fixture.WriteValuesAsync("committed");

        await storage.CompleteAsync(fixture.Logger);

        Assert.Equal(PackageStorageActivationOutcome.Committed, fixture.ReadJournal().Outcome);
        Assert.NotEmpty(fixture.Backups);
        Assert.Contains(fixture.Logs.Entries, entry => entry.Level == LogLevel.Warning
            && entry.Exception is IOException && entry.Message.Contains("cleanup is pending", StringComparison.Ordinal));
        await fixture.InitializeAsync();
        await fixture.AssertValuesAsync("committed");
        Assert.Empty(fixture.Backups);
        Assert.Empty(fixture.Journals);
        await fixture.InitializeAsync();
    }

    [Fact]
    public async Task CommittedCleanup_RemainsRecognizedWhenDeletionFailsAgain()
    {
        using var fixture = new Fixture();
        var fileSystem = new FailingFileSystem { FailCleanup = true };
        var storage = await fixture.BeginAsync(fileSystem);
        await storage.CompleteAsync(fixture.Logger);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var recognized = await PackageStorageActivationJournal.RecoverAsync(
                fixture.PackageRoot, fixture.Logger, CancellationToken.None, fileSystem);
            Assert.All(fixture.Backups, backup => Assert.Contains(backup, recognized));
            Assert.Equal(PackageStorageActivationOutcome.Committed, fixture.ReadJournal().Outcome);
        }
        await fixture.InitializeAsync();
        Assert.Empty(fixture.Backups);
        Assert.Empty(fixture.Journals);
    }

    [Fact]
    public async Task CommittedOutcomeWriteFailure_PreservesBackupsAndRequiresExplicitRecovery()
    {
        using var fixture = new Fixture();
        var storage = await fixture.BeginAsync(new FailingFileSystem { FailCommit = true });
        await fixture.WriteValuesAsync("committed");

        await storage.CompleteAsync(fixture.Logger);
        await storage.RollbackAsync(fixture.Logger); // Published data must never be rolled back by cleanup.

        Assert.Equal(PackageStorageActivationOutcome.Pending, fixture.ReadJournal().Outcome);
        Assert.Equal(2, fixture.Backups.Length);
        Assert.Contains(fixture.Logs.Entries, entry => entry.Level == LogLevel.Error && entry.Exception is IOException);
        await Assert.ThrowsAsync<PackageStorageActivationRecoveryException>(fixture.InitializeAsync);
        await fixture.AssertValuesAsync("committed");
        Assert.Equal(2, fixture.Backups.Length);
    }

    [Fact]
    public async Task InterruptedActivation_PreservesCurrentStorageAndBackupsAndReportsRecoveryCode()
    {
        using var fixture = new Fixture();
        _ = await fixture.BeginAsync();
        await fixture.WriteValuesAsync("unconfirmed");

        var failure = await Assert.ThrowsAsync<PackageStorageActivationRecoveryException>(fixture.InitializeAsync);

        Assert.Contains("interrupted without a durable outcome", failure.Message, StringComparison.Ordinal);
        var diagnostic = RuntimeStartupDiagnosticReporter.CreateDiagnostic(failure);
        Assert.Equal(PackageStorageActivationRecoveryException.Code, diagnostic.Code);
        Assert.Contains("test.package", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(2, fixture.Backups.Length);
        Assert.All(fixture.Backups, backup => Assert.Equal("baseline",
            File.ReadAllText(Path.Combine(backup, "baseline.txt"))));
        await fixture.AssertValuesAsync("unconfirmed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedRollback_RestoresAllStoresAndCanRetryJournalCleanup(bool failJournalCleanup)
    {
        using var fixture = new Fixture();
        var storage = await fixture.BeginAsync(new FailingFileSystem { FailJournalDelete = failJournalCleanup });
        await fixture.WriteValuesAsync("rejected");

        await storage.RollbackAsync(fixture.Logger);

        await fixture.AssertValuesAsync("baseline");
        Assert.Empty(fixture.Backups);
        if (failJournalCleanup)
        {
            Assert.Equal(PackageStorageActivationOutcome.RolledBack, fixture.ReadJournal().Outcome);
            Assert.Contains(fixture.Logs.Entries, entry => entry.Level == LogLevel.Warning);
        }
        await fixture.InitializeAsync();
        Assert.Empty(fixture.Journals);
    }

    [Fact]
    public async Task CommittedJournalDeleteFailure_IsRetryableAfterAllBackupsWereDeleted()
    {
        using var fixture = new Fixture();
        var storage = await fixture.BeginAsync(new FailingFileSystem { FailJournalDelete = true });

        await storage.CompleteAsync(fixture.Logger);

        Assert.Empty(fixture.Backups);
        Assert.Single(fixture.Journals);
        await fixture.InitializeAsync();
        Assert.Empty(fixture.Journals);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("package")]
    [InlineData("transaction")]
    [InlineData("path")]
    [InlineData("duplicate-directory")]
    [InlineData("outcome")]
    [InlineData("duplicate-property")]
    public async Task MalformedJournal_NeverAuthorizesDeletingStorage(string corruption)
    {
        using var fixture = new Fixture();
        var storage = await fixture.BeginAsync(new FailingFileSystem { FailCleanup = true });
        await storage.CompleteAsync(fixture.Logger);
        var journal = fixture.ReadJournal();
        journal = corruption switch
        {
            "version" => journal with { Version = 2 },
            "package" => journal with { PackageId = "another.package" },
            "transaction" => journal with { TransactionId = Guid.NewGuid().ToString("N") },
            "path" => journal with { Directories = ["../outside"] },
            "duplicate-directory" => journal with { Directories = ["workspace", "workspace"] },
            "outcome" => journal with { Outcome = (PackageStorageActivationOutcome)99 },
            _ => journal,
        };
        var json = JsonSerializer.Serialize(journal, PackageStorageActivationJournal.JsonOptions);
        if (corruption == "duplicate-property") json = json.Insert(1, "\"version\":1,");
        await File.WriteAllTextAsync(Assert.Single(fixture.Journals), json);
        var evidence = fixture.Backups.SelectMany(Directory.GetFiles).ToDictionary(
            path => path, File.ReadAllBytes);

        await Assert.ThrowsAsync<PackageStorageActivationRecoveryException>(fixture.InitializeAsync);

        Assert.Equal(json, await File.ReadAllTextAsync(Assert.Single(fixture.Journals)));
        Assert.All(evidence, pair => Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)));
    }

    [Fact]
    public async Task CommittedJournal_WithMissingLiveWorkspacePreservesTheRemainingBackup()
    {
        using var fixture = new Fixture();
        var storage = await fixture.BeginAsync(new FailingFileSystem { FailCleanup = true });
        await storage.CompleteAsync(fixture.Logger);
        Directory.Delete(fixture.Workspace, recursive: true);

        await Assert.ThrowsAsync<PackageStorageActivationRecoveryException>(fixture.InitializeAsync);

        Assert.Equal(2, fixture.Backups.Length);
        Assert.Single(fixture.Journals);
    }

    [UnixFact]
    public async Task TerminalCleanup_RejectsSymlinksWithoutTouchingTheirTargets()
    {
        foreach (var link in new[] { "workspace", "backup", "journal" })
        {
            using var fixture = new Fixture();
            var storage = await fixture.BeginAsync(new FailingFileSystem { FailCleanup = true });
            await storage.CompleteAsync(fixture.Logger);
            var outside = Path.Combine(fixture.Paths.RootPath, "outside");
            Directory.CreateDirectory(outside);
            var sentinel = Path.Combine(outside, "preserved.txt");
            await File.WriteAllTextAsync(sentinel, "preserve");
            if (link == "journal")
            {
                var journal = Assert.Single(fixture.Journals);
                var original = journal + ".preserved";
                File.Move(journal, original);
                File.CreateSymbolicLink(journal, original);
            }
            else
            {
                var path = link == "workspace" ? fixture.Workspace : fixture.Backups[0];
                Directory.Move(path, path + ".preserved");
                Directory.CreateSymbolicLink(path, outside);
            }

            await Assert.ThrowsAsync<InvalidDataException>(fixture.InitializeAsync);

            Assert.Equal("preserve", await File.ReadAllTextAsync(sentinel));
        }
    }

    [Fact]
    public void StartupDiagnostics_UnrelatedFailuresKeepTheirInternalCodeAndMessageBound()
    {
        var diagnostic = RuntimeStartupDiagnosticReporter.CreateDiagnostic(new IOException(new string('x', 3000)));
        Assert.Equal("runtime.v1.internal", diagnostic.Code);
        Assert.Equal(2048, diagnostic.Message.Length);
    }

    private sealed class FailingFileSystem : PackageStorageActivationFileSystem
    {
        internal bool FailCleanup { get; init; }
        internal bool FailIntent { get; init; }
        internal bool FailCommit { get; init; }
        internal bool FailJournalDelete { get; init; }

        internal override Task WriteJournalAsync(string path, PackageStorageActivationJournalDocument document,
            CancellationToken cancellationToken)
        {
            if (FailIntent && document.Outcome == PackageStorageActivationOutcome.Pending)
                throw new IOException("Injected durable intent failure.");
            if (FailCommit && document.Outcome == PackageStorageActivationOutcome.Committed)
                throw new IOException("Injected durable outcome failure.");
            return base.WriteJournalAsync(path, document, cancellationToken);
        }

        internal override void DeleteBackup(string path)
        {
            if (FailCleanup)
            {
                // Reproduce a recursive delete which removes some files before failing.
                File.Delete(Path.Combine(path, "baseline.txt"));
                throw new IOException("Injected partial cleanup failure.");
            }
            base.DeleteBackup(path);
        }

        internal override void DeleteJournal(string path)
        {
            if (FailJournalDelete) throw new IOException("Injected journal cleanup failure.");
            base.DeleteJournal(path);
        }
    }

    private sealed class WorkspaceWorker(string workspace) : TestRuntimePackageWorker
    {
        public override string? WorkspaceRootPath => workspace;
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(string path, uint mode);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "sunder-storage-activation-tests", Guid.NewGuid().ToString("N"));
        private readonly LocalPackageStorageContext _storage;
        private readonly ActiveLoadedPackage _package;
        internal RuntimePackagePaths Paths { get; }
        internal string PackageRoot { get; }
        internal string Workspace => Path.Combine(PackageRoot, "workspace");
        internal RecordingLogSink Logs { get; } = new();
        internal ILogger Logger => Logs.CreateLogger<PackageStorageActivationTransactionTests>();
        internal string[] Backups => Directory.GetDirectories(PackageRoot, ".*.sunder-activation-*");
        internal string[] Journals => Directory.GetFiles(PackageRoot, PackageStorageActivationJournal.FilePattern);

        internal Fixture()
        {
            Paths = new RuntimePackagePaths(Path.Combine(_root, "store"));
            PackageRoot = Path.Combine(Paths.PackageDataRootPath, "test.package");
            _storage = new LocalPackageStorageContext("test.package", Paths.PackageDataRootPath);
            _package = new ActiveLoadedPackage(
                new ActivePackageDescriptor("test.package", "Test Package", "1.0.0", PackageHostRoles.Runtime,
                    null, true, PackageReadinessState.Ready, []),
                new RuntimePackageSource("test.package", PackageSourceKind.Dev, _root),
                null, _storage.State,
                new JsonPackageSecretsStore(Path.Combine(_storage.DataRootPath, "secrets.json"), null, null,
                    new RestrictedFileMasterKeyProtection()),
                _storage.Files, new PackageSettings(_storage.SettingsStore), new WorkspaceWorker(Workspace));
        }

        internal async Task<PackageStorageActivationTransaction> BeginAsync(PackageStorageActivationFileSystem? fileSystem = null)
        {
            await WriteValuesAsync("baseline");
            File.WriteAllText(Path.Combine(Workspace, "retained.txt"), "preserve");
            await _storage.Files.WriteAsync("retained.txt", "preserve"u8.ToArray());
            return await PackageStorageActivationTransaction.BeginAsync([_package], CancellationToken.None, fileSystem);
        }

        internal async Task WriteValuesAsync(string value)
        {
            await File.WriteAllTextAsync(Path.Combine(Workspace, "baseline.txt"), value);
            await _storage.Files.WriteAsync("baseline.txt", System.Text.Encoding.UTF8.GetBytes(value));
            await _storage.State.SetValueAsync("value", value);
            await _storage.SettingsStore.SetValueAsync("value", value);
            await _package.SecretsStore.SetSecretAsync("value", value);
        }

        internal async Task AssertValuesAsync(string value)
        {
            Assert.Equal(value, await File.ReadAllTextAsync(Path.Combine(Workspace, "baseline.txt")));
            Assert.Equal(value, System.Text.Encoding.UTF8.GetString((await _storage.Files.ReadAsync("baseline.txt"))!));
            Assert.Equal(value, await _storage.State.GetValueAsync("value"));
            Assert.Equal(value, await _storage.SettingsStore.GetValueAsync("value"));
            Assert.Equal(value, await _package.SecretsStore.GetSecretAsync("value"));
        }

        internal Task InitializeAsync()
            => new PackageStoreCoordinator(Paths, new InstalledPackageStore(Paths), new SunderPackageArchiveInstaller(Paths),
                logger: Logs.CreateLogger<PackageStoreCoordinator>()).InitializeAsync();

        internal PackageStorageActivationJournalDocument ReadJournal()
            => JsonSerializer.Deserialize<PackageStorageActivationJournalDocument>(File.ReadAllText(Assert.Single(Journals)),
                PackageStorageActivationJournal.JsonOptions)!;

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
