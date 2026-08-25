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
        TaskCompletionSource generationRetired = new(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var firstPublication = await state.PublishSessionAsync(oldSession);
            Assert.Equal(1, firstPublication.Generation);

            var request = RunRequestAsync();
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var replacement = state.PublishSessionAsync(newSession);
            await Task.WhenAll(leaseRetired.Task, generationRetired.Task)
                .WaitAsync(TimeSpan.FromSeconds(2));

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
            lease.GenerationRetirementToken.Register(() => generationRetired.TrySetResult());
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
        var ignoredGenerationIdentity = ignoredLease.GenerationIdentity;

        try
        {
            var exception = await Assert.ThrowsAsync<RuntimeUnavailableException>(
                () => state.PublishSessionAsync(newSession));

            Assert.Contains("reload was not applied", exception.Message, StringComparison.Ordinal);
            Assert.True(ignoredLease.RetirementToken.IsCancellationRequested);
            Assert.True(ignoredLease.GenerationRetirementToken.IsCancellationRequested);
            Assert.True(Directory.Exists(oldFolder));
            using (var admitted = state.AcquireLease())
            {
                Assert.Same(oldSession, admitted.Session);
                Assert.Equal(1, admitted.Generation);
                Assert.NotSame(ignoredGenerationIdentity, admitted.GenerationIdentity);
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

    [Fact]
    public async Task PublishSessionAsync_WhenPostSwapCallbackFails_KeepsCommittedCandidateActive()
    {
        var callbackCount = 0;
        var state = new PackageSessionState(
            NullLogger.Instance,
            () =>
            {
                if (Interlocked.Increment(ref callbackCount) == 2)
                {
                    throw new IOException("Injected post-swap publication failure.");
                }
            },
            static _ => { });
        var oldFolder = CreateTempDirectory();
        var newFolder = CreateTempDirectory();
        var oldSession = CreateSession(oldFolder);
        var newSession = CreateSession(newFolder);

        try
        {
            await state.PublishSessionAsync(oldSession);

            await Assert.ThrowsAsync<IOException>(() => state.PublishSessionAsync(newSession));

            using var lease = state.AcquireLease();
            Assert.Same(newSession, lease.Session);
            Assert.Equal(2, lease.Generation);
            Assert.True(Directory.Exists(newFolder));
            Assert.False(Directory.Exists(oldFolder));
        }
        finally
        {
            await state.ClearActiveSessionAsync();
            TryDeleteDirectory(oldFolder);
            TryDeleteDirectory(newFolder);
        }
    }

    [Fact]
    public async Task PublicationHandle_FromDifferentState_IsRejectedBeforeMutation()
    {
        var owner = CreateState();
        var other = CreateState();
        var sessionFolder = CreateTempDirectory();
        var session = CreateSession(sessionFolder);
        PackageSessionState.SessionPublication? publication = null;

        try
        {
            publication = await owner.PreparePublicationAsync(
                session,
                expectedGeneration: 0,
                CancellationToken.None);

            var exception = Assert.Throws<InvalidOperationException>(
                () => other.RejectPublication(publication));

            Assert.Contains("does not belong", exception.Message, StringComparison.Ordinal);
            Assert.False(publication.Completed);
            Assert.Same(session, publication.Session);
        }
        finally
        {
            if (publication is not null)
            {
                if (!publication.Completed)
                {
                    owner.DiscardPublication(publication);
                }
                await owner.WaitForDiscardedCandidateDrainAsync(publication);
            }
            await session.DisposeAsync();
            await owner.ClearActiveSessionAsync();
            await other.ClearActiveSessionAsync();
            TryDeleteDirectory(sessionFolder);
        }
    }

    [Fact]
    public async Task PublicationSnapshot_RemainsImmutableAcrossLaterSessionMutation()
    {
        var state = CreateState();
        var sessionFolder = CreateTempDirectory();
        var views = new List<PackageViewDescriptor>
        {
            new(
                "main",
                "snapshot.package",
                "Main",
                new PackageIconDescriptor("S", null),
                "main"),
        };
        var contractUse = new PackageRpcContractUseDescriptor(
            "consumer",
            "snapshot.contract",
            "1.0.0",
            new string('a', 64),
            Required: true,
            Actions: ["read"]);
        var descriptor = new SessionPackageDescriptor(
            "snapshot.package",
            "Snapshot Package",
            "1.0.0",
            PackageHostRoles.Runtime,
            new PackageIconDescriptor("S", null),
            IsEnabled: true,
            Readiness: PackageReadinessState.Ready,
            Views: views,
            FailureOrigin: null,
            LastError: null,
            LastFailureAtUtc: null,
            FailureCount: 0,
            RpcContractUses: [contractUse]);
        var session = CreateSession(sessionFolder, descriptor);

        try
        {
            await state.PublishSessionAsync(session);
            var snapshot = state.CapturePublicationSnapshot(expectedGeneration: 1);
            Assert.Throws<RuntimeStaleGenerationException>(
                () => state.CapturePublicationSnapshot(expectedGeneration: 0));

            views.Add(new PackageViewDescriptor(
                "later",
                "snapshot.package",
                "Later",
                Icon: null,
                DefaultPlacement: null));
            Assert.True(session.MarkPackageFailed(
                "snapshot.package",
                PackageFailureOrigin.RuntimeBackgroundService,
                "Later failure.",
                out _));

            Assert.Equal(1, snapshot.Generation);
            var activePackage = Assert.Single(snapshot.ActivePackages);
            Assert.True(activePackage.IsEnabled);
            Assert.Equal(PackageReadinessState.Ready, activePackage.Readiness);
            Assert.Single(activePackage.Views);
            var sessionPackage = Assert.Single(snapshot.SessionPackages);
            Assert.NotSame(descriptor, sessionPackage);
            Assert.NotSame(descriptor.Icon, sessionPackage.Icon);
            Assert.NotSame(descriptor.Views[0], sessionPackage.Views[0]);
            Assert.NotSame(contractUse, Assert.Single(sessionPackage.RpcContractUses));
            Assert.True(sessionPackage.IsEnabled);
            Assert.Equal(PackageReadinessState.Ready, sessionPackage.Readiness);
            Assert.Single(sessionPackage.Views);
            Assert.Empty(session.GetActivePackages());
            Assert.Equal(
                PackageReadinessState.Failed,
                Assert.Single(session.GetSessionPackages()).Readiness);
            Assert.Throws<NotSupportedException>(
                () => ((IList<ActivePackageDescriptor>)snapshot.ActivePackages).Clear());
            Assert.Throws<NotSupportedException>(
                () => ((IList<PackageViewDescriptor>)sessionPackage.Views).Clear());
        }
        finally
        {
            await state.ClearActiveSessionAsync();
            TryDeleteDirectory(sessionFolder);
        }
    }

    private static PackageSessionState CreateState()
        => new(NullLogger.Instance, static () => { }, static _ => { });

    private static ActivePackageSession CreateSession(
        string folder,
        params SessionPackageDescriptor[] packages)
        => new(
            folder,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase),
            packages.ToDictionary(
                package => package.PackageId,
                StringComparer.OrdinalIgnoreCase));

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
