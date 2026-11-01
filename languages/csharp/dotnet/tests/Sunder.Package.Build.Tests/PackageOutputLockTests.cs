using System.Diagnostics;
using System.Text.Json;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Xunit;
using static Sunder.Package.Build.Tests.PackageManifestFixture;
using static Sunder.Package.Build.Tests.PackageOutputLockFixture;

namespace Sunder.Package.Build.Tests;

[Collection("Package build artifacts")]
public sealed class PackageOutputLockTests
{
    [Fact]
    public void SharedOutputLock_ReclaimsSameHostPidWithDifferentProcessStart()
    {
        var root = CreateTempDirectory();
        var output = Path.Combine(root, "shared-output");
        using var ownerMarker = CreateOwnerDirectory(
            output + GeneratedOutputLock.LockSuffix,
            Environment.ProcessId,
            Environment.MachineName,
            DateTime.UtcNow.AddYears(-10));

        using var outputLock = GeneratedOutputLock.Acquire([output], TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void SharedOutputLock_SchedulerBackoffRunsOutsideTransitionAndFailedCallbacksReleasePartialAcquisition()
    {
        var root = CreateTempDirectory();
        try
        {
            var first = Path.Combine(root, "a-output"); var second = Path.Combine(root, "b-output");
            using var owner = GeneratedOutputLock.Acquire([second]);
            var calls = 0;
            var error = Assert.Throws<InvalidOperationException>(() => GeneratedOutputLock.Acquire([], [first, second],
                TimeSpan.FromSeconds(2), waitBeforeRetry: () =>
                {
                    calls++;
                    Assert.False(Directory.Exists(second + GeneratedOutputLock.LockSuffix + ".transition"));
                    throw new InvalidOperationException("The scheduler wait was cancelled.");
                }));
            Assert.Equal("The scheduler wait was cancelled.", error.Message); Assert.Equal(1, calls);
            using (GeneratedOutputLock.Acquire([first], TimeSpan.FromSeconds(2))) { }
            using var acquired = GeneratedOutputLock.Acquire([], [first, second], TimeSpan.FromSeconds(2),
                waitBeforeRetry: owner.Dispose);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SharedOutputLock_RetainReleasesUnneededPathsWithoutUnlockingRetainedPath()
    {
        var root = CreateTempDirectory();
        try
        {
            var retained = Path.Combine(root, "retained-output");
            var released = Path.Combine(root, "released-output");
            using (var outputLock = GeneratedOutputLock.Acquire([retained, released]))
            {
                outputLock.Retain([retained]);
                using var releasedLock = GeneratedOutputLock.Acquire([released], TimeSpan.FromSeconds(2));
                Assert.Throws<TimeoutException>(() =>
                    GeneratedOutputLock.Acquire([retained], TimeSpan.FromMilliseconds(250)));
            }

            Assert.False(Directory.Exists(retained + GeneratedOutputLock.LockSuffix));
            Assert.False(Directory.Exists(released + GeneratedOutputLock.LockSuffix));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SharedOutputLock_SameHostOwnerWithoutProcessMarkerTimesOutWithoutReclamation()
    {
        var root = CreateTempDirectory();
        var output = Path.Combine(root, "shared-output");
        var lockPath = output + GeneratedOutputLock.LockSuffix;
        using var ownerMarker = CreateOwnerDirectory(
            lockPath,
            int.MaxValue,
            Environment.MachineName,
            DateTime.UtcNow.AddYears(-10));
        ownerMarker.Delete();

        Assert.Throws<TimeoutException>(() => GeneratedOutputLock.Acquire([output], TimeSpan.FromMilliseconds(250)));
        Assert.True(Directory.Exists(lockPath));
    }

    [LinuxTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void SharedOutputLock_LinuxBootOrPidNamespaceMismatchTimesOutWithoutReclamation(bool mismatchBoot)
    {
        var root = CreateTempDirectory();
        var output = Path.Combine(root, "shared-output");
        var lockPath = output + GeneratedOutputLock.LockSuffix;
        using var ownerMarker = CreateOwnerDirectory(
            lockPath,
            int.MaxValue,
            Environment.MachineName,
            DateTime.UtcNow.AddYears(-10),
            linuxBootIdOverride: mismatchBoot ? Guid.NewGuid().ToString("D") : null,
            linuxPidNamespaceOverride: mismatchBoot ? null : "pid:[0]");

        Assert.Throws<TimeoutException>(() => GeneratedOutputLock.Acquire([output], TimeSpan.FromMilliseconds(250)));
        Assert.True(Directory.Exists(lockPath));
    }

    [Fact]
    public void SharedOutputLock_UnknownHostOwnerAndGuardTimeOutWithoutReclamation()
    {
        var root = CreateTempDirectory();
        var output = Path.Combine(root, "shared-output");
        var lockPath = output + GeneratedOutputLock.LockSuffix;
        var unknownHost = Environment.MachineName + ".unknown-host";
        using var ownerMarker = CreateOwnerDirectory(lockPath, Environment.ProcessId, unknownHost, DateTime.UtcNow.AddYears(-10));
        File.SetLastWriteTimeUtc(Path.Combine(lockPath, GeneratedOutputLock.OwnerFileName), DateTime.UtcNow.AddDays(-1));

        Assert.Throws<TimeoutException>(() => GeneratedOutputLock.Acquire([output], TimeSpan.FromMilliseconds(250)));
        Assert.True(Directory.Exists(lockPath));

        Directory.Delete(lockPath, recursive: true);
        var guardPath = lockPath + ".transition";
        using var guardMarker = CreateOwnerDirectory(guardPath, Environment.ProcessId, unknownHost, DateTime.UtcNow.AddYears(-10));
        File.SetLastWriteTimeUtc(Path.Combine(guardPath, GeneratedOutputLock.OwnerFileName), DateTime.UtcNow.AddDays(-1));
        Assert.Throws<TimeoutException>(() => GeneratedOutputLock.Acquire([output], TimeSpan.FromMilliseconds(250)));
        Assert.True(Directory.Exists(guardPath));
    }

    [Fact]
    public async Task SharedOutputLock_RetainsLiveStaleHeartbeatAndSerializesTwoReclaimers()
    {
        var root = CreateTempDirectory();
        var output = Path.Combine(root, "shared-output");
        var holder = AcquireGeneratedOutputLock(output);
        var heartbeatPath = Path.Combine(output + ".sunder-output.lock", "owner.json");
        File.SetLastWriteTimeUtc(heartbeatPath, DateTime.UtcNow.AddMinutes(-10));
        using var waiter = OutputLockContenderBarrier<IDisposable>.Start(
            output,
            () => AcquireGeneratedOutputLock(output),
            () => $"lock={output + GeneratedOutputLock.LockSuffix}; owner={File.ReadAllText(heartbeatPath)}");
        await waiter.WaitForAttemptAsync(TimeSpan.FromSeconds(5));
        await waiter.AssertContendedAsync();
        holder.Dispose();
        using (await waiter.Completion.WaitAsync(TimeSpan.FromSeconds(5)))
        {
        }

        using var deadOwnerMarker = CreateDeadOwnerLock(output);
        using var guardMarker = CreateOwnerDirectory(
            output + GeneratedOutputLock.LockSuffix + ".transition",
            int.MaxValue,
            Environment.MachineName,
            DateTime.UtcNow.AddMinutes(-1));
        var acquired = new[]
        {
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var order = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var release = new[] { new ManualResetEventSlim(), new ManualResetEventSlim() };
        var reclaimers = Enumerable.Range(0, 2).Select(index =>
            OutputLockContenderBarrier<object?>.Start(output, () =>
            {
                using var outputLock = AcquireGeneratedOutputLock(output);
                order.Enqueue(index);
                acquired[index].TrySetResult();
                release[index].Wait(TimeSpan.FromSeconds(10));
                return null;
            }, () => $"reclaimer={index}; acquired={string.Join(',', order)}; lock={output + GeneratedOutputLock.LockSuffix}"))
            .ToArray();
        await Task.WhenAll(reclaimers.Select(reclaimer => reclaimer.WaitForAttemptAsync(TimeSpan.FromSeconds(5))));
        await Task.WhenAny(acquired.Select(signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(order.TryPeek(out var first));
        await Eventually.AssertPendingAsync(
            acquired[first == 0 ? 1 : 0].Task,
            () => $"Both stale-lock reclaimers acquired before release; order={string.Join(',', order)}");
        Assert.Single(order);
        release[first].Set();
        await acquired[first == 0 ? 1 : 0].Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, order.Count);
        release[first == 0 ? 1 : 0].Set();
        await Task.WhenAll(reclaimers.Select(reclaimer => reclaimer.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(Directory.Exists(output + ".sunder-output.lock"));
        Assert.False(Directory.Exists(output + ".sunder-output.lock.transition"));
        Assert.Single(Directory.EnumerateDirectories(root, "shared-output.sunder-output.lock.transition.stale-*"));
    }

    [ExternalArtifactFact("SUNDER_REQUIRE_NODE_OUTPUT_LOCK_INTEROP")]
    [Trait("Category", "ExternalArtifact")]
    public async Task SharedOutputLock_InteroperatesWithNodeProducerWhenAvailable()
    {
        var helper = Path.Combine(
            FindRepositoryRoot(),
            "languages",
            "typescript",
            "node",
            "packages",
            "package-tool",
            "dist-test",
            "test",
            "output-child.js");
        Assert.True(File.Exists(helper), $"Required Node output-lock helper does not exist: {helper}");
        Assert.True(CanStartNode(), "The selected output-lock interop lane cannot start Node.");
        var scope = TemporaryDirectoryScope.Create("sunder-output-lock-interop");
        var root = scope.RootPath;
        var output = Path.Combine(root, "shared-output");
        var ready = Path.Combine(root, "node.ready");
        var release = Path.Combine(root, "node.release");
        var attempting = Path.Combine(root, "node.attempting");
        IDisposable? holder = null;
        CapturedProcess? node = null;
        CapturedProcess? crashedNode = null;
        OutputLockContenderBarrier<IDisposable>? reclaimer = null;
        IDisposable? reclaimed = null;
        try
        {
            holder = AcquireGeneratedOutputLock(output);
            node = StartCapturedNodeInTempRoot(Path.Combine(root, "node-job-temp"), helper, "hold", output, ready, release, attempting);
            var nodeResult = node.WaitForExitAsync(TimeSpan.FromSeconds(15));
            await FileSignal.WaitForAsync(attempting, TimeSpan.FromSeconds(5), () => $"ready={File.Exists(ready)}; {node.Diagnostics}");
            await Eventually.AssertPendingAsync(nodeResult, () => $"attempting={File.Exists(attempting)}; ready={File.Exists(ready)}; {node.Diagnostics}");
            Assert.False(File.Exists(ready));
            holder.Dispose();
            holder = null;
            await FileSignal.WaitForAsync(ready, TimeSpan.FromSeconds(5), () => node.Diagnostics);
            File.WriteAllText(release, "release");
            var result = await nodeResult;
            Assert.True(result.ExitCode == 0, result.CombinedOutput);
            await node.DisposeAsync();
            node = null;
            Assert.False(Directory.Exists(output + ".sunder-output.lock"));

            using (CreateDeadOwnerLock(output))
            {
                await using var nodeRecovery = StartCapturedNode(helper, "recover", output);
                var recoveryResult = await nodeRecovery.WaitForExitAsync(TimeSpan.FromSeconds(10));
                Assert.True(recoveryResult.ExitCode == 0, recoveryResult.CombinedOutput);
                Assert.False(Directory.Exists(output + ".sunder-output.lock"));
            }

            var crashedReady = Path.Combine(root, "crashed-node.ready");
            var crashedRelease = Path.Combine(root, "crashed-node.release");
            var crashedAttempting = Path.Combine(root, "crashed-node.attempting");
            var crashedTemp = Path.Combine(root, "crashed-job-temp");
            crashedNode = StartCapturedNodeInTempRoot(crashedTemp, helper, "hold", output, crashedReady, crashedRelease, crashedAttempting);
            await FileSignal.WaitForAsync(crashedReady, TimeSpan.FromSeconds(5), () => crashedNode.Diagnostics);
            using var ownerDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                output + GeneratedOutputLock.LockSuffix,
                GeneratedOutputLock.OwnerFileName)));
            using var crashedNodeMarker = new TemporaryProcessMarker(
                ownerDocument.RootElement.GetProperty("processMarkerPath").GetString()
                ?? throw new InvalidDataException("Node lock owner omitted its process marker path."));
            reclaimer = OutputLockContenderBarrier<IDisposable>.Start(
                output,
                () => GeneratedOutputLock.Acquire([output], TimeSpan.FromSeconds(5)),
                () => $"nodePid={crashedNode.Id}; lock={output + GeneratedOutputLock.LockSuffix}; {crashedNode.Diagnostics}");
            await reclaimer.WaitForAttemptAsync(TimeSpan.FromSeconds(5));
            await reclaimer.AssertContendedAsync();
            crashedNode.Process.Kill(entireProcessTree: true);
            var crashedResult = await crashedNode.WaitForExitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(0, crashedResult.ExitCode);
            Directory.Delete(crashedTemp, recursive: true);
            Assert.True(File.Exists(crashedNodeMarker.Path));
            reclaimed = await reclaimer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            var cleanupErrors = new List<Exception>();
            TryWriteReleaseSignal(release);
            TryWriteReleaseSignal(Path.Combine(root, "crashed-node.release"));
            CaptureCleanup(cleanupErrors, () => holder?.Dispose());
            if (crashedNode is not null)
            {
                await CaptureCleanupAsync(cleanupErrors, () => crashedNode.DisposeAsync());
            }
            if (node is not null)
            {
                await CaptureCleanupAsync(cleanupErrors, () => node.DisposeAsync());
            }
            if (reclaimed is null && reclaimer is not null)
            {
                try
                {
                    reclaimed = await reclaimer.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception exception)
                {
                    cleanupErrors.Add(exception);
                }
            }
            CaptureCleanup(cleanupErrors, () => reclaimed?.Dispose());
            CaptureCleanup(cleanupErrors, () => reclaimer?.Dispose());
            await CaptureCleanupAsync(cleanupErrors, () => scope.DisposeAsync());
            if (cleanupErrors.Count > 0)
            {
                throw new AggregateException("Output-lock interop cleanup did not complete.", cleanupErrors);
            }
        }
        Assert.False(Directory.Exists(root), $"Output-lock interop temporary root was retained: {root}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Conformance")]
    [Trait("Capability", "Filesystem")]
    [Trait("Size", "Small")]
    public void SharedOutputDocuments_MatchCrossLanguagePropertyCorpusAndRecoverTransaction(bool developmentHandoff)
    {
        using var corpus = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "GeneratedOutputConformance",
            "documents.json")));
        var documents = corpus.RootElement.GetProperty("documents");
        Assert.Equal(1, corpus.RootElement.GetProperty("formatVersion").GetInt32());
        var root = CreateTempDirectory();
        var output = Path.Combine(root, developmentHandoff ? "sunder-dev" : "shared-output");
        using (AcquireGeneratedOutputLock(output))
        {
            using var owner = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
                output + GeneratedOutputLock.LockSuffix,
                GeneratedOutputLock.OwnerFileName)));
            AssertDocumentProperties(owner.RootElement, documents.GetProperty("owner"));
            var markerPath = owner.RootElement.GetProperty("processMarkerPath").GetString()!;
            using var marker = JsonDocument.Parse(File.ReadAllBytes(markerPath));
            AssertDocumentProperties(marker.RootElement, documents.GetProperty("processMarker"));
        }

        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "generation.txt"), "old");
        var token = Guid.NewGuid().ToString("D");
        var staged = output + ".stage-" + Guid.NewGuid().ToString("N");
        var backup = output + ".backup-" + token;
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "generation.txt"), "new");
        var journal = output + GeneratedOutputTransaction.TransactionSuffix;
        var imports = output + SunderPackageFormat.DevelopmentImportsSuffix;
        var stagedImports = imports + ".stage-" + Guid.NewGuid().ToString("N");
        var importsBackup = imports + ".backup-" + token;
        var outputs = new List<object> { new { finalPath = output, stagedPath = staged, backupPath = backup, existed = true } };
        if (developmentHandoff)
        {
            File.WriteAllText(imports, "old selection");
            File.WriteAllText(stagedImports, "new selection");
            outputs.Add(new { finalPath = imports, stagedPath = stagedImports, backupPath = importsBackup, existed = true });
        }
        File.WriteAllText(journal, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            token,
            coordinatorPath = output,
            outputs,
        }));
        using (var transaction = JsonDocument.Parse(File.ReadAllBytes(journal)))
        {
            AssertDocumentProperties(transaction.RootElement, documents.GetProperty("transaction"));
            AssertDocumentProperties(
                transaction.RootElement.GetProperty("outputs")[0],
                documents.GetProperty("transactionOutput"));
        }
        Directory.Move(output, backup);
        Directory.Move(staged, output);
        if (developmentHandoff)
        {
            File.Move(imports, importsBackup);
            File.Move(stagedImports, imports);
        }

        GeneratedOutputTransaction.RecoverAndCleanup(
            GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(output));

        Assert.Equal("old", File.ReadAllText(Path.Combine(output, "generation.txt")));
        if (developmentHandoff) Assert.Equal("old selection", File.ReadAllText(imports));
        Assert.False(File.Exists(journal));

        using var mutated = JsonDocument.Parse("""{"schemaVersion":1,"token":"x","coordinatorPath":"x","outputs":[],"seeded":true}""");
        Assert.ThrowsAny<Exception>(() => AssertDocumentProperties(
            mutated.RootElement,
            documents.GetProperty("transaction")));
    }

    [Fact]
    public void SharedOutputLock_MacOsCaseAliasesFailInsteadOfSelfContending()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var root = CreateTempDirectory();
        try
        {
            var output = Path.Combine(root, "CaseSensitiveProbe");
            var alias = Path.Combine(root, "casesensitiveprobe");
            Directory.CreateDirectory(output);
            if (!Directory.Exists(alias)) return;

            var exception = Assert.Throws<InvalidDataException>(() =>
                GeneratedOutputLock.Acquire([output], [alias], TimeSpan.FromMilliseconds(250)));
            Assert.Contains("differ only by case", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SharedOutputLock_MacOsSystemAliasAndPhysicalPathDeduplicate()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var root = CreateTempDirectory();
        try
        {
            string aliasRoot;
            string physicalRoot;
            if (root.StartsWith("/private/var/", StringComparison.Ordinal))
            {
                aliasRoot = root["/private".Length..];
                physicalRoot = root;
            }
            else if (root.StartsWith("/var/", StringComparison.Ordinal))
            {
                aliasRoot = root;
                physicalRoot = "/private" + root;
            }
            else
            {
                return;
            }

            using var outputLock = GeneratedOutputLock.Acquire(
                [Path.Combine(aliasRoot, "shared-output")],
                [Path.Combine(physicalRoot, "shared-output")],
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertDocumentProperties(JsonElement document, JsonElement specification)
    {
        if (specification.TryGetProperty("schemaVersion", out var schemaVersion))
        {
            Assert.Equal(schemaVersion.GetInt32(), document.GetProperty("schemaVersion").GetInt32());
        }
        var expected = specification.GetProperty("properties").EnumerateArray()
            .Select(static property => property.GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actual = document.EnumerateObject().Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, actual);
    }

    private static IDisposable AcquireGeneratedOutputLock(params string[] paths)
        => GeneratedOutputLock.Acquire(paths);

    private static TemporaryProcessMarker CreateDeadOwnerLock(string output)
        => CreateOwnerDirectory(
            output + GeneratedOutputLock.LockSuffix,
            int.MaxValue,
            Environment.MachineName,
            DateTime.UtcNow.AddMinutes(-1));

    private static bool CanStartNode()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "node",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "--version" },
            });
            if (process is null) return false;
            process.WaitForExit(5_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static CapturedProcess StartCapturedNode(string helper, params string[] arguments)
        => StartCapturedNodeInTempRoot(null, helper, arguments);

    private static CapturedProcess StartCapturedNodeInTempRoot(string? tempRoot, string helper, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = FindRepositoryRoot(),
            UseShellExecute = false,
        };
        if (tempRoot is not null)
        {
            Directory.CreateDirectory(tempRoot);
            foreach (var name in new[] { "TMPDIR", "TMP", "TEMP" }) startInfo.Environment[name] = tempRoot;
        }
        startInfo.ArgumentList.Add(helper);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return CapturedProcess.Start(startInfo);
    }

    private static void TryWriteReleaseSignal(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "release");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Process-tree cleanup below remains authoritative if the signal cannot be written.
        }
    }

    private static void CaptureCleanup(ICollection<Exception> errors, Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
    }

    private static async Task CaptureCleanupAsync(
        ICollection<Exception> errors,
        Func<ValueTask> cleanup)
    {
        try
        {
            await cleanup();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
    }
}
