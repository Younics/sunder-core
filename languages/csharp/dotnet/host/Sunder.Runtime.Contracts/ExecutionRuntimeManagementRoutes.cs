namespace Sunder.Runtime.Contracts;

public static partial class RuntimeApiRoutes
{
    public const string ExecutionRuntimeSettings = ExecutionRuntimes + "/settings";
    public const string ExecutionRuntimeUpdateStatus = ExecutionRuntimes + "/updates/status";
    public const string ExecutionRuntimeCatalog = ExecutionRuntimes + "/catalog";
    public const string ExecutionRuntimeInstallPreview = ExecutionRuntimes + "/install/preview";
    public const string ExecutionRuntimeInstallApply = ExecutionRuntimes + "/install/apply";
    public const string ExecutionRuntimeUpdatePreview = ExecutionRuntimes + "/updates/preview";
    public const string ExecutionRuntimeUpdatePrepare = ExecutionRuntimes + "/updates/prepare";
    public const string ExecutionRuntimeUpdateApply = ExecutionRuntimes + "/updates/apply";
    public const string ExecutionRuntimeUninstallPreview = ExecutionRuntimes + "/uninstall/preview";
    public const string ExecutionRuntimeUninstallApply = ExecutionRuntimes + "/uninstall/apply";
    public const string PackageRuntimePrerequisites = Root + "/packages/runtime-prerequisites";
}
