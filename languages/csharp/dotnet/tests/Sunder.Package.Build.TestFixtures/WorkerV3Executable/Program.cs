using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Authentication;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Settings;
using Sunder.Sdk.Storage;
using Sunder.Sdk.Worker;

[assembly: SunderPackage(Id = "test.worker.v3.executable", Name = "Worker V3 Executable Fixture")]

await SunderWorker.RunAsync(context => new SunderWorkerOptions([])
{
    Composition = new SunderWorkerComposition(
        new FixtureServiceProvider(),
        [new FixtureLifecycleService()]),
    CallbackHandlers = [new FixtureCallbackHandler()],
    AuthHandler = new FixtureAuthHandler(),
    StateMigrations =
    [
        PackageStorageKeyMigration.Exact("legacy.state.v1", "fixture.state.v1"),
    ],
    SecretMigrations =
    [
        PackageStorageKeyMigration.DynamicCleanup(key => key == "legacy.secret.v1"
            ? PackageStorageKeyMigrationAction.Rewrite("fixture.secret.v1", precedence: 1)
            : PackageStorageKeyMigrationAction.NoMatch),
    ],
    SettingsSchema = new PackageSettingsSchema(
        "Fixture settings.",
        [
            new PackageSettingsSection(
                "general",
                "General",
                null,
                [
                    new PackageSettingsField(
                        "enabled",
                        "Enabled",
                        PackageSettingsFieldKind.Boolean,
                        defaultValue: "true"),
                ]),
        ]),
    OnActivated = async (activationContext, cancellationToken) =>
    {
        await activationContext.Settings.GetValueAsync("enabled", cancellationToken);
        await activationContext.State.ContainsKeyAsync("initialized", cancellationToken);
        await activationContext.Files.ReadAsync("fixture.txt", cancellationToken);
        await activationContext.Secrets.GetSecretAsync("fixture.token", cancellationToken);
        await activationContext.Logging.Events.WriteAsync(
            PackageLogLevel.Information,
            "fixture.activated",
            "Worker fixture activated.",
            cancellationToken: cancellationToken);
    },
});

sealed class FixtureServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}

sealed class FixtureLifecycleService : ISunderWorkerLifecycleService
{
    public Task Completion => Task.CompletedTask;

    public ValueTask PrepareCandidateAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask CommitGenerationAsync(
        SunderWorkerGenerationContext generation,
        CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask ActivateAsync(
        SunderWorkerContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask ShutdownAsync(
        SunderWorkerShutdownContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}

sealed class FixtureCallbackHandler : IPackageCallbackHandler
{
    public string CallbackHandlerId => "fixture.callback";

    public Task<PackageCallbackStartResult?> StartCallbackAsync(
        PackageCallbackStartContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult<PackageCallbackStartResult?>(new(
            "test.worker.v3.executable",
            context.CallbackSessionId,
            PackageCallbackFlowKind.Browser,
            "https://example.invalid/callback",
            context.Parameters.GetValueOrDefault("message") ?? string.Empty));

    public Task<PackageCallbackCompletionResult> CompleteCallbackAsync(
        PackageCallbackCompletionContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new PackageCallbackCompletionResult(
            "test.worker.v3.executable",
            context.CallbackSessionId,
            PackageCallbackCompletionState.Completed,
            context.QueryValues.GetValueOrDefault("code") ?? string.Empty));
}

sealed class FixtureAuthHandler : IPackageAuthHandler
{
    public ValueTask<PackageAuthStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Status(PackageAuthStatusKind.NotConnected));

    public Task<PackageAuthSessionStartResult?> StartAuthorizationAsync(
        PackageAuthSessionStartContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult<PackageAuthSessionStartResult?>(new(
            "test.worker.v3.executable",
            context.AuthSessionId,
            PackageAuthFlowKind.Browser,
            "https://example.invalid/auth",
            "Authorize the fixture."));

    public Task<PackageAuthStatus> CompleteAuthorizationAsync(
        PackageAuthSessionCompletionContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Status(PackageAuthStatusKind.Connected));

    public Task<PackageAuthStatus> DisconnectAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Status(PackageAuthStatusKind.NotConnected));

    private static PackageAuthStatus Status(PackageAuthStatusKind status)
        => new(
            "test.worker.v3.executable",
            status,
            status == PackageAuthStatusKind.Connected ? "Connected." : "Not connected.",
            CanAuthorize: status != PackageAuthStatusKind.Connected,
            CanDisconnect: status == PackageAuthStatusKind.Connected);
}
