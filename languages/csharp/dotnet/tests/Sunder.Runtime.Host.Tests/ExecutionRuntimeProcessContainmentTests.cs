using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Core.TestSupport;
using Sunder.Package.Projections;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class ExecutionRuntimeProcessContainmentTests
{
    [Fact]
    [Trait("Capability", "Process")]
    public async Task PayloadIdentity_AllowsRecoveryWhenGuardianDiesFirst()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            ".sunder-runtime-containment-guardian-loss-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "mode.txt"), "containment-hang");
        var startInfo = TestWorkerFixture.CreateProcessStartInfo(root);
        var processes = new SystemExecutionRuntimeProcessController();
        var controller = new SystemExecutionRuntimeProcessContainmentController(processes);
        var leaseId = Guid.NewGuid().ToString("N");
        var generation = Guid.NewGuid();
        var reportPath = Path.Combine(root, "payload.report");
        IExecutionRuntimePendingContainedProcess? pending = null;
        try
        {
            pending = await controller.StartAsync(
                leaseId,
                generation,
                startInfo,
                reportPath,
                CancellationToken.None);
            var contained = await pending.AuthorizePayloadAsync();
            var payload = processes.Observe(contained.Process);
            var now = DateTimeOffset.UtcNow;
            var lease = new ExecutionRuntimeProcessLeaseV1(
                ExecutionRuntimeProcessLeaseV1.CurrentSchemaVersion,
                leaseId,
                Guid.NewGuid(),
                "containment.test",
                Guid.NewGuid().ToString("N"),
                Guid.NewGuid(),
                new string('a', 64),
                now,
                ProcessStartRequestedAtUtc: now,
                ContainmentGeneration: generation,
                ContainmentKind: pending.Kind,
                ContainmentIdentity: pending.Identity,
                ContainmentProcessId: pending.ContainmentProcess.ProcessId,
                ContainmentProcessBirthIdentity: pending.ContainmentProcess.BirthIdentity,
                ContainmentProcessStartedAtUtc: pending.ContainmentProcess.StartedAtUtc,
                ContainmentExecutablePath: pending.ContainmentProcess.ExecutablePath,
                ContainmentAttachedAtUtc: now,
                ProcessId: payload.ProcessId,
                ProcessBirthIdentity: payload.BirthIdentity,
                ProcessStartedAtUtc: payload.StartedAtUtc,
                ExecutablePath: payload.ExecutablePath,
                RuntimeRootPath: root,
                AttachedAtUtc: now);

            using (var guardian = Process.GetProcessById(pending.ContainmentProcess.ProcessId))
            {
                guardian.Kill();
                await guardian.WaitForExitAsync();
            }

            Assert.Equal(ExecutionRuntimeContainmentProbe.Active, controller.Probe(lease));
            Assert.True(await controller.TerminateAndDrainAsync(lease, CancellationToken.None));
            await contained.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(contained.Process.HasExited);
        }
        finally
        {
            if (pending is not null)
            {
                await pending.TerminateAndDrainAsync();
                await pending.DisposeAsync();
            }
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [UnixFact]
    [Trait("Capability", "Process")]
    public async Task DetachedPayload_IsIdentityMismatchAndGroupIsNotSignaled()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            ".sunder-runtime-containment-detach-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "mode.txt"), "containment-detach");
        var startInfo = TestWorkerFixture.CreateProcessStartInfo(root);
        var processes = new SystemExecutionRuntimeProcessController();
        var controller = new SystemExecutionRuntimeProcessContainmentController(processes);
        var leaseId = Guid.NewGuid().ToString("N");
        var generation = Guid.NewGuid();
        var reportPath = Path.Combine(root, "payload.report");
        IExecutionRuntimePendingContainedProcess? pending = null;
        IExecutionRuntimeContainedProcess? contained = null;
        try
        {
            pending = await controller.StartAsync(
                leaseId,
                generation,
                startInfo,
                reportPath,
                CancellationToken.None);
            contained = await pending.AuthorizePayloadAsync();
            await Eventually.ObserveAsync(
                _ => ValueTask.FromResult(File.Exists(Path.Combine(root, "containment-detach.ready"))),
                static ready => ready,
                TimeSpan.FromSeconds(5));
            var payload = processes.Observe(contained.Process);
            var now = DateTimeOffset.UtcNow;
            var lease = new ExecutionRuntimeProcessLeaseV1(
                ExecutionRuntimeProcessLeaseV1.CurrentSchemaVersion,
                leaseId,
                Guid.NewGuid(),
                "containment.test",
                Guid.NewGuid().ToString("N"),
                Guid.NewGuid(),
                new string('a', 64),
                now,
                ProcessStartRequestedAtUtc: now,
                ContainmentGeneration: generation,
                ContainmentKind: pending.Kind,
                ContainmentIdentity: pending.Identity,
                ContainmentProcessId: pending.ContainmentProcess.ProcessId,
                ContainmentProcessBirthIdentity: pending.ContainmentProcess.BirthIdentity,
                ContainmentProcessStartedAtUtc: pending.ContainmentProcess.StartedAtUtc,
                ContainmentExecutablePath: pending.ContainmentProcess.ExecutablePath,
                ContainmentAttachedAtUtc: now,
                ProcessId: payload.ProcessId,
                ProcessBirthIdentity: payload.BirthIdentity,
                ProcessStartedAtUtc: payload.StartedAtUtc,
                ExecutablePath: payload.ExecutablePath,
                RuntimeRootPath: root,
                AttachedAtUtc: now);

            Assert.Equal(ExecutionRuntimeContainmentProbe.IdentityMismatch, controller.Probe(lease));
            Assert.False(await controller.TerminateAndDrainAsync(lease, CancellationToken.None));
            Assert.False(contained.Process.HasExited);

            contained.Process.Kill();
            await contained.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await controller.TerminateAndDrainAsync(lease, CancellationToken.None));
        }
        finally
        {
            if (contained?.Process is { HasExited: false } payload)
            {
                payload.Kill();
                await payload.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            if (pending is not null)
            {
                try
                {
                    await pending.TerminateAndDrainAsync();
                }
                catch
                {
                }
                await pending.DisposeAsync();
            }
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    [Trait("Capability", "Process")]
    public async Task Guardian_DrainsDescendantsAfterPayloadRootStartsThem()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            ".sunder-runtime-containment-child-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "mode.txt"), "containment-child");
        var startInfo = TestWorkerFixture.CreateProcessStartInfo(root);
        var reportPath = Path.Combine(root, "payload.report");
        var controller = new SystemExecutionRuntimeProcessContainmentController(
            new SystemExecutionRuntimeProcessController());
        IExecutionRuntimePendingContainedProcess? pending = null;
        Process? child = null;
        try
        {
            pending = await controller.StartAsync(
                Guid.NewGuid().ToString("N"),
                Guid.NewGuid(),
                startInfo,
                reportPath,
                CancellationToken.None);
            var contained = await pending.AuthorizePayloadAsync();
            var childPidPath = Path.Combine(root, "containment-child.pid");
            var childPid = await Eventually.ObserveAsync<int>(
                async cancellationToken =>
                {
                    if (!File.Exists(childPidPath)) return 0;
                    var content = await File.ReadAllTextAsync(childPidPath, cancellationToken);
                    return int.TryParse(
                        content,
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var processId) && processId > 0
                            ? processId
                            : 0;
                },
                static processId => processId > 0,
                TimeSpan.FromSeconds(5),
                describe: (processId, error) =>
                    $"path={childPidPath}; processId={processId}; error={error?.Message}");
            child = Process.GetProcessById(childPid);
            Assert.False(child.HasExited);

            await contained.TerminateAndDrainAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(child.HasExited);
        }
        finally
        {
            child?.Dispose();
            if (pending is not null)
            {
                await pending.TerminateAndDrainAsync();
                await pending.DisposeAsync();
            }
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [UnixFact]
    [Trait("Capability", "Process")]
    public async Task Guardian_DrainsDescendantsWhenPayloadRootExitsFirst()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            ".sunder-runtime-containment-root-exit-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "mode.txt"), "containment-child-exit");
        var startInfo = TestWorkerFixture.CreateProcessStartInfo(root);
        var reportPath = Path.Combine(root, "payload.report");
        var controller = new SystemExecutionRuntimeProcessContainmentController(
            new SystemExecutionRuntimeProcessController());
        IExecutionRuntimePendingContainedProcess? pending = null;
        Process? child = null;
        try
        {
            pending = await controller.StartAsync(
                Guid.NewGuid().ToString("N"),
                Guid.NewGuid(),
                startInfo,
                reportPath,
                CancellationToken.None);
            var contained = await pending.AuthorizePayloadAsync();
            var childPidPath = Path.Combine(root, "containment-child.pid");
            var childPid = await Eventually.ObserveAsync<int>(
                async cancellationToken =>
                {
                    if (!File.Exists(childPidPath)) return 0;
                    var content = await File.ReadAllTextAsync(childPidPath, cancellationToken);
                    return int.TryParse(content, out var processId) ? processId : 0;
                },
                static processId => processId > 0,
                TimeSpan.FromSeconds(5));
            child = Process.GetProcessById(childPid);

            await contained.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(child.HasExited);
        }
        finally
        {
            if (child is { HasExited: false })
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            child?.Dispose();
            if (pending is not null)
            {
                try
                {
                    await pending.TerminateAndDrainAsync();
                }
                catch
                {
                }
                await pending.DisposeAsync();
            }
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [UnixFact]
    [Trait("Capability", "Process")]
    public async Task Guardian_ForceKillsDescendantWithoutAnUnanchoredEscalationDelay()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            ".sunder-runtime-containment-stubborn-child-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "mode.txt"), "containment-child-ignore-term");
        var startInfo = TestWorkerFixture.CreateProcessStartInfo(root);
        var reportPath = Path.Combine(root, "payload.report");
        var controller = new SystemExecutionRuntimeProcessContainmentController(
            new SystemExecutionRuntimeProcessController());
        IExecutionRuntimePendingContainedProcess? pending = null;
        Process? child = null;
        try
        {
            pending = await controller.StartAsync(
                Guid.NewGuid().ToString("N"),
                Guid.NewGuid(),
                startInfo,
                reportPath,
                CancellationToken.None);
            var contained = await pending.AuthorizePayloadAsync();
            var childPidPath = Path.Combine(root, "containment-child.pid");
            var childReadyPath = Path.Combine(root, "containment-child.ready");
            var childPid = await Eventually.ObserveAsync<int>(
                async cancellationToken =>
                {
                    if (!File.Exists(childPidPath) || !File.Exists(childReadyPath)) return 0;
                    var content = await File.ReadAllTextAsync(childPidPath, cancellationToken);
                    return int.TryParse(
                        content,
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var processId) && processId > 0
                            ? processId
                            : 0;
                },
                static processId => processId > 0,
                TimeSpan.FromSeconds(5),
                describe: (processId, error) =>
                    $"pidPath={childPidPath}; readyPath={childReadyPath}; processId={processId}; error={error?.Message}");
            child = Process.GetProcessById(childPid);
            Assert.False(child.HasExited);

            await contained.TerminateAndDrainAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(child.HasExited);
            Assert.False(File.Exists(Path.Combine(root, "containment-child.sigterm")));
        }
        finally
        {
            if (child is { HasExited: false })
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            child?.Dispose();
            if (pending is not null)
            {
                try
                {
                    await pending.TerminateAndDrainAsync();
                }
                catch
                {
                }
                await pending.DisposeAsync();
            }
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    [Trait("Capability", "Process")]
    public async Task DurableLease_AttachesContainmentBeforePayloadAndReleasesAfterDrain()
    {
        var stateRoot = Path.Combine(
            AppContext.BaseDirectory,
            ".sunder-runtime-contained-lease-tests",
            Guid.NewGuid().ToString("N"));
        var layout = new ExecutionRuntimeStoreLayout(stateRoot);
        foreach (var path in layout.DurableRootPaths)
        {
            Directory.CreateDirectory(path);
        }
        var contentIdentity = new string('a', 64);
        var runtimeRoot = layout.GetContentRootPath(contentIdentity);
        Directory.CreateDirectory(runtimeRoot);
        TestWorkerFixture.CopyPayload(runtimeRoot);
        await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "mode.txt"), "containment-echo");
        var launcherPath = TestWorkerFixture.CopyAppHost(runtimeRoot);
        var registry = new ExecutionRuntimeLeaseRegistry(
            layout,
            Guid.NewGuid(),
            TimeProvider.System);

        try
        {
            await using var lease = await registry.ReserveAsync(
                new ExecutionRuntimeLeaseOwner(
                    "containment.test",
                    Guid.NewGuid().ToString("N"),
                    Guid.NewGuid()),
                contentIdentity,
                runtimeRoot,
                launcherPath,
                CancellationToken.None);
            var startInfo = TestWorkerFixture.CreateAppHostProcessStartInfo(launcherPath, runtimeRoot);
            await using var contained = await lease.LaunchAsync(startInfo);

            var document = JsonSerializer.Deserialize<ExecutionRuntimeProcessLeaseV1>(
                await File.ReadAllBytesAsync(layout.GetLeasePath(lease.LeaseId)),
                ExecutionRuntimeStore.JsonOptions);
            Assert.NotNull(document);
            Assert.True(document.HasContainment);
            Assert.True(document.IsAttached);
            Assert.False(File.Exists(layout.GetLeasePayloadReportPath(lease.LeaseId)));

            var expected = new byte[] { 0x10, 0x20, 0x30 };
            await contained.StandardInput.WriteAsync(expected);
            await contained.StandardInput.FlushAsync();
            var actual = new byte[expected.Length];
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                await contained.StandardOutput.ReadExactlyAsync(actual, timeout.Token);
            }
            Assert.Equal(expected, actual);

            await contained.TerminateAndDrainAsync();
            await lease.ReleaseAfterProcessTreeTerminationAsync();
            Assert.False(File.Exists(layout.GetLeasePath(lease.LeaseId)));
        }
        finally
        {
            try
            {
                Directory.Delete(stateRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    [Trait("Capability", "Process")]
    public async Task RuntimeProjectionLease_SurvivesOrphanWorkerUntilDurableContainmentRecovery()
    {
        var testRoot = Path.Combine(
            AppContext.BaseDirectory,
            ".sunder-runtime-projection-lease-tests",
            Guid.NewGuid().ToString("N"));
        var stateRoot = Path.Combine(testRoot, "state");
        var sourceRoot = Path.Combine(testRoot, "projection-source");
        Directory.CreateDirectory(sourceRoot);
        var modePath = Path.Combine(sourceRoot, "mode.txt");
        await File.WriteAllTextAsync(modePath, "containment-hang");
        var modeBytes = await File.ReadAllBytesAsync(modePath);
        var projectionCache = new PackageProjectionCache(stateRoot);
        PackageProjectionAcquisition? projection = null;
        IExecutionRuntimeProcessLease? runtimeLease = null;
        IExecutionRuntimeContainedProcess? contained = null;
        try
        {
            projection = await projectionCache.GetOrBuildAndLeaseAsync(
                new PackageProjectionRequest(
                    sourceRoot,
                    "projection.containment",
                    Convert.ToHexStringLower(SHA256.HashData("projection-source"u8)),
                    PackageProjectionRole.Runtime,
                    PackageProjectionCache.DefaultRulesIdentity,
                    ["runtime/test-rid"],
                    [new PackageProjectionSourceFile(
                        "mode.txt",
                        "mode.txt",
                        Convert.ToHexStringLower(SHA256.HashData(modeBytes)),
                        modeBytes.LongLength)],
                    ["mode.txt"]),
                new PackageProjectionOwner(
                    "runtime-host",
                    Guid.NewGuid(),
                    "projection.containment",
                    Guid.NewGuid().ToString("N")));
            var projectionLeasePath = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(projectionCache.RootPath, "leases"),
                "*.json",
                SearchOption.AllDirectories));

            var layout = new ExecutionRuntimeStoreLayout(stateRoot);
            foreach (var path in layout.DurableRootPaths)
            {
                Directory.CreateDirectory(path);
            }
            var contentIdentity = new string('a', 64);
            var runtimeRoot = layout.GetContentRootPath(contentIdentity);
            Directory.CreateDirectory(runtimeRoot);
            TestWorkerFixture.CopyPayload(runtimeRoot);
            var launcherPath = TestWorkerFixture.CopyAppHost(runtimeRoot);
            var originalHostInstance = Guid.NewGuid();
            var registry = new ExecutionRuntimeLeaseRegistry(
                layout,
                originalHostInstance,
                TimeProvider.System);
            runtimeLease = await registry.ReserveAsync(
                new ExecutionRuntimeLeaseOwner(
                    "projection.containment",
                    Guid.NewGuid().ToString("N"),
                    Guid.NewGuid()),
                contentIdentity,
                runtimeRoot,
                launcherPath,
                CancellationToken.None);
            contained = await runtimeLease.LaunchAsync(
                TestWorkerFixture.CreateAppHostProcessStartInfo(launcherPath, projection.RootPath),
                CancellationToken.None,
                projection);

            var runtimeDocument = JsonSerializer.Deserialize<ExecutionRuntimeProcessLeaseV1>(
                await File.ReadAllBytesAsync(layout.GetLeasePath(runtimeLease.LeaseId)),
                ExecutionRuntimeStore.JsonOptions);
            Assert.NotNull(runtimeDocument);
            Assert.True(runtimeDocument.HasContainment);
            Assert.True(runtimeDocument.IsAttached);
            using (var projectionDocument = JsonDocument.Parse(
                       await File.ReadAllBytesAsync(projectionLeasePath)))
            {
                var root = projectionDocument.RootElement;
                Assert.Equal(runtimeLease.LeaseId, root.GetProperty("processTreeLeaseId").GetString());
                Assert.Equal(2, root.GetProperty("processTreeProcesses").GetArrayLength());
                Assert.Equal(Environment.ProcessId, root.GetProperty("ownerProcessId").GetInt32());
            }

            var recoveringProjectionCache = new PackageProjectionCache(stateRoot);
            Assert.Equal(0, await recoveringProjectionCache.RecoverLeasesAsync());
            Assert.True(File.Exists(projectionLeasePath));

            using (var guardian = Process.GetProcessById(runtimeDocument.ContainmentProcessId!.Value))
            {
                guardian.Kill();
                await guardian.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            contained.Process.Refresh();
            Assert.False(contained.Process.HasExited);
            Assert.Equal(0, await recoveringProjectionCache.RecoverLeasesAsync());
            Assert.True(File.Exists(projectionLeasePath));

            var recoveringRegistry = new ExecutionRuntimeLeaseRegistry(
                layout,
                Guid.NewGuid(),
                TimeProvider.System);
            await recoveringRegistry.RecoverAsync(CancellationToken.None);
            await contained.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await runtimeLease.ReleaseAfterProcessTreeTerminationAsync();

            Assert.False(File.Exists(layout.GetLeasePath(runtimeLease.LeaseId)));
            Assert.Equal(1, await recoveringProjectionCache.RecoverLeasesAsync());
            Assert.False(File.Exists(projectionLeasePath));
            Assert.True(Directory.Exists(projection.RootPath));
        }
        finally
        {
            if (contained is not null)
            {
                try
                {
                    await contained.TerminateAndDrainAsync();
                }
                catch
                {
                }
                await contained.DisposeAsync();
            }
            if (runtimeLease is not null)
            {
                try
                {
                    await runtimeLease.ReleaseAfterProcessTreeTerminationAsync();
                }
                catch
                {
                }
                try
                {
                    await runtimeLease.DisposeAsync();
                }
                catch
                {
                }
            }
            if (projection is not null)
            {
                try
                {
                    await projectionCache.RecoverLeasesAsync();
                    await projection.DisposeAsync();
                }
                catch
                {
                }
            }
            try
            {
                await using (var reset = await projectionCache.AcquireOfflineResetScopeAsync())
                {
                    await reset.ValidateAsync();
                    await reset.PrepareForDeletionAsync();
                    if (Directory.Exists(projectionCache.RootPath))
                    {
                        Directory.Delete(projectionCache.RootPath, recursive: true);
                    }
                }
                Directory.Delete(testRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    [Trait("Capability", "Process")]
    public async Task Guardian_GatesPayloadAndDrainsPlatformContainment()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "sunder-runtime-containment-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "mode.txt"), "containment-echo");
        var startInfo = TestWorkerFixture.CreateProcessStartInfo(root);
        var generation = Guid.NewGuid();
        var reportPath = Path.Combine(root, "payload.report");
        var controller = new SystemExecutionRuntimeProcessContainmentController(
            new SystemExecutionRuntimeProcessController());
        IExecutionRuntimePendingContainedProcess? pending = null;
        try
        {
            pending = await controller.StartAsync(
                Guid.NewGuid().ToString("N"),
                generation,
                startInfo,
                reportPath,
                CancellationToken.None);

            Assert.Equal(
                OperatingSystem.IsWindows()
                    ? SystemExecutionRuntimeProcessContainmentController.WindowsJobKind
                    : SystemExecutionRuntimeProcessContainmentController.UnixProcessGroupKind,
                pending.Kind);
            Assert.False(File.Exists(reportPath));

            var contained = await pending.AuthorizePayloadAsync();
            Assert.False(contained.Process.HasExited);
            Assert.True(File.Exists(reportPath));
            var expected = new byte[] { 0x00, 0x01, 0x7f, 0xff };
            await contained.StandardInput.WriteAsync(expected);
            await contained.StandardInput.FlushAsync();
            var actual = new byte[expected.Length];
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                await contained.StandardOutput.ReadExactlyAsync(actual, timeout.Token);
            }
            Assert.Equal(expected, actual);

            await contained.TerminateAndDrainAsync();
            contained.Process.Refresh();
            Assert.True(contained.Process.HasExited);
        }
        finally
        {
            if (pending is not null)
            {
                await pending.TerminateAndDrainAsync();
                await pending.DisposeAsync();
            }
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

}
