using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Format;
using Sunder.Package.Hosting;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Stacks;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeStackImportPlanTests
{
    [Fact]
    public async Task ImportAsync_WhenRuntimeGenerationChanged_RejectsStalePlanWithoutCallingContributor()
    {
        await using var fixture = await StackImportFixture.CreateAsync(new TestContributor("one"));
        var preview = await fixture.PreviewAsync(["one-fragment"]);

        await fixture.PublishSessionAsync(new TestContributor("one"));
        var result = await fixture.Service.ImportAsync(new RuntimeStackImportRequest(preview.PlanId!, ["one-fragment"], ActionIds(preview)));

        Assert.Equal(RuntimeStackImportOutcome.Failed, result.Outcome);
        Assert.Contains(result.Errors, error => error.Contains("generation", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, fixture.Contributors[0].ImportCount);
        Assert.Empty(Directory.EnumerateFiles(fixture.Paths.TransferRootPath));
    }

    [Fact]
    public async Task ImportAsync_WhenPlanExpired_RejectsPlanAndReleasesUpload()
    {
        await using var fixture = await StackImportFixture.CreateAsync(new TestContributor("one"));
        var preview = await fixture.PreviewAsync(["one-fragment"]);

        fixture.Clock.Advance(new RuntimeStackPolicyOptions().ImportPlanLifetime);
        var result = await fixture.Service.ImportAsync(new RuntimeStackImportRequest(preview.PlanId!, ["one-fragment"], ActionIds(preview)));

        Assert.Equal(RuntimeStackImportOutcome.Failed, result.Outcome);
        Assert.Contains(result.Errors, error => error.Contains("expired", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, fixture.Contributors[0].ImportCount);
        Assert.Empty(Directory.EnumerateFiles(fixture.Paths.TransferRootPath));
    }

    [Fact]
    public async Task SweepExpired_WhenPlanExpired_ReleasesUploadAndInvalidatesPlan()
    {
        await using var fixture = await StackImportFixture.CreateAsync(new TestContributor("one"));
        var preview = await fixture.PreviewAsync(["one-fragment"]);

        fixture.Clock.Advance(new RuntimeStackPolicyOptions().ImportPlanLifetime);
        fixture.Service.SweepExpired();
        var result = await fixture.Service.ImportAsync(new RuntimeStackImportRequest(preview.PlanId!, ["one-fragment"], ActionIds(preview)));

        Assert.Equal(RuntimeStackImportOutcome.Failed, result.Outcome);
        Assert.Contains(result.Errors, error => error.Contains("not found", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Directory.EnumerateFiles(fixture.Paths.TransferRootPath));
    }

    [Fact]
    public async Task DiscardPlan_ReleasesUploadAndInvalidatesPlan()
    {
        await using var fixture = await StackImportFixture.CreateAsync(new TestContributor("one"));
        var preview = await fixture.PreviewAsync(["one-fragment"]);

        Assert.True(fixture.Service.DiscardPlan(preview.PlanId!));
        var result = await fixture.Service.ImportAsync(new RuntimeStackImportRequest(preview.PlanId!, ["one-fragment"], ["one-action"]));

        Assert.Equal(RuntimeStackImportOutcome.Failed, result.Outcome);
        Assert.Empty(Directory.EnumerateFiles(fixture.Paths.TransferRootPath));
    }

    [Fact]
    public async Task ImportAsync_WhenPlanConsumedTwice_OnlyFirstCallInvokesContributor()
    {
        await using var fixture = await StackImportFixture.CreateAsync(new TestContributor("one"));
        var preview = await fixture.PreviewAsync(["one-fragment"]);
        var request = new RuntimeStackImportRequest(preview.PlanId!, ["one-fragment"], ActionIds(preview));

        var first = await fixture.Service.ImportAsync(request);
        var duplicate = await fixture.Service.ImportAsync(request);

        Assert.Equal(RuntimeStackImportOutcome.Completed, first.Outcome);
        Assert.Equal(RuntimeStackImportOutcome.Failed, duplicate.Outcome);
        Assert.Contains(duplicate.Errors, error => error.Contains("already consumed", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, fixture.Contributors[0].ImportCount);
    }

    [Fact]
    public async Task ImportAsync_WhenContributorCommitsItems_InvokesRuntimeAppliedHandler()
    {
        await using var fixture = await StackImportFixture.CreateAsync(new TestContributor("one"));
        var preview = await fixture.PreviewAsync(["one-fragment"]);

        var result = await fixture.Service.ImportAsync(new RuntimeStackImportRequest(
            preview.PlanId!,
            ["one-fragment"],
            ActionIds(preview)));

        Assert.Equal(RuntimeStackImportOutcome.Completed, result.Outcome);
        var contributor = fixture.Contributors[0];
        Assert.Equal(1, contributor.AppliedCount);
        var context = Assert.IsType<StackImportAppliedContext>(contributor.LastAppliedContext);
        Assert.Equal(contributor.PackageId, context.OwnerPackageId);
        Assert.Equal(contributor.ContributorId, context.ContributorId);
        Assert.Equal(["one-fragment"], context.FragmentIds);
        Assert.Equal(["one-item"], context.ImportedItems.Select(item => item.ItemId));
    }

    [Fact]
    public async Task ImportAsync_WhenAppliedHandlerCancelsIndependently_KeepsCommittedImportAndWarns()
    {
        var contributor = new TestContributor(
            "one",
            appliedAction: static _ => Task.FromException(new OperationCanceledException("Refresh cancelled.")));
        await using var fixture = await StackImportFixture.CreateAsync(contributor);
        var preview = await fixture.PreviewAsync(["one-fragment"]);

        var result = await fixture.Service.ImportAsync(new RuntimeStackImportRequest(
            preview.PlanId!,
            ["one-fragment"],
            ActionIds(preview)));

        Assert.Equal(RuntimeStackImportOutcome.Completed, result.Outcome);
        Assert.Single(result.ImportedItems);
        Assert.Empty(result.Errors);
        Assert.Contains(result.Warnings, warning => warning.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
        var contributorResult = Assert.Single(result.ContributorResults);
        Assert.Contains(contributorResult.Warnings, warning => warning.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImportAsync_WhenHostCancelsAppliedHandler_PropagatesCancellation()
    {
        var appliedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contributor = new TestContributor(
            "one",
            appliedAction: async cancellationToken =>
            {
                appliedEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        await using var fixture = await StackImportFixture.CreateAsync(contributor);
        var preview = await fixture.PreviewAsync(["one-fragment"]);
        using var cancellation = new CancellationTokenSource();

        var import = fixture.Service.ImportAsync(
            new RuntimeStackImportRequest(preview.PlanId!, ["one-fragment"], ActionIds(preview)),
            cancellation.Token);
        await appliedEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import);
    }

    [Fact]
    public async Task PreviewAsync_WhenFragmentIdUnknown_RejectsSelectionAndReleasesUpload()
    {
        await using var fixture = await StackImportFixture.CreateAsync(new TestContributor("one"));

        var preview = await fixture.PreviewAsync(["unknown-fragment"]);

        Assert.False(preview.Success);
        Assert.Null(preview.PlanId);
        Assert.Contains(preview.Errors, error => error.Contains("unknown fragment", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Directory.EnumerateFiles(fixture.Paths.TransferRootPath));
    }

    [Fact]
    public async Task ImportAsync_WhenActionIdUnknown_RejectsAndConsumesPlan()
    {
        await using var fixture = await StackImportFixture.CreateAsync(new TestContributor("one"));
        var preview = await fixture.PreviewAsync(["one-fragment"]);

        var result = await fixture.Service.ImportAsync(new RuntimeStackImportRequest(preview.PlanId!, ["one-fragment"], ["unknown-action"]));
        var duplicate = await fixture.Service.ImportAsync(new RuntimeStackImportRequest(preview.PlanId!, ["one-fragment"], ActionIds(preview)));

        Assert.Equal(RuntimeStackImportOutcome.Failed, result.Outcome);
        Assert.Contains(result.Errors, error => error.Contains("unknown action", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(RuntimeStackImportOutcome.Failed, duplicate.Outcome);
        Assert.Equal(0, fixture.Contributors[0].ImportCount);
    }

    [Fact]
    public async Task ImportAsync_WhenFragmentIdUnknown_RejectsAndConsumesPlan()
    {
        await using var fixture = await StackImportFixture.CreateAsync(new TestContributor("one"), new TestContributor("two"));
        var preview = await fixture.PreviewAsync(["one-fragment", "two-fragment"]);

        var result = await fixture.Service.ImportAsync(new RuntimeStackImportRequest(preview.PlanId!, ["one-fragment", "unknown-fragment"], ["one-action"]));

        Assert.Equal(RuntimeStackImportOutcome.Failed, result.Outcome);
        Assert.Contains(result.Errors, error => error.Contains("unknown fragment", StringComparison.OrdinalIgnoreCase));
        Assert.All(fixture.Contributors, contributor => Assert.Equal(0, contributor.ImportCount));
    }

    [Fact]
    public async Task ImportAsync_WhenOneContributorFails_ReturnsPartialWithPerContributorResults()
    {
        await using var fixture = await StackImportFixture.CreateAsync(
            new TestContributor("one"),
            new TestContributor("two", succeeds: false));
        var preview = await fixture.PreviewAsync(["one-fragment", "two-fragment"]);

        var result = await fixture.Service.ImportAsync(new RuntimeStackImportRequest(
            preview.PlanId!,
            ["one-fragment", "two-fragment"],
            ActionIds(preview)));

        Assert.Equal(RuntimeStackImportOutcome.Partial, result.Outcome);
        Assert.Collection(
            result.ContributorResults,
            completed =>
            {
                Assert.Equal("one", completed.ContributorId);
                Assert.Equal(RuntimeStackImportOutcome.Completed, completed.Outcome);
                Assert.Single(completed.ImportedItems);
            },
            failed =>
            {
                Assert.Equal("two", failed.ContributorId);
                Assert.Equal(RuntimeStackImportOutcome.Failed, failed.Outcome);
                Assert.NotEmpty(failed.Errors);
            });
        Assert.Single(result.ContributorResults, contributor => contributor.ImportedItems.Count > 0);
        Assert.All(fixture.Contributors, contributor => Assert.Equal(1, contributor.ImportCount));
        Assert.Equal(1, fixture.Contributors[0].AppliedCount);
        Assert.Equal(0, fixture.Contributors[1].AppliedCount);
    }

    [Fact]
    public async Task ImportAsync_WhenFailedContributorReportsCommittedItems_NormalizesOutcomeToPartial()
    {
        await using var fixture = await StackImportFixture.CreateAsync(
            new TestContributor("one", succeeds: false, reportsCommittedItemOnFailure: true));
        var preview = await fixture.PreviewAsync(["one-fragment"]);

        var result = await fixture.Service.ImportAsync(new RuntimeStackImportRequest(
            preview.PlanId!,
            ["one-fragment"],
            ActionIds(preview)));

        Assert.Equal(RuntimeStackImportOutcome.Partial, result.Outcome);
        var contributor = Assert.Single(result.ContributorResults);
        Assert.Equal(RuntimeStackImportOutcome.Partial, contributor.Outcome);
        Assert.Single(contributor.ImportedItems);
        Assert.Contains(contributor.Errors, error => error.Contains("committed items", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PreviewAndImport_ScopeDuplicateLocalIdsAcrossContributors()
    {
        await using var fixture = await StackImportFixture.CreateAsync(
            new TestContributor("one", actionId: "shared", inputId: "shared"),
            new TestContributor("two", actionId: "shared", inputId: "shared"));

        var preview = await fixture.PreviewAsync(["one-fragment", "two-fragment"]);

        Assert.True(preview.Success, string.Join(Environment.NewLine, preview.Errors));
        Assert.Equal(2, preview.Actions.Select(action => action.ActionId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, preview.RequiredInputs.Select(input => input.InputId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(preview.RequiredInputs, input => Assert.Equal(RuntimeStackInputSensitivity.Secret, input.Sensitivity));
        var result = await fixture.Service.ImportAsync(new RuntimeStackImportRequest(
            preview.PlanId!,
            ["one-fragment", "two-fragment"],
            ActionIds(preview)));
        Assert.Equal(RuntimeStackImportOutcome.Completed, result.Outcome);
        Assert.All(fixture.Contributors, contributor => Assert.Equal(["shared"], contributor.LastSelectedActionIds));
    }

    [Fact]
    public async Task PreviewAsync_WhenContributorReturnsDuplicateActionId_ReturnsError()
    {
        await using var fixture = await StackImportFixture.CreateAsync(new TestContributor("one", duplicateAction: true));

        var preview = await fixture.PreviewAsync(["one-fragment"]);

        Assert.False(preview.Success);
        Assert.Contains(preview.Errors, error => error.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
                                                && error.Contains("action", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PreviewAsync_WhenContributorReturnsDuplicateRequiredInputId_ReturnsError()
    {
        await using var fixture = await StackImportFixture.CreateAsync(new TestContributor("one", duplicateInput: true));

        var preview = await fixture.PreviewAsync(["one-fragment"]);

        Assert.False(preview.Success);
        Assert.Contains(preview.Errors, error => error.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
                                                && error.Contains("required input", StringComparison.OrdinalIgnoreCase));
    }

    private static string[] ActionIds(RuntimeStackImportPreviewResponse preview)
        => preview.Actions.Select(action => action.ActionId).ToArray();

    private sealed class StackImportFixture : IAsyncDisposable
    {
        private StackImportFixture(
            RuntimePackagePaths paths,
            RuntimeContentTransferStore transfers,
            RuntimeSessionOwner owner,
            RuntimeRpcCatalog rpcCatalog,
            RuntimeRpcPermissionStore rpcPermissions,
            RuntimeRpcBroker rpcBroker,
            TestTimeProvider clock,
            IReadOnlyList<TestContributor> contributors)
        {
            Paths = paths;
            Transfers = transfers;
            Owner = owner;
            RpcCatalog = rpcCatalog;
            RpcPermissions = rpcPermissions;
            Clock = clock;
            Contributors = contributors;
            Service = new RuntimeStackImportService(owner, transfers, rpcBroker, clock);
        }

        public RuntimePackagePaths Paths { get; }
        public RuntimeContentTransferStore Transfers { get; }
        public RuntimeSessionOwner Owner { get; }
        public RuntimeRpcCatalog RpcCatalog { get; }
        public RuntimeRpcPermissionStore RpcPermissions { get; }
        public TestTimeProvider Clock { get; }
        public IReadOnlyList<TestContributor> Contributors { get; }
        public RuntimeStackImportService Service { get; }

        public static async Task<StackImportFixture> CreateAsync(params TestContributor[] contributors)
        {
            var root = Path.Combine(Path.GetTempPath(), "sunder-stack-import-plan-tests", Guid.NewGuid().ToString("N"));
            var paths = new RuntimePackagePaths(root);
            var clock = new TestTimeProvider();
            var transfers = new RuntimeContentTransferStore(paths, timeProvider: clock);
            var rpcCatalog = new RuntimeRpcCatalog();
            var owner = new RuntimeSessionOwner(
                NullLogger<RuntimeSessionOwner>.Instance,
                new RuntimeEventStreamService(),
                timeProvider: clock,
                rpcCatalog: rpcCatalog);
            var rpcPermissions = new RuntimeRpcPermissionStore(paths, clock);
            var rpcBroker = new RuntimeRpcBroker(
                rpcCatalog,
                rpcPermissions,
                owner.State,
                owner,
                timeProvider: clock);
            var fixture = new StackImportFixture(
                paths,
                transfers,
                owner,
                rpcCatalog,
                rpcPermissions,
                rpcBroker,
                clock,
                contributors);
            await fixture.PublishSessionAsync(contributors);
            return fixture;
        }

        public async Task PublishSessionAsync(params TestContributor[] contributors)
        {
            var packages = contributors.Select(CreatePackage).ToArray();
            var session = new ActivePackageSession(
                sessionFolder: null,
                packages.ToDictionary(static package => package.Descriptor.PackageId, StringComparer.OrdinalIgnoreCase),
                packages.ToDictionary(
                    static package => package.Descriptor.PackageId,
                    static package => new SessionPackageDescriptor(
                        package.Descriptor.PackageId,
                        package.Descriptor.DisplayName,
                        package.Descriptor.Version,
                        PackageHostRoles.Runtime,
                        null,
                        true,
                        PackageReadinessState.Ready,
                        [],
                        null,
                        null,
                        null,
                        0),
                    StringComparer.OrdinalIgnoreCase));
            await Owner.PublishAsync(
                session,
                Owner.Sources.Snapshot(),
                [],
                [],
                Owner.Generation);
            RpcCatalog.ActivateSession(session, Owner.Generation);
        }

        public async Task<RuntimeStackImportPreviewResponse> PreviewAsync(IReadOnlyList<string> selectedFragmentIds)
        {
            var archivePath = Path.Combine(Paths.RootPath, $"{Guid.NewGuid():N}.sunderstack");
            Directory.CreateDirectory(Paths.RootPath);
            var payloads = new Dictionary<string, string>(StringComparer.Ordinal);
            var fragments = new List<SunderStackFragmentManifest>();
            foreach (var contributor in Contributors)
            {
                var fragmentId = contributor.ContributorId + "-fragment";
                var payloadPath = Path.Combine(Paths.RootPath, fragmentId + ".json");
                await File.WriteAllTextAsync(payloadPath, "{\"value\":true}");
                var archivePayloadPath = $"payload/fragments/{fragmentId}.json";
                payloads[archivePayloadPath] = payloadPath;
                fragments.Add(new SunderStackFragmentManifest
                {
                    FragmentId = fragmentId,
                    OwnerPackageId = contributor.PackageId,
                    ContributorId = contributor.ContributorId,
                    SchemaId = contributor.ContributorId + "/schema",
                    SchemaVersion = 1,
                    DisplayName = contributor.ContributorId,
                    DefaultSelected = true,
                    PayloadPath = archivePayloadPath,
                });
            }

            await SunderStackArchiveWriter.WriteAsync(new SunderStackManifest
            {
                SchemaVersion = SunderStackFormat.CurrentSchemaVersion,
                MinReaderVersion = SunderStackFormat.CurrentReaderVersion,
                StackId = "test.stack",
                Name = "Test Stack",
                CreatedAtUtc = Clock.GetUtcNow(),
                UpdatedAtUtc = Clock.GetUtcNow(),
                Packages = [],
                Fragments = fragments,
            }, archivePath, payloads);
            await using var stream = File.OpenRead(archivePath);
            var upload = await Transfers.CreateUploadAsync(
                RuntimeUploadKind.Stack,
                stream,
                stream.Length,
                expectedHash: null,
                Path.GetFileName(archivePath),
                "application/vnd.sunder.stack",
                Owner.Generation,
                CancellationToken.None);

            var inputValues = Contributors.ToDictionary(
                contributor => RuntimeStackScopedKey.Create(
                    RuntimeStackScopedKey.InputKind,
                    contributor.PackageId,
                    contributor.ContributorId,
                    contributor.InputId),
                _ => "preview-bound",
                StringComparer.OrdinalIgnoreCase);
            var remaps = Contributors.ToDictionary(
                contributor => RuntimeStackScopedKey.Create(
                    RuntimeStackScopedKey.RemapKind,
                    contributor.PackageId,
                    contributor.ContributorId,
                    "source"),
                _ => "target",
                StringComparer.OrdinalIgnoreCase);
            return await Service.PreviewAsync(new RuntimeStackImportPreviewRequest(
                upload.UploadId,
                selectedFragmentIds,
                inputValues,
                remaps));
        }

        public ValueTask DisposeAsync()
        {
            Service.Dispose();
            Transfers.Dispose();
            RpcPermissions.Dispose();
            RpcCatalog.Dispose();
            try
            {
                Directory.Delete(Paths.RootPath, recursive: true);
            }
            catch
            {
            }
            return ValueTask.CompletedTask;
        }

        private ActiveLoadedPackage CreatePackage(TestContributor contributor)
        {
            var descriptor = SunderStackContributorRpc.Descriptor;
            var providerId = contributor.PackageId + ".stack";
            var declaration = new SunderPackageProviderManifest
            {
                ProviderId = providerId,
                ContractId = descriptor.ContractId,
                ContractVersion = descriptor.Version,
                ContractSha256 = descriptor.Sha256,
                Role = SunderPackageFormat.RuntimeHostRole,
            };
            var manifest = new SunderPackageManifest
            {
                Id = contributor.PackageId,
                Name = contributor.PackageId,
                Version = "1.0.0",
                ContractBundles =
                [
                    new SunderPackageContractBundleManifest
                    {
                        ContractId = descriptor.ContractId,
                        Version = descriptor.Version,
                        DescriptorPath = "contracts/sunder.stack.contributor.rpc.json",
                        Sha256 = descriptor.Sha256,
                    },
                ],
                Provides = [declaration],
            };
            var source = new RuntimePackageSource(
                contributor.PackageId,
                PackageSourceKind.Dev,
                Paths.RootPath,
                Manifest: manifest);
            return new ActiveLoadedPackage(
                new ActivePackageDescriptor(
                    contributor.PackageId,
                    contributor.PackageId,
                    "1.0.0",
                    PackageHostRoles.Runtime,
                    null,
                    true,
                    PackageReadinessState.Ready,
                    []),
                source,
                SettingsSchema: null,
                new JsonPackageKeyValueStore(Path.Combine(Paths.RootPath, contributor.PackageId + ".state.json")),
                new JsonPackageSecretsStore(
                    Path.Combine(Paths.RootPath, contributor.PackageId + ".secrets.json"),
                    null,
                    null,
                    new RestrictedFileMasterKeyProtection()),
                AuthHandler: null,
                CallbackHandlers: new Dictionary<string, IPackageCallbackHandler>(StringComparer.OrdinalIgnoreCase),
                BackgroundServices: [],
                new ServiceCollection().BuildServiceProvider(),
                LoadContext: null,
                EmptyTestPackageSettings.Instance)
            {
                RuntimeActivationId = Guid.NewGuid(),
                RpcProviders = new Dictionary<string, RuntimeRpcProviderRegistration>(StringComparer.Ordinal)
                {
                    [providerId] = new RuntimeRpcProviderRegistration(
                        providerId,
                        descriptor.ContractId,
                        descriptor.Version,
                        descriptor.Sha256,
                        descriptor,
                        SunderStackContributorRpc.CreateHandler(contributor),
                        declaration),
                },
                RpcContracts = new Dictionary<string, SunderRpcContractDescriptor>(StringComparer.Ordinal)
                {
                    [PackageSessionPreparer.ContractKey(descriptor.ContractId, descriptor.Version)] = descriptor,
                },
                RpcManifestSha256 = new string('a', 64),
            };
        }
    }

    private sealed class TestContributor(
        string contributorId,
        bool succeeds = true,
        string? actionId = null,
        string? inputId = null,
        bool duplicateAction = false,
        bool duplicateInput = false,
        bool reportsCommittedItemOnFailure = false,
        Func<CancellationToken, Task>? appliedAction = null) : IPackageStackImporter, IPackageStackImportAppliedHandler
    {
        private readonly Func<CancellationToken, Task>? _appliedAction = appliedAction;

        public string PackageId => "test.package." + contributorId;
        public string ContributorId => contributorId;
        public string DisplayName => contributorId;
        public int ImportCount { get; private set; }
        public int AppliedCount { get; private set; }
        public StackImportAppliedContext? LastAppliedContext { get; private set; }
        public string ActionId { get; } = actionId ?? contributorId + "-action";
        public string InputId { get; } = inputId ?? "input";
        public IReadOnlyList<string> LastSelectedActionIds { get; private set; } = [];

        public ValueTask<StackImportPreview> PreviewImportAsync(
            StackImportPreviewRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new StackImportPreview(
                duplicateAction
                    ? [
                        new StackImportAction(ActionId, "Import " + ContributorId, StackImportActionKind.Create),
                        new StackImportAction(ActionId, "Import duplicate " + ContributorId, StackImportActionKind.Create),
                    ]
                    : [new StackImportAction(ActionId, "Import " + ContributorId, StackImportActionKind.Create)],
                duplicateInput
                    ? [
                        new StackRequiredInputDescriptor(InputId, "Input", StackValueSensitivity.Secret),
                        new StackRequiredInputDescriptor(InputId, "Duplicate input", StackValueSensitivity.Secret),
                    ]
                    : [new StackRequiredInputDescriptor(InputId, "Input", StackValueSensitivity.Secret)],
                [],
                []));

        public ValueTask<StackImportResult> ImportAsync(
            StackImportRequest request,
            CancellationToken cancellationToken = default)
        {
            ImportCount++;
            LastSelectedActionIds = request.SelectedActionIds;
            Assert.Equal("preview-bound", request.InputValues[InputId]);
            Assert.Equal("target", request.IdRemaps["source"]);
            return ValueTask.FromResult(succeeds
                ? new StackImportResult(
                    StackImportOutcome.Completed,
                    [new StackImportedItem(ContributorId + "-item", ContributorId, "test")],
                    new Dictionary<string, string>(),
                    [],
                    [])
                : new StackImportResult(
                    StackImportOutcome.Failed,
                    reportsCommittedItemOnFailure
                        ? [new StackImportedItem(ContributorId + "-item", ContributorId, "test")]
                        : [],
                    new Dictionary<string, string>(),
                    [],
                    ["Contributor failed."]));
        }

        public async ValueTask OnStackImportAppliedAsync(
            StackImportAppliedContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppliedCount++;
            LastAppliedContext = context;
            if (_appliedAction is not null)
            {
                await _appliedAction(cancellationToken);
            }
        }
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan value) => _utcNow += value;
    }
}
