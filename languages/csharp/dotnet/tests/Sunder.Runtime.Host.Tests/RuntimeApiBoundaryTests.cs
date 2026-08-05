using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeApiBoundaryTests
{
    [Fact]
    public void RuntimeHandshakeContract_JsonMatchesGoldenFixture()
    {
        var handshake = new RuntimeHandshakeResponse(
            RuntimeProtocol.Identity,
            RuntimeProtocol.CurrentRevision,
            RuntimeProtocol.MinimumSupportedRevision,
            RuntimeProtocol.MaximumSupportedRevision,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            [RuntimeProtocolFeatures.VersionedApiV1, RuntimeProtocolFeatures.PackageRuntimeStreamEnvelopesV1],
            new RuntimeProductVersionDiagnostics("Sunder.Runtime.Host", "1.2.3", "1.2.3+test"));
        var actual = JsonSerializer.SerializeToNode(handshake, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "RuntimeProtocolContracts.golden.json");
        var expected = JsonNode.Parse(File.ReadAllText(fixturePath));

        Assert.Equal(expected?.ToJsonString(), actual?.ToJsonString());
    }

    [Fact]
    public void RuntimePackageSnapshotContract_JsonMatchesGoldenFixture()
    {
        var active = new ActivePackageDescriptor(
            "test.package",
            "Test Package",
            "1.2.3",
            PackageHostRoles.App | PackageHostRoles.Runtime,
            Icon: null,
            IsEnabled: true,
            PackageReadinessState.Ready,
            Views: []);
        var session = new SessionPackageDescriptor(
            active.PackageId,
            active.DisplayName,
            active.Version,
            active.HostRoles,
            active.Icon,
            active.IsEnabled,
            active.Readiness,
            active.Views,
            FailureOrigin: null,
            LastError: null,
            LastFailureAtUtc: null,
            FailureCount: 0);
        var snapshot = new RuntimePackageSnapshot(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            SessionGeneration: 7,
            EventSequence: 12,
            RuntimeBootstrapState.Ready,
            [active],
            [session],
            ["warning"],
            []);
        var actual = JsonSerializer.SerializeToNode(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "RuntimePackageSnapshot.golden.json");
        var expected = JsonNode.Parse(File.ReadAllText(fixturePath));

        Assert.Equal(expected?.ToJsonString(), actual?.ToJsonString());
    }

    [Fact]
    public void StackImportContracts_JsonMatchesGoldenFixture()
    {
        var importedItem = new RuntimeStackImportedItemDescriptor("profile-1", "test.package", "profiles", "Profile", "profile");
        var preview = new RuntimeStackImportPreviewResponse(
            true,
            "opaque-plan",
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            [new RuntimeStackImportActionDescriptor("action-key", "test.package", "profiles", "profile.create", "Create profile", "Create", true)],
            [new RuntimeStackRequiredInputDescriptor(
                "input-key",
                "test.package",
                "profiles",
                "api-key",
                "API key",
                RuntimeStackInputSensitivity.Secret,
                true,
                "Provider credential.")],
            [],
            [],
            []);
        var import = new RuntimeStackImportResponse(
            RuntimeStackImportOutcome.Partial,
            [importedItem],
            new Dictionary<string, string> { ["remap-key"] = "profile-1" },
            [
                new RuntimeStackImportContributorResultDescriptor(
                    "test.package",
                    "profiles",
                    ["profile-fragment"],
                    RuntimeStackImportOutcome.Completed,
                    [importedItem],
                    new Dictionary<string, string> { ["remap-key"] = "profile-1" },
                    [],
                    []),
                new RuntimeStackImportContributorResultDescriptor(
                    "other.package",
                    "settings",
                    ["settings-fragment"],
                    RuntimeStackImportOutcome.Failed,
                    [],
                    new Dictionary<string, string>(),
                    [],
                    ["Settings import failed."]),
            ],
            [],
            ["Settings import failed."]);
        var actual = JsonSerializer.SerializeToNode(new { Preview = preview, Import = import }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "StackImportContracts.golden.json");
        var expected = JsonNode.Parse(File.ReadAllText(fixturePath));

        Assert.Equal(expected?.ToJsonString(), actual?.ToJsonString());
    }

    [Fact]
    public void PackageUiSnapshotDescriptor_JsonDoesNotExposeRuntimeRoot()
    {
        var root = CreateTempDirectory();
        var source = CreateSnapshotSource(root);
        using var store = new PackageUiSnapshotStore(new RuntimePackagePaths(root));

        var descriptor = Assert.Single(store.CreateSnapshots([
            CanonicalPackageTestBuilder.CreateTargetSource(source, PackageSourceKind.Dev),
        ], generation: 7));
        var json = JsonSerializer.Serialize(descriptor, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.DoesNotContain(root, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(source, json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(7, descriptor.SessionGeneration);
        Assert.Equal(64, descriptor.ContentHash.Length);
    }

    [Fact]
    public void PackageUiSnapshotStore_RejectsGenerationStaleSnapshot()
    {
        var root = CreateTempDirectory();
        var source = CreateSnapshotSource(root);
        using var store = new PackageUiSnapshotStore(new RuntimePackagePaths(root));
        var descriptor = Assert.Single(store.CreateSnapshots([
            CanonicalPackageTestBuilder.CreateTargetSource(source, PackageSourceKind.Installed),
        ], generation: 3));

        Assert.Null(store.Acquire(descriptor.SnapshotId, generation: 4, stageId: null));
    }

    [Fact]
    public void PackageUiSnapshotStore_CandidateCleanupDoesNotRemovePeerGenerationSnapshot()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(root);
        var source = CreateSnapshotSource(root);
        using var store = new PackageUiSnapshotStore(paths);
        var runtimeSource = CanonicalPackageTestBuilder.CreateTargetSource(source, PackageSourceKind.Dev);
        var first = Assert.Single(store.CreateSnapshots([runtimeSource], generation: 5));
        var second = Assert.Single(store.CreateSnapshots([runtimeSource], generation: 5));

        store.RemoveSnapshots([first]);

        Assert.NotEqual(first.SnapshotId, second.SnapshotId);
        Assert.Null(store.Acquire(first.SnapshotId, generation: 5, stageId: null));
        using var peerLease = store.Acquire(second.SnapshotId, generation: 5, stageId: null);
        Assert.NotNull(peerLease);
        Assert.Single(Directory.EnumerateFiles(paths.CacheRootPath, "*.snapshot", SearchOption.AllDirectories));
    }

    [Fact]
    public void PackageUiSnapshotStore_ReusesPersistentObjectAcrossStoreInstancesAndInvalidatesChangedDevBytes()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(root);
        var source = CreateSnapshotSource(root);
        var runtimeSource = CanonicalPackageTestBuilder.CreateTargetSource(source, PackageSourceKind.Dev);
        PackageUiSnapshotDescriptor first;
        using (var firstStore = new PackageUiSnapshotStore(paths))
        {
            first = Assert.Single(firstStore.CreateSnapshots([runtimeSource], generation: 1));
        }
        using (var secondStore = new PackageUiSnapshotStore(paths))
        {
            var reused = Assert.Single(secondStore.CreateSnapshots([runtimeSource], generation: 2));
            Assert.Equal(first.ContentHash, reused.ContentHash);
            Assert.Single(Directory.EnumerateFiles(paths.CacheRootPath, "*.snapshot", SearchOption.AllDirectories));

            File.WriteAllBytes(
                Path.Combine(
                    source,
                    "payload",
                    "shared",
                    "lib",
                    Path.GetFileName(typeof(PackageSessionOverlayTestPackageModule).Assembly.Location)),
                [3, 2, 1]);
            CanonicalPackageTestBuilder.WriteContentIndex(source);
            var changed = Assert.Single(secondStore.CreateSnapshots([
                CanonicalPackageTestBuilder.CreateTargetSource(source, PackageSourceKind.Dev),
            ], generation: 3));
            Assert.NotEqual(first.ContentHash, changed.ContentHash);
            Assert.Equal(2, Directory.EnumerateFiles(paths.CacheRootPath, "*.snapshot", SearchOption.AllDirectories).Count());
        }
    }

    [Fact]
    public void PackageUiSnapshotStore_QuarantinesCorruptPersistentObjectAndRebuildsIt()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(root);
        var source = CreateSnapshotSource(root);
        var runtimeSource = CanonicalPackageTestBuilder.CreateTargetSource(source, PackageSourceKind.Dev);
        PackageUiSnapshotDescriptor first;
        using (var store = new PackageUiSnapshotStore(paths))
        {
            first = Assert.Single(store.CreateSnapshots([runtimeSource], generation: 1));
        }
        var objectPath = Assert.Single(Directory.EnumerateFiles(paths.CacheRootPath, "*.snapshot", SearchOption.AllDirectories));
        File.WriteAllText(objectPath, "corrupt");

        using var replacementStore = new PackageUiSnapshotStore(paths);
        var rebuilt = Assert.Single(replacementStore.CreateSnapshots([runtimeSource], generation: 2));

        Assert.Equal(first.ContentHash, rebuilt.ContentHash);
        Assert.Contains(Directory.EnumerateFiles(paths.CacheRootPath, "*", SearchOption.AllDirectories),
            path => path.Contains("quarantine", StringComparison.Ordinal));
    }

    [Fact]
    public void PackageUiSnapshotStore_KeepsSimultaneousAppRidProjectionsIsolated()
    {
        var root = CreateTempDirectory();
        var source = Path.Combine(root, "multi-rid-source");
        var currentRid = RuntimeInformation.RuntimeIdentifier;
        var alternateRid = SunderPackageFormat.SupportedRuntimeIdentifiers
            .First(rid => !string.Equals(rid, currentRid, StringComparison.Ordinal));
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            source,
            "test.package",
            "1.0.0",
            typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
            roles: [SunderPackageFormat.AppHostRole],
            runtimeIdentifiers: [currentRid, alternateRid]);
        WriteTargetVariant(currentRid, "current");
        WriteTargetVariant(alternateRid, "alternate");
        CanonicalPackageTestBuilder.WriteContentIndex(source);
        using var store = new PackageUiSnapshotStore(new RuntimePackagePaths(root));

        var current = Assert.Single(store.CreateSnapshots([
            CanonicalPackageTestBuilder.CreateTargetSource(source, PackageSourceKind.Dev, rid: currentRid),
        ], generation: 4));
        var alternate = Assert.Single(store.CreateSnapshots([
            CanonicalPackageTestBuilder.CreateTargetSource(source, PackageSourceKind.Dev, rid: alternateRid),
        ], generation: 4));

        Assert.Equal(currentRid, current.Target.Rid);
        Assert.Equal(alternateRid, alternate.Target.Rid);
        Assert.NotEqual(current.ContentHash, alternate.ContentHash);
        using var currentLease = store.Acquire(current.SnapshotId, generation: 4, stageId: null);
        using var alternateLease = store.Acquire(alternate.SnapshotId, generation: 4, stageId: null);
        Assert.Equal("current", ReadArchiveText(currentLease!, "variant.txt"));
        Assert.Equal("alternate", ReadArchiveText(alternateLease!, "variant.txt"));

        void WriteTargetVariant(string rid, string value)
        {
            var folder = Path.Combine(source, "payload", "app", rid);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "variant.txt"), value);
        }
    }

    [Fact]
    public void LifecycleContracts_DoNotExposeTargetlessPackageUiSnapshots()
    {
        var contractTypes = new[]
        {
            typeof(RuntimePackageSnapshot),
            typeof(PackageLifecycleStageResult),
            typeof(PackageLifecycleOperationResult),
            typeof(PackageStoreStageResult),
        };

        Assert.All(contractTypes, type => Assert.Null(type.GetProperty("PackageUiSnapshots")));
        Assert.Equal(typeof(PackageTargetDescriptor), typeof(PackageUiSnapshotDescriptor).GetProperty(nameof(PackageUiSnapshotDescriptor.Target))?.PropertyType);
    }

    [Fact]
    public void ActivePackageSession_AppSourcesContainOnlyAppDependencyClosureInLoadOrder()
    {
        var sources = new[]
        {
            new RuntimePackageSource("shared", PackageSourceKind.Installed, SourceFolder: "/shared", HostRoles: PackageHostRoles.None),
            new RuntimePackageSource("runtime.dependency", PackageSourceKind.Installed, SourceFolder: "/runtime-dependency", HostRoles: PackageHostRoles.Runtime, Dependencies: [new PackageDependencyDescriptor("shared", ">=0.0.0-0")]),
            new RuntimePackageSource("app", PackageSourceKind.Installed, SourceFolder: "/app", HostRoles: PackageHostRoles.App, Dependencies: [new PackageDependencyDescriptor("runtime.dependency", ">=0.0.0-0")]),
            new RuntimePackageSource("runtime.independent", PackageSourceKind.Installed, SourceFolder: "/runtime-independent", HostRoles: PackageHostRoles.Runtime),
        };
        var descriptors = sources.ToDictionary(
            static source => source.PackageId,
            static source => new SessionPackageDescriptor(
                source.PackageId,
                source.PackageId,
                "1.0.0",
                source.HostRoles,
                null,
                true,
                PackageReadinessState.Ready,
                [],
                null,
                null,
                null,
                0),
            StringComparer.OrdinalIgnoreCase);
        var session = new ActivePackageSession(
            null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase),
            descriptors,
            readySources: sources);

        Assert.Equal(["shared", "runtime.dependency", "app"], session.GetAppPackageSources().Select(static source => source.PackageId));
    }

    [Fact]
    public async Task PackageUiSnapshotLease_OwnsAlreadyOpenStreamAcrossStoreRemoval()
    {
        var root = CreateTempDirectory();
        var source = CreateSnapshotSource(root);
        using var store = new PackageUiSnapshotStore(new RuntimePackagePaths(root));
        var descriptor = Assert.Single(store.CreateSnapshots([
            CanonicalPackageTestBuilder.CreateTargetSource(source, PackageSourceKind.Dev),
        ], generation: 6));
        await using var lease = store.Acquire(descriptor.SnapshotId, generation: 6, stageId: null);
        Assert.NotNull(lease);

        store.RemoveSnapshots([descriptor]);
        using var copied = new MemoryStream();
        await lease.Stream.CopyToAsync(copied);

        Assert.True(copied.Length > 0);
        Assert.Empty(Directory.EnumerateFiles(new RuntimePackagePaths(root).TransferRootPath));
    }

    [Fact]
    public async Task ContentTransferStore_RejectsStaleUploadHandleAndCleansFile()
    {
        var paths = new RuntimePackagePaths(CreateTempDirectory());
        using var store = new RuntimeContentTransferStore(paths);
        await using var content = new MemoryStream([1, 2, 3]);
        var upload = await store.CreateUploadAsync(
            RuntimeUploadKind.Package,
            content,
            content.Length,
            expectedHash: null,
            "test.sunderpkg",
            "application/vnd.sunder.package",
            generation: 2,
            CancellationToken.None);

        Assert.Null(store.AcquireUpload(upload.UploadId, RuntimeUploadKind.Package, generation: 3, consume: true));
        Assert.Empty(Directory.EnumerateFiles(paths.TransferRootPath));
    }

    [Fact]
    public async Task ContentTransferStore_CancellationDeletesPartialUpload()
    {
        var paths = new RuntimePackagePaths(CreateTempDirectory());
        using var store = new RuntimeContentTransferStore(paths);
        await using var content = new CancelAfterFirstReadStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.CreateUploadAsync(
                RuntimeUploadKind.Stack,
                content,
                contentLength: null,
                expectedHash: null,
                "test.sunderstack",
                "application/vnd.sunder.stack",
                generation: 1,
                CancellationToken.None));

        Assert.Empty(Directory.EnumerateFiles(paths.TransferRootPath));
    }

    [Fact]
    public async Task ContentTransferStore_RejectsDeclaredOversizedUploadWithoutCreatingFile()
    {
        var paths = new RuntimePackagePaths(CreateTempDirectory());
        using var store = new RuntimeContentTransferStore(paths);
        await using var content = new MemoryStream([1]);

        await Assert.ThrowsAsync<RuntimeUploadLimitException>(async () =>
            await store.CreateUploadAsync(
                RuntimeUploadKind.StackMedia,
                content,
                RuntimeContentTransferStore.MaxMediaUploadBytes + 1,
                expectedHash: null,
                "large.png",
                "image/png",
                generation: 1,
                CancellationToken.None));

        Assert.False(Directory.Exists(paths.TransferRootPath));
    }

    [Fact]
    public async Task ContentTransferStore_HashMismatchRejectsAndCleansUpload()
    {
        var paths = new RuntimePackagePaths(CreateTempDirectory());
        using var store = new RuntimeContentTransferStore(paths);
        await using var content = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAsync<RuntimeValidationException>(async () =>
            await store.CreateUploadAsync(
                RuntimeUploadKind.Package,
                content,
                content.Length,
                expectedHash: new string('0', 64),
                "test.sunderpkg",
                "application/vnd.sunder.package",
                generation: 1,
                CancellationToken.None));

        Assert.Empty(Directory.EnumerateFiles(paths.TransferRootPath));
    }

    [Fact]
    public async Task ContentTransferStore_ConsumedUploadHasExactlyOneOwner()
    {
        var paths = new RuntimePackagePaths(CreateTempDirectory());
        using var store = new RuntimeContentTransferStore(paths);
        await using var content = new MemoryStream([1, 2, 3]);
        var upload = await store.CreateUploadAsync(
            RuntimeUploadKind.Package,
            content,
            content.Length,
            expectedHash: null,
            "test.sunderpkg",
            "application/vnd.sunder.package",
            generation: 2,
            CancellationToken.None);
        using var start = new ManualResetEventSlim();
        var acquisitions = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                return store.AcquireUpload(upload.UploadId, RuntimeUploadKind.Package, generation: 2, consume: true);
            }))
            .ToArray();

        start.Set();
        var leases = await Task.WhenAll(acquisitions);

        var lease = Assert.Single(leases, value => value is not null);
        store.ReleaseUpload(lease!);
        Assert.Empty(Directory.EnumerateFiles(paths.TransferRootPath));
    }

    [Fact]
    public async Task ContentTransferStore_SweepRemovesExpiredHandlesAndFiles()
    {
        var paths = new RuntimePackagePaths(CreateTempDirectory());
        using var store = new RuntimeContentTransferStore(paths);
        await using var content = new MemoryStream([1, 2, 3]);
        var upload = await store.CreateUploadAsync(
            RuntimeUploadKind.Stack,
            content,
            content.Length,
            expectedHash: null,
            "test.sunderstack",
            "application/vnd.sunder.stack",
            generation: 1,
            CancellationToken.None);

        store.SweepExpired(DateTimeOffset.UtcNow.AddHours(1));

        Assert.Null(store.AcquireUpload(upload.UploadId, RuntimeUploadKind.Stack, generation: 1, consume: true));
        Assert.Empty(Directory.EnumerateFiles(paths.TransferRootPath));
    }

    [Fact]
    public void RuntimeContractTypes_DoNotExposeFilesystemShapedMembers()
    {
        var forbiddenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Folder",
            "PackagePath",
            "StackPath",
            "SourcePath",
            "OutputPath",
            "InstalledPath",
            "StagingPath",
            "ArchivePath",
        };
        var offenders = typeof(PackageUiSnapshotDescriptor).Assembly.ExportedTypes
            .SelectMany(type => type.GetProperties().Select(property => new { Type = type, Property = property }))
            .Where(member => member.Type != typeof(DevPackageOwnerFolder))
            .Select(member => $"{member.Type.Name}.{member.Property.Name}")
            .Where(member => forbiddenNames.Contains(member[(member.LastIndexOf('.') + 1)..]))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void StackImportContracts_ExposeOutcomeWithoutCompatibilityProjections()
    {
        Assert.NotNull(typeof(RuntimeStackImportResponse).GetProperty(nameof(RuntimeStackImportResponse.Outcome)));
        Assert.Null(typeof(RuntimeStackImportResponse).GetProperty("Success"));
        Assert.Null(typeof(RuntimeStackImportResponse).GetProperty("AppliedContributions"));
        Assert.Equal(
            typeof(Sunder.Sdk.Stacks.StackImportOutcome),
            typeof(Sunder.Sdk.Stacks.StackImportResult).GetProperty(nameof(Sunder.Sdk.Stacks.StackImportResult.Outcome))?.PropertyType);
    }

    [Fact]
    public void RuntimeCollectionDtos_DefensivelyFreezeCallerOwnedCollections()
    {
        var selectedFragments = new[] { "fragment" };
        var inputValues = new Dictionary<string, string> { ["input"] = "original" };
        var preview = new RuntimeStackImportPreviewRequest(
            "upload",
            selectedFragments,
            inputValues,
            new Dictionary<string, string>());
        var settingValues = new Dictionary<string, string?> { ["setting"] = "original" };
        var settings = new UpdatePackageSettingsRequest(settingValues);

        selectedFragments[0] = "mutated";
        inputValues["input"] = "mutated";
        settingValues["setting"] = "mutated";

        Assert.Equal("fragment", preview.SelectedFragmentIds[0]);
        Assert.Equal("original", preview.InputValues["input"]);
        Assert.Equal("original", settings.Values["setting"]);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, string>)preview.InputValues).Add("new", "value"));
    }

    private static string CreateSnapshotSource(string root)
    {
        var source = Path.Combine(root, "private-runtime-source");
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            source,
            "test.package",
            "1.0.0",
            typeof(PackageSessionOverlayTestPackageModule).Assembly.Location);
        var assets = Path.Combine(source, "payload", "shared", "assets");
        Directory.CreateDirectory(assets);
        File.WriteAllBytes(Path.Combine(assets, "icon.png"), [4, 5, 6]);
        CanonicalPackageTestBuilder.WriteContentIndex(source);
        return source;
    }

    private static string ReadArchiveText(PackageUiSnapshotLease lease, string path)
    {
        using var archive = new ZipArchive(lease.Stream, ZipArchiveMode.Read, leaveOpen: true);
        using var reader = new StreamReader(archive.GetEntry(path)?.Open()
                                            ?? throw new InvalidDataException($"Snapshot entry '{path}' was not found."));
        return reader.ReadToEnd();
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-runtime-boundary-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class CancelAfterFirstReadStream : Stream
    {
        private bool _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            _read = true;
            buffer.Span[0] = 1;
            return ValueTask.FromResult(1);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
