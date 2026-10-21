namespace Sunder.Sdk.Compatibility;

/// <summary>Defines versioned host capability identifiers emitted into package manifests.</summary>
public static class SunderSdkCapabilities
{
    /// <summary>Clean-break Sunder SDK 1.1 contract baseline.</summary>
    public const string Baseline11V1 = "sdk-baseline-1-1.v1";
    /// <summary>Core lifecycle contracts.</summary>
    public const string CoreV1 = "core.v1";
    /// <summary>Packaging metadata contracts.</summary>
    public const string PackagingV1 = "packaging.v1";
    /// <summary>Contribution registry contracts.</summary>
    public const string ContributionsV1 = "contributions.v1";
    /// <summary>Package view contracts.</summary>
    public const string ViewsV1 = "views.v1";
    /// <summary>Settings view contracts.</summary>
    public const string SettingsViewsV1 = "settings-views.v1";
    /// <summary>Settings navigation contracts.</summary>
    public const string SettingsNavigationV1 = "settings-navigation.v1";
    /// <summary>App activation-owned development overlay sessions.</summary>
    public const string PackageDevelopmentV1 = "package-development.v1";
    /// <summary>Nullable, Host-source-bound observations of Runtime and applied development revisions.</summary>
    public const string PackageDevelopmentRevisionsV1 = "package-development-revisions.v1";
    /// <summary>App observations of Host-owned cleanup evidence for exact worker executions.</summary>
    public const string WorkerExecutionObservationsV1 = "worker-execution-observations.v1";
    /// <summary>User navigation to package-filtered developer logs.</summary>
    public const string PackageLogNavigationV1 = "package-log-navigation.v1";
    /// <summary>Host-owned accepted App state work and pre-retirement settlement.</summary>
    public const string AppStateUpdatesV1 = "app-state-updates.v1";
    /// <summary>Activation-scoped worker background service contracts.</summary>
    public const string BackgroundServicesV1 = "background-services.v1";
    /// <summary>Staged worker generation lifecycle contracts.</summary>
    public const string RuntimeGenerationsV1 = "runtime-generations.v1";
    /// <summary>Background process contracts.</summary>
    public const string BackgroundProcessesV1 = "background-processes.v1";
    /// <summary>Host-rendered settings schema contracts.</summary>
    public const string SettingsSchemaV1 = "settings.schema.v1";
    /// <summary>Writable package settings contracts.</summary>
    public const string SettingsV1 = "settings.v1";
    /// <summary>Package storage contracts.</summary>
    public const string StorageV1 = "storage.v1";
    /// <summary>Portable physical-key derivation and host-atomic key migration contracts.</summary>
    public const string StorageKeyMigrationV1 = "storage.key-migration.v1";
    /// <summary>Activation-owned role-local workspace contracts.</summary>
    public const string RoleLocalWorkspaceV1 = "role-local-workspace.v1";
    /// <summary>Consent-gated propagation of allowlisted Docker CLI connection variables to Runtime workers.</summary>
    public const string DockerEnvironmentV1 = "docker-environment.v1";
    /// <summary>Secret storage contracts.</summary>
    public const string SecretsV1 = "secrets.v1";
    /// <summary>Logging contracts.</summary>
    public const string LoggingV1 = "logging.v1";
    /// <summary>Notification contracts.</summary>
    public const string NotificationsV1 = "notifications.v1";
    /// <summary>Shell view contracts.</summary>
    public const string ShellViewV1 = "shell-view.v1";
    /// <summary>Hidden package-view navigation preparation and presentation acknowledgement contracts.</summary>
    public const string ViewNavigationPreparationV1 = "view-navigation-preparation.v1";
    /// <summary>Canonical schema-first RPC contracts, discovery, invocation, and provider registration.</summary>
    public const string RpcV1 = "rpc.v1";
    /// <summary>Capability-gated support for caller-requested RPC deadlines beyond the Host default.</summary>
    public const string RpcExtendedDeadlineV1 = "rpc.extended-deadline.v1";
    /// <summary>Stack data contracts.</summary>
    public const string StacksV1 = "stacks.v1";
    /// <summary>Stack RPC provider and adapter contracts.</summary>
    public const string StacksRpcV1 = "stacks.rpc.v1";
    /// <summary>Callback contracts.</summary>
    public const string CallbacksV1 = "callbacks.v1";
    /// <summary>Authentication contracts.</summary>
    public const string AuthV1 = "auth.v1";
    /// <summary>Avalonia theming contracts.</summary>
    public const string ThemingV1 = "theming.v1";
}
