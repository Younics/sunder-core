using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class ExecutionRuntimeArtifactDownloaderTests
{
    private static readonly DateTimeOffset ObservedAtUtc = DateTimeOffset.Parse("2026-10-07T12:00:00Z");

    [Fact]
    public async Task DownloadAsync_InstallsVerifiedOfficialArtifactAndCleansStaging()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var handler = fixture.CreateArtifactHandler();

        var result = await fixture.CreateDownloader(handler).DownloadAsync(fixture.Artifact, CancellationToken.None);

        Assert.False(result.AlreadyPresent);
        Assert.Equal(fixture.Artifact.Identity, result.Receipt.Identity.ToContract());
        Assert.Equal(fixture.ArchiveSha256, result.Receipt.ArchiveSha256);
        Assert.Equal(fixture.ArchiveBytes.LongLength, result.Receipt.ArchiveSize);
        Assert.Equal(fixture.Artifact.Distribution, result.Receipt.Provenance.Distribution);
        Assert.Equal(fixture.Artifact.Distribution.DownloadUrl, result.Receipt.Provenance.FinalDownloadUrl);
        Assert.True(File.Exists(fixture.Layout.GetReceiptPath(result.Receipt.Identity)));
        Assert.True(File.Exists(fixture.Layout.GetArchivePath(fixture.ArchiveSha256)));
        Assert.Equal(1, handler.RequestCount);
        fixture.AssertNoTransientState();
    }

    [Fact]
    public async Task DownloadAsync_ReusesHealthyExactReceiptWithoutAnotherRequest()
    {
        var logs = new RecordingLogSink();
        var telemetry = new ExecutionRuntimePhaseTelemetry(logs.CreateLogger<ExecutionRuntimePhaseTelemetry>());
        using var fixture = await DownloaderFixture.CreateAsync(telemetry: telemetry);
        var handler = fixture.CreateArtifactHandler();
        var downloader = fixture.CreateDownloader(handler);
        var first = await downloader.DownloadAsync(fixture.Artifact, CancellationToken.None);
        logs.Clear();

        var reused = await downloader.DownloadAsync(fixture.Artifact, CancellationToken.None);

        Assert.False(first.AlreadyPresent);
        Assert.True(reused.AlreadyPresent);
        Assert.Equal(first.Receipt.ContentIdentity, reused.Receipt.ContentIdentity);
        Assert.Equal(1, handler.RequestCount);
        var health = RecordedRuntimePhaseAssertions.Single(logs, ExecutionRuntimePhaseNames.HealthCheck);
        RecordedRuntimePhaseAssertions.Completed(health, ExecutionRuntimePhaseNames.HealthCheck);
        Assert.Equal(true, health.Properties["CacheHit"]);
        fixture.AssertNoTransientState();
    }

    [Fact]
    public async Task DownloadAsync_ConcurrentExactRequestsPerformOneNetworkAcquisition()
    {
        var logs = new RecordingLogSink();
        var telemetry = new ExecutionRuntimePhaseTelemetry(logs.CreateLogger<ExecutionRuntimePhaseTelemetry>());
        using var fixture = await DownloaderFixture.CreateAsync(telemetry: telemetry);
        var handler = fixture.CreateArtifactHandler();
        var downloader = fixture.CreateDownloader(handler);

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            downloader.DownloadAsync(fixture.Artifact, CancellationToken.None)));

        Assert.Equal(1, handler.RequestCount);
        Assert.All(results, result => Assert.Equal(results[0].Receipt.ContentIdentity, result.Receipt.ContentIdentity));
        Assert.Single(RecordedRuntimePhaseAssertions.Entries(logs), entry =>
            Equals(entry.Properties["RuntimePhase"], ExecutionRuntimePhaseNames.Download)
            && Equals(entry.Properties["CacheHit"], false));
        Assert.All(results, result => Assert.False(result.AlreadyPresent));
        fixture.AssertNoTransientState();
    }

    [Fact]
    public async Task DownloadAsync_RetriesTransientTransportAndHttpFailuresThenSucceeds()
    {
        var logs = new RecordingLogSink();
        var telemetry = new ExecutionRuntimePhaseTelemetry(logs.CreateLogger<ExecutionRuntimePhaseTelemetry>());
        using var fixture = await DownloaderFixture.CreateAsync(telemetry: telemetry);
        var failedBody = new PrefixThenThrowingStream(
            fixture.ArchiveBytes.AsMemory(0, Math.Min(32, fixture.ArchiveBytes.Length)),
            new HttpRequestException("The response transport failed."));
        var attempt = 0;
        var handler = new RecordingHandler((request, _) =>
        {
            var current = Interlocked.Increment(ref attempt);
            if (current > 1)
            {
                Assert.True(failedBody.IsDisposed);
                Assert.Empty(Directory.EnumerateFiles(fixture.Layout.StagingRootPath, "official-download.archive", SearchOption.AllDirectories));
            }

            return Task.FromResult(current switch
            {
                1 => Response(request, new UnknownLengthContent(failedBody)),
                2 => Response(request, HttpStatusCode.ServiceUnavailable),
                _ => Response(request, new ByteArrayContent(fixture.ArchiveBytes)),
            });
        });

        var result = await fixture.CreateDownloader(handler).DownloadAsync(fixture.Artifact, CancellationToken.None);

        Assert.False(result.AlreadyPresent);
        Assert.Equal(3, handler.RequestCount);
        Assert.True(failedBody.IsDisposed);
        var download = RecordedRuntimePhaseAssertions.Single(logs, ExecutionRuntimePhaseNames.Download);
        RecordedRuntimePhaseAssertions.Completed(download, ExecutionRuntimePhaseNames.Download);
        Assert.Equal(false, download.Properties["CacheHit"]);
        Assert.True((long)download.Properties["Bytes"]! >= fixture.ArchiveBytes.LongLength);
        fixture.AssertNoTransientState();
    }

    [Fact]
    public async Task DownloadAsync_StopsAfterTransientFailureAttemptsAreExhausted()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var handler = new RecordingHandler((request, _) => Task.FromResult(Response(request, HttpStatusCode.ServiceUnavailable)));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.CreateDownloader(handler).DownloadAsync(fixture.Artifact, CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal(3, handler.RequestCount);
        fixture.AssertNoStorePublication();
        fixture.AssertNoTransientState();
    }

    [Fact]
    public async Task DownloadAsync_CancellationDuringRetryDelayStopsFurtherAttempts()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var clock = new ManualTimeProvider(ObservedAtUtc);
        var handler = new RecordingHandler((request, _) => Task.FromResult(Response(request, HttpStatusCode.TooManyRequests)));
        using var cancellation = new CancellationTokenSource();

        var pending = fixture.CreateDownloader(handler, clock).DownloadAsync(fixture.Artifact, cancellation.Token);
        await WaitUntilAsync(() => clock.PendingTimerCount == 1);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, handler.RequestCount);
        fixture.AssertNoStorePublication();
        fixture.AssertNoTransientState();
    }

    [Fact]
    public async Task DownloadAsync_DoesNotRetryPermanentHttpFailure()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var handler = new RecordingHandler((request, _) => Task.FromResult(Response(request, HttpStatusCode.NotFound)));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.CreateDownloader(handler).DownloadAsync(fixture.Artifact, CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Equal(1, handler.RequestCount);
        fixture.AssertNoStorePublication();
        fixture.AssertNoTransientState();
    }

    [Fact]
    public async Task DownloadAsync_RejectsMismatchedContentLengthBeforeReadingBody()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var body = new TrackingStream(fixture.ArchiveBytes);
        var handler = new RecordingHandler((request, _) =>
        {
            var content = new StreamContent(body);
            content.Headers.ContentLength = fixture.ArchiveBytes.LongLength - 1;
            return Task.FromResult(Response(request, content));
        });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.CreateDownloader(handler).DownloadAsync(fixture.Artifact, CancellationToken.None));

        Assert.Contains("Content-Length", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, body.BytesRead);
        Assert.True(body.IsDisposed);
        fixture.AssertNoStorePublication();
        fixture.AssertNoTransientState();
    }

    [Fact]
    public async Task DownloadAsync_RejectsVendorDigestMismatch()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var wrong = fixture.Artifact with
        {
            Identity = fixture.Artifact.Identity with { VendorChecksumDigest = new string('f', 128) },
        };
        var handler = fixture.CreateArtifactHandler();

        var exception = await Assert.ThrowsAsync<RuntimeExecutionRuntimeException>(() =>
            fixture.CreateDownloader(handler).DownloadAsync(wrong, CancellationToken.None));

        Assert.Equal(ExecutionRuntimeErrorCodes.IntegrityFailed, exception.Code);
        Assert.Equal(1, handler.RequestCount);
        fixture.AssertNoStorePublication(wrong.Identity);
        fixture.AssertNoTransientState();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadAsync_RejectsPartialAndOversizeStreams(bool oversize)
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var body = oversize ? [.. fixture.ArchiveBytes, (byte)0x42] : fixture.ArchiveBytes[..^1];
        var handler = new RecordingHandler((request, _) => Task.FromResult(Response(request,
            new UnknownLengthContent(new MemoryStream(body)))));

        var exception = await Record.ExceptionAsync(() =>
            fixture.CreateDownloader(handler).DownloadAsync(fixture.Artifact, CancellationToken.None));

        if (oversize)
        {
            Assert.IsType<InvalidDataException>(exception);
            Assert.Contains("exceeds", exception!.Message, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var runtime = Assert.IsType<RuntimeExecutionRuntimeException>(exception);
            Assert.Equal(ExecutionRuntimeErrorCodes.IntegrityFailed, runtime.Code);
        }
        fixture.AssertNoStorePublication();
        fixture.AssertNoTransientState();
    }

    [Fact]
    public async Task DownloadAsync_LastWaiterCancellationWaitsForTerminalTelemetryAndCleanup()
    {
        var logs = new RecordingLogSink();
        var telemetry = new ExecutionRuntimePhaseTelemetry(logs.CreateLogger<ExecutionRuntimePhaseTelemetry>());
        using var fixture = await DownloaderFixture.CreateAsync(telemetry: telemetry);
        using var allowStreamDisposal = new ManualResetEventSlim(initialState: false);
        var body = new PrefixThenBlockingStream(
            fixture.ArchiveBytes.AsMemory(0, Math.Min(32, fixture.ArchiveBytes.Length)),
            allowStreamDisposal);
        var handler = new RecordingHandler((request, _) => Task.FromResult(Response(request, new UnknownLengthContent(body))));
        using var cancellation = new CancellationTokenSource();

        var pending = fixture.CreateDownloader(handler).DownloadAsync(fixture.Artifact, cancellation.Token);
        try
        {
            await body.Blocked.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEmpty(Directory.EnumerateFiles(fixture.Layout.StagingRootPath, "official-download.archive", SearchOption.AllDirectories));
            cancellation.Cancel();
            await body.DisposeStarted.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(pending.IsCompleted);
            Assert.DoesNotContain(RecordedRuntimePhaseAssertions.Entries(logs), entry =>
                Equals(entry.Properties["RuntimePhase"], ExecutionRuntimePhaseNames.Download));
            Assert.NotEmpty(Directory.EnumerateFiles(fixture.Layout.StagingRootPath, "official-download.archive", SearchOption.AllDirectories));

            allowStreamDisposal.Set();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.True(body.IsDisposed);
            var download = RecordedRuntimePhaseAssertions.Single(logs, ExecutionRuntimePhaseNames.Download);
            RecordedRuntimePhaseAssertions.Completed(download, ExecutionRuntimePhaseNames.Download, "canceled");
            Assert.Equal(false, download.Properties["CacheHit"]);
            fixture.AssertNoStorePublication();
            fixture.AssertNoTransientState();
        }
        finally
        {
            allowStreamDisposal.Set();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        }
    }

    [Fact]
    public async Task DownloadAsync_AllowsPolicyApprovedRedirectAndRecordsFinalUrl()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var redirected = fixture.OfficialDownloadUri("dotnetcli.azureedge.net");
        var handler = new RecordingHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "builds.dotnet.microsoft.com")
            {
                var response = Response(request, HttpStatusCode.Redirect);
                response.Headers.Location = redirected;
                return Task.FromResult(response);
            }
            return Task.FromResult(Response(request, new ByteArrayContent(fixture.ArchiveBytes)));
        });

        var result = await fixture.CreateDownloader(handler).DownloadAsync(fixture.Artifact, CancellationToken.None);

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(redirected.AbsoluteUri, result.Receipt.Provenance.FinalDownloadUrl);
        fixture.AssertNoTransientState();
    }

    [Fact]
    public async Task DownloadAsync_RejectsResponseRedirectedOutsideOfficialPolicy()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var handler = new RecordingHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/runtime.tar.gz"),
            Content = new ByteArrayContent(fixture.ArchiveBytes),
        }));

        var exception = await Assert.ThrowsAsync<RuntimeExecutionRuntimeException>(() =>
            fixture.CreateDownloader(handler).DownloadAsync(fixture.Artifact, CancellationToken.None));

        Assert.Equal(ExecutionRuntimeErrorCodes.SourceNotAllowed, exception.Code);
        Assert.Equal(1, handler.RequestCount);
        fixture.AssertNoStorePublication();
        fixture.AssertNoTransientState();
    }

    [Fact]
    public async Task DownloadAsync_RejectsOfficialNativeProfileIdentityMismatchAfterDownload()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        const string otherVersion = "10.0.13";
        var mismatched = fixture.Artifact with
        {
            Identity = fixture.Artifact.Identity with { VendorVersion = otherVersion },
            Distribution = fixture.Artifact.Distribution with
            {
                DownloadUrl = fixture.OfficialDownloadUri(version: otherVersion).AbsoluteUri,
                DownloadSize = fixture.ArchiveBytes.LongLength,
            },
        };
        var handler = fixture.CreateArtifactHandler();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.CreateDownloader(handler).DownloadAsync(mismatched, CancellationToken.None));

        Assert.Contains("Microsoft.NETCore.App", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.RequestCount);
        fixture.AssertNoStorePublication(mismatched.Identity);
        fixture.AssertNoTransientState();
    }

    [Fact]
    public async Task DownloadAsync_RejectsWrongRidBeforeNetwork()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var otherRid = SunderPackageFormat.SupportedRuntimeIdentifiers.First(rid => rid != fixture.Rid);
        var artifact = fixture.Artifact with { Identity = fixture.Artifact.Identity with { Rid = otherRid } };
        var handler = fixture.CreateArtifactHandler();

        await Assert.ThrowsAsync<RuntimeExecutionRuntimeException>(() =>
            fixture.CreateDownloader(handler).DownloadAsync(artifact, CancellationToken.None));

        Assert.Equal(0, handler.RequestCount);
        fixture.AssertNoStorePublication(artifact.Identity);
        fixture.AssertNoTransientState();
    }

    [Fact]
    public async Task DownloadAsync_RejectsRuntimeIdAndModuleAbiMismatchBeforeNetwork()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var artifact = fixture.Artifact with
        {
            Identity = fixture.Artifact.Identity with { ModuleAbi = SunderPackageFormat.NodeWorkerModuleAbi },
        };
        var handler = fixture.CreateArtifactHandler();

        await Assert.ThrowsAsync<RuntimeExecutionRuntimeException>(() =>
            fixture.CreateDownloader(handler).DownloadAsync(artifact, CancellationToken.None));

        Assert.Equal(0, handler.RequestCount);
        fixture.AssertNoStorePublication(artifact.Identity);
        fixture.AssertNoTransientState();
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("oversized")]
    [InlineData("null")]
    public async Task DownloadAsync_EnforcesBoundedOrUnknownDeclaredDownloadSize(string sizeCase)
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var artifact = fixture.Artifact with
        {
            Distribution = fixture.Artifact.Distribution with
            {
                DownloadSize = sizeCase switch
                {
                    "zero" => 0,
                    "oversized" => OfficialRuntimeSourcePolicy.MaxArchiveBytes + 1,
                    _ => null,
                },
            },
        };
        var handler = fixture.CreateArtifactHandler();

        if (sizeCase == "null")
        {
            var result = await fixture.CreateDownloader(handler).DownloadAsync(artifact, CancellationToken.None);
            Assert.False(result.AlreadyPresent);
            Assert.Equal(1, handler.RequestCount);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                fixture.CreateDownloader(handler).DownloadAsync(artifact, CancellationToken.None));
            Assert.Equal(0, handler.RequestCount);
            fixture.AssertNoStorePublication();
        }
        fixture.AssertNoTransientState();
    }

    private static HttpResponseMessage Response(HttpRequestMessage request, HttpContent content)
        => new(HttpStatusCode.OK) { RequestMessage = request, Content = content };

    private static HttpResponseMessage Response(HttpRequestMessage request, HttpStatusCode statusCode)
        => new(statusCode) { RequestMessage = request, Content = new ByteArrayContent([]) };

    private sealed class DownloaderFixture : IDisposable
    {
        private TestHttpClientFactory? _factory;

        private DownloaderFixture(string rootPath, byte[] archiveBytes, RuntimeExecutionRuntimeVendorArtifact artifact,
            ExecutionRuntimeStoreLayout layout, ExecutionRuntimeStore store, ExecutionRuntimePhaseTelemetry? telemetry, string rid)
        {
            RootPath = rootPath;
            ArchiveBytes = archiveBytes;
            Artifact = artifact;
            Layout = layout;
            Store = store;
            Telemetry = telemetry;
            Rid = rid;
            ArchiveSha256 = Hash(SHA256.HashData(archiveBytes));
        }

        public string RootPath { get; }
        public byte[] ArchiveBytes { get; }
        public string ArchiveSha256 { get; }
        public RuntimeExecutionRuntimeVendorArtifact Artifact { get; }
        public ExecutionRuntimeStoreLayout Layout { get; }
        public ExecutionRuntimeStore Store { get; }
        public ExecutionRuntimePhaseTelemetry? Telemetry { get; }
        public string Rid { get; }

        public static async Task<DownloaderFixture> CreateAsync(ExecutionRuntimePhaseTelemetry? telemetry = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "SunderOfficialRuntimeDownloaderTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
            const string version = "10.0.12";
            var archivePath = Path.Combine(root, rid.StartsWith("win-", StringComparison.Ordinal) ? "runtime.zip" : "runtime.tar.gz");
            await CreateDotNetArchiveAsync(archivePath, version, rid);
            var archiveBytes = await File.ReadAllBytesAsync(archivePath);
            var vendorDigest = Hash(SHA512.HashData(archiveBytes));
            var identity = new RuntimeExecutionRuntimeIdentity(
                ConfiguredOfficialSourceId.DotNet,
                SunderPackageFormat.DotNetExecutionRuntimeId,
                version,
                rid,
                SunderPackageFormat.DotNetWorkerModuleAbi,
                RuntimeExecutionRuntimeChecksumAlgorithm.Sha512,
                vendorDigest);
            var distribution = new RuntimeExecutionRuntimeDistribution(
                DotNetDownloadUri(version, rid).AbsoluteUri,
                OfficialRuntimeSourcePolicy.MetadataUri(ConfiguredOfficialSourceId.DotNet).AbsoluteUri,
                new string('a', 64),
                ChecksumUrl: null,
                ChecksumDocumentSha256: null,
                rid.StartsWith("win-", StringComparison.Ordinal)
                    ? RuntimeExecutionRuntimeArchiveFormat.Zip
                    : RuntimeExecutionRuntimeArchiveFormat.TarGzip,
                OfficialRuntimeSourcePolicy.Profile(ConfiguredOfficialSourceId.DotNet),
                archiveBytes.LongLength);
            var support = new RuntimeExecutionRuntimeSupportStatus(
                RuntimeExecutionRuntimeSupportState.Supported,
                "10.0 LTS",
                ObservedAtUtc,
                EndOfLifeAtUtc: null,
                Message: null);
            var artifact = new RuntimeExecutionRuntimeVendorArtifact(identity, distribution, support);
            var layout = new ExecutionRuntimeStoreLayout(Path.Combine(root, "state"));
            var store = new ExecutionRuntimeStore(
                layout,
                new TestExecutionRuntimeGarbageCollectionAuthority(),
                timeProvider: new FixedTimeProvider(ObservedAtUtc),
                telemetry: telemetry,
                materializer: new OfficialRuntimeMaterializer(new NoopPlatformProbe()));
            return new DownloaderFixture(root, archiveBytes, artifact, layout, store, telemetry, rid);
        }

        public Uri OfficialDownloadUri(string host = "builds.dotnet.microsoft.com", string? version = null)
            => DotNetDownloadUri(version ?? Artifact.Identity.VendorVersion, Rid, host);

        public RecordingHandler CreateArtifactHandler()
            => new((request, _) => Task.FromResult(Response(request, new ByteArrayContent(ArchiveBytes))));

        public ExecutionRuntimeArtifactDownloader CreateDownloader(HttpMessageHandler handler, TimeProvider? clock = null)
        {
            _factory?.Dispose();
            var factory = new TestHttpClientFactory(handler);
            _factory = factory;
            return new ExecutionRuntimeArtifactDownloader(
                new OfficialRuntimeHttpClient(factory),
                Store,
                clock ?? new FixedTimeProvider(ObservedAtUtc),
                Telemetry);
        }

        public void AssertNoStorePublication(RuntimeExecutionRuntimeIdentity? identity = null)
        {
            var value = identity ?? Artifact.Identity;
            var storeIdentity = ExecutionRuntimeIdentity.FromContract(value);
            Assert.False(File.Exists(Layout.GetReceiptPath(storeIdentity)));
            Assert.False(File.Exists(Layout.GetArchivePath(ArchiveSha256)));
        }

        public void AssertNoTransientState()
        {
            Assert.Empty(Directory.Exists(Layout.StagingRootPath) ? Directory.EnumerateFileSystemEntries(Layout.StagingRootPath) : []);
            Assert.Empty(Directory.Exists(Layout.TransactionRootPath) ? Directory.EnumerateFileSystemEntries(Layout.TransactionRootPath) : []);
        }

        public void Dispose()
        {
            _factory?.Dispose();
            _ = ExecutionRuntimeRootFileSystem.TryDeleteOwnedTree(RootPath);
        }

        private static Uri DotNetDownloadUri(string version, string rid, string host = "builds.dotnet.microsoft.com")
            => new($"https://{host}/dotnet/Runtime/{version}/{OfficialRuntimeSourcePolicy.ArchiveName(ConfiguredOfficialSourceId.DotNet, version, rid)}");

        private static async Task CreateDotNetArchiveAsync(string path, string version, string rid)
        {
            var launcher = rid.StartsWith("win-", StringComparison.Ordinal) ? "dotnet.exe" : "dotnet";
            var coreLib = $"shared/Microsoft.NETCore.App/{version}/System.Private.CoreLib.dll";
            if (rid.StartsWith("win-", StringComparison.Ordinal))
            {
                await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                await WriteZipEntryAsync(zip, launcher, [0x4d, 0x5a, 0x01, 0x02]);
                await WriteZipEntryAsync(zip, coreLib, [0x42, 0x53, 0x4a, 0x42]);
                await WriteZipEntryAsync(zip, "LICENSE.txt", [0x4c]);
                return;
            }

            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            await using var gzip = new GZipStream(file, CompressionLevel.NoCompression, leaveOpen: true);
            using var tar = new System.Formats.Tar.TarWriter(gzip, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: true);
            await WriteTarEntryAsync(tar, launcher, [0x7f, 0x45, 0x4c, 0x46]);
            await WriteTarEntryAsync(tar, coreLib, [0x42, 0x53, 0x4a, 0x42]);
            await WriteTarEntryAsync(tar, "LICENSE.txt", [0x4c]);
        }

        private static async Task WriteZipEntryAsync(ZipArchive zip, string name, byte[] bytes)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
            entry.LastWriteTime = DateTimeOffset.UnixEpoch;
            await using var output = entry.Open();
            await output.WriteAsync(bytes);
        }

        private static async Task WriteTarEntryAsync(System.Formats.Tar.TarWriter tar, string name, byte[] bytes)
        {
            using var data = new MemoryStream(bytes);
            await tar.WriteEntryAsync(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, name)
            {
                DataStream = data,
                ModificationTime = DateTimeOffset.UnixEpoch,
            });
        }
    }

    private sealed class NoopPlatformProbe : IOfficialRuntimePlatformProbe
    {
        public Task VerifyAsync(RuntimeExecutionRuntimeVendorArtifact artifact, string rootPath, string launcherRelativePath, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.True(File.Exists(Path.Combine(rootPath, launcherRelativePath.Replace('/', Path.DirectorySeparatorChar))));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return send(request, cancellationToken);
        }
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(handler, disposeHandler: true);
        public HttpClient CreateClient(string name) => _client;
        public void Dispose() => _client.Dispose();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        public int PendingTimerCount { get { lock (_gate) return _timers.Count(timer => !timer.Disposed); } }
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            lock (_gate) _timers.Add(timer);
            return timer;
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            public bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
            public void Fire() => callback(state);
        }
    }

    private sealed class UnknownLengthContent(Stream stream) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context) => stream.CopyToAsync(destination);
        protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context, CancellationToken cancellationToken)
            => stream.CopyToAsync(destination, cancellationToken);
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult(stream);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { if (disposing) stream.Dispose(); base.Dispose(disposing); }
    }

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public long BytesRead { get; private set; }
        public bool IsDisposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }
        protected override void Dispose(bool disposing) { IsDisposed = true; base.Dispose(disposing); }
    }

    private sealed class PrefixThenBlockingStream(ReadOnlyMemory<byte> prefix, ManualResetEventSlim? disposeRelease = null) : Stream
    {
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;
        private int _disposeStartedFlag;
        public Task Blocked => _blocked.Task;
        public Task DisposeStarted => _disposeStarted.Task;
        public bool IsDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                prefix.CopyTo(buffer);
                return prefix.Length;
            }
            _blocked.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposeStartedFlag, 1) == 0)
            {
                _disposeStarted.TrySetResult();
                disposeRelease?.Wait();
            }
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class PrefixThenThrowingStream(ReadOnlyMemory<byte> prefix, Exception failure) : Stream
    {
        private int _reads;
        public bool IsDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _reads) == 1)
            {
                prefix.CopyTo(buffer);
                return ValueTask.FromResult(prefix.Length);
            }
            return ValueTask.FromException<int>(failure);
        }
        protected override void Dispose(bool disposing) { IsDisposed = true; base.Dispose(disposing); }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(bytes);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed >= TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("Timed out waiting for downloader test condition.");
            }
            await Task.Delay(10);
        }
    }
}
