using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class ExecutionRuntimeArtifactDownloaderTests
{
    private const string RegistryOriginValue = "https://registry.example/";
    private const string ArtifactUrl = "https://registry.example/artifacts/dotnet.sunderruntime";
    private const string ReleaseKeyId = "sunder.release.downloader-test.v1";
    private const string AttestationKeyId = "sunder.ci.downloader-test.v1";
    private static readonly DateTimeOffset PolicyNow = DateTimeOffset.Parse("2026-08-28T12:00:00Z");
    private static readonly byte[] ReleaseKey =
        Enumerable.Range(1, SunderExecutionRuntimeFormat.Ed25519PublicKeyBytes)
            .Select(static value => (byte)value)
            .ToArray();

    [Fact]
    public async Task DownloadAsync_InstallsExactProductionRegistryPlanAndCleansTemporaryFile()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var handler = fixture.CreateArtifactHandler();
        var downloader = fixture.CreateDownloader(handler);

        var result = await downloader.DownloadAsync(
            new Uri("https://REGISTRY.example"),
            fixture.Artifact,
            [],
            CancellationToken.None);

        Assert.False(result.AlreadyPresent);
        Assert.Equal(fixture.Artifact.Identity.RuntimeId, result.Receipt.Identity.RuntimeId);
        Assert.Equal(fixture.Artifact.ArtifactSha256, result.Receipt.ArtifactSha256);
        Assert.Equal(fixture.Artifact.ManifestSha256, result.Receipt.ManifestSha256);
        Assert.Equal(fixture.Artifact.NativeAttestationSha256, result.Receipt.NativeAttestationSha256);
        Assert.Equal(fixture.Artifact.NativeAttestationKeyId, result.Receipt.NativeAttestationKeyId);
        Assert.Equal(ExecutionRuntimeTrustEnvironment.Production, result.Receipt.TrustEnvironment);
        Assert.Equal(RuntimeExecutionRuntimeAcquisitionSources.Registry, result.Receipt.AcquisitionSource);
        Assert.Equal(RegistryOriginValue, result.Receipt.RegistryOrigin);
        Assert.True(File.Exists(fixture.Layout.GetReceiptPath(result.Receipt.Identity)));
        Assert.True(fixture.SignatureVerifier.Count > 0);
        Assert.Equal(1, handler.RequestCount);
        fixture.AssertTemporaryRootEmpty();
    }

    [Fact]
    public async Task DownloadAsync_ClassifiesHostConfiguredDevelopmentKeyAndPreservesRegistryOrigin()
    {
        using var fixture = await DownloaderFixture.CreateAsync(developmentTrustEnabled: true);
        var handler = fixture.CreateArtifactHandler();

        var result = await fixture.CreateDownloader(handler).DownloadAsync(
            new Uri("https://REGISTRY.example"),
            fixture.Artifact,
            [],
            CancellationToken.None);

        Assert.False(result.AlreadyPresent);
        Assert.Equal(ExecutionRuntimeTrustEnvironment.Development, result.Receipt.TrustEnvironment);
        Assert.Equal(RuntimeExecutionRuntimeAcquisitionSources.Development, result.Receipt.AcquisitionSource);
        Assert.Equal(RegistryOriginValue, result.Receipt.RegistryOrigin);
        Assert.Equal(1, handler.RequestCount);
        fixture.AssertTemporaryRootEmpty();
    }

    [Fact]
    public async Task DownloadAsync_ProductionHostRejectsDevelopmentSigningKeyBeforeHttpDownload()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var handler = fixture.CreateArtifactHandler();
        var developmentResponse = fixture.Artifact with
        {
            SigningKeyId = "sunder.release.development.v1",
        };

        await Assert.ThrowsAsync<ExecutionRuntimeTrustException>(() =>
            fixture.CreateDownloader(handler).DownloadAsync(
                new Uri(RegistryOriginValue),
                developmentResponse,
                [],
                CancellationToken.None));

        Assert.Equal(0, handler.RequestCount);
        fixture.AssertNoStorePublication();
        fixture.AssertTemporaryRootEmpty();
    }

    [Fact]
    public async Task DownloadAsync_EmitsCorrelatedDownloadAndStorePhasesWithSafeStructuredFields()
    {
        var logs = new RecordingLogSink();
        var telemetry = new ExecutionRuntimePhaseTelemetry(
            logs.CreateLogger<ExecutionRuntimePhaseTelemetry>());
        using var fixture = await DownloaderFixture.CreateAsync(telemetry);
        var downloader = fixture.CreateDownloader(fixture.CreateArtifactHandler());

        var result = await downloader.DownloadAsync(
            new Uri(RegistryOriginValue),
            fixture.Artifact,
            [],
            CancellationToken.None);

        var expectedPhases = new[]
        {
            ExecutionRuntimePhaseNames.Download,
            ExecutionRuntimePhaseNames.Inspect,
            ExecutionRuntimePhaseNames.Extract,
            ExecutionRuntimePhaseNames.PublishRoot,
            ExecutionRuntimePhaseNames.HealthCheck,
        };
        var entries = RecordedRuntimePhaseAssertions.Entries(logs);
        Assert.Equal(expectedPhases.Order(StringComparer.Ordinal), entries
            .Select(entry => Assert.IsType<string>(entry.Properties["RuntimePhase"]))
            .Order(StringComparer.Ordinal));

        var correlationIds = new HashSet<Guid>();
        foreach (var phase in expectedPhases)
        {
            var entry = RecordedRuntimePhaseAssertions.Single(logs, phase);
            correlationIds.Add(RecordedRuntimePhaseAssertions.Completed(entry, phase));
            RecordedRuntimePhaseAssertions.Identity(entry, result.Receipt.Identity);
            Assert.Equal(false, entry.Properties["CacheHit"]);
            Assert.True(Assert.IsType<long>(entry.Properties["Bytes"]) > 0);
        }
        Assert.Single(correlationIds);
        var download = RecordedRuntimePhaseAssertions.Single(logs, ExecutionRuntimePhaseNames.Download);
        Assert.Equal(fixture.Artifact.ArtifactSize, download.Properties["Bytes"]);
    }

    [Fact]
    public async Task DownloadAsync_FailureTelemetryDoesNotExposeUrlTokenOrStorePaths()
    {
        const string secret = "runtime-download-secret-sentinel";
        var logs = new RecordingLogSink();
        var telemetry = new ExecutionRuntimePhaseTelemetry(
            logs.CreateLogger<ExecutionRuntimePhaseTelemetry>());
        using var fixture = await DownloaderFixture.CreateAsync(telemetry);
        var artifact = fixture.Artifact with { DownloadUrl = ArtifactUrl + "?token=" + secret };
        var handler = new RecordingHandler((request, _) => Task.FromResult(Response(
            request,
            HttpStatusCode.Forbidden)));

        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.CreateDownloader(handler).DownloadAsync(
            new Uri(RegistryOriginValue),
            artifact,
            [],
            CancellationToken.None));

        var entry = RecordedRuntimePhaseAssertions.Single(logs, ExecutionRuntimePhaseNames.Download);
        RecordedRuntimePhaseAssertions.Completed(entry, ExecutionRuntimePhaseNames.Download, "failed");
        RecordedRuntimePhaseAssertions.Identity(entry, new ExecutionRuntimeIdentity(
            artifact.Identity.RuntimeId,
            artifact.Identity.Version,
            artifact.Identity.PackagingRevision,
            artifact.Identity.Rid,
            artifact.Identity.ModuleAbi));
        Assert.Equal(0L, entry.Properties["Bytes"]);
        Assert.Equal(false, entry.Properties["CacheHit"]);
        var flattened = RecordedRuntimePhaseAssertions.Flatten(logs);
        Assert.DoesNotContain(secret, flattened, StringComparison.Ordinal);
        Assert.DoesNotContain(artifact.DownloadUrl, flattened, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.RootPath, flattened, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.TemporaryRootPath, flattened, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadAsync_ReusesHealthyExactReceiptWithoutAnotherRequest()
    {
        var logs = new RecordingLogSink();
        var telemetry = new ExecutionRuntimePhaseTelemetry(
            logs.CreateLogger<ExecutionRuntimePhaseTelemetry>());
        using var fixture = await DownloaderFixture.CreateAsync(telemetry);
        var handler = fixture.CreateArtifactHandler();
        var downloader = fixture.CreateDownloader(handler);
        var first = await downloader.DownloadAsync(
            new Uri(RegistryOriginValue),
            fixture.Artifact,
            [],
            CancellationToken.None);
        logs.Clear();

        var reused = await downloader.DownloadAsync(
            new Uri(RegistryOriginValue),
            fixture.Artifact,
            [],
            CancellationToken.None);

        Assert.False(first.AlreadyPresent);
        Assert.True(reused.AlreadyPresent);
        Assert.Equal(first.Receipt.ContentIdentity, reused.Receipt.ContentIdentity);
        Assert.Equal(1, handler.RequestCount);
        var download = RecordedRuntimePhaseAssertions.Single(logs, ExecutionRuntimePhaseNames.Download);
        RecordedRuntimePhaseAssertions.Completed(download, ExecutionRuntimePhaseNames.Download);
        Assert.Equal(0L, download.Properties["Bytes"]);
        Assert.Equal(true, download.Properties["CacheHit"]);
        fixture.AssertTemporaryRootEmpty();
    }

    [Fact]
    public async Task DownloadAsync_AssociatesHealthyExactLocalImportWithoutDownloadingOrRewritingReceipt()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var imported = await fixture.ImportLocalAsync();
        var receiptPath = fixture.Layout.GetReceiptPath(imported.Receipt.Identity);
        var receiptBytes = await File.ReadAllBytesAsync(receiptPath);
        var handler = fixture.CreateArtifactHandler();

        var reused = await fixture.CreateDownloader(handler).DownloadAsync(
            new Uri(RegistryOriginValue),
            fixture.Artifact,
            [],
            CancellationToken.None);

        Assert.True(reused.AlreadyPresent);
        Assert.Equal(RuntimeExecutionRuntimeAcquisitionSources.LocalImport, reused.Receipt.AcquisitionSource);
        Assert.Null(reused.Receipt.RegistryOrigin);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(receiptBytes, await File.ReadAllBytesAsync(receiptPath));
        fixture.AssertTemporaryRootEmpty();
    }

    [Fact]
    public async Task DownloadAsync_DoesNotAssociateMismatchedLocalImport()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var imported = await fixture.ImportLocalAsync();
        var receiptPath = fixture.Layout.GetReceiptPath(imported.Receipt.Identity);
        var receiptBytes = await File.ReadAllBytesAsync(receiptPath);
        var handler = fixture.CreateArtifactHandler();
        var mismatched = fixture.Artifact with { ArtifactSha256 = new string('f', 64) };

        await Assert.ThrowsAsync<ExecutionRuntimeStoreConflictException>(() =>
            fixture.CreateDownloader(handler).DownloadAsync(
                new Uri(RegistryOriginValue),
                mismatched,
                [],
                CancellationToken.None));

        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(receiptBytes, await File.ReadAllBytesAsync(receiptPath));
    }

    [Fact]
    public async Task DownloadAsync_DoesNotReplaceUnhealthyExactLocalImport()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var imported = await fixture.ImportLocalAsync();
        var launcherPath = Path.Combine(
            fixture.Layout.GetContentRootPath(imported.Receipt.ContentIdentity),
            imported.Receipt.LauncherPath.Replace('/', Path.DirectorySeparatorChar));
        ExecutionRuntimeRootFileSystem.MakeOwnedTreeWritable(
            fixture.Layout.GetContentRootPath(imported.Receipt.ContentIdentity));
        await File.WriteAllBytesAsync(launcherPath, [0x00]);
        ExecutionRuntimeRootFileSystem.Seal(
            fixture.Layout.GetContentRootPath(imported.Receipt.ContentIdentity),
            launcherPath);
        var handler = fixture.CreateArtifactHandler();

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.CreateDownloader(handler).DownloadAsync(
                new Uri(RegistryOriginValue),
                fixture.Artifact,
                [],
                CancellationToken.None));

        Assert.Contains("unhealthy", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(
            RuntimeExecutionRuntimeAcquisitionSources.LocalImport,
            (await fixture.Store.GetReceiptAsync(imported.Receipt.Identity))?.AcquisitionSource);
    }

    [Fact]
    public async Task DownloadAsync_ConcurrentExactRequestsPerformOneNetworkAcquisition()
    {
        var logs = new RecordingLogSink();
        var telemetry = new ExecutionRuntimePhaseTelemetry(
            logs.CreateLogger<ExecutionRuntimePhaseTelemetry>());
        using var fixture = await DownloaderFixture.CreateAsync(telemetry);
        var handler = fixture.CreateArtifactHandler();
        var downloader = fixture.CreateDownloader(handler);

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => downloader.DownloadAsync(
            new Uri(RegistryOriginValue),
            fixture.Artifact,
            [],
            CancellationToken.None)));

        Assert.Equal(1, handler.RequestCount);
        Assert.All(results, result => Assert.False(result.AlreadyPresent));
        Assert.All(results, result => Assert.Equal(results[0].Receipt.ContentIdentity, result.Receipt.ContentIdentity));
        var downloads = RecordedRuntimePhaseAssertions.Entries(logs)
            .Where(entry => Equals(
                entry.Properties["RuntimePhase"],
                ExecutionRuntimePhaseNames.Download))
            .ToArray();
        Assert.Equal(16, downloads.Length);
        var cacheMiss = Assert.Single(downloads, entry => Equals(entry.Properties["CacheHit"], false));
        Assert.Equal(fixture.Artifact.ArtifactSize, cacheMiss.Properties["Bytes"]);
        var cacheHits = downloads.Where(entry => Equals(entry.Properties["CacheHit"], true)).ToArray();
        Assert.Equal(15, cacheHits.Length);
        Assert.All(cacheHits, entry => Assert.Equal(0L, entry.Properties["Bytes"]));
        fixture.AssertTemporaryRootEmpty();
    }

    [Fact]
    public async Task DownloadAsync_RetriesTransientTransportAndHttpFailuresThenSucceeds()
    {
        var logs = new RecordingLogSink();
        var telemetry = new ExecutionRuntimePhaseTelemetry(
            logs.CreateLogger<ExecutionRuntimePhaseTelemetry>());
        using var fixture = await DownloaderFixture.CreateAsync(telemetry);
        var failedBody = new PrefixThenThrowingStream(
            fixture.ArchiveBytes.AsMemory(0, 32),
            new HttpRequestException("The response transport failed."));
        var attempt = 0;
        var handler = new RecordingHandler((request, _) =>
        {
            var currentAttempt = Interlocked.Increment(ref attempt);
            if (currentAttempt > 1)
            {
                Assert.True(failedBody.IsDisposed);
                Assert.Single(Directory.EnumerateDirectories(fixture.TemporaryRootPath));
                Assert.Empty(Directory.EnumerateFiles(
                    fixture.TemporaryRootPath,
                    "*",
                    SearchOption.AllDirectories));
            }

            return Task.FromResult(currentAttempt switch
            {
                1 => Response(request, new UnknownLengthContent(failedBody)),
                2 => Response(request, HttpStatusCode.ServiceUnavailable),
                _ => Response(request, new ByteArrayContent(fixture.ArchiveBytes)),
            });
        });

        var result = await fixture.CreateDownloader(handler).DownloadAsync(
            new Uri(RegistryOriginValue),
            fixture.Artifact,
            [],
            CancellationToken.None);

        Assert.False(result.AlreadyPresent);
        Assert.Equal(3, handler.RequestCount);
        Assert.True(failedBody.IsDisposed);
        var download = RecordedRuntimePhaseAssertions.Single(logs, ExecutionRuntimePhaseNames.Download);
        RecordedRuntimePhaseAssertions.Completed(download, ExecutionRuntimePhaseNames.Download);
        Assert.Equal(fixture.Artifact.ArtifactSize + 32, download.Properties["Bytes"]);
        Assert.Equal(false, download.Properties["CacheHit"]);
        fixture.AssertTemporaryRootEmpty();
    }

    [Fact]
    public async Task DownloadAsync_StopsAfterTransientFailureAttemptsAreExhausted()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var handler = new RecordingHandler((request, _) => Task.FromResult(Response(
            request,
            HttpStatusCode.ServiceUnavailable)));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.CreateDownloader(handler).DownloadAsync(
                new Uri(RegistryOriginValue),
                fixture.Artifact,
                [],
                CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal(3, handler.RequestCount);
        fixture.AssertNoStorePublication();
        fixture.AssertTemporaryRootEmpty();
    }

    [Fact]
    public async Task DownloadAsync_CancellationDuringRetryDelayStopsFurtherAttempts()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var timeProvider = new ManualTimeProvider(PolicyNow);
        var handler = new RecordingHandler((request, _) => Task.FromResult(Response(
            request,
            HttpStatusCode.TooManyRequests)));
        using var cancellation = new CancellationTokenSource();

        var pending = fixture.CreateDownloader(handler, timeProvider).DownloadAsync(
            new Uri(RegistryOriginValue),
            fixture.Artifact,
            [],
            cancellation.Token);
        await WaitUntilAsync(() => timeProvider.PendingTimerCount == 1);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, handler.RequestCount);
        fixture.AssertNoStorePublication();
        fixture.AssertTemporaryRootEmpty();
    }

    [Fact]
    public async Task DownloadAsync_DoesNotRetryPermanentHttpFailure()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var handler = new RecordingHandler((request, _) => Task.FromResult(Response(
            request,
            HttpStatusCode.NotFound)));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.CreateDownloader(handler).DownloadAsync(
                new Uri(RegistryOriginValue),
                fixture.Artifact,
                [],
                CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Equal(1, handler.RequestCount);
        fixture.AssertNoStorePublication();
        fixture.AssertTemporaryRootEmpty();
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
            fixture.CreateDownloader(handler).DownloadAsync(
                new Uri(RegistryOriginValue),
                fixture.Artifact,
                [],
                CancellationToken.None));

        Assert.Contains("content length", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, body.BytesRead);
        Assert.True(body.IsDisposed);
        fixture.AssertNoStorePublication();
        fixture.AssertTemporaryRootEmpty();
    }

    [Fact]
    public async Task DownloadAsync_RejectsWrongDownloadedHash()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var wrongHash = new string(fixture.Artifact.ArtifactSha256[0] == 'a' ? 'b' : 'a', 64);
        var artifact = fixture.Artifact with { ArtifactSha256 = wrongHash };
        var handler = fixture.CreateArtifactHandler();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.CreateDownloader(handler).DownloadAsync(
                new Uri(RegistryOriginValue),
                artifact,
                [],
                CancellationToken.None));

        Assert.Contains("SHA-256", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.RequestCount);
        fixture.AssertNoStorePublication(artifact.Identity);
        fixture.AssertTemporaryRootEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadAsync_RejectsPartialAndOversizeStreams(bool oversize)
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var body = oversize
            ? [.. fixture.ArchiveBytes, (byte)0x42]
            : fixture.ArchiveBytes[..^1];
        var handler = new RecordingHandler((request, _) => Task.FromResult(Response(
            request,
            new UnknownLengthContent(new MemoryStream(body)))));

        var exception = await Record.ExceptionAsync(() => fixture.CreateDownloader(handler).DownloadAsync(
            new Uri(RegistryOriginValue),
            fixture.Artifact,
            [],
            CancellationToken.None));

        if (oversize)
        {
            Assert.IsType<RegistryArtifactTooLargeException>(exception);
        }
        else
        {
            Assert.IsType<InvalidDataException>(exception);
            Assert.Contains("ended before", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        fixture.AssertNoStorePublication();
        fixture.AssertTemporaryRootEmpty();
    }

    [Fact]
    public async Task DownloadAsync_LastWaiterCancellationWaitsForTerminalTelemetryAndPartialCleanup()
    {
        var logs = new RecordingLogSink();
        var telemetry = new ExecutionRuntimePhaseTelemetry(
            logs.CreateLogger<ExecutionRuntimePhaseTelemetry>());
        using var fixture = await DownloaderFixture.CreateAsync(telemetry);
        using var allowStreamDisposal = new ManualResetEventSlim(initialState: false);
        var body = new PrefixThenBlockingStream(
            fixture.ArchiveBytes.AsMemory(0, 32),
            allowStreamDisposal);
        var handler = new RecordingHandler((request, _) => Task.FromResult(Response(
            request,
            new UnknownLengthContent(body))));
        using var cancellation = new CancellationTokenSource();

        var pending = fixture.CreateDownloader(handler).DownloadAsync(
            new Uri(RegistryOriginValue),
            fixture.Artifact,
            [],
            cancellation.Token);
        try
        {
            await body.Blocked.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEmpty(Directory.EnumerateFiles(
                fixture.TemporaryRootPath,
                "*",
                SearchOption.AllDirectories));
            cancellation.Cancel();
            await body.DisposeStarted.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(pending.IsCompleted);
            Assert.DoesNotContain(RecordedRuntimePhaseAssertions.Entries(logs), entry => Equals(
                entry.Properties["RuntimePhase"],
                ExecutionRuntimePhaseNames.Download));
            Assert.NotEmpty(Directory.EnumerateFiles(
                fixture.TemporaryRootPath,
                "*",
                SearchOption.AllDirectories));

            allowStreamDisposal.Set();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.True(body.IsDisposed);
            var download = RecordedRuntimePhaseAssertions.Single(logs, ExecutionRuntimePhaseNames.Download);
            RecordedRuntimePhaseAssertions.Completed(
                download,
                ExecutionRuntimePhaseNames.Download,
                "canceled");
            Assert.Equal(32L, download.Properties["Bytes"]);
            Assert.Equal(false, download.Properties["CacheHit"]);
            fixture.AssertNoStorePublication();
            fixture.AssertTemporaryRootEmpty();
        }
        finally
        {
            allowStreamDisposal.Set();
            try
            {
                await pending.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task DownloadAsync_RejectsUntrustedArtifactUrlWithoutSendingRequest()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var handler = fixture.CreateArtifactHandler();
        var artifact = fixture.Artifact with
        {
            DownloadUrl = "https://untrusted.example/runtime.sunderruntime",
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateDownloader(handler).DownloadAsync(
            new Uri(RegistryOriginValue),
            artifact,
            [],
            CancellationToken.None));

        Assert.Equal(0, handler.RequestCount);
        fixture.AssertTemporaryRootEmpty();
    }

    [Fact]
    public async Task DownloadAsync_RejectsResponseRedirectedFromTrustedArtifactOrigin()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var artifact = fixture.Artifact with
        {
            DownloadUrl = "https://artifacts.example/runtime.sunderruntime",
        };
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://untrusted.example/runtime.sunderruntime"),
            Content = new ByteArrayContent(fixture.ArchiveBytes),
        }));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.CreateDownloader(handler).DownloadAsync(
                new Uri(RegistryOriginValue),
                artifact,
                ["https://artifacts.example/"],
                CancellationToken.None));

        Assert.Contains("untrusted origin", exception.Message, StringComparison.OrdinalIgnoreCase);
        fixture.AssertNoStorePublication();
        fixture.AssertTemporaryRootEmpty();
    }

    [Theory]
    [InlineData("http://artifacts.example/")]
    [InlineData("https://user@artifacts.example/")]
    [InlineData("https://artifacts.example/not-an-origin")]
    public async Task DownloadAsync_RejectsInvalidAdvertisedArtifactOrigins(string advertisedOrigin)
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var handler = fixture.CreateArtifactHandler();

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateDownloader(handler).DownloadAsync(
            new Uri(RegistryOriginValue),
            fixture.Artifact,
            [advertisedOrigin],
            CancellationToken.None));

        Assert.Equal(0, handler.RequestCount);
        fixture.AssertTemporaryRootEmpty();
    }

    [Theory]
    [InlineData("http://localhost:5288/")]
    [InlineData("https://user@registry.example/")]
    public async Task DownloadAsync_RequiresTrustedHttpsRegistryOrigin(string origin)
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var handler = fixture.CreateArtifactHandler();

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateDownloader(handler).DownloadAsync(
            new Uri(origin),
            fixture.Artifact,
            [],
            CancellationToken.None));

        Assert.Equal(0, handler.RequestCount);
        fixture.AssertTemporaryRootEmpty();
    }

    [Fact]
    public async Task DownloadAsync_AllowsExplicitDevelopmentLoopbackRegistryOrigin()
    {
        using var fixture = await DownloaderFixture.CreateAsync(
            developmentTrustEnabled: true,
            developmentAllowInsecureLoopbackRegistry: true);
        var handler = fixture.CreateArtifactHandler();
        var artifact = fixture.Artifact with
        {
            DownloadUrl = "/artifacts/dotnet.sunderruntime",
        };

        var result = await fixture.CreateDownloader(
                handler,
                developmentAllowInsecureLoopbackRegistry: true)
            .DownloadAsync(
                new Uri("http://127.0.0.1:5288/"),
                artifact,
                ["http://127.0.0.1:5288/"],
                CancellationToken.None);

        Assert.Equal(ExecutionRuntimeTrustEnvironment.Development, result.Receipt.TrustEnvironment);
        Assert.Equal(1, handler.RequestCount);
        fixture.AssertTemporaryRootEmpty();
    }

    [Fact]
    public async Task DownloadAsync_DelegatesPlanManifestMismatchToStoreInspection()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var mismatched = fixture.Artifact with
        {
            Identity = fixture.Artifact.Identity with { Version = "10.0.2" },
        };
        var handler = fixture.CreateArtifactHandler();

        await Assert.ThrowsAsync<ExecutionRuntimePlanMismatchException>(() =>
            fixture.CreateDownloader(handler).DownloadAsync(
                new Uri(RegistryOriginValue),
                mismatched,
                [],
                CancellationToken.None));

        Assert.Equal(1, handler.RequestCount);
        Assert.True(fixture.SignatureVerifier.Count > 0);
        fixture.AssertNoStorePublication(mismatched.Identity);
        fixture.AssertTemporaryRootEmpty();
    }

    [Theory]
    [InlineData(ExecutionRuntimeLifecycleStates.Staged)]
    [InlineData(ExecutionRuntimeLifecycleStates.Retired)]
    [InlineData(ExecutionRuntimeLifecycleStates.Revoked)]
    public async Task DownloadAsync_RejectsLifecycleThatDoesNotPermitInstallation(string state)
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var artifact = fixture.Artifact with
        {
            Lifecycle = fixture.Artifact.Lifecycle with { State = state },
        };
        var handler = fixture.CreateArtifactHandler();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.CreateDownloader(handler).DownloadAsync(
                new Uri(RegistryOriginValue),
                artifact,
                [],
                CancellationToken.None));

        Assert.Contains("lifecycle", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_RejectsDeprecatedRuntimeAtInstallBlockDeadline()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var artifact = fixture.Artifact with
        {
            Lifecycle = fixture.Artifact.Lifecycle with
            {
                State = ExecutionRuntimeLifecycleStates.Deprecated,
                DeprecatedAtUtc = PolicyNow.AddHours(-1),
                InstallBlockedAtUtc = PolicyNow,
            },
        };
        var handler = fixture.CreateArtifactHandler();

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateDownloader(handler).DownloadAsync(
            new Uri(RegistryOriginValue),
            artifact,
            [],
            CancellationToken.None));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_RejectsArtifactForDifferentRid()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var alternateRid = SunderExecutionRuntimeFormat.SupportedRuntimeIdentifiers.First(rid =>
            !string.Equals(rid, fixture.Artifact.Identity.Rid, StringComparison.Ordinal));
        var artifact = fixture.Artifact with
        {
            Identity = fixture.Artifact.Identity with { Rid = alternateRid },
        };
        var handler = fixture.CreateArtifactHandler();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.CreateDownloader(handler).DownloadAsync(
                new Uri(RegistryOriginValue),
                artifact,
                [],
                CancellationToken.None));

        Assert.Contains("current Host RID", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_RejectsRuntimeIdAndModuleAbiMismatch()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var artifact = fixture.Artifact with
        {
            Identity = fixture.Artifact.Identity with { ModuleAbi = SunderExecutionRuntimeFormat.NodeModuleAbi },
        };
        var handler = fixture.CreateArtifactHandler();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.CreateDownloader(handler).DownloadAsync(
                new Uri(RegistryOriginValue),
                artifact,
                [],
                CancellationToken.None));

        Assert.Contains("module ABI", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("manifest-sha")]
    [InlineData("artifact-sha")]
    [InlineData("signing-key")]
    [InlineData("attestation-sha")]
    [InlineData("attestation-key")]
    public async Task DownloadAsync_RejectsNoncanonicalTrustEvidence(string field)
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var artifact = field switch
        {
            "manifest-sha" => fixture.Artifact with
            {
                ManifestSha256 = fixture.Artifact.ManifestSha256.ToUpperInvariant(),
            },
            "artifact-sha" => fixture.Artifact with
            {
                ArtifactSha256 = fixture.Artifact.ArtifactSha256.ToUpperInvariant(),
            },
            "signing-key" => fixture.Artifact with { SigningKeyId = ReleaseKeyId.ToUpperInvariant() },
            "attestation-sha" => fixture.Artifact with
            {
                NativeAttestationSha256 = fixture.Artifact.NativeAttestationSha256.ToUpperInvariant(),
            },
            _ => fixture.Artifact with { NativeAttestationKeyId = AttestationKeyId.ToUpperInvariant() },
        };
        var handler = fixture.CreateArtifactHandler();

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateDownloader(handler).DownloadAsync(
            new Uri(RegistryOriginValue),
            artifact,
            [],
            CancellationToken.None));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_RejectsNonpositiveDeclaredArtifactSize()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var handler = fixture.CreateArtifactHandler();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.CreateDownloader(handler).DownloadAsync(
                new Uri(RegistryOriginValue),
                fixture.Artifact with { ArtifactSize = 0 },
                [],
                CancellationToken.None));

        Assert.Contains("positive", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_RejectsNonpositiveRuntimePolicyRevision()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var handler = fixture.CreateArtifactHandler();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.CreateDownloader(handler).DownloadAsync(
                new Uri(RegistryOriginValue),
                fixture.Artifact with { ObservedRuntimePolicyRevision = 0 },
                [],
                CancellationToken.None));

        Assert.Contains("policy", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_RejectsOversizedDeclaredArtifact()
    {
        using var fixture = await DownloaderFixture.CreateAsync();
        var handler = fixture.CreateArtifactHandler();

        await Assert.ThrowsAsync<RegistryArtifactTooLargeException>(() =>
            fixture.CreateDownloader(handler).DownloadAsync(
                new Uri(RegistryOriginValue),
                fixture.Artifact with { ArtifactSize = SunderExecutionRuntimeFormat.MaxArchiveBytes + 1 },
                [],
                CancellationToken.None));

        Assert.Equal(0, handler.RequestCount);
    }

    private static HttpResponseMessage Response(HttpRequestMessage request, HttpContent content)
        => new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = content,
        };

    private static HttpResponseMessage Response(HttpRequestMessage request, HttpStatusCode statusCode)
        => new(statusCode)
        {
            RequestMessage = request,
            Content = new ByteArrayContent([]),
        };

    private static byte[] CreateTestSignature(ReadOnlySpan<byte> input, ReadOnlySpan<byte> key)
    {
        var bytes = new byte[key.Length + input.Length];
        key.CopyTo(bytes);
        input.CopyTo(bytes.AsSpan(key.Length));
        return SHA512.HashData(bytes);
    }

    private sealed class DownloaderFixture : IDisposable
    {
        private readonly List<TestHttpClientFactory> _httpClientFactories = [];
        private readonly ExecutionRuntimePhaseTelemetry? _telemetry;

        private DownloaderFixture(
            string rootPath,
            string temporaryRootPath,
            byte[] archiveBytes,
            RegistryResolvedExecutionRuntimeArtifact artifact,
            ExecutionRuntimeStoreLayout layout,
            ExecutionRuntimeHostTrustService trust,
            CountingSignatureVerifier signatureVerifier,
            ExecutionRuntimeStore store,
            ExecutionRuntimePhaseTelemetry? telemetry)
        {
            RootPath = rootPath;
            TemporaryRootPath = temporaryRootPath;
            ArchiveBytes = archiveBytes;
            Artifact = artifact;
            Layout = layout;
            Trust = trust;
            SignatureVerifier = signatureVerifier;
            Store = store;
            _telemetry = telemetry;
        }

        public string RootPath { get; }
        public string TemporaryRootPath { get; }
        public byte[] ArchiveBytes { get; }
        public RegistryResolvedExecutionRuntimeArtifact Artifact { get; }
        public ExecutionRuntimeStoreLayout Layout { get; }
        public ExecutionRuntimeHostTrustService Trust { get; }
        public CountingSignatureVerifier SignatureVerifier { get; }
        public ExecutionRuntimeStore Store { get; }

        public static async Task<DownloaderFixture> CreateAsync(
            ExecutionRuntimePhaseTelemetry? telemetry = null,
            bool developmentTrustEnabled = false,
            bool developmentAllowInsecureLoopbackRegistry = false)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "SunderExecutionRuntimeDownloaderTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var artifactRoot = Path.Combine(root, "artifact");
            var sources = Path.Combine(artifactRoot, "sources");
            Directory.CreateDirectory(sources);
            var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
            var launcherPath = Path.Combine(sources, rid.StartsWith("win-", StringComparison.Ordinal)
                ? "sunder-runtime.exe"
                : "sunder-runtime");
            var runtimePath = Path.Combine(sources, "runtime.bin");
            var attestationPath = Path.Combine(sources, "native-attestation.json");
            await File.WriteAllBytesAsync(launcherPath, [0x53, 0x55, 0x4e, 0x44, 0x45, 0x52]);
            await File.WriteAllBytesAsync(runtimePath, Enumerable.Repeat((byte)0x42, 64).ToArray());

            const string runtimeVersion = "10.0.1";
            const int packagingRevision = 7;
            var upstreamSha256 = new string('a', 64);
            var nativeAttestation = new SunderExecutionRuntimeNativeAttestation
            {
                SchemaVersion = SunderExecutionRuntimeFormat.CurrentNativeAttestationVersion,
                RuntimeId = SunderExecutionRuntimeFormat.DotNetRuntimeId,
                RuntimeVersion = runtimeVersion,
                PackagingRevision = packagingRevision,
                Rid = rid,
                UpstreamSha256 = upstreamSha256,
                Workflow = new SunderExecutionRuntimeAttestationWorkflow
                {
                    Name = "sunder.runtime.release",
                    RunId = "downloader-test-run",
                    SourceRevision = new string('b', 40),
                },
                Toolchain = new SunderExecutionRuntimeAttestationToolchain
                {
                    Name = "dotnet-sdk",
                    Version = "10.0.100",
                },
                LauncherSmoke = new SunderExecutionRuntimeAttestationLauncherSmoke
                {
                    Result = SunderExecutionRuntimeFormat.LauncherSmokePassed,
                },
                Platform = new SunderExecutionRuntimeAttestationPlatform
                {
                    Signature = rid.StartsWith("linux-", StringComparison.Ordinal)
                        ? SunderExecutionRuntimeFormat.PlatformCheckNotApplicable
                        : SunderExecutionRuntimeFormat.PlatformCheckPassed,
                    Notarization = rid.StartsWith("osx-", StringComparison.Ordinal)
                        ? SunderExecutionRuntimeFormat.PlatformCheckPassed
                        : SunderExecutionRuntimeFormat.PlatformCheckNotApplicable,
                },
                Signing = new SunderExecutionRuntimeAttestationSigning
                {
                    Algorithm = SunderExecutionRuntimeFormat.SigningAlgorithm,
                    KeyId = AttestationKeyId,
                    Signature = Convert.ToBase64String(new byte[SunderExecutionRuntimeFormat.Ed25519SignatureBytes]),
                },
            };
            var nativeAttestationBytes = SunderExecutionRuntimeCanonicalJson.SerializeNativeAttestation(
                nativeAttestation);
            await File.WriteAllBytesAsync(attestationPath, nativeAttestationBytes);

            var manifest = new SunderExecutionRuntimeManifest
            {
                SchemaVersion = SunderExecutionRuntimeFormat.CurrentSchemaVersion,
                Id = SunderExecutionRuntimeFormat.DotNetRuntimeId,
                Version = runtimeVersion,
                PackagingRevision = packagingRevision,
                Rid = rid,
                ModuleAbi = SunderExecutionRuntimeFormat.DotNetModuleAbi,
                WorkerProtocol = SunderExecutionRuntimeFormat.WorkerProtocol,
                Launch = new SunderExecutionRuntimeLaunch
                {
                    Executable = "payload/bin/" + Path.GetFileName(launcherPath),
                    Arguments =
                    [
                        SunderExecutionRuntimeFormat.DotNetExecArgument,
                        SunderExecutionRuntimeFormat.PackageEntryPointPlaceholder,
                    ],
                },
                ContentIndex = SunderExecutionRuntimeFormat.ContentIndexPath,
                Upstream = new SunderExecutionRuntimeUpstream
                {
                    Name = "dotnet",
                    Version = runtimeVersion,
                    SourceUri = "https://example.test/dotnet-runtime.tar.gz",
                    SourceSha256 = upstreamSha256,
                },
                Provenance = new SunderExecutionRuntimeProvenance
                {
                    AttestationPath = SunderExecutionRuntimeFormat.NativeAttestationPath,
                    AttestationSha256 = Hash(nativeAttestationBytes),
                    AttestationKeyId = AttestationKeyId,
                },
                Licenses = ["MIT"],
                Signing = new SunderExecutionRuntimeSigning
                {
                    Algorithm = SunderExecutionRuntimeFormat.SigningAlgorithm,
                    KeyId = ReleaseKeyId,
                    Signature = SunderExecutionRuntimeFormat.SignaturePath,
                },
            };
            var archivePath = Path.Combine(artifactRoot, "runtime.sunderruntime");
            await SunderExecutionRuntimeArchiveWriter.WriteAsync(
                manifest,
                archivePath,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [manifest.Launch.Executable!] = launcherPath,
                    ["payload/runtime/runtime.bin"] = runtimePath,
                    [SunderExecutionRuntimeFormat.NativeAttestationPath] = attestationPath,
                },
                new TestSigner());
            var archiveBytes = await File.ReadAllBytesAsync(archivePath);
            var identity = new RegistryExecutionRuntimeIdentity(
                manifest.Id!,
                manifest.Version!,
                manifest.PackagingRevision,
                manifest.Rid!,
                manifest.ModuleAbi!);
            var lifecycle = new RegistryExecutionRuntimeLifecycle(
                ExecutionRuntimeLifecycleStates.Supported,
                PolicyNow.AddDays(-2),
                PolicyNow.AddDays(-1),
                DeprecatedAtUtc: null,
                InstallBlockedAtUtc: null,
                ActivationBlockedAtUtc: null,
                RevokedAtUtc: null,
                Message: null);
            var artifact = new RegistryResolvedExecutionRuntimeArtifact(
                identity,
                Hash(SunderExecutionRuntimeCanonicalJson.SerializeManifest(manifest)),
                Hash(archiveBytes),
                archiveBytes.LongLength,
                ReleaseKeyId,
                MinimumHostTrustGeneration: 1,
                manifest.Provenance.AttestationSha256!,
                AttestationKeyId,
                lifecycle,
                ObservedRuntimePolicyRevision: 7,
                ArtifactUrl);
            var layout = new ExecutionRuntimeStoreLayout(Path.Combine(root, "state"));
            var trust = new ExecutionRuntimeHostTrustService(
                "downloader-test",
                generation: 3,
                developmentTrustEnabled,
                productionKeys: developmentTrustEnabled
                    ? []
                    : [new ExecutionRuntimeHostTrustKey(ReleaseKeyId, ReleaseKey, 1)],
                developmentKeys: developmentTrustEnabled
                    ? [new ExecutionRuntimeHostTrustKey(ReleaseKeyId, ReleaseKey, 1)]
                    : null);
            var verifier = new CountingSignatureVerifier();
            var store = new ExecutionRuntimeStore(
                layout,
                trust,
                new TestExecutionRuntimeGarbageCollectionAuthority(),
                signatureVerifier: verifier,
                telemetry: telemetry,
                developmentAllowInsecureLoopbackRegistry: developmentAllowInsecureLoopbackRegistry);
            return new DownloaderFixture(
                root,
                Path.Combine(root, "temporary-downloads"),
                archiveBytes,
                artifact,
                layout,
                trust,
                verifier,
                store,
                telemetry);
        }

        public RecordingHandler CreateArtifactHandler()
            => new((request, _) => Task.FromResult(Response(
                request,
                new ByteArrayContent(ArchiveBytes))));

        public async Task<ExecutionRuntimeEnsureResult> ImportLocalAsync()
        {
            var archivePath = Path.Combine(RootPath, "local-import.sunderruntime");
            await File.WriteAllBytesAsync(archivePath, ArchiveBytes);
            var identity = Artifact.Identity;
            return await Store.ImportLocalArchiveAsync(
                new ExecutionRuntimeAcquisitionPlan(
                    new ExecutionRuntimeIdentity(
                        identity.RuntimeId,
                        identity.Version,
                        identity.PackagingRevision,
                        identity.Rid,
                        identity.ModuleAbi),
                    Artifact.ArtifactSha256,
                    Artifact.ArtifactSize,
                    Artifact.ManifestSha256,
                    ContentIdentity: null,
                    Artifact.SigningKeyId,
                    Artifact.MinimumHostTrustGeneration,
                    ExecutionRuntimeTrustEnvironment.Production,
                    RuntimeExecutionRuntimeAcquisitionSources.LocalImport,
                    RegistryOrigin: null,
                    Artifact.NativeAttestationSha256,
                    Artifact.NativeAttestationKeyId),
                archivePath);
        }

        public ExecutionRuntimeArtifactDownloader CreateDownloader(
            HttpMessageHandler handler,
            TimeProvider? timeProvider = null,
            bool developmentAllowInsecureLoopbackRegistry = false)
        {
            var factory = new TestHttpClientFactory(handler);
            _httpClientFactories.Add(factory);
            var registryClient = new RegistryHttpClient(
                factory,
                new RuntimeTransportPolicyOptions { RegistryRequestTimeout = TimeSpan.FromSeconds(30) },
                TimeProvider.System);
            return new ExecutionRuntimeArtifactDownloader(
                registryClient,
                Store,
                Trust,
                timeProvider ?? new FixedTimeProvider(PolicyNow),
                TemporaryRootPath,
                _telemetry,
                developmentAllowInsecureLoopbackRegistry);
        }

        public void AssertNoStorePublication(RegistryExecutionRuntimeIdentity? identity = null)
        {
            var value = identity ?? Artifact.Identity;
            var storeIdentity = new ExecutionRuntimeIdentity(
                value.RuntimeId,
                value.Version,
                value.PackagingRevision,
                value.Rid,
                value.ModuleAbi);
            Assert.False(File.Exists(Layout.GetReceiptPath(storeIdentity)));
            Assert.False(File.Exists(Layout.GetArchivePath(Artifact.ArtifactSha256)));
        }

        public void AssertTemporaryRootEmpty()
            => Assert.Empty(Directory.Exists(TemporaryRootPath)
                ? Directory.EnumerateFileSystemEntries(TemporaryRootPath)
                : []);

        public void Dispose()
        {
            foreach (var factory in _httpClientFactories)
            {
                factory.Dispose();
            }
            _ = ExecutionRuntimeRootFileSystem.TryDeleteOwnedTree(RootPath);
        }
    }

    private sealed class TestSigner : ISunderExecutionRuntimeArchiveSigner
    {
        public string KeyId => ReleaseKeyId;

        public byte[] Sign(ReadOnlySpan<byte> signatureInput)
            => CreateTestSignature(signatureInput, ReleaseKey);
    }

    private sealed class CountingSignatureVerifier : ISunderExecutionRuntimeArchiveSignatureVerifier
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);

        public bool Verify(
            ReadOnlySpan<byte> signatureInput,
            ReadOnlySpan<byte> signature,
            ReadOnlySpan<byte> trustedPublicKey)
        {
            Interlocked.Increment(ref _count);
            return CryptographicOperations.FixedTimeEquals(
                signature,
                CreateTestSignature(signatureInput, trustedPublicKey));
        }
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return send(request, cancellationToken);
        }
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client;

        public TestHttpClientFactory(HttpMessageHandler handler)
        {
            _client = new HttpClient(handler, disposeHandler: true);
        }

        public HttpClient CreateClient(string name) => _client;

        public void Dispose() => _client.Dispose();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class UnknownLengthContent(Stream stream) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context)
            => stream.CopyToAsync(destination);

        protected override Task SerializeToStreamAsync(
            Stream destination,
            TransportContext? context,
            CancellationToken cancellationToken)
            => stream.CopyToAsync(destination, cancellationToken);

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult(stream);

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => Task.FromResult(stream);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                stream.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public long BytesRead { get; private set; }
        public bool IsDisposed { get; private set; }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class PrefixThenBlockingStream : Stream
    {
        private readonly ReadOnlyMemory<byte> _prefix;
        private readonly ManualResetEventSlim? _disposeRelease;
        private int _reads;
        private int _disposeStartedFlag;
        public Task Blocked => _blocked.Task;
        public Task DisposeStarted => _disposeStarted.Task;
        public Task Disposed => _disposed.Task;
        public bool IsDisposed { get; private set; }
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PrefixThenBlockingStream(
            ReadOnlyMemory<byte> prefix,
            ManualResetEventSlim? disposeRelease = null)
        {
            _prefix = prefix;
            _disposeRelease = disposeRelease;
        }

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

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                _prefix.CopyTo(buffer);
                return _prefix.Length;
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
                _disposeRelease?.Wait();
            }
            IsDisposed = true;
            _disposed.TrySetResult();
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

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _reads) == 1)
            {
                prefix.CopyTo(buffer);
                return ValueTask.FromResult(prefix.Length);
            }

            return ValueTask.FromException<int>(failure);
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed >= TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("The execution-runtime download cleanup did not complete.");
            }
            await Task.Delay(10);
        }
    }
}
