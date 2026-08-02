using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Sunder.Host.Contracts;
using Sunder.Host.Supervisor;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;

var options = HostStartupOptions.Parse(args);
HostDeploymentIdentityVerifier.Validate(AppContext.BaseDirectory, options.DeploymentIdentity);
var builder = WebApplication.CreateBuilder(args);
if (builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
{
    throw new InvalidOperationException(
        "Configured Kestrel endpoints are not supported by the local Sunder Host. Configure one loopback URL with --urls or ASPNETCORE_URLS instead.");
}
var listenUrl = HostListenUrlValidator.ParseAndValidateLocal(builder.Configuration["urls"]);
builder.WebHost.UseUrls(listenUrl);

var externalToken = Environment.GetEnvironmentVariable("SUNDER_RUNTIME_BEARER_TOKEN");
Environment.SetEnvironmentVariable("SUNDER_RUNTIME_BEARER_TOKEN", null);
if (string.IsNullOrWhiteSpace(externalToken))
{
    externalToken = RuntimeBearerToken.Create();
}

var configuredConnectionPath = Environment.GetEnvironmentVariable("SUNDER_RUNTIME_CONNECTION_FILE");
Environment.SetEnvironmentVariable("SUNDER_RUNTIME_CONNECTION_FILE", null);
var hostStateRoot = options.HostStateRoot ?? HostStatePaths.GetDefaultRootPath();
var runtimeStateRoot = options.RuntimeStateRoot ?? RuntimeLocalState.GetV1RootPath();
var externalConnectionPath = string.IsNullOrWhiteSpace(configuredConnectionPath)
    ? Path.Combine(hostStateRoot, "connection", "host.json")
    : Path.GetFullPath(configuredConnectionPath);
var workerConnectionPath = Path.Combine(
    hostStateRoot,
    "connection",
    "runtime-worker.json");
var hostId = new HostIdentityStore(hostStateRoot).LoadOrCreateHostId();
var supervisorInstanceId = Guid.NewGuid();
var hostFeatures = new List<string>
{
    HostProtocolFeatures.RuntimeGatewayV1,
    HostProtocolFeatures.RuntimeLifecycleV1,
    HostProtocolFeatures.DurableOperationsV1,
};

builder.Services.AddSingleton(new HostBearerTokenValidator(externalToken));
builder.Services.AddSingleton(new HostLifecycleStore(hostStateRoot));
builder.Services.AddSingleton(provider => new RuntimeWorkerCoordinator(
    options.RuntimeHostPath,
    workerConnectionPath,
    options.WorkerStartupTimeout,
    provider.GetRequiredService<HostLifecycleStore>(),
    runtimeStateRoot: runtimeStateRoot));
builder.Services.AddSingleton<IRuntimeWorkerConnectionSource>(provider =>
    provider.GetRequiredService<RuntimeWorkerCoordinator>());
builder.Services.AddSingleton<RuntimeGateway>();
builder.Services.AddSingleton<HostRuntimeResetChallengeService>();

await using var app = builder.Build();
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (HostLifecycleConflictException exception) when (!context.Response.HasStarted)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = exception.Code };
        if (exception.ActiveOperationId is not null)
        {
            extensions["activeOperationId"] = exception.ActiveOperationId;
        }
        await Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Host lifecycle conflict",
                detail: exception.Message,
                extensions: extensions)
            .ExecuteAsync(context);
    }
});
app.Use(async (context, next) =>
{
    var validator = context.RequestServices.GetRequiredService<HostBearerTokenValidator>();
    if (!validator.IsValid(context.Request.Headers.Authorization))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return;
    }
    await next(context);
});

app.MapGet("/api/host/handshake", () => Results.Ok(new HostHandshakeResponse(
    HostProtocol.Identity,
    HostProtocol.CurrentRevision,
    HostProtocol.MinimumSupportedRevision,
    HostProtocol.MaximumSupportedRevision,
    hostId,
    supervisorInstanceId,
    hostFeatures,
    CreateProductDiagnostics())
{
    DeploymentIdentity = options.DeploymentIdentity,
}));
app.MapGet("/api/host/v1/status", (RuntimeWorkerCoordinator worker) => Results.Ok(worker.GetStatus()));
app.MapGet("/api/host/v1/operations/{operationId}", (
    string operationId,
    RuntimeWorkerCoordinator worker) =>
    worker.TryGetOperation(operationId, out var operation)
        ? Results.Ok(operation)
        : Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Host lifecycle operation not found",
            detail: $"Lifecycle operation '{operationId}' does not exist.",
            extensions: new Dictionary<string, object?> { ["code"] = "host.operation-not-found" }));
