using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class ExecutionRuntimeLifecyclePolicyCacheTests
{
    private static readonly DateTimeOffset StagedAt = DateTimeOffset.Parse("2026-08-27T12:00:00Z");
    private static readonly DateTimeOffset SupportedAt = DateTimeOffset.Parse("2026-08-28T12:00:00Z");
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-08-29T12:00:00Z");

    [Fact]
    public async Task Observe_PersistsExactDeprecatedPolicyAndReloads()
    {
        var root = CreateRoot();
        try
        {
            var layout = new ExecutionRuntimeStoreLayout(Path.Combine(root, "state"));
            var receipt = CreateReceipt();
            var cache = new ExecutionRuntimeLifecyclePolicyCache(layout);

            await cache.ObserveAsync([
                CreateObservation(receipt, DeprecatedLifecycle(), revision: 7),
            ]);

            var snapshot = cache.CaptureSnapshot();
            Assert.Equal(1, snapshot.Generation);
            Assert.True(snapshot.AuthorizesCollection(receipt));
            Assert.Equal(1, Assert.Single(snapshot.Entries).MinimumHostTrustGeneration);
            Assert.True(File.Exists(layout.LifecyclePolicyPath));

            var reloaded = new ExecutionRuntimeLifecyclePolicyCache(layout);
            await reloaded.InitializeAsync();
            Assert.Equal(1, reloaded.CaptureSnapshot().Generation);
            Assert.True(reloaded.CaptureSnapshot().AuthorizesCollection(receipt));
            Assert.False(reloaded.CaptureSnapshot().AuthorizesCollection(
                CopyReceipt(receipt, artifactSha256: new string('f', 64))));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Observe_PersistsAndMatchesAuthoritativeDevelopmentReceipt()
    {
        var root = CreateRoot();
        try
        {
            var layout = new ExecutionRuntimeStoreLayout(Path.Combine(root, "state"));
            var receipt = AsDevelopment(CreateReceipt());
            var cache = new ExecutionRuntimeLifecyclePolicyCache(layout);

            await cache.ObserveAsync([
                CreateObservation(receipt, SupportedLifecycle(), revision: 7),
            ]);

            var policy = Assert.Single(cache.CaptureSnapshot().Entries);
            Assert.True(policy.MatchesReceipt(receipt));
            Assert.False(policy.MatchesReceipt(CopyReceipt(
                receipt,
                receipt.ArtifactSha256,
                trustEnvironment: ExecutionRuntimeTrustEnvironment.Production)));
            Assert.False(policy.MatchesReceipt(CopyReceipt(
                receipt,
                receipt.ArtifactSha256,
                acquisitionSource: RuntimeExecutionRuntimeAcquisitionSources.Registry)));

            var reloaded = new ExecutionRuntimeLifecyclePolicyCache(layout);
            await reloaded.InitializeAsync();
            Assert.True(Assert.Single(reloaded.CaptureSnapshot().Entries).MatchesReceipt(receipt));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Observe_RejectsDevelopmentTrustAndSourceMismatchesBothWays()
    {
        var root = CreateRoot();
        try
        {
            var cache = new ExecutionRuntimeLifecyclePolicyCache(
                new ExecutionRuntimeStoreLayout(Path.Combine(root, "state")));
            var productionTrustWithDevelopmentSource = CopyReceipt(
                CreateReceipt(),
                new string('b', 64),
                acquisitionSource: RuntimeExecutionRuntimeAcquisitionSources.Development);
            var developmentTrustWithRegistrySource = CopyReceipt(
                CreateReceipt(),
                new string('b', 64),
                trustEnvironment: ExecutionRuntimeTrustEnvironment.Development);

            await Assert.ThrowsAsync<InvalidDataException>(() => cache.ObserveAsync([
                CreateObservation(productionTrustWithDevelopmentSource, SupportedLifecycle(), revision: 7),
            ]));
            await Assert.ThrowsAsync<InvalidDataException>(() => cache.ObserveAsync([
                CreateObservation(developmentTrustWithRegistrySource, SupportedLifecycle(), revision: 7),
            ]));
            Assert.Empty(cache.CaptureSnapshot().Entries);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task GenerationFence_RejectsPolicyChangedAfterLocalBindingResolution()
    {
        var root = CreateRoot();
        try
        {
            var cache = new ExecutionRuntimeLifecyclePolicyCache(
                new ExecutionRuntimeStoreLayout(Path.Combine(root, "state")));
            var receipt = CreateReceipt();
            await cache.ObserveAsync([
                CreateObservation(receipt, SupportedLifecycle(), revision: 7),
            ]);
            var expectedGeneration = cache.CaptureSnapshot().Generation;
            await cache.ObserveAsync([
                CreateObservation(receipt, DeprecatedLifecycle(), revision: 8),
            ]);
            var invoked = false;

            var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
                cache.ExecuteUnderGenerationFenceAsync(
                    expectedGeneration,
                    [receipt.Identity],
                    [receipt.Identity],
                    _ =>
                    {
                        invoked = true;
                        return Task.FromResult(true);
                    },
                    CancellationToken.None));

            Assert.False(invoked);
            Assert.StartsWith(ExecutionRuntimeErrorCodes.PolicyRequired, failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task GenerationFence_BlocksPolicyMutationThroughProtectedOperation()
    {
        var root = CreateRoot();
        try
        {
            var cache = new ExecutionRuntimeLifecyclePolicyCache(
                new ExecutionRuntimeStoreLayout(Path.Combine(root, "state")));
            var receipt = CreateReceipt();
            await cache.ObserveAsync([
                CreateObservation(receipt, SupportedLifecycle(), revision: 7),
            ]);
            var expectedGeneration = cache.CaptureSnapshot().Generation;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fenced = cache.ExecuteUnderGenerationFenceAsync(
                expectedGeneration,
                [receipt.Identity],
                [receipt.Identity],
                async cancellationToken =>
                {
                    entered.SetResult();
                    await release.Task.WaitAsync(cancellationToken);
                    return 42;
                },
                CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var mutation = cache.ObserveAsync([
                CreateObservation(receipt, DeprecatedLifecycle(), revision: 8),
            ]);
            var early = await Task.WhenAny(mutation, Task.Delay(TimeSpan.FromMilliseconds(100)));
            Assert.NotSame(mutation, early);

            release.SetResult();
            Assert.Equal(42, await fenced.WaitAsync(TimeSpan.FromSeconds(10)));
            await mutation.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(expectedGeneration + 1, cache.CaptureSnapshot().Generation);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task GenerationFence_RejectsLifecycleDeadlineThatExpiredWithoutPolicyMutation()
    {
        var root = CreateRoot();
        try
        {
            var time = new MutableTimeProvider(ObservedAt);
            var cache = new ExecutionRuntimeLifecyclePolicyCache(
                new ExecutionRuntimeStoreLayout(Path.Combine(root, "state")),
                time);
            var receipt = CreateReceipt();
            await cache.ObserveAsync([
                CreateObservation(
                    receipt,
                    DeprecatedLifecycle() with { InstallBlockedAtUtc = ObservedAt.AddMinutes(1) },
                    revision: 7),
            ]);
            var generation = cache.CaptureSnapshot().Generation;
            time.Advance(TimeSpan.FromMinutes(2));
            var invoked = false;

            var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
                cache.ExecuteUnderGenerationFenceAsync(
                    generation,
                    [receipt.Identity],
                    [receipt.Identity],
                    _ =>
                    {
                        invoked = true;
                        return Task.FromResult(true);
                    },
                    CancellationToken.None));

            Assert.False(invoked);
            Assert.StartsWith(ExecutionRuntimeErrorCodes.PolicyRequired, failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task GenerationFence_AppliesInstallationPolicyOnlyToNewBindings()
    {
        var root = CreateRoot();
        try
        {
            var cache = new ExecutionRuntimeLifecyclePolicyCache(
                new ExecutionRuntimeStoreLayout(Path.Combine(root, "state")));
            var receipt = CreateReceipt();
            await cache.ObserveAsync([
                CreateObservation(
                    receipt,
                    Lifecycle(ExecutionRuntimeLifecycleStates.Retired),
                    revision: 7),
            ]);
            var generation = cache.CaptureSnapshot().Generation;

            var activation = await cache.ExecuteUnderGenerationFenceAsync(
                generation,
                [],
                [receipt.Identity],
                static _ => Task.FromResult(42),
                CancellationToken.None);
            var installation = await Assert.ThrowsAsync<InvalidDataException>(() =>
                cache.ExecuteUnderGenerationFenceAsync(
                    generation,
                    [receipt.Identity],
                    [],
                    static _ => Task.FromResult(42),
                    CancellationToken.None));

            Assert.Equal(42, activation);
            Assert.StartsWith(ExecutionRuntimeErrorCodes.PolicyRequired, installation.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Observe_RejectsLifecycleDeadlineBroadening()
    {
        var root = CreateRoot();
        try
        {
            var cache = new ExecutionRuntimeLifecyclePolicyCache(
                new ExecutionRuntimeStoreLayout(Path.Combine(root, "state")));
            var receipt = CreateReceipt();
            var originalDeadline = ObservedAt.AddHours(1);
            await cache.ObserveAsync([
                CreateObservation(
                    receipt,
                    DeprecatedLifecycle() with
                    {
                        InstallBlockedAtUtc = originalDeadline,
                        ActivationBlockedAtUtc = originalDeadline,
                    },
                    revision: 7),
            ]);

            var failure = await Assert.ThrowsAsync<InvalidDataException>(() => cache.ObserveAsync([
                CreateObservation(
                    receipt,
                    Lifecycle(ExecutionRuntimeLifecycleStates.Retired) with
                    {
                        InstallBlockedAtUtc = originalDeadline.AddHours(1),
                        ActivationBlockedAtUtc = originalDeadline.AddHours(1),
                    },
                    revision: 8),
            ]));

            Assert.Contains("broadened", failure.Message, StringComparison.OrdinalIgnoreCase);
            var policy = Assert.Single(cache.CaptureSnapshot().Entries);
            Assert.Equal(7, policy.RuntimePolicyRevision);
            Assert.Equal(originalDeadline, policy.Lifecycle.ActivationBlockedAtUtc);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task PolicyMinimumTrustGenerationRejectsDowngradedReceiptEvidence()
    {
        var root = CreateRoot();
        try
        {
            var cache = new ExecutionRuntimeLifecyclePolicyCache(
                new ExecutionRuntimeStoreLayout(Path.Combine(root, "state")));
            var receipt = CreateReceipt();
            await cache.ObserveAsync([
                CreateObservation(
                    receipt,
                    SupportedLifecycle(),
                    revision: 7,
                    minimumHostTrustGeneration: receipt.TrustGeneration),
            ]);
            var policy = Assert.Single(cache.CaptureSnapshot().Entries);
            var downgraded = CopyReceipt(
                receipt,
                receipt.ArtifactSha256,
                trustGeneration: receipt.TrustGeneration - 1);

            Assert.True(policy.MatchesReceipt(receipt));
            Assert.False(policy.MatchesReceipt(downgraded));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Observe_RejectsRollbackBackwardTransitionAndChangedImmutableEvidence()
    {
        var root = CreateRoot();
        try
        {
            var cache = new ExecutionRuntimeLifecyclePolicyCache(
                new ExecutionRuntimeStoreLayout(Path.Combine(root, "state")));
            var receipt = CreateReceipt();
            await cache.ObserveAsync([
                CreateObservation(receipt, SupportedLifecycle(), revision: 7),
            ]);
            await cache.ObserveAsync([
                CreateObservation(receipt, DeprecatedLifecycle(), revision: 8),
            ]);

            await Assert.ThrowsAsync<InvalidDataException>(() => cache.ObserveAsync([
                CreateObservation(receipt, SupportedLifecycle(), revision: 7),
            ]));
            await Assert.ThrowsAsync<InvalidDataException>(() => cache.ObserveAsync([
                CreateObservation(receipt, SupportedLifecycle(), revision: 9),
            ]));
            await Assert.ThrowsAsync<InvalidDataException>(() => cache.ObserveAsync([
                CreateObservation(
                    CopyReceipt(receipt, artifactSha256: new string('f', 64)),
                    DeprecatedLifecycle(),
                    revision: 9),
            ]));

            var snapshot = cache.CaptureSnapshot();
            var policy = Assert.Single(snapshot.Entries);
            Assert.Equal(2, snapshot.Generation);
            Assert.Equal(8, policy.RuntimePolicyRevision);
            Assert.Equal(ExecutionRuntimeLifecycleStates.Deprecated, policy.Lifecycle.State);
            Assert.True(snapshot.AuthorizesCollection(receipt));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Observe_RequiresExactReceiptRegistryOrigin()
    {
        var root = CreateRoot();
        try
        {
            var cache = new ExecutionRuntimeLifecyclePolicyCache(
                new ExecutionRuntimeStoreLayout(Path.Combine(root, "state")));
            var receipt = CreateReceipt();

            await Assert.ThrowsAsync<InvalidDataException>(() => cache.ObserveAsync([
                CreateObservation(
                    receipt,
                    DeprecatedLifecycle(),
                    revision: 7,
                    registryOrigin: "https://other.example/"),
            ]));
            Assert.Empty(cache.CaptureSnapshot().Entries);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Observe_AcceptsHigherRevisionLifecycleDeadlineUpdate()
    {
        var root = CreateRoot();
        try
        {
            var cache = new ExecutionRuntimeLifecyclePolicyCache(
                new ExecutionRuntimeStoreLayout(Path.Combine(root, "state")));
            var receipt = CreateReceipt();
            var firstDeadline = ObservedAt.AddDays(3);
            await cache.ObserveAsync([
                CreateObservation(
                    receipt,
                    DeprecatedLifecycle() with { ActivationBlockedAtUtc = firstDeadline },
                    revision: 7),
            ]);
            var revisedDeadline = ObservedAt.AddDays(2);

            await cache.ObserveAsync([
                CreateObservation(
                    receipt,
                    DeprecatedLifecycle() with
                    {
                        State = ExecutionRuntimeLifecycleStates.Retired,
                        InstallBlockedAtUtc = ObservedAt.AddDays(1),
                        ActivationBlockedAtUtc = revisedDeadline,
                    },
                    revision: 8),
            ]);

            var policy = Assert.Single(cache.CaptureSnapshot().Entries);
            Assert.Equal(ExecutionRuntimeLifecycleStates.Retired, policy.Lifecycle.State);
            Assert.Equal(revisedDeadline, policy.Lifecycle.ActivationBlockedAtUtc);
            Assert.Equal(8, policy.RuntimePolicyRevision);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Observe_PersistsDeniedTrustAndRejectsLaterBroadening()
    {
        var root = CreateRoot();
        try
        {
            var layout = new ExecutionRuntimeStoreLayout(Path.Combine(root, "state"));
            var receipt = CreateReceipt();
            var cache = new ExecutionRuntimeLifecyclePolicyCache(layout);
            await cache.ObserveAsync([
                CreateObservation(receipt, SupportedLifecycle(), revision: 7),
            ]);

            await cache.ObserveAsync([
                CreateObservation(
                    receipt,
                    SupportedLifecycle(),
                    revision: 8,
                    trustDisposition: ExecutionRuntimeTrustDispositions.Denied),
            ]);

            var denied = Assert.Single(cache.CaptureSnapshot().Entries);
            Assert.Equal(ExecutionRuntimeTrustDispositions.Denied, denied.TrustDisposition);
            Assert.False(denied.PermitsInstallation(ObservedAt));
            Assert.False(denied.PermitsActivation(ObservedAt));
            Assert.True(denied.AuthorizesCollection);

            var failure = await Assert.ThrowsAsync<InvalidDataException>(() => cache.ObserveAsync([
                CreateObservation(receipt, SupportedLifecycle(), revision: 9),
            ]));
            Assert.Contains("broadened", failure.Message, StringComparison.OrdinalIgnoreCase);

            var reloaded = new ExecutionRuntimeLifecyclePolicyCache(layout);
            await reloaded.InitializeAsync();
            var persisted = Assert.Single(reloaded.CaptureSnapshot().Entries);
            Assert.Equal(8, persisted.RuntimePolicyRevision);
            Assert.Equal(ExecutionRuntimeTrustDispositions.Denied, persisted.TrustDisposition);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Observe_RejectsTrustConflictAtSameRegistryRevision()
    {
        var root = CreateRoot();
        try
        {
            var cache = new ExecutionRuntimeLifecyclePolicyCache(
                new ExecutionRuntimeStoreLayout(Path.Combine(root, "state")));
            var receipt = CreateReceipt();
            await cache.ObserveAsync([
                CreateObservation(receipt, SupportedLifecycle(), revision: 7),
            ]);

            var failure = await Assert.ThrowsAsync<InvalidDataException>(() => cache.ObserveAsync([
                CreateObservation(
                    receipt,
                    SupportedLifecycle(),
                    revision: 7,
                    trustDisposition: ExecutionRuntimeTrustDispositions.Denied),
            ]));

            Assert.Contains("same revision", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                ExecutionRuntimeTrustDispositions.Trusted,
                Assert.Single(cache.CaptureSnapshot().Entries).TrustDisposition);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Observe_RejectsNewIdentityBelowRegistryRevisionWatermark()
    {
        var root = CreateRoot();
        try
        {
            var cache = new ExecutionRuntimeLifecyclePolicyCache(
                new ExecutionRuntimeStoreLayout(Path.Combine(root, "state")));
            var receipt = CreateReceipt();
            await cache.ObserveAsync([
                CreateObservation(receipt, SupportedLifecycle(), revision: 10),
            ]);
            var otherReceipt = CopyReceipt(
                receipt,
                receipt.ArtifactSha256,
                receipt.Identity with { Version = "10.0.2" });

            await Assert.ThrowsAsync<InvalidDataException>(() => cache.ObserveAsync([
                CreateObservation(otherReceipt, SupportedLifecycle(), revision: 9),
            ]));

            Assert.Single(cache.CaptureSnapshot().Entries);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Observe_RejectsMixedRegistryRevisionSnapshotRegardlessOfOrder(bool reverse)
    {
        var root = CreateRoot();
        try
        {
            var cache = new ExecutionRuntimeLifecyclePolicyCache(
                new ExecutionRuntimeStoreLayout(Path.Combine(root, "state")));
            var first = CreateReceipt();
            var second = CopyReceipt(
                first,
                first.ArtifactSha256,
                first.Identity with { Version = "10.0.2" });
            var observations = new[]
            {
                CreateObservation(first, SupportedLifecycle(), revision: 10),
                CreateObservation(second, SupportedLifecycle(), revision: 11),
            };
            if (reverse)
            {
                Array.Reverse(observations);
            }

            await Assert.ThrowsAsync<InvalidDataException>(() => cache.ObserveAsync(observations));

            Assert.Empty(cache.CaptureSnapshot().Entries);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Initialize_RejectsDuplicateJsonPropertiesAndPreservesEvidence()
    {
        var root = CreateRoot();
        try
        {
            var layout = new ExecutionRuntimeStoreLayout(Path.Combine(root, "state"));
            Directory.CreateDirectory(layout.PolicyRootPath);
            var malformed = "{\"schemaVersion\":1,\"schemaVersion\":1,\"generation\":0,\"policies\":[]}";
            await File.WriteAllTextAsync(layout.LifecyclePolicyPath, malformed);

            var cache = new ExecutionRuntimeLifecyclePolicyCache(layout);
            await Assert.ThrowsAsync<InvalidDataException>(() => cache.InitializeAsync());

            Assert.Equal(malformed, await File.ReadAllTextAsync(layout.LifecyclePolicyPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Initialize_RejectsPersistedPolicyWithoutTrustDispositionAndPreservesEvidence()
    {
        var root = CreateRoot();
        try
        {
            var layout = new ExecutionRuntimeStoreLayout(Path.Combine(root, "state"));
            var receipt = CreateReceipt();
            var cache = new ExecutionRuntimeLifecyclePolicyCache(layout);
            await cache.ObserveAsync([
                CreateObservation(receipt, SupportedLifecycle(), revision: 7),
            ]);
            var document = JsonNode.Parse(await File.ReadAllTextAsync(layout.LifecyclePolicyPath))!
                .AsObject();
            Assert.True(document["policies"]!.AsArray()[0]!.AsObject().Remove("trustDisposition"));
            var persistedWithoutTrustDisposition = document.ToJsonString(ExecutionRuntimeStore.JsonOptions);
            await File.WriteAllTextAsync(
                layout.LifecyclePolicyPath,
                persistedWithoutTrustDisposition);

            var reloaded = new ExecutionRuntimeLifecyclePolicyCache(layout);
            var failure = await Assert.ThrowsAsync<InvalidDataException>(() => reloaded.InitializeAsync());

            Assert.Contains("invalid", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                persistedWithoutTrustDisposition,
                await File.ReadAllTextAsync(layout.LifecyclePolicyPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Initialize_AcceptsPolicyDocumentAtExactMetadataByteLimit()
    {
        var root = CreateRoot();
        try
        {
            var layout = new ExecutionRuntimeStoreLayout(Path.Combine(root, "state"));
            Directory.CreateDirectory(layout.PolicyRootPath);
            const string document = "{\"schemaVersion\":1,\"generation\":0,\"policies\":[]}";
            var documentBytes = System.Text.Encoding.UTF8.GetBytes(document);
            var bytes = new byte[SunderExecutionRuntimeFormat.MaxMetadataBytes];
            Array.Fill(bytes, (byte)' ');
            documentBytes.CopyTo(bytes, 0);
            await File.WriteAllBytesAsync(layout.LifecyclePolicyPath, bytes);

            var cache = new ExecutionRuntimeLifecyclePolicyCache(layout);
            await cache.InitializeAsync();

            Assert.Equal(0, cache.CaptureSnapshot().Generation);
            Assert.Empty(cache.CaptureSnapshot().Entries);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Initialize_RejectsPolicyDocumentOneByteOverMetadataLimit()
    {
        var root = CreateRoot();
        try
        {
            var layout = new ExecutionRuntimeStoreLayout(Path.Combine(root, "state"));
            Directory.CreateDirectory(layout.PolicyRootPath);
            const string document = "{\"schemaVersion\":1,\"generation\":0,\"policies\":[]}";
            var documentBytes = System.Text.Encoding.UTF8.GetBytes(document);
            var bytes = new byte[SunderExecutionRuntimeFormat.MaxMetadataBytes + 1];
            Array.Fill(bytes, (byte)' ');
            documentBytes.CopyTo(bytes, 0);
            await File.WriteAllBytesAsync(layout.LifecyclePolicyPath, bytes);

            var cache = new ExecutionRuntimeLifecyclePolicyCache(layout);
            await Assert.ThrowsAsync<InvalidDataException>(() => cache.InitializeAsync());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData(ExecutionRuntimeLifecycleStates.Supported, false)]
    [InlineData(ExecutionRuntimeLifecycleStates.Deprecated, true)]
    [InlineData(ExecutionRuntimeLifecycleStates.Retired, true)]
    [InlineData(ExecutionRuntimeLifecycleStates.Revoked, true)]
    public async Task Snapshot_AuthorizesOnlyNonSupportedLifecycle(string state, bool expected)
    {
        var root = CreateRoot();
        try
        {
            var receipt = CreateReceipt();
            var cache = new ExecutionRuntimeLifecyclePolicyCache(
                new ExecutionRuntimeStoreLayout(Path.Combine(root, "state")));
            await cache.ObserveAsync([
                CreateObservation(receipt, Lifecycle(state), revision: 7),
            ]);

            Assert.Equal(expected, cache.CaptureSnapshot().AuthorizesCollection(receipt));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Refresh_ObservesOnlyAnExactRegistryRelease()
    {
        var root = CreateRoot();
        try
        {
            var layout = new ExecutionRuntimeStoreLayout(Path.Combine(root, "state"));
            var receipt = CreateReceipt();
            var cache = new ExecutionRuntimeLifecyclePolicyCache(layout);
            var store = CreateStore(layout, new TestExecutionRuntimeGarbageCollectionAuthority());
            await WriteReceiptAsync(store, layout, receipt);
            using var factory = new StubHttpClientFactory(new StubHttpHandler(request =>
                JsonResponse(request, CreateReleaseDetails(receipt, DeprecatedLifecycle()))));
            var refresher = new ExecutionRuntimeLifecyclePolicyRefresher(
                CreateRegistryClient(factory),
                store,
                cache,
                TimeProvider.System);

            var result = await refresher.RefreshAsync(CancellationToken.None);

            Assert.Equal(1, result.ReceiptsObserved);
            Assert.Equal(0, result.ReleasesUnavailable);
            Assert.Empty(result.NewlyActivationBlocked);
            Assert.True(cache.CaptureSnapshot().AuthorizesCollection(receipt));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Refresh_ObservesAuthoritativeDevelopmentReceipt()
    {
        var root = CreateRoot();
        try
        {
            var layout = new ExecutionRuntimeStoreLayout(Path.Combine(root, "state"));
            var receipt = AsDevelopment(CreateReceipt());
            var cache = new ExecutionRuntimeLifecyclePolicyCache(layout);
            var store = CreateStore(layout, new TestExecutionRuntimeGarbageCollectionAuthority());
            await WriteReceiptAsync(store, layout, receipt);
            using var factory = new StubHttpClientFactory(new StubHttpHandler(request =>
                JsonResponse(request, CreateReleaseDetails(receipt, DeprecatedLifecycle()))));
            var refresher = new ExecutionRuntimeLifecyclePolicyRefresher(
                CreateRegistryClient(factory),
                store,
                cache,
                TimeProvider.System);

            var result = await refresher.RefreshAsync(CancellationToken.None);

            Assert.Equal(1, result.ReceiptsObserved);
            var policy = Assert.Single(cache.CaptureSnapshot().Entries);
            Assert.True(policy.MatchesReceipt(receipt));
            Assert.Equal(ExecutionRuntimeLifecycleStates.Deprecated, policy.Lifecycle.State);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Refresh_DoesNotContactRegistryForUnassociatedLocalImport()
    {
        var root = CreateRoot();
        try
        {
            var layout = new ExecutionRuntimeStoreLayout(Path.Combine(root, "state"));
            var receipt = AsLocalImport(CreateReceipt());
            var cache = new ExecutionRuntimeLifecyclePolicyCache(layout);
            var store = CreateStore(layout, new TestExecutionRuntimeGarbageCollectionAuthority());
            await WriteReceiptAsync(store, layout, receipt);
            var requests = 0;
            using var factory = new StubHttpClientFactory(new StubHttpHandler(request =>
            {
                requests++;
                return JsonResponse(request, CreateReleaseDetails(receipt, DeprecatedLifecycle()));
            }));
            var refresher = new ExecutionRuntimeLifecyclePolicyRefresher(
                CreateRegistryClient(factory),
                store,
                cache,
                TimeProvider.System);

            var result = await refresher.RefreshAsync(CancellationToken.None);

            Assert.Equal(0, result.ReceiptsObserved);
            Assert.Equal(0, requests);
            Assert.Empty(cache.CaptureSnapshot().Entries);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Refresh_UsesAssociatedPolicyOriginForExactLocalImport()
    {
        var root = CreateRoot();
        try
        {
            var layout = new ExecutionRuntimeStoreLayout(Path.Combine(root, "state"));
            var receipt = AsLocalImport(CreateReceipt());
            var cache = new ExecutionRuntimeLifecyclePolicyCache(layout);
            await cache.ObserveAsync([
                CreateObservation(receipt, SupportedLifecycle(), revision: 6),
            ]);
            var store = CreateStore(layout, new TestExecutionRuntimeGarbageCollectionAuthority());
            await WriteReceiptAsync(store, layout, receipt);
            var requests = 0;
            using var factory = new StubHttpClientFactory(new StubHttpHandler(request =>
            {
                requests++;
                return JsonResponse(
                    request,
                    CreateReleaseDetails(receipt, Lifecycle(ExecutionRuntimeLifecycleStates.Revoked)));
            }));
            var refresher = new ExecutionRuntimeLifecyclePolicyRefresher(
                CreateRegistryClient(factory),
                store,
                cache,
                TimeProvider.System);

            var result = await refresher.RefreshAsync(CancellationToken.None);

            Assert.Equal(1, requests);
            Assert.Equal(1, result.ReceiptsObserved);
            Assert.Equal(receipt.Identity, Assert.Single(result.NewlyActivationBlocked));
            Assert.Equal(
                ExecutionRuntimeLifecycleStates.Revoked,
                Assert.Single(cache.CaptureSnapshot().Entries).Lifecycle.State);
            var persisted = await store.GetReceiptAsync(receipt.Identity);
            Assert.Equal(RuntimeExecutionRuntimeAcquisitionSources.LocalImport, persisted?.AcquisitionSource);
            Assert.Null(persisted?.RegistryOrigin);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Refresh_RejectsRegistryEvidenceThatDoesNotMatchReceipt()
    {
        var root = CreateRoot();
        try
        {
            var layout = new ExecutionRuntimeStoreLayout(Path.Combine(root, "state"));
            var receipt = CreateReceipt();
            var cache = new ExecutionRuntimeLifecyclePolicyCache(layout);
            var store = CreateStore(layout, new TestExecutionRuntimeGarbageCollectionAuthority());
            await WriteReceiptAsync(store, layout, receipt);
            var mismatched = CopyReceipt(receipt, artifactSha256: new string('f', 64));
            using var factory = new StubHttpClientFactory(new StubHttpHandler(request =>
                JsonResponse(request, CreateReleaseDetails(mismatched, DeprecatedLifecycle()))));
            var refresher = new ExecutionRuntimeLifecyclePolicyRefresher(
                CreateRegistryClient(factory),
                store,
                cache,
                TimeProvider.System);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => refresher.RefreshAsync(CancellationToken.None));

            Assert.Empty(cache.CaptureSnapshot().Entries);
            Assert.True(File.Exists(layout.GetReceiptPath(receipt.Identity)));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Refresh_ReportsNewlyActivationBlockedExactRuntime()
    {
        var root = CreateRoot();
        try
        {
            var layout = new ExecutionRuntimeStoreLayout(Path.Combine(root, "state"));
            var receipt = CreateReceipt();
            var cache = new ExecutionRuntimeLifecyclePolicyCache(layout);
            await cache.ObserveAsync([
                CreateObservation(receipt, SupportedLifecycle(), revision: 6),
            ]);
            var store = CreateStore(layout, new TestExecutionRuntimeGarbageCollectionAuthority());
            await WriteReceiptAsync(store, layout, receipt);
            using var factory = new StubHttpClientFactory(new StubHttpHandler(request =>
                JsonResponse(request, CreateReleaseDetails(receipt, Lifecycle(ExecutionRuntimeLifecycleStates.Revoked)))));
            var refresher = new ExecutionRuntimeLifecyclePolicyRefresher(
                CreateRegistryClient(factory),
                store,
                cache,
                TimeProvider.System);

            var result = await refresher.RefreshAsync(CancellationToken.None);

            Assert.Equal(receipt.Identity, Assert.Single(result.NewlyActivationBlocked));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task StartupMaintenance_UsesGraceIntervalBeforeCollectingUnreferencedRuntime()
    {
        var root = CreateRoot();
        try
        {
            var layout = new ExecutionRuntimeStoreLayout(Path.Combine(root, "state"));
            var receipt = CreateReceipt();
            var cache = new ExecutionRuntimeLifecyclePolicyCache(layout);
            await cache.ObserveAsync([
                CreateObservation(receipt, DeprecatedLifecycle(), revision: 7),
            ]);
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            var store = CreateStore(layout, new CacheGarbageCollectionAuthority(cache), clock);
            await WriteReceiptAsync(store, layout, receipt);
            Directory.CreateDirectory(layout.GetContentRootPath(receipt.ContentIdentity));
            Directory.CreateDirectory(Path.GetDirectoryName(layout.GetArchivePath(receipt.ArtifactSha256))!);
            Directory.CreateDirectory(Path.GetDirectoryName(layout.GetManifestPath(receipt.ManifestSha256))!);
            await File.WriteAllTextAsync(layout.GetArchivePath(receipt.ArtifactSha256), "archive");
            await File.WriteAllTextAsync(layout.GetManifestPath(receipt.ManifestSha256), "manifest");
            File.SetLastWriteTimeUtc(
                layout.GetArchivePath(receipt.ArtifactSha256),
                clock.GetUtcNow().UtcDateTime);
            File.SetLastWriteTimeUtc(
                layout.GetManifestPath(receipt.ManifestSha256),
                clock.GetUtcNow().UtcDateTime);
            var maintenance = new ExecutionRuntimeStartupMaintenance(
                store,
                NullLogger<ExecutionRuntimeStartupMaintenance>.Instance);

            await maintenance.RunOnceAsync(CancellationToken.None);

            Assert.True(Directory.Exists(layout.GetContentRootPath(receipt.ContentIdentity)));
            Assert.True(File.Exists(layout.GetReceiptPath(receipt.Identity)));
            Assert.True(File.Exists(layout.GetArchivePath(receipt.ArtifactSha256)));
            Assert.True(File.Exists(layout.GetManifestPath(receipt.ManifestSha256)));

            clock.Advance(ExecutionRuntimeStore.GarbageCollectionGraceInterval - TimeSpan.FromSeconds(1));
            await maintenance.RunOnceAsync(CancellationToken.None);
            Assert.True(Directory.Exists(layout.GetContentRootPath(receipt.ContentIdentity)));

            clock.Advance(TimeSpan.FromSeconds(1));
            await maintenance.RunOnceAsync(CancellationToken.None);
            Assert.False(Directory.Exists(layout.GetContentRootPath(receipt.ContentIdentity)));
            Assert.False(File.Exists(layout.GetReceiptPath(receipt.Identity)));
            Assert.False(File.Exists(layout.GetArchivePath(receipt.ArtifactSha256)));
            Assert.False(File.Exists(layout.GetManifestPath(receipt.ManifestSha256)));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task StartupMaintenance_DoesNotRefreshLifecyclePolicy()
    {
        var root = CreateRoot();
        try
        {
            var layout = new ExecutionRuntimeStoreLayout(Path.Combine(root, "state"));
            var receipt = CreateReceipt();
            var cache = new ExecutionRuntimeLifecyclePolicyCache(layout);
            await cache.ObserveAsync([
                CreateObservation(receipt, SupportedLifecycle(), revision: 6),
            ]);
            var store = CreateStore(layout, new CacheGarbageCollectionAuthority(cache));
            await WriteReceiptAsync(store, layout, receipt);
            Directory.CreateDirectory(layout.GetContentRootPath(receipt.ContentIdentity));
            Directory.CreateDirectory(Path.GetDirectoryName(layout.GetArchivePath(receipt.ArtifactSha256))!);
            Directory.CreateDirectory(Path.GetDirectoryName(layout.GetManifestPath(receipt.ManifestSha256))!);
            await File.WriteAllTextAsync(layout.GetArchivePath(receipt.ArtifactSha256), "archive");
            await File.WriteAllTextAsync(layout.GetManifestPath(receipt.ManifestSha256), "manifest");
            var maintenance = new ExecutionRuntimeStartupMaintenance(
                store,
                NullLogger<ExecutionRuntimeStartupMaintenance>.Instance);

            await maintenance.RunOnceAsync(CancellationToken.None);

            var policy = Assert.Single(cache.CaptureSnapshot().Entries);
            Assert.Equal(6, policy.RuntimePolicyRevision);
            Assert.Equal(ExecutionRuntimeLifecycleStates.Supported, policy.Lifecycle.State);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static ExecutionRuntimeLifecyclePolicyObservation CreateObservation(
        ExecutionRuntimeReceiptV1 receipt,
        RegistryExecutionRuntimeLifecycle lifecycle,
        long revision,
        string registryOrigin = "https://registry.example/",
        long minimumHostTrustGeneration = 1,
        string trustDisposition = ExecutionRuntimeTrustDispositions.Trusted)
        => new(
            receipt,
            registryOrigin,
            lifecycle,
            revision,
            minimumHostTrustGeneration,
            ObservedAt,
            trustDisposition);

    private static RegistryExecutionRuntimeLifecycle Lifecycle(string state)
        => state switch
        {
            ExecutionRuntimeLifecycleStates.Supported => SupportedLifecycle(),
            ExecutionRuntimeLifecycleStates.Deprecated => DeprecatedLifecycle(),
            ExecutionRuntimeLifecycleStates.Retired => DeprecatedLifecycle() with
            {
                State = state,
                InstallBlockedAtUtc = ObservedAt.AddHours(1),
            },
            ExecutionRuntimeLifecycleStates.Revoked => DeprecatedLifecycle() with
            {
                State = state,
                InstallBlockedAtUtc = ObservedAt.AddHours(1),
                ActivationBlockedAtUtc = ObservedAt.AddHours(1),
                RevokedAtUtc = ObservedAt.AddHours(2),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

    private static RegistryExecutionRuntimeLifecycle SupportedLifecycle()
        => new(
            ExecutionRuntimeLifecycleStates.Supported,
            StagedAt,
            SupportedAt,
            DeprecatedAtUtc: null,
            InstallBlockedAtUtc: null,
            ActivationBlockedAtUtc: null,
            RevokedAtUtc: null,
            Message: null);

    private static RegistryExecutionRuntimeLifecycle DeprecatedLifecycle()
        => SupportedLifecycle() with
        {
            State = ExecutionRuntimeLifecycleStates.Deprecated,
            DeprecatedAtUtc = ObservedAt,
        };

    private static ExecutionRuntimeReceiptV1 CreateReceipt()
        => new(
            ExecutionRuntimeReceiptV1.CurrentSchemaVersion,
            new ExecutionRuntimeIdentity(
                SunderExecutionRuntimeFormat.DotNetRuntimeId,
                "10.0.1",
                7,
                "linux-x64",
                SunderExecutionRuntimeFormat.DotNetModuleAbi),
            SunderExecutionRuntimeFormat.WorkerProtocol,
            new string('b', 64),
            1024,
            new string('a', 64),
            new string('c', 64),
            "payload/bin/sunder-runtime",
            new string('e', 64),
            6,
            [
                SunderExecutionRuntimeFormat.DotNetExecArgument,
                SunderExecutionRuntimeFormat.PackageEntryPointPlaceholder,
            ],
            [
                new ExecutionRuntimeReceiptContentV1(
                    "payload/bin/sunder-runtime",
                    new string('e', 64),
                    6),
            ],
            new string('d', 64),
            "sunder.ci.test.v1",
            "sunder.release.test.v1",
            3,
            ExecutionRuntimeTrustEnvironment.Production,
            RuntimeExecutionRuntimeAcquisitionSources.Registry,
            "https://registry.example/",
            SupportedAt);

    private static ExecutionRuntimeReceiptV1 CopyReceipt(
        ExecutionRuntimeReceiptV1 receipt,
        string artifactSha256,
        ExecutionRuntimeIdentity? identity = null,
        long? trustGeneration = null,
        ExecutionRuntimeTrustEnvironment? trustEnvironment = null,
        string? acquisitionSource = null)
        => new(
            receipt.SchemaVersion,
            identity ?? receipt.Identity,
            receipt.WorkerProtocol,
            artifactSha256,
            receipt.ArtifactSize,
            receipt.ManifestSha256,
            receipt.ContentIdentity,
            receipt.LauncherPath,
            receipt.LauncherSha256,
            receipt.LauncherSize,
            receipt.LauncherArguments,
            receipt.Content,
            receipt.NativeAttestationSha256,
            receipt.NativeAttestationKeyId,
            receipt.SigningKeyId,
            trustGeneration ?? receipt.TrustGeneration,
            trustEnvironment ?? receipt.TrustEnvironment,
            acquisitionSource ?? receipt.AcquisitionSource,
            receipt.RegistryOrigin,
            receipt.InstalledAtUtc);

    private static ExecutionRuntimeReceiptV1 AsDevelopment(ExecutionRuntimeReceiptV1 receipt)
        => CopyReceipt(
            receipt,
            receipt.ArtifactSha256,
            trustEnvironment: ExecutionRuntimeTrustEnvironment.Development,
            acquisitionSource: RuntimeExecutionRuntimeAcquisitionSources.Development);

    private static ExecutionRuntimeReceiptV1 AsLocalImport(ExecutionRuntimeReceiptV1 receipt)
        => new(
            receipt.SchemaVersion,
            receipt.Identity,
            receipt.WorkerProtocol,
            receipt.ArtifactSha256,
            receipt.ArtifactSize,
            receipt.ManifestSha256,
            receipt.ContentIdentity,
            receipt.LauncherPath,
            receipt.LauncherSha256,
            receipt.LauncherSize,
            receipt.LauncherArguments,
            receipt.Content,
            receipt.NativeAttestationSha256,
            receipt.NativeAttestationKeyId,
            receipt.SigningKeyId,
            receipt.TrustGeneration,
            receipt.TrustEnvironment,
            RuntimeExecutionRuntimeAcquisitionSources.LocalImport,
            null,
            receipt.InstalledAtUtc);

    private static RegistryExecutionRuntimeReleaseDetails CreateReleaseDetails(
        ExecutionRuntimeReceiptV1 receipt,
        RegistryExecutionRuntimeLifecycle lifecycle)
    {
        var identity = new RegistryExecutionRuntimeIdentity(
            receipt.Identity.RuntimeId,
            receipt.Identity.Version,
            receipt.Identity.PackagingRevision,
            receipt.Identity.Rid,
            receipt.Identity.ModuleAbi);
        var artifact = new RegistryResolvedExecutionRuntimeArtifact(
            identity,
            receipt.ManifestSha256,
            receipt.ArtifactSha256,
            receipt.ArtifactSize,
            receipt.SigningKeyId,
            MinimumHostTrustGeneration: 1,
            receipt.NativeAttestationSha256,
            receipt.NativeAttestationKeyId,
            lifecycle,
            ObservedRuntimePolicyRevision: 7,
            "https://registry.example/artifacts/runtime.sunderruntime");
        return new RegistryExecutionRuntimeReleaseDetails(
            new RegistryExecutionRuntimeReleaseSummary(
                identity.RuntimeId,
                identity.Version,
                identity.PackagingRevision,
                identity.ModuleAbi,
                SunderExecutionRuntimeFormat.WorkerProtocol,
                MinimumHostTrustGeneration: 1,
                lifecycle,
                [identity.Rid],
                ObservedAt),
            [new RegistryExecutionRuntimeArtifactDetails(artifact, receipt.ContentIdentity, ObservedAt)]);
    }

    private static ExecutionRuntimeStore CreateStore(
        ExecutionRuntimeStoreLayout layout,
        IExecutionRuntimeGarbageCollectionAuthority authority,
        TimeProvider? timeProvider = null)
        => new(
            layout,
            new ExecutionRuntimeHostTrustService(
                "policy-test",
                generation: 3,
                developmentTrustEnabled: false,
                productionKeys: []),
            authority,
            timeProvider);

    private static async Task WriteReceiptAsync(
        ExecutionRuntimeStore store,
        ExecutionRuntimeStoreLayout layout,
        ExecutionRuntimeReceiptV1 receipt)
    {
        await store.InitializeAsync();
        var path = layout.GetReceiptPath(receipt.Identity);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await DurableJsonDocument.WriteAsync(path, receipt, ExecutionRuntimeStore.JsonOptions);
    }

    private static RegistryHttpClient CreateRegistryClient(IHttpClientFactory factory)
        => new(
            factory,
            new RuntimeTransportPolicyOptions { RegistryRequestTimeout = TimeSpan.FromSeconds(10) },
            TimeProvider.System);

    private static HttpResponseMessage JsonResponse(
        HttpRequestMessage request,
        RegistryExecutionRuntimeReleaseDetails details)
        => new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = JsonContent.Create(details),
        };

    private static string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            nameof(ExecutionRuntimeLifecyclePolicyCacheTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class StubHttpHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler)
        : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(handler, disposeHandler: true);

        public HttpClient CreateClient(string name) => _client;

        public void Dispose() => _client.Dispose();
    }

    private sealed class CacheGarbageCollectionAuthority(
        ExecutionRuntimeLifecyclePolicyCache cache) : IExecutionRuntimeGarbageCollectionAuthority
    {
        public async Task<TResult> ExecuteAsync<TResult>(
            Func<ExecutionRuntimeGarbageCollectionAuthoritySnapshot, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
        {
            await cache.InitializeAsync(cancellationToken);
            return await operation(
                new ExecutionRuntimeGarbageCollectionAuthoritySnapshot([], cache.CaptureSnapshot()),
                cancellationToken);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}
