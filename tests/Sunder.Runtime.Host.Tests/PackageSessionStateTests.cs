using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageSessionStateTests
{
    [Fact]
    public async Task PublishSessionAsync_DrainsBeforePublishingReplacement()
    {
        var state = CreateState();
        var oldFolder = CreateTempDirectory();
        var newFolder = CreateTempDirectory();
        var oldSession = CreateSession(oldFolder);
        var newSession = CreateSession(newFolder);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource leaseRetired = new(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var firstPublication = await state.PublishSessionAsync(oldSession);
            Assert.Equal(1, firstPublication.Generation);

            var request = RunRequestAsync();
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var replacement = state.PublishSessionAsync(newSession);
            await leaseRetired.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(replacement.IsCompleted);
            Assert.True(Directory.Exists(oldFolder));
            Assert.Throws<RuntimeUnavailableException>(() => state.AcquireLease());

            releaseRequest.TrySetResult();
            await request.WaitAsync(TimeSpan.FromSeconds(2));
            var secondPublication = await replacement.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(2, secondPublication.Generation);
            Assert.False(Directory.Exists(oldFolder));
            using (var newRequest = state.AcquireLease())
            {
                Assert.Same(newSession, newRequest.Session);
                Assert.Equal(2, newRequest.Generation);
            }
        }
        finally
        {
            releaseRequest.TrySetResult();
            await state.ClearActiveSessionAsync();
            TryDeleteDirectory(oldFolder);
            TryDeleteDirectory(newFolder);
        }

        async Task RunRequestAsync()
        {
            using var lease = state.AcquireLease();
            Assert.Same(oldSession, lease.Session);
            Assert.Equal(1, lease.Generation);
            requestStarted.TrySetResult();
            lease.RetirementToken.Register(() => leaseRetired.TrySetResult());
            await releaseRequest.Task;
            Assert.True(Directory.Exists(oldFolder));
            Assert.Empty(lease.Session.GetActivePackages());
        }
    }

    [Fact]
    public async Task ClearActiveSessionAsync_DrainsOutstandingLeaseBeforeDisposal()
    {
        var state = CreateState();
        var sessionFolder = CreateTempDirectory();
        var session = CreateSession(sessionFolder);
        await state.PublishSessionAsync(session);
        var lease = state.AcquireLease();

        try
        {
            var clear = state.ClearActiveSessionAsync();
            await Task.Yield();

            Assert.False(clear.IsCompleted);
            Assert.True(Directory.Exists(sessionFolder));
            Assert.True(lease.RetirementToken.IsCancellationRequested);
            Assert.Throws<RuntimeUnavailableException>(() => state.AcquireLease());

            lease.Dispose();
            await clear.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(Directory.Exists(sessionFolder));
        }
        finally
        {
            lease.Dispose();
            await state.ClearActiveSessionAsync();
            TryDeleteDirectory(sessionFolder);
        }
    }

    [Fact]
    public async Task PublishSessionAsync_WhenLeaseIgnoresRetirement_FailsAndReopensAdmission()
    {
        var state = new PackageSessionState(
            NullLogger.Instance,
            static () => { },
            static _ => { },
            TimeSpan.FromMilliseconds(50));
        var oldFolder = CreateTempDirectory();
        var newFolder = CreateTempDirectory();
        var oldSession = CreateSession(oldFolder);
        var newSession = CreateSession(newFolder);
        await state.PublishSessionAsync(oldSession);
        var ignoredLease = state.AcquireLease();

        try
        {
            var exception = await Assert.ThrowsAsync<RuntimeUnavailableException>(
                () => state.PublishSessionAsync(newSession));

            Assert.Contains("reload was not applied", exception.Message, StringComparison.Ordinal);
            Assert.True(ignoredLease.RetirementToken.IsCancellationRequested);
            Assert.True(Directory.Exists(oldFolder));
            using (var admitted = state.AcquireLease())
            {
                Assert.Same(oldSession, admitted.Session);
                Assert.Equal(1, admitted.Generation);
            }
        }
        finally
        {
            ignoredLease.Dispose();
            await newSession.DisposeAsync();
            await state.ClearActiveSessionAsync();
            TryDeleteDirectory(oldFolder);
            TryDeleteDirectory(newFolder);
        }
    }

    private static PackageSessionState CreateState()
        => new(NullLogger.Instance, static () => { }, static _ => { });

    private static ActivePackageSession CreateSession(string folder)
        => new(
            folder,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase));

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