app.MapPost("/api/host/v1/runtime/start", async (
    HostLifecycleRequest request,
    RuntimeWorkerCoordinator worker,
    CancellationToken cancellationToken) =>
{
    ValidateLifecycleRequest(request);
    return CreateLifecycleSubmissionResult(await worker.SubmitStartAsync(request, cancellationToken));
});
app.MapPost("/api/host/v1/runtime/stop", async (
    HostLifecycleRequest request,
    RuntimeWorkerCoordinator worker,
    CancellationToken cancellationToken) =>
{
    ValidateLifecycleRequest(request);
    return CreateLifecycleSubmissionResult(await worker.SubmitStopAsync(request, cancellationToken));
});
app.MapPost("/api/host/v1/runtime/restart", async (
    HostLifecycleRequest request,
    RuntimeWorkerCoordinator worker,
    CancellationToken cancellationToken) =>
{
    ValidateLifecycleRequest(request);
    return CreateLifecycleSubmissionResult(await worker.SubmitRestartAsync(request, cancellationToken));
});
app.MapPost("/api/host/v1/shutdown", (HttpContext context, RuntimeWorkerCoordinator worker) =>
{
    if (string.Equals(context.Request.Query["mode"], "uninstall", StringComparison.Ordinal))
    {
        worker.PrepareForFastShutdown();
    }
    context.Response.OnCompleted(() =>
    {
        app.Lifetime.StopApplication();
        return Task.CompletedTask;
    });
    return Results.NoContent();
});
app.MapPost("/api/v1/system/shutdown", async (HttpContext context, RuntimeWorkerCoordinator worker) =>
{
    await worker.EnsureStoppedDurablyAsync(context.RequestAborted);
    return Results.NoContent();
});
app.MapPost("/api/v1/system/reset/prepare", (HostRuntimeResetChallengeService challenges) =>
{
    var challenge = challenges.Create();
    return Results.Ok(new RuntimeResetChallengeResponse(challenge.Challenge, challenge.ExpiresAtUtc));
});
app.MapPost("/api/v1/system/reset/drain", async (
    RuntimeResetConfirmRequest request,
    HostRuntimeResetChallengeService challenges,
    RuntimeWorkerCoordinator worker,
    CancellationToken cancellationToken) =>
{
    if (!challenges.TryConsume(request.Challenge))
    {
        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Runtime state conflict",
            detail: "The Runtime reset confirmation challenge is missing, expired, or already used.",
            extensions: new Dictionary<string, object?> { ["code"] = "runtime.v1.conflict" });
    }

    await worker.EnsureStoppedDurablyAsync(cancellationToken);
    return Results.Ok(new RuntimeResetDrainResponse(
        RuntimeV1StateDescriptor.ResetCategories.Select(static category => category.Id).ToArray()));
});

var forwardedMethods = new[] { "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS" };
app.MapMethods("/api/handshake", ["GET"], (HttpContext context, RuntimeGateway gateway) => gateway.ForwardAsync(context));
app.MapMethods("/api/v1/{**path}", forwardedMethods, (HttpContext context, RuntimeGateway gateway) => gateway.ForwardAsync(context))
    .WithMetadata(new DisableRequestSizeLimitAttribute());

RuntimeConnectionInfo? externalConnection = null;
var workerCoordinator = app.Services.GetRequiredService<RuntimeWorkerCoordinator>();
try
{
    await app.StartAsync();
    var addresses = app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()?.Addresses;
    var boundAddress = addresses is { Count: 1 }
        ? addresses.Single()
        : throw new InvalidOperationException("The Sunder Host did not publish exactly one listener address.");
    externalConnection = new RuntimeConnectionInfo(new Uri(boundAddress), externalToken);
    RuntimeConnectionInfoStore.Save(externalConnection, externalConnectionPath);

    try
    {
        await workerCoordinator.ReconcileStartupAsync(app.Lifetime.ApplicationStopping);
    }
    catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
    }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "Sunder Host started without a ready Runtime worker");
    }
    await app.WaitForShutdownAsync();
}
finally
{
    if (externalConnection is not null)
    {
        RuntimeConnectionInfoStore.DeleteIfMatches(externalConnection, externalConnectionPath);
    }
}

static HostProductVersionDiagnostics CreateProductDiagnostics()
{
    var assembly = Assembly.GetExecutingAssembly();
    var name = assembly.GetName();
    var productVersion = FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion;
    var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    return new HostProductVersionDiagnostics(
        name.Name ?? "Sunder.Host.Supervisor",
        string.IsNullOrWhiteSpace(productVersion) ? "Development" : productVersion,
        string.IsNullOrWhiteSpace(informationalVersion) ? "Development" : informationalVersion);
}

static void ValidateLifecycleRequest(HostLifecycleRequest request)
{
    if (request.MutationId == Guid.Empty || request.ExpectedDeploymentGeneration < 0)
    {
        throw new BadHttpRequestException("A mutation ID and non-negative deployment generation are required.");
    }
}

static IResult CreateLifecycleSubmissionResult(HostLifecycleSubmission submission)
{
    var location = $"/api/host/v1/operations/{Uri.EscapeDataString(submission.Operation.OperationId)}";
    return Results.Accepted(location, submission);
}
