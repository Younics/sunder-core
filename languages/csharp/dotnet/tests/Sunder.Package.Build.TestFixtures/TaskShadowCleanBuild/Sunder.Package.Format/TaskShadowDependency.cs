namespace Sunder.Package.Build.TestFixtures.TaskShadow;

public static class TaskShadowDependency
{
    public static string Value
        =>
#if TASK_SHADOW_FIRST
            "first";
#elif TASK_SHADOW_SECOND
            "second";
#else
            "format-ready";
#endif
}
