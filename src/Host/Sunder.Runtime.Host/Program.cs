using System.Diagnostics;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host;
using Sunder.Runtime.Host.Endpoints;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;

var startupPhaseStarted = Stopwatch.GetTimestamp();
var startupOptions = RuntimeHostStartupOptions.Parse(args);
if (startupOptions.WaitForDebugger)
{
    while (!Debugger.IsAttached)
    {
        Thread.Sleep(100);
    }
}

var builder = WebApplication.CreateBuilder(args);
if (builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
{
    throw new InvalidOperationException(
        "Configured Kestrel endpoints are not supported by the local Runtime. Configure one loopback URL with --urls or ASPNETCORE_URLS instead.");
}

var listenUrls = RuntimeListenUrlValidator.ParseAndValidate(
    builder.Configuration["urls"],
    startupOptions.DevelopmentAllowNonLoopbackRuntimeListen);
builder.WebHost.UseUrls(listenUrls.ToArray());
LogStartupPhase("host builder", ref startupPhaseStarted);

var packagePaths = new RuntimePackagePaths();
Environment.SetEnvironmentVariable(RuntimeLocalState.StateRootEnvironmentVariable, null);
RuntimeLocalState.Validate(packagePaths.RootPath);
using var runtimeRootLease = RuntimeRootLease.Acquire(packagePaths);
RuntimeLocalState.EnsureInitialized(packagePaths.RootPath);
LogStartupPhase("local state and lease", ref startupPhaseStarted);

var bearerToken = Environment.GetEnvironmentVariable("SUNDER_RUNTIME_BEARER_TOKEN");
Environment.SetEnvironmentVariable("SUNDER_RUNTIME_BEARER_TOKEN", null);
if (string.IsNullOrWhiteSpace(bearerToken))
{
    bearerToken = RuntimeBearerToken.Create();
}

var configuredConnectionInfoPath = Environment.GetEnvironmentVariable("SUNDER_RUNTIME_CONNECTION_FILE");
Environment.SetEnvironmentVariable("SUNDER_RUNTIME_CONNECTION_FILE", null);
var connectionInfoPath = string.IsNullOrWhiteSpace(configuredConnectionInfoPath)
    ? packagePaths.ConnectionInfoFilePath
    : Path.GetFullPath(configuredConnectionInfoPath);
RuntimeConnectionInfo? connectionInfo = null;

try
{
    builder.Services.AddRuntimeHostServices(
        packagePaths,
        new RuntimeBearerTokenValidator(bearerToken));
    LogStartupPhase("service registration", ref startupPhaseStarted);

    await using var app = builder.Build();
    app.UseMiddleware<RuntimeProblemDetailsMiddleware>();
    app.UseMiddleware<RuntimeBearerAuthenticationMiddleware>();

    var startedAtUtc = DateTimeOffset.UtcNow;
    app.MapRuntimeHandshakeEndpoint();
    var api = app.MapGroup("/api/v1");
    api.MapSystemEndpoints(startedAtUtc)
        .MapPackageSessionEndpoints()
        .MapContentTransferEndpoints()
        .MapPackageDataEndpoints()
        .MapPackageRuntimeOperationEndpoints()
        .MapPackageSettingsEndpoints()
        .MapPackageCallbackEndpoints()
        .MapPackageAuthEndpoints()
        .MapPackageFaultEndpoints()
        .MapDevPackageOwnerEndpoints()
        .MapInstalledPackageEndpoints()
        .MapStackEndpoints()
        .MapRuntimeStreamEndpoints();
    api.MapRegistryEndpoints();
    app.MapFallback("/api/{**path}", RuntimeApiNotFound);

    var packageSessionService = app.Services.GetRequiredService<PackageSessionLifecycleService>();
    var installedPackageService = app.Services.GetRequiredService<InstalledPackageLifecycleService>();
    var devPackageWatchService = app.Services.GetRequiredService<DevPackageWatchService>();
    var packageLogStreamService = app.Services.GetRequiredService<PackageLogStreamService>();
    var runtimeEventStreamService = app.Services.GetRequiredService<RuntimeEventStreamService>();
    var runtimeSessionOwner = app.Services.GetRequiredService<RuntimeSessionOwner>();
    var transferStore = app.Services.GetRequiredService<RuntimeContentTransferStore>();
    var snapshotStore = app.Services.GetRequiredService<PackageUiSnapshotStore>();
    var shutdownLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RuntimeShutdown");
    var packageLogStartupTask = Task.CompletedTask;
    LogStartupPhase("host composition", ref startupPhaseStarted);
    try
    {
        await RuntimeHostBootstrapRunner.StartAsync(
            app,
            runtimeSessionOwner,
            async cancellationToken =>
            {
                await installedPackageService.InitializeAsync(cancellationToken);
                return await installedPackageService.LoadInstalledPackagesAsync(cancellationToken);
            },
            () =>
            {
                var addresses = app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()?.Addresses;
                var boundAddress = addresses is { Count: 1 }
                    ? addresses.Single()
                    : throw new InvalidOperationException("The Runtime did not publish exactly one bound listener address.");
                connectionInfo = new RuntimeConnectionInfo(new Uri(boundAddress), bearerToken);
                RuntimeConnectionInfoStore.Save(connectionInfo, connectionInfoPath);
                LogStartupPhase("listener bind and connection publication", ref startupPhaseStarted);
            });
        LogStartupPhase("package bootstrap", ref startupPhaseStarted);
        packageLogStartupTask = Task.Run(() =>
        {
            try
            {
                packageLogStreamService.Start(app.Lifetime.ApplicationStopping);
                LogStartupPhase("package log replay", ref startupPhaseStarted);
            }
            catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                app.Logger.LogWarning(exception, "Package log discovery was unavailable during Runtime startup");
            }
        });

        await app.WaitForShutdownAsync();
    }
    finally
    {
        runtimeSessionOwner.MarkShuttingDown();
        await RunCleanupStepAsync(
            "finish package log discovery",
            () => packageLogStartupTask,
            shutdownLogger);
        await RunCleanupStepAsync(
            "stop package callbacks",
            runtimeSessionOwner.Callbacks.ShutdownAsync,
            shutdownLogger);
        await RunCleanupStepAsync(
            "stop dev package watching",
            () => devPackageWatchService.DisposeAsync().AsTask(),
            shutdownLogger);
        await RunCleanupStepAsync(
            "retire package sessions",
            () => installedPackageService.ShutdownAsync(packageSessionService),
            shutdownLogger);
        await RunCleanupStepAsync(
            "stop package log streaming",
            () => packageLogStreamService.DisposeAsync().AsTask(),
            shutdownLogger);
        await RunCleanupStepAsync(
            "clean content transfers",
            () => Task.Run(transferStore.Dispose),
            shutdownLogger);
        await RunCleanupStepAsync(
            "clean package UI snapshots",
            () => Task.Run(snapshotStore.Dispose),
            shutdownLogger);
        try
        {
            runtimeEventStreamService.Complete();
        }
        catch (Exception exception)
        {
            shutdownLogger.LogWarning(exception, "Failed to complete the Runtime event stream during shutdown");
        }
    }
}
finally
{
    if (connectionInfo is not null)
    {
        RuntimeConnectionInfoStore.DeleteIfMatches(connectionInfo, connectionInfoPath);
    }
}

static async Task RunCleanupStepAsync(
    string operation,
    Func<Task> cleanup,
    ILogger logger)
{
    try
    {
        await cleanup().WaitAsync(TimeSpan.FromSeconds(15));
    }
    catch (Exception exception)
    {
        logger.LogWarning(exception, "Failed to {ShutdownOperation} during Runtime shutdown", operation);
    }
}

static IResult RuntimeApiNotFound()
    => throw new RuntimeNotFoundException("The requested Runtime API endpoint was not found.");

static void LogStartupPhase(string phase, ref long phaseStarted)
{
    var now = Stopwatch.GetTimestamp();
    var elapsed = Stopwatch.GetElapsedTime(phaseStarted, now);
    Console.Error.WriteLine(FormattableString.Invariant(
        $"{DateTimeOffset.UtcNow:O} level=Information runtime-startup phase='{phase}' elapsed_ms={elapsed.TotalMilliseconds:0}"));
    phaseStarted = now;
}
