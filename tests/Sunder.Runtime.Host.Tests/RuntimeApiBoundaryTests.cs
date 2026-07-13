using System.Text.Json;
using System.Text.Json.Nodes;
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
    public void StackImportContracts_JsonMatchesGoldenFixture()
    {
        var importedItem = new RuntimeStackImportedItemDescriptor("profile-1", "profiles", "Profile", "profile")
        {
            OwnerPackageId = "test.package",
        };
        var preview = new RuntimeStackImportPreviewResponse(
            true,
            "opaque-plan",
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            [new RuntimeStackImportActionDescriptor("profile.create", "profiles", "Create profile", "Create", true)
            {
                OwnerPackageId = "test.package",
            }],
            [],
            [],
            [],
            []);
        var import = new RuntimeStackImportResponse(
            RuntimeStackImportOutcome.Partial,
            [importedItem],
            new Dictionary<string, string> { ["source-profile"] = "profile-1" },
            [
                new RuntimeStackImportContributorResultDescriptor(
                    "test.package",
                    "profiles",
                    ["profile-fragment"],
                    RuntimeStackImportOutcome.Completed,
                    [importedItem],
                    new Dictionary<string, string> { ["source-profile"] = "profile-1" },
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
            new RuntimePackageSource("test.package", PackageSourceKind.Dev, source, source),
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
            new RuntimePackageSource("test.package", PackageSourceKind.Installed, source, source),
        ], generation: 3));

        Assert.Null(store.Acquire(descriptor.SnapshotId, generation: 4, stageId: null));
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
            .SelectMany(type => type.GetProperties().Select(property => $"{type.Name}.{property.Name}"))
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

    private static string CreateSnapshotSource(string root)
    {
        var source = Path.Combine(root, "private-runtime-source");
        Directory.CreateDirectory(Path.Combine(source, "lib"));
        Directory.CreateDirectory(Path.Combine(source, "assets"));
        File.WriteAllText(Path.Combine(source, "sunder-package.json"), "{\"id\":\"test.package\",\"entryAssembly\":\"test.dll\"}");
        File.WriteAllBytes(Path.Combine(source, "lib", "test.dll"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(source, "assets", "icon.png"), [4, 5, 6]);
        return source;
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
