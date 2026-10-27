using Sunder.Package.Format;

namespace Sunder.App.ViewModels;

internal static class PackageFeatureDescriptions
{
    // Reviewed descriptions of Sunder-owned identifiers, not guesses about what
    // third-party code does or a claim that native package code is sandboxed.
    private sealed record Description(string Group, string Title, string Text, bool Sensitive = false, bool Technical = false);
    private static readonly IReadOnlyDictionary<string, Description> Known = new Dictionary<string, Description>(StringComparer.Ordinal)
    {
        ["storage.v1"] = new("storage", "Package storage", "Stores this package’s data through Sunder."),
        ["role-local-workspace.v1"] = new("workspace", "Package workspace", "Uses a persistent workspace owned by this package component."),
        ["secrets.v1"] = new("secrets", "Package secrets", "Uses this package’s secret storage, not other packages’ secrets.", true),
        ["docker-environment.v1"] = new("docker", "Docker connection environment", "Requests allowlisted Docker CLI connection variables for its worker. Worker propagation requires explicit approval.", true),
        ["notifications.v1"] = new("notifications", "Notifications", "Sends notifications through Sunder."),
        ["settings.v1"] = new("settings", "Package settings", "Reads or manages its package settings."),
        ["settings.schema.v1"] = new("settings", "Package settings", "Declares settings for Sunder to display."),
        ["settings-views.v1"] = new("settings-ui", "Settings interface", "Adds its own interface to Settings."),
        ["settings-navigation.v1"] = new("settings-navigation", "Settings navigation", "Can open a Sunder Settings section."),
        ["views.v1"] = new("views", "Package views", "Provides package views in the desktop app."),
        ["shell-view.v1"] = new("views", "Package views", "Integrates package views with the Sunder shell."),
        ["background-services.v1"] = new("background", "Background work", "Runs package-owned background services or tasks."),
        ["background-processes.v1"] = new("background", "Background work", "Reports package-owned tasks through Sunder."),
        ["app-activation-services.v1"] = new("background", "Background work", "Runs desktop-side services independently of view navigation."),
        ["logging.v1"] = new("logs", "Package logging", "Writes package diagnostics to Sunder’s logs."),
        ["auth.v1"] = new("auth", "Authentication", "Integrates its authentication flow with Sunder.", true),
        ["callbacks.v1"] = new("callbacks", "Authentication callbacks", "Receives callbacks routed to this package."),
        ["stacks.v1"] = new("stacks", "Stack integration", "Integrates package data with Sunder stacks."),
        ["stacks.rpc.v1"] = new("stacks", "Stack integration", "Provides stack import/export integration."),
        ["package-development.v1"] = new("development", "Development sessions", "Manages development sessions for packages.", true),
        ["package-development-reload.v1"] = new("reload", "Development reload", "Requests reloads of the development packages it owns.", true),
        ["package-development-views.v1"] = new("inspection", "Development view interaction", "Inspects and interacts with views in its owned development sessions.", true),
        ["package-development-feedback.v1"] = new("inspection", "Development view interaction", "Queries and opens views in its owned development sessions.", true),
        ["package-development-logs.v1"] = new("development-logs", "Development diagnostics", "Reads package-filtered development logs."),
        ["package-development-log-queries.v1"] = new("development-logs", "Development diagnostics", "Queries bounded, package-filtered development logs."),
        ["package-development-revisions.v1"] = new("observations", "Development status", "Observes development revisions and applied output."),
        ["worker-execution-observations.v1"] = new("observations", "Development status", "Observes worker execution and cleanup status."),
        ["package-log-navigation.v1"] = new("log-navigation", "Log navigation", "Opens package-filtered developer logs."),
    };
    private static readonly HashSet<string> Infrastructure = new(StringComparer.Ordinal)
    {
        "core.v1", "packaging.v1", "contributions.v1", "sdk-baseline-1-1.v1", "rpc.v1", "rpc.call-ownership.v1", "rpc.extended-deadline.v1",
        "worker-protocol.v1", "execution-runtime.v1", "runtime-generations.v1", "theming.v1", "view-navigation-preparation.v1", "app-state-updates.v1", "storage.key-migration.v1",
    };

    internal static IReadOnlyList<PackageFeatureItemViewModel> Project(IEnumerable<SunderPackageFeature> features)
        => features.GroupBy(value => Known.TryGetValue(value.Capability, out var description) ? description.Group : value.Capability, StringComparer.Ordinal)
            .Select(group =>
            {
                var identifiers = group.Select(value => value.Capability).Distinct().Order(StringComparer.Ordinal).ToArray();
                var descriptions = identifiers.Select(id => Known.GetValueOrDefault(id)
                    ?? new(id, id, Infrastructure.Contains(id) ? "Required Sunder infrastructure." : "Additional declared Sunder feature; no description is available.", Technical: Infrastructure.Contains(id))).ToArray();
                return new PackageFeatureItemViewModel(descriptions[0].Title, string.Join(" ", descriptions.Select(value => value.Text).Distinct()),
                    string.Join(" · ", group.Select(value => PackageInformationViewModel.RoleText(value.Role)).Distinct().Order(StringComparer.Ordinal)),
                    string.Join(" · ", identifiers), descriptions.Any(value => value.Sensitive), descriptions.All(value => value.Technical));
            }).OrderByDescending(value => value.IsSensitive).ThenBy(value => value.Title, StringComparer.Ordinal).ToArray();
}

internal static class PackageBindingNames
{
    private static readonly IReadOnlyDictionary<string, string> Known = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["sunder.stack.contributor"] = "Stack import and export",
        ["younics.ai.agent.tool.source"] = "Agent tools",
        ["younics.ai.agent.permission.surface"] = "Agent approval integration",
        ["younics.ai.agent.prompt.context.contributor"] = "Agent prompt context",
        ["younics.ai.agent.selectable.capability.provider"] = "Agent selectable capabilities",
        ["younics.ai.agent.execution.target"] = "Agent execution environments",
        ["younics.ai.agent.workspace.editor"] = "Agent workspace editing",
        ["younics.ai.agent.chat.provider"] = "Agent chat models",
        ["younics.ai.agent.embedding.provider"] = "Agent embedding models",
        ["younics.ai.agent.behavior.loop"] = "Agent run behavior",
        ["younics.ai.agent.child.run.executor"] = "Agent child runs",
        ["younics.ai.agent.durable.lifecycle.observer"] = "Agent lifecycle observation",
        ["younics.ai.agent.profile.capability.consumer"] = "Agent profile capabilities",
        ["younics.ai.agent.run.control"] = "Agent run control",
        ["younics.ai.agent.runtime.catalog"] = "Agent service catalog",
        ["younics.ai.agent.session.cleaner"] = "Agent session cleanup",
        ["younics.ai.agent.system.prompt.contributor"] = "Agent system prompts",
        ["younics.ai.agent.tool.execution.observed"] = "Agent tool activity",
        ["younics.ai.agent.workspace.execution.resolver"] = "Agent workspace execution",
    };
    internal static string Title(string id) => Known.GetValueOrDefault(id) ?? id;
}
