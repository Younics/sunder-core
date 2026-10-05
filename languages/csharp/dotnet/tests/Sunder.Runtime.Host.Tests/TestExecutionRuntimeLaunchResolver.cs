using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Sunder.Package.Projections;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
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
            [SunderExecutionRuntimeFormat.DotNetExecArgument, Path.GetFullPath(package.EntryAssemblyPath)],
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

        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(new ResolvedEphemeralExecutionRuntimeBinding(
            new RuntimePackageExecutionRuntimeBinding(
            targetKey.Role,
            targetKey.Rid,
            requirement.Id!,
            runtimeVersion,
            1,
            requirement.ModuleAbi!,
            new string('a', 64),
            new string('b', 64),
            new FileInfo(_launcherPath).Length,
            "https://test.invalid/",
            Sunder.Runtime.Contracts.RuntimeExecutionRuntimeAcquisitionSources.Development,
            "sunder.test.runtime.v1",
            1,
            1,
            "supported",
            now,
            now)));
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

internal sealed class TestLifecyclePolicyExecutionRuntimeLaunchResolver(
    ExecutionRuntimeLifecyclePolicyCache lifecyclePolicies,
    TimeProvider timeProvider) : IExecutionRuntimeLaunchResolver
{
    public async Task<ResolvedExecutionRuntimeLaunch> ResolveAsync(
        PreparedRuntimePackage package,
        Guid activationId,
        CancellationToken cancellationToken)
    {
        var binding = package.ExecutionRuntimeBinding
                      ?? throw new InvalidDataException(
                          $"Test package '{package.PackageId}' does not carry an exact execution-runtime binding.");
        await lifecyclePolicies.InitializeAsync(cancellationToken);
        var policy = package.ExecutionRuntimePolicyAuthority;
        if (policy is null)
        {
            lifecyclePolicies.CaptureSnapshot().TryGet(binding.Identity, out policy);
        }
        if (policy is not null
            && !policy.PermitsActivation(timeProvider.GetUtcNow()))
        {
            throw new InvalidDataException(
                $"Test package '{package.PackageId}' is blocked by its execution-runtime lifecycle policy.");
        }
        return await TestExecutionRuntimeLaunchResolver.Instance.ResolveAsync(
            package,
            activationId,
            cancellationToken);
    }
}

internal static class TestExecutionRuntimeServiceCollectionExtensions
{
    public static IServiceCollection UseTestExecutionRuntimeLaunchResolver(
        this IServiceCollection services,
        bool enforceLifecyclePolicy = false,
        IExecutionRuntimeLaunchResolver? launchResolver = null)
    {
        services.RemoveAll<LocalExecutionRuntimeBindingResolver>();
        services.RemoveAll<PackageSessionLoadService>();
        services.AddSingleton(provider => new PackageSessionLoadService(
            provider.GetRequiredService<ILogger<PackageSessionLoadService>>(),
            provider.GetRequiredService<RuntimePackagePaths>(),
            rpcBroker: provider.GetRequiredService<RuntimeRpcBroker>(),
            processPolicy: provider.GetRequiredService<RuntimeProcessPolicyOptions>(),
            hostStopping: provider.GetService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>()?.ApplicationStopping
                           ?? CancellationToken.None,
            packageSessionState: provider.GetRequiredService<PackageSessionState>(),
            executionRuntimeLaunchResolver: launchResolver ?? (enforceLifecyclePolicy
                ? new TestLifecyclePolicyExecutionRuntimeLaunchResolver(
                    provider.GetRequiredService<ExecutionRuntimeLifecyclePolicyCache>(),
                    provider.GetRequiredService<TimeProvider>())
                : TestExecutionRuntimeLaunchResolver.Instance),
            ephemeralExecutionRuntimeBindingResolver: TestExecutionRuntimeLaunchResolver.Instance));
        services.RemoveAll<RegistryExecutionRuntimeBindingPreparer>();
        services.AddSingleton(provider => new RegistryExecutionRuntimeBindingPreparer(
            static (origin, artifact, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                const string launcherPath = "payload/bin/test-runtime";
                var receipt = new ExecutionRuntimeReceiptV1(
                    ExecutionRuntimeReceiptV1.CurrentSchemaVersion,
                    new ExecutionRuntimeIdentity(
                        artifact.Identity.RuntimeId,
                        artifact.Identity.Version,
                        artifact.Identity.PackagingRevision,
                        artifact.Identity.Rid,
                        artifact.Identity.ModuleAbi),
                    SunderExecutionRuntimeFormat.WorkerProtocol,
                    artifact.ArtifactSha256,
                    artifact.ArtifactSize,
                    artifact.ManifestSha256,
                    new string('d', 64),
                    launcherPath,
                    new string('e', 64),
                    1,
                    [SunderExecutionRuntimeFormat.DotNetExecArgument, SunderExecutionRuntimeFormat.PackageEntryPointPlaceholder],
                    [new ExecutionRuntimeReceiptContentV1(launcherPath, new string('e', 64), 1)],
                    artifact.NativeAttestationSha256,
                    artifact.NativeAttestationKeyId,
                    artifact.SigningKeyId,
                    Math.Max(artifact.MinimumHostTrustGeneration, 1),
                    ExecutionRuntimeTrustEnvironment.Production,
                    RuntimeExecutionRuntimeAcquisitionSources.Registry,
                    origin.AbsoluteUri,
                    DateTimeOffset.UtcNow);
                return Task.FromResult(new ExecutionRuntimeEnsureResult(receipt, AlreadyPresent: false));
            },
            provider.GetRequiredService<IExecutionRuntimeRetentionManager>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ExecutionRuntimeLifecyclePolicyCache>()));
        return services;
    }
}
