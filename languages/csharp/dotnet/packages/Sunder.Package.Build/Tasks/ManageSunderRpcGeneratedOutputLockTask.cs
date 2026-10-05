using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Sunder.Package.Build.Tasks;

public sealed class ManageSunderRpcGeneratedOutputLockTask : Microsoft.Build.Utilities.Task
{
    private const string LeaseKeyPrefix = "sunder-rpc-generated-output-lock:";

    [Required]
    public string Action { get; set; } = string.Empty;

    [Required]
    public string ProjectDirectory { get; set; } = string.Empty;

    public ITaskItem[] OutputDirectories { get; set; } = [];

    internal System.Action? WaitBeforeRetry { get; init; }

    [Output]
    public string LeaseId { get; set; } = string.Empty;

    public override bool Execute()
    {
        try
        {
            var buildEngine = BuildEngine as IBuildEngine4
                              ?? throw new InvalidOperationException(
                                  "MSBuild does not expose build-lifetime task object registration.");
            if (string.Equals(Action, "Acquire", StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(LeaseId))
                {
                    throw new InvalidOperationException("An RPC generated-output lock lease is already assigned.");
                }

                var paths = ResolvePaths(ProjectDirectory, OutputDirectories);
                var outputLock = GeneratedOutputLock.Acquire(
                    paths.Select(static path => GeneratedOutputLock.TargetLeafDiscoveryKey(
                        path.DiscoveryRoot,
                        path.OutputDirectory)),
                    paths.Select(static path => path.OutputDirectory),
                    waitBeforeRetry: WaitBeforeRetry);
                var lease = new Lease(outputLock, paths.Select(static path => path.OutputDirectory));
                var leaseId = Guid.NewGuid().ToString("N");
                try
                {
                    buildEngine.RegisterTaskObject(
                        LeaseKeyPrefix + leaseId,
                        lease,
                        RegisteredTaskObjectLifetime.Build,
                        allowEarlyCollection: false);
                }
                catch
                {
                    lease.Dispose();
                    throw;
                }

                LeaseId = leaseId;
                return true;
            }

            if (string.Equals(Action, "Release", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(LeaseId)) return true;
                var lease = buildEngine.UnregisterTaskObject(
                    LeaseKeyPrefix + LeaseId,
                    RegisteredTaskObjectLifetime.Build) as IDisposable;
                if (lease is null)
                {
                    throw new InvalidOperationException(
                        $"RPC generated-output lock lease '{LeaseId}' is not registered in this build.");
                }
                lease.Dispose();
                LeaseId = string.Empty;
                return true;
            }

            if (string.Equals(Action, "Retain", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(LeaseId))
                {
                    throw new InvalidOperationException("An RPC generated-output lock lease is required for Retain.");
                }
                if (buildEngine.GetRegisteredTaskObject(
                        LeaseKeyPrefix + LeaseId,
                        RegisteredTaskObjectLifetime.Build) is not Lease lease)
                {
                    throw new InvalidOperationException(
                        $"RPC generated-output lock lease '{LeaseId}' is not registered in this build.");
                }
                lease.Retain(ResolvePaths(ProjectDirectory, OutputDirectories)
                    .Select(static path => path.OutputDirectory));
                return true;
            }

            throw new InvalidDataException($"Unsupported RPC generated-output lock action '{Action}'.");
        }
        catch (Exception exception)
        {
            Log.LogErrorFromException(exception, showStackTrace: false);
            return false;
        }
    }

    internal static bool Covers(IBuildEngine buildEngine, string leaseId, IEnumerable<string> outputDirectories)
    {
        if (buildEngine is not IBuildEngine4 buildEngine4 || string.IsNullOrWhiteSpace(leaseId)) return false;
        return buildEngine4.GetRegisteredTaskObject(
                   LeaseKeyPrefix + leaseId,
                   RegisteredTaskObjectLifetime.Build) is Lease lease
               && lease.Covers(outputDirectories);
    }

    private static IReadOnlyList<OutputPath> ResolvePaths(
        string projectDirectory,
        IEnumerable<ITaskItem> outputDirectories)
    {
        var project = Path.GetFullPath(projectDirectory);
        return outputDirectories.Select(item =>
            {
                var discoveryRoot = item.GetMetadata("DiscoveryRoot");
                if (string.IsNullOrWhiteSpace(discoveryRoot))
                {
                    throw new InvalidDataException(
                        $"RPC generated-output directory '{item.ItemSpec}' is missing DiscoveryRoot metadata.");
                }
                return new OutputPath(
                    GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(
                        Path.GetFullPath(item.ItemSpec, project)),
                    GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(
                        Path.GetFullPath(discoveryRoot, project)));
            })
            .DistinctBy(static path => path.OutputDirectory, PathComparer)
            .OrderBy(static path => path.OutputDirectory, PathComparer)
            .ToArray();
    }

    private sealed class Lease : IDisposable
    {
        private readonly GeneratedOutputLock _outputLock;
        private readonly HashSet<string> _outputDirectories;

        public Lease(GeneratedOutputLock outputLock, IEnumerable<string> outputDirectories)
        {
            _outputLock = outputLock;
            _outputDirectories = outputDirectories
                .Select(GeneratedOutputPathSafety.NormalizeTrustedSystemAliases)
                .ToHashSet(PathComparer);
        }

        public bool Covers(IEnumerable<string> outputDirectories)
            => outputDirectories
                .Select(GeneratedOutputPathSafety.NormalizeTrustedSystemAliases)
                .All(_outputDirectories.Contains);

        public void Retain(IEnumerable<string> outputDirectories)
        {
            var retained = outputDirectories
                .Select(GeneratedOutputPathSafety.NormalizeTrustedSystemAliases)
                .ToHashSet(PathComparer);
            if (!retained.IsSubsetOf(_outputDirectories))
            {
                throw new InvalidOperationException(
                    "Cannot retain an RPC generated-output directory that is not covered by this lease.");
            }
            _outputLock.Retain(retained);
            _outputDirectories.IntersectWith(retained);
        }

        public void Dispose() => _outputLock.Dispose();
    }

    private sealed record OutputPath(string OutputDirectory, string DiscoveryRoot);

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
