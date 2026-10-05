using System.Collections;
using Microsoft.Build.Framework;

namespace Sunder.Package.Build.Tasks;

/// <summary>Coordinates package operations without blocking MSBuild's dependency scheduler.</summary>
public sealed class ManageSunderPackageOperationLockTask : Microsoft.Build.Utilities.Task
{
    [Required] public string Action { get; set; } = string.Empty;
    [Required] public string ProjectDirectory { get; set; } = string.Empty;
    public string WaitProject { get; set; } = string.Empty;
    public ITaskItem[] OutputDirectories { get; set; } = [];
    [Output] public string LeaseId { get; set; } = string.Empty;

    public override bool Execute()
    {
        // Only operation coordination runs in the main MSBuild process. Compiler
        // tasks and their RPC-plan leases retain the isolated task host. Old task
        // hosts cannot make build callbacks (MSB5022), and their registered lease
        // objects cannot be used as proof by tasks in a different process.
        var task = new ManageSunderRpcGeneratedOutputLockTask
        {
            BuildEngine = BuildEngine, Action = Action, ProjectDirectory = ProjectDirectory,
            OutputDirectories = OutputDirectories, LeaseId = LeaseId, WaitBeforeRetry = WaitForBuildNode,
        };
        var result = task.Execute();
        LeaseId = task.LeaseId;
        return result;
    }

    private void WaitForBuildNode()
    {
        var engine = BuildEngine as IBuildEngine6
            ?? throw new InvalidOperationException("MSBuild does not expose scheduler-aware generated-output lock waiting.");
        // Yielded requests still count against the parallel-work budget. A short
        // dependency makes this contender blocked instead, allowing the holder's
        // nested requests to run. A fresh identity prevents caching the delay.
        var project = Path.GetFullPath(WaitProject, ProjectDirectory);
        var properties = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["_SunderPackageBuildTaskSourceLockWaitId"] = Guid.NewGuid().ToString("N"),
            ["_SunderPackageBuildTaskSourceLockWaitPath"] = project,
            ["SunderPackageBuildTaskSourceLockOwner"] = "false",
        };
        var remove = engine.GetGlobalProperties().Keys.Where(name => !properties.Contains(name)).ToArray();
        var result = engine.BuildProjectFilesInParallel([project], ["_WaitForSunderPackageBuildTaskSourceLock"],
            [properties], [remove], [null!], returnTargetOutputs: false);
        if (!result.Result) throw new InvalidOperationException("The generated-output lock wait dependency failed.");
    }
}
