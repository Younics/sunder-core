using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Package.Projections;
using Sunder.Core.TestSupport;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageSessionStartupTraceTests
{
    [Fact]
    public void SuccessfulAttempt_WritesOrderedNdjsonWithMonotonicTimingAndCumulativeBytes()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero));
        using var output = new MemoryStream();
        using var writer = PackageSessionStartupTraceWriter.Create(output, clock);
        var attempt = Assert.IsType<PackageSessionStartupTraceAttempt>(writer.BeginAttempt(baseGeneration: 7));

        clock.Advance(TimeSpan.FromMilliseconds(10));
        var installed = InstalledPackage("sunder.package.trace", "2.0.0");
        attempt.RecordSourceDiscovery([installed], []);
        var package = Assert.IsType<PackageSessionStartupTracePackage>(attempt.GetInstalledPackage(installed));

        clock.Advance(TimeSpan.FromMilliseconds(5));
        package.BeginProjection();
        package.AddIo(bytesRead: 120, bytesHashed: 80, bytesCopied: 40);
        clock.Advance(TimeSpan.FromMilliseconds(7));
        package.CompleteProjection("succeeded");

        package.BindActivation(Guid.Parse("6ce59868-477d-43f7-b84f-825c3d6c84de"));
        package.RecordProcessStartRequested();
        clock.Advance(TimeSpan.FromMilliseconds(2));
        var processStartedAtUtc = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        package.RecordProcessStarted(4242, processStartedAtUtc);
        clock.Advance(TimeSpan.FromMilliseconds(8));
        package.CompleteWorkerReady("succeeded");

        package.BeginCandidate();
        clock.Advance(TimeSpan.FromMilliseconds(3));
        package.CompleteCandidate("succeeded");
        attempt.BindCandidateGeneration(8);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(attempt.TryCommitGeneration(8));

        using var trace = ReadTrace(writer, output);
        var events = trace.Select(static record =>
            record.RootElement.GetProperty("event").GetString()
            ?? throw new InvalidDataException("Trace event name is missing.")).ToArray();
        Assert.Equal(new[]
        {
            PackageSessionStartupTraceEvents.LoadStarted,
            PackageSessionStartupTraceEvents.SourceDiscoveryCompleted,
            PackageSessionStartupTraceEvents.ProjectionPrepared,
            PackageSessionStartupTraceEvents.WorkerProcessStarted,
            PackageSessionStartupTraceEvents.WorkerReady,
            PackageSessionStartupTraceEvents.CandidateReady,
            PackageSessionStartupTraceEvents.GenerationCommitted,
        }, events);

        Assert.Equal(Enumerable.Range(1, trace.Count).Select(static value => (long)value),
            trace.Select(static record => record.RootElement.GetProperty("sequence").GetInt64()));
        Assert.Equal([0d, 10d, 22d, 24d, 32d, 35d, 36d],
            trace.Select(static record => record.RootElement.GetProperty("elapsedMilliseconds").GetDouble()));
        Assert.Equal(5d, trace[2].RootElement.GetProperty("queueDurationMilliseconds").GetDouble());
        Assert.Equal(7d, trace[2].RootElement.GetProperty("activeDurationMilliseconds").GetDouble());
        Assert.Equal(24d, trace[3].RootElement.GetProperty("processStartElapsedMilliseconds").GetDouble());
        Assert.Equal(4242, trace[3].RootElement.GetProperty("processId").GetInt32());
        Assert.Equal(24d, trace[4].RootElement.GetProperty("processStartElapsedMilliseconds").GetDouble());
        Assert.Equal(32d, trace[4].RootElement.GetProperty("processReadyElapsedMilliseconds").GetDouble());
        Assert.Equal(4242, trace[4].RootElement.GetProperty("processId").GetInt32());
        Assert.Equal(processStartedAtUtc, trace[4].RootElement.GetProperty("processStartedAtUtc").GetDateTimeOffset());
        Assert.All(trace, record => Assert.Equal(2, record.RootElement.GetProperty("schemaVersion").GetInt32()));
        Assert.Equal(120, trace[^1].RootElement.GetProperty("bytesRead").GetInt64());
        Assert.Equal(80, trace[^1].RootElement.GetProperty("bytesHashed").GetInt64());
        Assert.Equal(40, trace[^1].RootElement.GetProperty("bytesCopied").GetInt64());
        Assert.Equal(8, trace[^1].RootElement.GetProperty("candidateGeneration").GetInt64());
        Assert.Equal("committed", trace[^1].RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public void TerminalCompletion_IsAtomicAndAlwaysLast()
    {
        using var output = new MemoryStream();
        using var writer = PackageSessionStartupTraceWriter.Create(output);
        var attempt = Assert.IsType<PackageSessionStartupTraceAttempt>(writer.BeginAttempt(baseGeneration: 3));
        var installed = InstalledPackage("sunder.package.trace", "2.0.0");
        attempt.RecordSourceDiscovery([installed], []);
        var package = Assert.IsType<PackageSessionStartupTracePackage>(attempt.GetInstalledPackage(installed));

        Parallel.Invoke(
            () => attempt.TryCommitGeneration(4),
            () => attempt.TryFail(),
            () => attempt.TryFail("canceled"),
            () => attempt.TryCommitGeneration(4));
        package.BeginProjection();
        package.CompleteProjection("succeeded");

        using var trace = ReadTrace(writer, output);
        var terminal = trace.Where(record =>
            record.RootElement.GetProperty("event").GetString() is
                PackageSessionStartupTraceEvents.GenerationCommitted or
                PackageSessionStartupTraceEvents.LoadFailed).ToArray();
        Assert.Single(terminal);
        Assert.Equal(
            terminal[0].RootElement.GetProperty("event").GetString(),
            trace[^1].RootElement.GetProperty("event").GetString());
        Assert.DoesNotContain(trace, record =>
            record.RootElement.GetProperty("event").GetString() == PackageSessionStartupTraceEvents.ProjectionPrepared);
    }

    [Fact]
    public async Task CatalogFailure_EmitsStartedAndExactlyOneFailedTerminal()
    {
        using var directory = TemporaryDirectoryScope.Create("sunder-startup-trace-tests");
        var paths = new RuntimePackagePaths(directory.GetPath("runtime"));
        Directory.CreateDirectory(paths.CatalogRootPath);
        await File.WriteAllTextAsync(paths.StateFilePath, "{not-json");
        using var output = new MemoryStream();
        using var writer = PackageSessionStartupTraceWriter.Create(output);
        var reconciler = new PackageSessionReconciler(
            new InstalledPackageStore(paths),
            new PackageSessionLoadService(NullLogger.Instance, paths),
            NullLogger<PackageSessionReconciler>.Instance,
            writer);

        await Assert.ThrowsAsync<InvalidDataException>(() => reconciler.LoadMergedSessionAsync(
            [],
            baseGeneration: 11,
            CancellationToken.None));

        using var trace = ReadTrace(writer, output);
        Assert.Equal(new[]
        {
            PackageSessionStartupTraceEvents.LoadStarted,
            PackageSessionStartupTraceEvents.LoadFailed,
        }, trace.Select(static record => record.RootElement.GetProperty("event").GetString()));
        Assert.Single(trace, record =>
            record.RootElement.GetProperty("event").GetString()
            is PackageSessionStartupTraceEvents.GenerationCommitted or PackageSessionStartupTraceEvents.LoadFailed);
        Assert.Equal("failed", trace[^1].RootElement.GetProperty("outcome").GetString());
        Assert.Equal(11, trace[^1].RootElement.GetProperty("baseGeneration").GetInt64());
        Assert.Equal(
            trace[0].RootElement.GetProperty("correlationId").GetString(),
            trace[^1].RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task SlowOutput_DoesNotBlockEventProducers()
    {
        using var output = new BlockingWriteStream();
        using var writer = PackageSessionStartupTraceWriter.Create(output);
        var attempt = Assert.IsType<PackageSessionStartupTraceAttempt>(writer.BeginAttempt(baseGeneration: 0));
        Assert.True(output.WaitUntilBlocked(TimeSpan.FromSeconds(5)));

        await Task.Run(() => attempt.RecordSourceDiscovery([], []))
            .WaitAsync(TimeSpan.FromSeconds(1));

        output.Release();
        await writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void StalledOutput_DisablesAtTheBoundedQueueLimit()
    {
        using var output = new BlockingWriteStream();
        using var writer = PackageSessionStartupTraceWriter.Create(output);
        var firstAttempt = Assert.IsType<PackageSessionStartupTraceAttempt>(writer.BeginAttempt(baseGeneration: 0));
        Assert.True(output.WaitUntilBlocked(TimeSpan.FromSeconds(5)));

        for (var index = 0; index < PackageSessionStartupTraceWriter.MaxBufferedEvents + 100; index++)
        {
            var attempt = writer.BeginAttempt(index);
            if (attempt is null) break;
            Assert.True(attempt.TryFail());
        }

        Assert.False(writer.IsEnabled);
        Assert.True(firstAttempt.TryFail());
        output.Release();
    }

    [Fact]
    public void Dispose_TerminalizesOutstandingAttempts()
    {
        using var output = new MemoryStream();
        var writer = PackageSessionStartupTraceWriter.Create(output);
        var attempt = Assert.IsType<PackageSessionStartupTraceAttempt>(writer.BeginAttempt(baseGeneration: 8));

        writer.Dispose();

        var lines = Encoding.UTF8.GetString(output.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var terminal = JsonDocument.Parse(lines[^1]);
        Assert.Equal(
            PackageSessionStartupTraceEvents.LoadFailed,
            terminal.RootElement.GetProperty("event").GetString());
        Assert.Equal("shutdown", terminal.RootElement.GetProperty("outcome").GetString());
        Assert.False(attempt.TryFail("late"));
    }

    [Fact]
    public void Dispose_WhenOwnedOutputIsStalled_ReturnsWithinTheFlushDeadline()
    {
        var output = new BlockingWriteStream();
        var writer = PackageSessionStartupTraceWriter.Create(output, ownsStream: true);
        Assert.NotNull(writer.BeginAttempt(baseGeneration: 0));
        Assert.True(output.WaitUntilBlocked(TimeSpan.FromSeconds(5)));
        var elapsed = Stopwatch.StartNew();

        writer.Dispose();

        elapsed.Stop();
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), $"Trace disposal took {elapsed.Elapsed}.");
    }

    [Fact]
    public void Dispose_WhenOwnedStreamDisposalStalls_ReturnsWithoutBlocking()
    {
        var output = new BlockingDisposeStream();
        var writer = PackageSessionStartupTraceWriter.Create(output, ownsStream: true);
        var attempt = Assert.IsType<PackageSessionStartupTraceAttempt>(writer.BeginAttempt(baseGeneration: 0));
        Assert.True(attempt.TryFail());
        var elapsed = Stopwatch.StartNew();

        writer.Dispose();

        elapsed.Stop();
        Assert.True(output.WaitUntilDisposeBlocked(TimeSpan.FromSeconds(5)));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1), $"Trace disposal took {elapsed.Elapsed}.");
        output.ReleaseDispose();
    }

    [Fact]
    public void DevFailureAndInstalledFallback_UseIndependentPackageEvents()
    {
        using var output = new MemoryStream();
        using var writer = PackageSessionStartupTraceWriter.Create(output);
        var attempt = Assert.IsType<PackageSessionStartupTraceAttempt>(writer.BeginAttempt(baseGeneration: 2));
        var installed = InstalledPackage("sunder.package.trace", "2.0.0");
        const string devFolder = "/not-emitted/dev";
        attempt.RecordSourceDiscovery(
            [installed],
            [new PackageSessionDevOverlay(
                installed.PackageId,
                devFolder,
                Watch: false,
                PackageSessionOverlayOwner.Startup)]);
        var dev = Assert.IsType<PackageSessionStartupTracePackage>(attempt.GetDevPackage(devFolder));
        var fallback = Assert.IsType<PackageSessionStartupTracePackage>(attempt.GetInstalledPackage(installed));
        Assert.NotSame(dev, fallback);

        dev.BeginProjection();
        dev.CompleteProjection("failed");
        fallback.BeginProjection();
        fallback.CompleteProjection("succeeded");
        attempt.TryFail("test-complete");

        using var trace = ReadTrace(writer, output);
        var projections = trace.Where(record =>
            record.RootElement.GetProperty("event").GetString()
            == PackageSessionStartupTraceEvents.ProjectionPrepared).ToArray();
        Assert.Equal(2, projections.Length);
        Assert.Contains(projections, record =>
            record.RootElement.GetProperty("sourceKind").GetString() == "dev"
            && record.RootElement.GetProperty("outcome").GetString() == "failed");
        Assert.Contains(projections, record =>
            record.RootElement.GetProperty("sourceKind").GetString() == "installed"
            && record.RootElement.GetProperty("outcome").GetString() == "succeeded");
    }

    [Fact]
    public async Task InstalledPreparation_ReportsBytesFromExistingValidationAndCopyOperations()
    {
        using var directory = TemporaryDirectoryScope.Create("sunder-startup-trace-tests");
        var source = directory.GetPath("source");
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            source,
            "sunder.package.trace",
            "2.0.0",
            typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
            roles: [SunderPackageFormat.RuntimeHostRole]);
        var installed = CanonicalPackageTestBuilder.CreateInstalledRecord(
            source,
            "sunder.package.trace",
            "2.0.0");
        using var output = new MemoryStream();
        using var writer = PackageSessionStartupTraceWriter.Create(output);
        var attempt = Assert.IsType<PackageSessionStartupTraceAttempt>(writer.BeginAttempt(baseGeneration: 1));
        attempt.RecordSourceDiscovery([installed], []);
        var package = Assert.IsType<PackageSessionStartupTracePackage>(attempt.GetInstalledPackage(installed));
        package.BeginProjection();

        var errors = new List<string>();
        var stateRoot = directory.GetPath("state");
        RuntimeLocalState.EnsureInitialized(stateRoot);
        var prepared = await new PackageSessionPreparer(
            projectionCache: new PackageProjectionCache(stateRoot)).PrepareInstalledPackageAsync(
            0,
            installed,
            source,
            directory.GetPath("session"),
            errors,
            CancellationToken.None,
            package);
        Assert.NotNull(prepared);
        Assert.NotNull(prepared.PackageProjectionLease);
        await using var projectionLease = prepared.PackageProjectionLease;
        Assert.Empty(errors);
        package.SetPreparedIdentity(prepared);
        package.CompleteProjection("succeeded");
        attempt.TryFail("test-complete");

        using var trace = ReadTrace(writer, output);
        var projection = Assert.Single(trace, record =>
            record.RootElement.GetProperty("event").GetString()
            == PackageSessionStartupTraceEvents.ProjectionPrepared);
        Assert.True(projection.RootElement.GetProperty("bytesRead").GetInt64() > 0);
        Assert.True(projection.RootElement.GetProperty("bytesHashed").GetInt64() > 0);
        Assert.True(projection.RootElement.GetProperty("bytesCopied").GetInt64() > 0);
    }

    [Fact]
    public async Task InstalledReceiptValidation_HashesOnlyBoundedMetadataAndDefersPayloadChecksToProjectionCache()
    {
        using var directory = TemporaryDirectoryScope.Create("sunder-startup-trace-tests");
        var source = directory.GetPath("source");
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            source,
            "sunder.package.trace",
            "2.0.0",
            typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
            roles: [SunderPackageFormat.AppHostRole]);
        var payloadPath = Path.Combine(source, "payload", "shared", "assets", "large.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
        await File.WriteAllBytesAsync(payloadPath, new byte[1024 * 1024]);
        CanonicalPackageTestBuilder.WriteContentIndex(source);
        var installed = CanonicalPackageTestBuilder.CreateInstalledRecord(
            source,
            "sunder.package.trace",
            "2.0.0");
        await File.WriteAllTextAsync(payloadPath, "mutated after install");
        var manifestPath = Path.Combine(source, "manifest", "sunder-package.json");
        var contentIndexPath = Path.Combine(source, "manifest", "content-index.json");
        var expectedMetadataBytes = new FileInfo(manifestPath).Length + new FileInfo(contentIndexPath).Length;
        using var output = new MemoryStream();
        using var writer = PackageSessionStartupTraceWriter.Create(output);
        var attempt = Assert.IsType<PackageSessionStartupTraceAttempt>(writer.BeginAttempt(baseGeneration: 1));
        attempt.RecordSourceDiscovery([installed], []);
        var package = Assert.IsType<PackageSessionStartupTracePackage>(attempt.GetInstalledPackage(installed));
        package.BeginProjection();
        var errors = new List<string>();

        var validation = await new PackageSessionPreparer().ValidateInstalledPackageAsync(
            installed,
            source,
            errors,
            CancellationToken.None,
            package);

        Assert.NotNull(validation);
        Assert.Empty(errors);
        package.CompleteProjection("succeeded");
        attempt.TryFail("test-complete");
        using var trace = ReadTrace(writer, output);
        var projection = Assert.Single(trace, record =>
            record.RootElement.GetProperty("event").GetString()
            == PackageSessionStartupTraceEvents.ProjectionPrepared);
        Assert.Equal(expectedMetadataBytes, projection.RootElement.GetProperty("bytesRead").GetInt64());
        Assert.Equal(expectedMetadataBytes, projection.RootElement.GetProperty("bytesHashed").GetInt64());
        Assert.Equal(0, projection.RootElement.GetProperty("bytesCopied").GetInt64());

        await File.AppendAllTextAsync(manifestPath, " ");
        errors.Clear();
        Assert.Null(await new PackageSessionPreparer().ValidateInstalledPackageAsync(
            installed,
            source,
            errors,
            CancellationToken.None));
        Assert.Contains(errors, error => error.Contains("identity or inventory", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CanonicalMaterialization_TracePreservesTheUninstrumentedCopyBehavior()
    {
        using var directory = TemporaryDirectoryScope.Create("sunder-startup-trace-tests");
        var source = directory.GetPath("source");
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            source,
            "sunder.package.trace",
            "2.0.0",
            typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
            roles: [SunderPackageFormat.AppHostRole]);
        var installed = CanonicalPackageTestBuilder.CreateInstalledRecord(
            source,
            "sunder.package.trace",
            "2.0.0");
        var contentIndexPath = Path.Combine(source, "manifest", "content-index.json");
        File.SetLastWriteTimeUtc(contentIndexPath, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var contentIndex = JsonSerializer.Deserialize<SunderPackageContentIndex>(File.ReadAllText(contentIndexPath));
        Assert.NotNull(contentIndex);

        var uninstrumented = directory.GetPath("uninstrumented");
        await PackageProjectionMaterializer.MaterializeCanonicalPackageAsync(
            source,
            uninstrumented,
            contentIndex,
            CancellationToken.None);

        using var output = new MemoryStream();
        using var writer = PackageSessionStartupTraceWriter.Create(output);
        var attempt = Assert.IsType<PackageSessionStartupTraceAttempt>(writer.BeginAttempt(baseGeneration: 1));
        attempt.RecordSourceDiscovery([installed], []);
        var package = Assert.IsType<PackageSessionStartupTracePackage>(attempt.GetInstalledPackage(installed));
        package.BeginProjection();
        var instrumented = directory.GetPath("instrumented");
        await PackageProjectionMaterializer.MaterializeCanonicalPackageAsync(
            source,
            instrumented,
            contentIndex,
            CancellationToken.None,
            package);
        package.CompleteProjection("succeeded");
        attempt.TryFail("test-complete");

        var relativeFiles = Directory.EnumerateFiles(uninstrumented, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(uninstrumented, path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            relativeFiles,
            Directory.EnumerateFiles(instrumented, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(instrumented, path))
                .Order(StringComparer.Ordinal));
        foreach (var relativePath in relativeFiles)
        {
            var expected = Path.Combine(uninstrumented, relativePath);
            var actual = Path.Combine(instrumented, relativePath);
            Assert.Equal(File.ReadAllBytes(expected), File.ReadAllBytes(actual));
        }
        var uninstrumentedIndex = Path.Combine(uninstrumented, "manifest", "content-index.json");
        var instrumentedIndex = Path.Combine(instrumented, "manifest", "content-index.json");
        Assert.Equal(File.GetLastWriteTimeUtc(uninstrumentedIndex), File.GetLastWriteTimeUtc(instrumentedIndex));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(File.GetUnixFileMode(uninstrumentedIndex), File.GetUnixFileMode(instrumentedIndex));
        }

        using var trace = ReadTrace(writer, output);
        var projection = Assert.Single(trace, record =>
            record.RootElement.GetProperty("event").GetString()
            == PackageSessionStartupTraceEvents.ProjectionPrepared);
        Assert.True(projection.RootElement.GetProperty("bytesCopied").GetInt64() > 0);
    }

    [Fact]
    public async Task PackageInstall_EmitsTheCanonicalLifecycleFromReconciliationThroughPublication()
    {
        using var directory = TemporaryDirectoryScope.Create("sunder-startup-trace-tests");
        var source = directory.GetPath("source");
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            source,
            "sunder.package.trace",
            "2.0.0",
            typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
            roles: [SunderPackageFormat.RuntimeHostRole]);
        var archive = await CanonicalPackageTestBuilder.CreateArchiveAsync(source);
        var paths = new RuntimePackagePaths(directory.GetPath("runtime"));
        using var output = new MemoryStream();
        using var writer = PackageSessionStartupTraceWriter.Create(output);
        var host = new RuntimePackageSessionTestHost(
            NullLogger<RuntimePackageSessionTestHost>.Instance,
            new InstalledPackageStore(paths),
            new SunderPackageArchiveInstaller(paths),
            startupTraceWriter: writer);
        try
        {
            await host.InitializeAsync();

            var installed = await host.InstallPackageFromRuntimePathAsync(archive);

            Assert.True(installed.Success, string.Join(Environment.NewLine, installed.Errors));
            using var trace = ReadTrace(writer, output);
            Assert.Equal(new[]
            {
                PackageSessionStartupTraceEvents.LoadStarted,
                PackageSessionStartupTraceEvents.SourceDiscoveryCompleted,
                PackageSessionStartupTraceEvents.ProjectionCacheCompleted,
                PackageSessionStartupTraceEvents.ProjectionPrepared,
                PackageSessionStartupTraceEvents.GraphPlanCompleted,
                PackageSessionStartupTraceEvents.RuntimeBindingCompleted,
                PackageSessionStartupTraceEvents.WorkerWaveStarted,
                PackageSessionStartupTraceEvents.WorkerProcessStarted,
                PackageSessionStartupTraceEvents.WorkerReady,
                PackageSessionStartupTraceEvents.CandidateReady,
                PackageSessionStartupTraceEvents.WorkerWaveCompleted,
                PackageSessionStartupTraceEvents.GenerationCommitted,
            }, trace.Select(static record =>
                record.RootElement.GetProperty("event").GetString()
                ?? throw new InvalidDataException("Trace event name is missing.")));
            Assert.Single(trace, record =>
                record.RootElement.GetProperty("event").GetString()
                == PackageSessionStartupTraceEvents.GenerationCommitted);
            Assert.DoesNotContain(trace, record =>
                record.RootElement.GetProperty("event").GetString()
                == PackageSessionStartupTraceEvents.LoadFailed);
        }
        finally
        {
            await host.ShutdownAsync();
            File.Delete(archive);
        }
    }

    [Fact]
    public async Task DisabledInstalledPackage_ReportsValidationBytesWithoutProjectionCopy()
    {
        using var directory = TemporaryDirectoryScope.Create("sunder-startup-trace-tests");
        var source = directory.GetPath("source");
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            source,
            "sunder.package.trace",
            "2.0.0",
            typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
            roles: [SunderPackageFormat.RuntimeHostRole]);
        var installed = CanonicalPackageTestBuilder.CreateInstalledRecord(
            source,
            "sunder.package.trace",
            "2.0.0") with
        {
            IsEnabled = false,
        };
        using var output = new MemoryStream();
        using var writer = PackageSessionStartupTraceWriter.Create(output);
        var attempt = Assert.IsType<PackageSessionStartupTraceAttempt>(writer.BeginAttempt(baseGeneration: 1));
        attempt.RecordSourceDiscovery([installed], []);

        var loaded = await new PackageSessionLoadService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance).LoadInstalledAsync(
            [installed],
            cancellationToken: CancellationToken.None,
            startupTrace: attempt);
        Assert.NotNull(loaded.Session);
        await loaded.Session.DisposeAsync();

        using var trace = ReadTrace(writer, output);
        var projection = Assert.Single(trace, record =>
            record.RootElement.GetProperty("event").GetString()
            == PackageSessionStartupTraceEvents.ProjectionPrepared);
        Assert.Equal("not-required", projection.RootElement.GetProperty("outcome").GetString());
        Assert.True(projection.RootElement.GetProperty("bytesRead").GetInt64() > 0);
        Assert.True(projection.RootElement.GetProperty("bytesHashed").GetInt64() > 0);
        Assert.Equal(0, projection.RootElement.GetProperty("bytesCopied").GetInt64());
    }

    [Fact]
    public async Task FailedMetadataIdentityValidation_ReportsBytesReadBeforeTheHashMismatch()
    {
        using var directory = TemporaryDirectoryScope.Create("sunder-startup-trace-tests");
        var source = directory.GetPath("source");
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            source,
            "sunder.package.trace",
            "2.0.0",
            typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
            roles: [SunderPackageFormat.RuntimeHostRole]);
        var installed = CanonicalPackageTestBuilder.CreateInstalledRecord(
            source,
            "sunder.package.trace",
            "2.0.0");
        await File.AppendAllTextAsync(Path.Combine(source, SunderPackageFormat.ManifestPath), " ");
        using var output = new MemoryStream();
        using var writer = PackageSessionStartupTraceWriter.Create(output);
        var attempt = Assert.IsType<PackageSessionStartupTraceAttempt>(writer.BeginAttempt(baseGeneration: 1));
        attempt.RecordSourceDiscovery([installed], []);
        var package = Assert.IsType<PackageSessionStartupTracePackage>(attempt.GetInstalledPackage(installed));
        package.BeginProjection();
        var errors = new List<string>();

        var activation = await new PackageSessionPreparer().ReadInstalledActivationStateAsync(
            installed,
            source,
            errors,
            CancellationToken.None,
            package);
        package.CompleteProjection("failed");
        attempt.TryFail();

        Assert.Null(activation);
        Assert.NotEmpty(errors);
        using var trace = ReadTrace(writer, output);
        var projection = Assert.Single(trace, record =>
            record.RootElement.GetProperty("event").GetString()
            == PackageSessionStartupTraceEvents.ProjectionPrepared);
        Assert.True(projection.RootElement.GetProperty("bytesRead").GetInt64() > 0);
        Assert.True(projection.RootElement.GetProperty("bytesHashed").GetInt64() > 0);
    }

    [Fact]
    public async Task EmptyInstalledSession_EmitsOneNoOpTerminalEvent()
    {
        using var directory = TemporaryDirectoryScope.Create("sunder-startup-trace-tests");
        var paths = new RuntimePackagePaths(directory.GetPath("runtime"));
        using var output = new MemoryStream();
        using var writer = PackageSessionStartupTraceWriter.Create(output);
        var host = new RuntimePackageSessionTestHost(
            NullLogger<RuntimePackageSessionTestHost>.Instance,
            new InstalledPackageStore(paths),
            new SunderPackageArchiveInstaller(paths),
            startupTraceWriter: writer);
        try
        {
            await host.InitializeAsync();

            var loaded = await host.LoadInstalledPackagesAsync();

            Assert.True(loaded.Success, string.Join(Environment.NewLine, loaded.Errors));
            using var trace = ReadTrace(writer, output);
            var terminal = Assert.Single(trace, record =>
                record.RootElement.GetProperty("event").GetString()
                is PackageSessionStartupTraceEvents.GenerationCommitted or
                PackageSessionStartupTraceEvents.LoadFailed);
            Assert.Equal(
                PackageSessionStartupTraceEvents.GenerationCommitted,
                terminal.RootElement.GetProperty("event").GetString());
            Assert.Equal("no-op", terminal.RootElement.GetProperty("outcome").GetString());
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    [Fact]
    public async Task TraceSchema_ContainsOnlyAllowlistedTypedFields()
    {
        using var output = new MemoryStream();
        using var writer = PackageSessionStartupTraceWriter.Create(output);
        var attempt = Assert.IsType<PackageSessionStartupTraceAttempt>(writer.BeginAttempt(baseGeneration: 0));
        attempt.TryFail();

        await writer.FlushAsync();
        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.DoesNotContain("message", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exception", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("header", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("environment", text, StringComparison.OrdinalIgnoreCase);

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "schemaVersion",
            "sequence",
            "correlationId",
            "event",
            "timestampUtc",
            "elapsedMilliseconds",
            "baseGeneration",
            "candidateGeneration",
            "packageId",
            "packageVersion",
            "sourceKind",
            "targetRole",
            "runtimeIdentifier",
            "activationId",
            "processId",
            "processStartedAtUtc",
            "processStartElapsedMilliseconds",
            "processReadyElapsedMilliseconds",
            "queueDurationMilliseconds",
            "activeDurationMilliseconds",
            "bytesRead",
            "bytesHashed",
            "bytesCopied",
            "projectionCacheResult",
            "executionRuntimeSourceId",
            "executionRuntimeId",
            "executionRuntimeVendorVersion",
            "executionRuntimeModuleAbi",
            "executionRuntimeVendorChecksumAlgorithm",
            "executionRuntimeVendorChecksumDigest",
            "waveIndex",
            "waveCount",
            "wavePackageCount",
            "dependencyCount",
            "preparationConcurrency",
            "workerStartupConcurrency",
            "installedPackageCount",
            "devPackageCount",
            "packageCount",
            "outcome",
        };
        using var trace = ReadTrace(writer, output);
        Assert.All(trace, record => Assert.All(
            record.RootElement.EnumerateObject(),
            property => Assert.Contains(property.Name, allowed)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Open_WithoutOutputPathIsDisabled(string? outputPath)
    {
        using var writer = PackageSessionStartupTraceWriter.Open(outputPath);

        Assert.False(writer.IsEnabled);
        Assert.Null(writer.BeginAttempt(0));
    }

    [Fact]
    public void Open_OnUnixRestrictsTraceFileToTheCurrentUser()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = TemporaryDirectoryScope.Create("sunder-startup-trace-tests");
        var path = directory.WriteAllText("trace.jsonl", "pre-existing");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        using (var writer = PackageSessionStartupTraceWriter.Open(path))
        {
            Assert.True(writer.IsEnabled);
        }

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(path));
    }

    [Fact]
    public void OutputFailure_DisablesTraceWithoutAffectingTheAttempt()
    {
        var output = new MemoryStream();
        using var writer = PackageSessionStartupTraceWriter.Create(output);
        output.Dispose();

        var exception = Record.Exception(() =>
        {
            var attempt = writer.BeginAttempt(0);
            attempt?.TryFail();
            writer.FlushAsync().GetAwaiter().GetResult();
        });

        Assert.Null(exception);
        Assert.False(writer.IsEnabled);
    }

    private static InstalledPackageRecord InstalledPackage(string packageId, string version)
        => new(
            packageId,
            "Trace package",
            "Trace package summary",
            version,
            Icon: null,
            InstallPath: "/not-emitted/installed",
            ManifestPath: "/not-emitted/installed/manifest/sunder-package.json",
            ContentIdentity: new string('a', 64),
            ContentInventory: [],
            DependsOn: [],
            IsEnabled: true,
            InstalledAtUtc: DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
            InstalledPackageProvenanceRecord.Unknown,
            CapabilityGrants: []);

    private static TraceDocuments ReadTrace(
        PackageSessionStartupTraceWriter writer,
        MemoryStream output)
    {
        writer.FlushAsync().GetAwaiter().GetResult();
        var lines = Encoding.UTF8.GetString(output.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return new TraceDocuments(lines.Select(static line => JsonDocument.Parse(line)).ToArray());
    }

    private sealed class TraceDocuments(JsonDocument[] documents) : List<JsonDocument>(documents), IDisposable
    {
        public void Dispose()
        {
            foreach (var document in this) document.Dispose();
        }
    }

    private sealed class BlockingWriteStream : Stream
    {
        private readonly ManualResetEventSlim _blocked = new();
        private readonly ManualResetEventSlim _release = new();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public bool WaitUntilBlocked(TimeSpan timeout) => _blocked.Wait(timeout);

        public void Release() => _release.Set();

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _blocked.Set();
            _release.Wait();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _release.Set();
                _blocked.Dispose();
                _release.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class BlockingDisposeStream : MemoryStream
    {
        private readonly ManualResetEventSlim _disposeBlocked = new();
        private readonly ManualResetEventSlim _releaseDispose = new();

        public bool WaitUntilDisposeBlocked(TimeSpan timeout) => _disposeBlocked.Wait(timeout);

        public void ReleaseDispose() => _releaseDispose.Set();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposeBlocked.Set();
                _releaseDispose.Wait();
            }
            base.Dispose(disposing);
        }
    }
}
