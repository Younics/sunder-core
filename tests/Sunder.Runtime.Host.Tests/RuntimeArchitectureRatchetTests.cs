using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Endpoints;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeArchitectureRatchetTests
{
    [Fact]
    public void RuntimeProductionFiles_StayWithinFamilySizeRatchet()
    {
        var root = LocateRepositoryRoot();
        var runtimeRoot = Path.Combine(root, "src", "Host", "Sunder.Runtime.Host");
        var offenders = Directory.EnumerateFiles(runtimeRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .Select(path => new { Path = path, Lines = File.ReadLines(path).Count() })
            .Where(file => file.Lines > 900
                           || (file.Path.Contains($"{Path.DirectorySeparatorChar}Services{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                               || file.Path.Contains($"{Path.DirectorySeparatorChar}Endpoints{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                           && file.Lines > 700)
            .Select(file => $"{Path.GetRelativePath(root, file.Path)}: {file.Lines}")
            .ToArray();

        Assert.Empty(offenders);
        Assert.True(File.ReadLines(Path.Combine(runtimeRoot, "Services", "RuntimePackageSessionService.cs")).Count() < 200);
    }

    [Fact]
    public void RuntimeEndpoints_DependOnFocusedServicesInsteadOfSessionFacadeOrStateOwners()
    {
        var endpointRoot = Path.Combine(LocateRepositoryRoot(), "src", "Host", "Sunder.Runtime.Host", "Endpoints");
        var source = string.Join('\n', Directory.EnumerateFiles(endpointRoot, "*.cs").Select(File.ReadAllText));

        Assert.DoesNotContain(nameof(RuntimePackageSessionService), source, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(RuntimeSessionOwner), source, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(PackageSessionState), source, StringComparison.Ordinal);
        Assert.Contains(nameof(PackageSessionLifecycleService), source, StringComparison.Ordinal);
        Assert.Contains(nameof(InstalledPackageLifecycleService), source, StringComparison.Ordinal);
        Assert.Contains(nameof(RuntimeStackExportService), source, StringComparison.Ordinal);
        Assert.Contains(nameof(RuntimeStackImportService), source, StringComparison.Ordinal);
        Assert.Contains(nameof(PackageConfigurationAccessService), source, StringComparison.Ordinal);
        Assert.Contains(nameof(PackageAuthAccessService), source, StringComparison.Ordinal);
        Assert.Contains(nameof(PackageFaultService), source, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeSessionGeneration_HasOneStateOwner()
    {
        var servicesRoot = Path.Combine(LocateRepositoryRoot(), "src", "Host", "Sunder.Runtime.Host", "Services");
        var owners = Directory.EnumerateFiles(servicesRoot, "*.cs")
            .Where(path => File.ReadAllText(path).Contains("private long _generation;", StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path)!)
            .ToArray();

        Assert.Equal(new[] { "PackageSessionState.cs" }, owners);
        Assert.Single(typeof(PackageSessionState).GetMethods(BindingFlags.Instance | BindingFlags.Public), method => method.Name == nameof(PackageSessionState.PublishSession));
    }

    [Fact]
    public void RuntimeContracts_RemainHostAndSdkNeutral()
    {
        var references = typeof(SystemStatusResponse).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();

        Assert.DoesNotContain(references, name => name is not null
            && (name.StartsWith("Sunder.Sdk", StringComparison.Ordinal)
                || name.StartsWith("Sunder.Package.Format", StringComparison.Ordinal)
                || name.StartsWith("Sunder.Runtime.Host", StringComparison.Ordinal)));
        Assert.DoesNotContain(typeof(SystemStatusResponse).Assembly.ExportedTypes, type =>
            type.GetProperties().Any(property => property.PropertyType.Assembly == typeof(PackageSessionState).Assembly));
    }

    [Fact]
    public void ExistingRuntimeEndpointRoutes_RemainMapped()
    {
        var endpointRoot = Path.Combine(LocateRepositoryRoot(), "src", "Host", "Sunder.Runtime.Host", "Endpoints");
        var source = string.Join('\n', Directory.EnumerateFiles(endpointRoot, "*.cs").Select(File.ReadAllText));
        var stableRoutes = new[]
        {
            "session/load", "session/reload-installed", "session/stage", "store/stage",
            "ui-snapshots", "auth/status", "config/values", "export/items", "import/preview",
            "/downloads/{downloadId}", "/dev-packages/watch", "runtime-events", "package-logs",
        };

        foreach (var route in stableRoutes) Assert.Contains(route, source, StringComparison.Ordinal);
    }

    private static bool IsBuildOutput(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
           || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the Core repository root.");
    }
}

public sealed class RuntimeProblemDetailsTests
{
    [Fact]
    public void ProblemDetailsTaxonomy_MatchesGoldenFixture()
    {
        var root = LocateRepositoryRoot();
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests", "Sunder.Runtime.Host.Tests", "Fixtures", "ProblemDetails.golden.json")));
        var expected = fixture.RootElement.EnumerateArray()
            .Select(item => (item.GetProperty("code").GetString(), item.GetProperty("title").GetString(), item.GetProperty("status").GetInt32()))
            .ToArray();
        var exceptions = new RuntimeException[]
        {
            new RuntimeValidationException("safe"),
            new RuntimeAuthenticationException(),
            new RuntimeConflictException("safe"),
            new RuntimeStaleGenerationException("safe"),
            new RuntimeNotFoundException("safe"),
            new RuntimeUnavailableException("safe"),
            new RuntimeCancellationException(),
            new RuntimeUploadLimitException("safe"),
            new RuntimePackageValidationException("safe"),
        };
        var actual = exceptions.Select(exception => ((string?)exception.Code, (string?)exception.Title, exception.StatusCode))
            .Append(((string?)RuntimeErrorCodes.Internal, (string?)"Internal Runtime error", 500))
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ExpectedClientError_WritesCorrelatedProblemWithoutInternalLog()
    {
        var logger = new CollectingLogger<RuntimeProblemDetailsMiddleware>();
        var middleware = new RuntimeProblemDetailsMiddleware(
            _ => throw new RuntimeValidationException("The request is invalid."),
            logger);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Headers[RuntimeProblemDetailsMiddleware.CorrelationHeader] = "test-correlation";

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var problem = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        Assert.Equal(RuntimeErrorCodes.Validation, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("test-correlation", problem.RootElement.GetProperty("correlationId").GetString());
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task InternalError_DoesNotLeakExceptionOrFilesystemPath()
    {
        var logger = new CollectingLogger<RuntimeProblemDetailsMiddleware>();
        var middleware = new RuntimeProblemDetailsMiddleware(
            _ => throw new InvalidOperationException("secret /private/runtime/path"),
            logger);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/private/runtime/path", body, StringComparison.Ordinal);
        Assert.Single(logger.Entries);
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the Core repository root.");
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<LogLevel> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Entries.Add(logLevel);
    }
}
