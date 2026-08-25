using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Sunder.Core.TestSupport;
using Xunit;

namespace Sunder.Core.TestSupport.Tests;

public sealed class TestSupportFaultProbeTests
{
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Size", "Small")]
    public async Task TemporaryScope_ReportsCleanupFailureAndRemainingTree()
    {
        var diagnostics = new List<string>();
        var scope = TemporaryDirectoryScope.Create(
            "sunder-test-support-probe",
            new TemporaryDirectoryScopeOptions(
                CleanupAttempts: 2,
                (_, _) => ValueTask.FromException(new IOException("seeded delete fault")),
                diagnostics.Add));
        scope.WriteAllText("retained.txt", "fault probe");

        var failure = await Assert.ThrowsAsync<TemporaryDirectoryCleanupException>(
            () => scope.DisposeAsync().AsTask());

        Assert.Equal(2, diagnostics.Count);
        Assert.Contains("seeded delete fault", failure.Message, StringComparison.Ordinal);
        Assert.Contains("retained.txt", failure.Message, StringComparison.Ordinal);
        Directory.Delete(scope.RootPath, recursive: true);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Size", "Small")]
    public async Task ManualTime_DelayCannotCompleteBeforeItsClockBoundary()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-22T00:00:00Z"));
        var delay = time.DelayAsync(TimeSpan.FromSeconds(10));

        time.Advance(TimeSpan.FromSeconds(9));
        await Eventually.AssertPendingAsync(delay, () => $"now={time.GetUtcNow():O}; timers={time.PendingTimerCount}");
        time.Advance(TimeSpan.FromSeconds(1));

        await delay.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(DateTimeOffset.Parse("2026-08-22T00:00:10Z"), time.GetUtcNow());
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Size", "Small")]
    public async Task Eventual_TimeoutContainsLastObservationAndSeededFault()
    {
        var attempts = 0;

        var failure = await Assert.ThrowsAsync<EventualTimeoutException>(() => Eventually.ObserveAsync(
            _ =>
            {
                attempts++;
                return attempts == 1
                    ? ValueTask.FromException<int>(new InvalidOperationException("seeded observation fault"))
                    : ValueTask.FromResult(attempts);
            },
            _ => false,
            TimeSpan.FromMilliseconds(75),
            TimeSpan.FromMilliseconds(10),
            (last, error) => $"last={last}; error={error?.Message}"));

        Assert.Contains("last=", failure.Message, StringComparison.Ordinal);
        Assert.True(attempts > 1);
    }

    [Fact]
    [Trait("Category", "Component")]
    [Trait("Capability", "LoopbackNetwork")]
    [Trait("Size", "Small")]
    public async Task LoopbackStreaming_BodyCannotCrossUnreleasedChunkBarrier()
    {
        var gate = new ObservableGate();
        var response = new LoopbackHttpResponse(
            200,
            [new LoopbackHttpChunk("ok"u8.ToArray(), gate)],
            declaredContentLength: 2);
        await using var server = LoopbackStreamingHttpServer.Start(response);
        using var client = new HttpClient { BaseAddress = server.BaseAddress, Timeout = Timeout.InfiniteTimeSpan };

        using var message = await client.GetAsync("stream", HttpCompletionOption.ResponseHeadersRead);
        await response.HeadersSent.WaitAsync(TimeSpan.FromSeconds(2));
        await using var stream = await message.Content.ReadAsStreamAsync();
        var read = stream.ReadAsync(new byte[2]).AsTask();
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(2));

        await Eventually.AssertPendingAsync(read, () => $"requests={server.Requests.Count}; gateReleased={gate.IsReleased}");
        gate.Release();
        Assert.Equal(2, await read.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    [Trait("Category", "Component")]
    [Trait("Capability", "Process")]
    [Trait("Size", "Small")]
    public async Task CapturedProcess_NonzeroExitSurfacesStdoutAndStderrDiagnostics()
    {
        var startInfo = DotnetStartInfo("definitely-not-a-real-dotnet-command");
        await using var process = CapturedProcess.Start(startInfo);

        var failure = await Assert.ThrowsAsync<ProcessFixtureException>(
            () => process.EnsureSuccessAsync(TimeSpan.FromSeconds(15)));

        Assert.Contains("exited with code", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stderr:", failure.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Filesystem")]
    [Trait("Size", "Small")]
    public async Task ArchiveFixture_IsDeterministicAndMutationProbeChangesBytes()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-archive-fixture-probe");
        var builder = new PackageArchiveFixtureBuilder()
            .AddText("manifest/sunder-package.json", "{\"manifestVersion\":3}")
            .AddText("payload/shared/assets/value.txt", "original");
        var first = builder.WriteArchive(scope.GetPath("first.sunderpkg"));
        var second = new PackageArchiveFixtureBuilder()
            .AddText("payload/shared/assets/value.txt", "original")
            .AddText("manifest/sunder-package.json", "{\"manifestVersion\":3}")
            .WriteArchive(scope.GetPath("second.sunderpkg"));

        Assert.Equal(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(second));

        var mutated = new PackageArchiveFixtureBuilder()
            .AddText("manifest/sunder-package.json", "{\"manifestVersion\":3}")
            .AddText("payload/shared/assets/value.txt", "original")
            .Mutate("payload/shared/assets/value.txt", _ => Encoding.UTF8.GetBytes("seeded mutation"))
            .WriteArchive(scope.GetPath("mutated.sunderpkg"));
        Assert.NotEqual(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(mutated));
        using var archive = ZipFile.OpenRead(mutated);
        Assert.Equal(2, archive.Entries.Count);
    }

    [Fact]
    [Trait("Category", "Component")]
    [Trait("Capability", "Filesystem")]
    [Trait("Size", "Small")]
    public async Task OutputLockBarrier_FailsWhenSeededContenderDoesNotBlock()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-output-lock-probe");
        using var barrier = OutputLockContenderBarrier<object>.Start(
            scope.GetPath("generated"),
            () =>
            {
                Thread.Sleep(200);
                File.WriteAllText(scope.GetPath("unlocked-output.txt"), "seeded mutation");
                return new object();
            },
            static () => "seeded contender performed delayed I/O without acquiring the lock");

        var failure = await Assert.ThrowsAsync<TimeoutException>(
            () => barrier.WaitForAttemptAsync(TimeSpan.FromMilliseconds(100)));

        Assert.Contains("without acquiring", failure.Message, StringComparison.Ordinal);
        await barrier.Completion;
    }

    private static ProcessStartInfo DotnetStartInfo(string argument)
    {
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(argument);
        return startInfo;
    }
}
