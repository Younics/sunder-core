using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Sunder.Package.Projections;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Packaging;

namespace Sunder.Runtime.Host.Services;

internal sealed class TestExecutionRuntimeLaunchResolver :
    IExecutionRuntimeLaunchResolver,
    ITestOnlyInstalledExecutionRuntimeBindingResolver
{
    public static TestExecutionRuntimeLaunchResolver Instance { get; } = new();

    private readonly string _runtimeRoot;
    private readonly string _launcherPath;

    private TestExecutionRuntimeLaunchResolver()
    {
        var runtimeVersionRoot = Directory.GetParent(typeof(object).Assembly.Location)
                                 ?? throw new DirectoryNotFoundException("The loaded .NET runtime root is unavailable.");
        _runtimeRoot = runtimeVersionRoot.Parent?.Parent?.Parent?.FullName
                       ?? throw new DirectoryNotFoundException("The loaded .NET installation root is unavailable.");
        _launcherPath = Path.Combine(_runtimeRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (!File.Exists(_launcherPath))
        {
            throw new FileNotFoundException("The exact loaded .NET installation launcher is unavailable.", _launcherPath);
        }
    }

    internal string LauncherPath => _launcherPath;

    internal string RuntimeRootPath => _runtimeRoot;

    internal static RuntimePackageExecutionRuntimeBinding CreateBinding(string role, string rid)
    {
        var now = DateTimeOffset.UtcNow;
        var requirement = new RuntimeExecutionRuntimeRequirement(
            SunderPackageFormat.DotNetExecutionRuntimeId,
            SunderPackageFormat.DotNetExecutionRuntimeVersionRange,
            SunderPackageFormat.DotNetWorkerModuleAbi);
        var identity = new RuntimeExecutionRuntimeIdentity(
            ConfiguredOfficialSourceId.DotNet,
            requirement.RuntimeId,
            "10.0.0",
            rid,
            requirement.ModuleAbi,
            RuntimeExecutionRuntimeChecksumAlgorithm.Sha512,
            new string('a', 128));
        var distribution = new RuntimeExecutionRuntimeDistribution(
            $"https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.0/{OfficialRuntimeSourcePolicy.ArchiveName(ConfiguredOfficialSourceId.DotNet, "10.0.0", rid)}",
            OfficialRuntimeSourcePolicy.DotNetMetadata,
            new string('b', 64),
            null,
            null,
            rid.StartsWith("win-", StringComparison.Ordinal) ? RuntimeExecutionRuntimeArchiveFormat.Zip : RuntimeExecutionRuntimeArchiveFormat.TarGzip,
            OfficialRuntimeSourcePolicy.Profile(ConfiguredOfficialSourceId.DotNet),
            1);
        return new RuntimePackageExecutionRuntimeBinding(
            role,
            rid,
            requirement,
            identity,
            new RuntimeExecutionRuntimeInstalledArtifact(
                new string('c', 64),
                1,
                new string('d', 64),
                new RuntimeExecutionRuntimeProvenance(distribution, distribution.DownloadUrl, now)),
            now);
    }

    public Task<ResolvedExecutionRuntimeLaunch> ResolveAsync(
        PreparedRuntimePackage package,
        Guid activationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = package.SelectedTargetKey;
        var target = package.SelectedTarget;
        if (key is null
            || !string.Equals(key.Value.Role, SunderPackageFormat.RuntimeHostRole, StringComparison.Ordinal)
            || !string.Equals(key.Value.Rid, PackageTargetSelection.GetCurrentRuntimeIdentifier(), StringComparison.Ordinal)
            || package.EntryAssemblyPath is null
            || !package.EntryAssemblyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(package.EntryAssemblyPath))
        {
            throw new InvalidDataException(
                $"Test package '{package.PackageId}' does not declare the exact current-RID .NET execution-runtime target.");
        }

        var launch = new ResolvedExecutionRuntimeLaunch(
            _runtimeRoot,
            _launcherPath,
            ["exec", Path.GetFullPath(package.EntryAssemblyPath)],
            new TestExecutionRuntimeProcessLease(_runtimeRoot),
            package.ExecutionRuntimeBinding?.Identity);
        return Task.FromResult(launch);
    }

    public Task<ResolvedEphemeralExecutionRuntimeBinding> ResolveEphemeralBindingAsync(
        SunderPackageTargetKey targetKey,
        SunderPackageTargetManifest target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requirement = target.ExecutionRuntime
                          ?? throw new InvalidDataException("A test Runtime target is missing its execution-runtime requirement.");
        var loadedVersion = typeof(object).Assembly.GetName().Version?.ToString(3) ?? "10.0.0";
        var runtimeVersion = PackageVersionRange.IsSatisfiedBy(loadedVersion, requirement.VersionRange!)
            ? loadedVersion
            : PackageVersionRange.IsSatisfiedBy("1.0.0", requirement.VersionRange!)
                ? "1.0.0"
                : throw new InvalidDataException(
                    "The shared test resolver cannot synthesize a version for the target runtime range.");
        if (target.EntryPoint is null || !target.EntryPoint.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The shared test resolver only launches managed Worker DLL fixtures.");
        }

        return Task.FromResult(new ResolvedEphemeralExecutionRuntimeBinding(
            CreateBinding(targetKey.Role, targetKey.Rid)));
    }
}

internal sealed class TestExecutionRuntimeProcessLease(string runtimeRootPath)
    : IExecutionRuntimeProcessLease
{
    private Process? _process;
    private bool _released;

    public string LeaseId { get; } = Guid.NewGuid().ToString("N");

    public string RuntimeRootPath { get; } = Path.GetFullPath(runtimeRootPath);

    public bool Released => _released;

    public int? ProcessId { get; private set; }

    public bool ProcessExitedAtRelease { get; private set; }

    public async Task<IExecutionRuntimeContainedProcess> LaunchAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken = default,
        PackageProjectionAcquisition? packageProjection = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_released, this);
        ArgumentNullException.ThrowIfNull(startInfo);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The test Worker process did not start.");
        }
        _process = process;
        ProcessId = process.Id;
        try
        {
            if (packageProjection is not null)
            {
                await packageProjection.AttachToProcessTreeAsync(
                    [new PackageProjectionProcessIdentity(
                        process.Id,
                        ExecutionRuntimeProcessBirthIdentity.Capture(process),
                        new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero))],
                    cancellationToken);
            }
            return new TestContainedProcess(process);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            throw;
        }
    }

    public Task ReleaseAfterProcessTreeTerminationAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_process is { HasExited: false })
        {
            throw new InvalidOperationException("The test Worker process is still running.");
        }
        ProcessExitedAtRelease = _process is not null;
        _released = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("An attached test lease requires termination proof.");
        }
        _released = true;
        return ValueTask.CompletedTask;
    }

    private sealed class TestContainedProcess(Process process) : IExecutionRuntimeContainedProcess
    {
        private readonly object _gate = new();
        private Task? _termination;

        public Process Process => process;
        public Stream StandardInput => process.StandardInput.BaseStream;
        public Stream StandardOutput => process.StandardOutput.BaseStream;
        public Stream StandardError => process.StandardError.BaseStream;

        public Task TerminateAndDrainAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return _termination ??= TerminateCoreAsync(cancellationToken);
            }
        }

        private async Task TerminateCoreAsync(CancellationToken cancellationToken)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public ValueTask DisposeAsync()
        {
            process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal static class TestExecutionRuntimeServiceCollectionExtensions
{
    public static IServiceCollection UseTestExecutionRuntimeLaunchResolver(
        this IServiceCollection services,
        IExecutionRuntimeLaunchResolver? launchResolver = null)
    {
        // This fixture replaces installed runtime authority with a synthetic launcher and
        // receipt factory. Official runtime source/store coverage lives in dedicated tests.
        services.RemoveAll<PackageRuntimePrerequisiteService>();
        services.RemoveAll<ExecutionRuntimeActivationUpdates>();
        foreach (var descriptor in services.Where(value => value.ImplementationType == typeof(ExecutionRuntimeUpdateScheduler)).ToArray())
            services.Remove(descriptor);
        services.RemoveAll<LocalExecutionRuntimeBindingResolver>();
        services.RemoveAll<IHostExecutionRuntimeBindingPreparer>();
        services.AddSingleton<IHostExecutionRuntimeBindingPreparer>(new PackageOnlyRuntimeBindingPreparer());
        services.RemoveAll<PackageSessionLoadService>();
        services.AddSingleton(provider => new PackageSessionLoadService(
            provider.GetRequiredService<ILogger<PackageSessionLoadService>>(),
            provider.GetRequiredService<RuntimePackagePaths>(),
            rpcBroker: provider.GetRequiredService<RuntimeRpcBroker>(),
            processPolicy: provider.GetRequiredService<RuntimeProcessPolicyOptions>(),
            hostStopping: provider.GetService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>()?.ApplicationStopping
                           ?? CancellationToken.None,
            packageSessionState: provider.GetRequiredService<PackageSessionState>(),
            executionRuntimeLaunchResolver: launchResolver ?? TestExecutionRuntimeLaunchResolver.Instance,
            ephemeralExecutionRuntimeBindingResolver: TestExecutionRuntimeLaunchResolver.Instance));
        return services;
    }

    // The package orchestration fixture substitutes only runtime authority. Real source,
    // receipt, prerequisite and native launch behavior is exercised by the official-runtime suites.
    private sealed class PackageOnlyRuntimeBindingPreparer : IHostExecutionRuntimeBindingPreparer
    {
        public Task<PreparedExecutionRuntimeBindings> PrepareAsync(IReadOnlyList<RegistryPackagePlanResolution> contexts,
            IReadOnlyDictionary<string, InstalledPackageRecord> installed, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var result = new Dictionary<string, IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in contexts.SelectMany(context => context.Plan.Items))
            {
                if (item.ActionKind == Sunder.Registry.Contracts.RegistryPackageActionKinds.Keep)
                {
                    result[item.PackageId] = installed.TryGetValue(item.PackageId, out var existing) ? existing.ExecutionRuntimeBindings : [];
                    continue;
                }
                result[item.PackageId] = item.Targets.Where(target => target.ExecutionRuntime is not null
                    && item.Artifacts.Any(artifact => artifact.Kind == target.Role && artifact.Rid == target.Rid)).Select(target =>
                {
                    var binding = TestExecutionRuntimeLaunchResolver.CreateBinding(target.Role, target.Rid);
                    var requirement = target.ExecutionRuntime!;
                    if (requirement.RuntimeId != binding.Runtime.RuntimeId || requirement.ModuleAbi != binding.Runtime.ModuleAbi)
                        throw new InvalidDataException("The package-only .NET fixture does not emulate another runtime family.");
                    return new InstalledPackageExecutionRuntimeBindingRecord(target.Role, target.Rid,
                        new(requirement.RuntimeId, requirement.VersionRange, requirement.ModuleAbi), binding.Runtime, binding.InstalledArtifact, binding.ResolvedAtUtc);
                }).ToArray();
            }
            return Task.FromResult(new PreparedExecutionRuntimeBindings(result, []));
        }
    }
}
