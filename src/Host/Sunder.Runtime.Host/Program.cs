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
var supervisorEndpoint = RuntimeSupervisedTransport.ReadFromEnvironment();
var supervisorLifetimeHandle = Environment.GetEnvironmentVariable("SUNDER_RUNTIME_SUPERVISOR_PIPE");
Environment.SetEnvironmentVariable("SUNDER_RUNTIME_SUPERVISOR_PIPE", null);
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
        "Configured Kestrel endpoints are not supported by the Runtime. Use Supervisor IPC or configure one standalone loopback URL.");
}

if (supervisorEndpoint is null)
{
    var listenUrls = RuntimeListenUrlValidator.ParseAndValidate(
        builder.Configuration["urls"],
        startupOptions.DevelopmentAllowNonLoopbackRuntimeListen);
    builder.WebHost.UseUrls(listenUrls.ToArray());
}
else
{
    RuntimeSupervisedTransport.Configure(builder.WebHost, supervisorEndpoint);
}
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
    await using var supervisorLifetime = RuntimeSupervisorLifetime.Start(
        supervisorLifetimeHandle,
        app.Lifetime,
        app.Logger);
    using var shutdownDeadline = new RuntimeShutdownDeadline(
        app.Lifetime.ApplicationStopping,
        RuntimeShutdownDeadline.DefaultTimeout);
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
                Uri runtimeUrl;
                if (supervisorEndpoint is not null)
                {
                    RuntimeSupervisedTransport.RestrictEndpoint(supervisorEndpoint);
                    runtimeUrl = RuntimeIpcEndpoint.LogicalRuntimeUrl;
                }
                else
                {
                    var addresses = app.Services.GetRequiredService<IServer>()
                        .Features.Get<IServerAddressesFeature>()?.Addresses;
                    var boundAddress = addresses is { Count: 1 }
                        ? addresses.Single()
                        : throw new InvalidOperationException("The Runtime did not publish exactly one bound listener address.");
                    runtimeUrl = new Uri(boundAddress);
                }
                connectionInfo = new RuntimeConnectionInfo(runtimeUrl, bearerToken);
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

        await WaitForApplicationStoppingAsync(app.Lifetime.ApplicationStopping);
    }
    finally
    {
        shutdownDeadline.Start();
        app.Lifetime.StopApplication();
        runtimeSessionOwner.MarkShuttingDown();
        await RunCleanupStepAsync(
            "stop the Runtime web host",
            cancellationToken => app.StopAsync(cancellationToken),
            shutdownDeadline.Token,
            shutdownLogger);
        await RunCleanupStepAsync(
            "finish package log discovery",
            _ => packageLogStartupTask,
            shutdownDeadline.Token,
            shutdownLogger);
        await RunCleanupStepAsync(
            "stop package callbacks",
            _ => runtimeSessionOwner.Callbacks.ShutdownAsync(),
            shutdownDeadline.Token,
            shutdownLogger);
        await RunCleanupStepAsync(
            "stop dev package watching",
            _ => devPackageWatchService.DisposeAsync().AsTask(),
            shutdownDeadline.Token,
            shutdownLogger);
        await RunCleanupStepAsync(
            "retire package sessions",
            cancellationToken => installedPackageService.ShutdownAsync(packageSessionService, cancellationToken),
            shutdownDeadline.Token,
            shutdownLogger,
            startWhenDeadlineElapsed: true);
        await RunCleanupStepAsync(
            "stop package log streaming",
            _ => packageLogStreamService.DisposeAsync().AsTask(),
            shutdownDeadline.Token,
            shutdownLogger);
        await RunCleanupStepAsync(
            "clean content transfers",
            _ => Task.Run(transferStore.Dispose),
            shutdownDeadline.Token,
            shutdownLogger);
        await RunCleanupStepAsync(
            "clean package UI snapshots",
            _ => Task.Run(snapshotStore.Dispose),
            shutdownDeadline.Token,
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
    RuntimeSupervisedTransport.CleanupEndpoint(supervisorEndpoint);
}

static async Task RunCleanupStepAsync(
    string operation,
    Func<CancellationToken, Task> cleanup,
    CancellationToken cancellationToken,
    ILogger logger,
    bool startWhenDeadlineElapsed = false)
{
    if (cancellationToken.IsCancellationRequested && !startWhenDeadlineElapsed)
    {
        logger.LogWarning("Skipped {ShutdownOperation} because the Runtime shutdown deadline elapsed", operation);
        return;
    }
    try
    {
        await cleanup(cancellationToken).WaitAsync(cancellationToken);
    }
    catch (Exception exception)
    {
        logger.LogWarning(exception, "Failed to {ShutdownOperation} during Runtime shutdown", operation);
    }
}

static async Task WaitForApplicationStoppingAsync(CancellationToken applicationStopping)
{
    try
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, applicationStopping);
    }
    catch (OperationCanceledException) when (applicationStopping.IsCancellationRequested)
    {
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
