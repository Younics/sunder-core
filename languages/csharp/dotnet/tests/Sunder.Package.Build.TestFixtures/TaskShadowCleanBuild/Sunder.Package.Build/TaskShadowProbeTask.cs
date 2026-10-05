using Microsoft.Build.Framework;
using Sunder.Package.Build.TestFixtures.TaskShadow;

namespace Sunder.Package.Build.Tasks;

public sealed class TaskShadowProbeTask : Microsoft.Build.Utilities.Task
{
    [Output]
    public string Result { get; private set; } = string.Empty;

    public override bool Execute()
    {
        Result = TaskShadowDependency.Value;
        return true;
    }
}
