namespace Sunder.App.ViewModels;

internal static class StackContentKindLabels
{
    public static string FormatGroupName(string? kind)
    {
        var normalized = string.IsNullOrWhiteSpace(kind) ? string.Empty : kind.Trim().ToLowerInvariant();
        return normalized switch
        {
            "agent-profile" => "Agent Profiles",
            "agent-workspace" => "Agent Workspaces",
            "docker-image" => "Docker Images",
            "mcp-server" => "MCP Servers",
            "package-requirement" => "Package Install",
            "package-settings" => "Package Settings",
            "skill" => "Agent Skills",
            "subagent" => "Subagents",
            "" => "Content",
            _ => Pluralize(HumanizeToken(normalized)),
        };
    }

    public static string? InferKind(
        string? kind,
        string? packageId,
        string? schemaId = null,
        string? itemId = null,
        string? displayName = null,
        string? summary = null)
    {
        if (!string.IsNullOrWhiteSpace(kind))
        {
            return kind;
        }

        var package = Normalize(packageId);
        var text = Normalize(string.Join(' ', new[] { packageId, schemaId, itemId, displayName, summary }
            .Where(value => !string.IsNullOrWhiteSpace(value))));

        if (package.Contains("agent.mcp", StringComparison.Ordinal) || text.Contains("mcp", StringComparison.Ordinal))
        {
            return "mcp-server";
        }

        if (package.Contains("agent.skills", StringComparison.Ordinal) || text.Contains("skill", StringComparison.Ordinal))
        {
            return "skill";
        }

        if (package.Contains("agent.subagents", StringComparison.Ordinal) || text.Contains("subagent", StringComparison.Ordinal))
        {
            return "subagent";
        }

        if (text.Contains("workspace", StringComparison.Ordinal))
        {
            return "agent-workspace";
        }

        if (text.Contains("profile", StringComparison.Ordinal)
            || text.Contains("custom instructions", StringComparison.Ordinal)
            || text.Contains("provider connections", StringComparison.Ordinal)
            || text.Contains("model choices", StringComparison.Ordinal))
        {
            return "agent-profile";
        }

        return text.Contains("settings", StringComparison.Ordinal) || text.Contains("configuration", StringComparison.Ordinal)
            ? "package-settings"
            : null;
    }

    public static string HumanizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Content";
        }

        var words = value
            .Replace('_', '-')
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return value;
        }

        return string.Join(" ", words.Select(word => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private static string Pluralize(string label)
    {
        if (string.IsNullOrWhiteSpace(label) || label.EndsWith('s'))
        {
            return label;
        }

        return label.EndsWith('y')
            ? label[..^1] + "ies"
            : label + "s";
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Replace('_', '-').ToLowerInvariant();
}
