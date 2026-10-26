using Sunder.App.ViewModels;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed class SettingsPackageNavigationTests
{
    [Fact]
    public void RequiredDependentsGroupUnderClickableRootButOptionalIndependentSharedAndUnknownPackagesStayVisible()
    {
        var sections = new[] { "agent", "provider", "nested", "maker", "other", "shared", "unknown", "cycle-a", "cycle-b" }
            .Select(id => new SettingsSectionItemViewModel(id, id, "", true, id)).ToArray();
        var packages = new[]
        {
            Package("agent"), Package("provider", "AGENT"), Package("bridge", "agent"), Package("nested", "bridge"),
            Package("maker"), // optional Agent dependency is intentionally absent from required IDs
            Package("other"), Package("shared", "agent", "other"),
            Package("cycle-a", "cycle-b"), Package("cycle-b", "cycle-a"),
        };
        var groups = SettingsPackageNavigationProjector.Project(sections, packages);
        var agent = Assert.Single(groups, group => group.Root.PackageId == "agent");
        Assert.Same(sections[0], agent.Root);
        Assert.False(agent.IsExpanded);
        Assert.Equal(new[] { "nested", "provider" }, agent.Children.Select(child => child.PackageId));
        Assert.Contains(groups, group => group.Root.PackageId == "maker" && !group.HasChildren);
        Assert.Contains(groups, group => group.Root.PackageId == "shared");
        Assert.Contains(groups, group => group.Root.PackageId == "unknown");
        Assert.Contains(groups, group => group.Root.PackageId == "cycle-a");
        Assert.Contains(groups, group => group.Root.PackageId == "cycle-b");
        Assert.Equal(sections.Length, groups.Sum(group => 1 + group.Children.Count));
    }

    [Theory]
    [InlineData("AI Agent - OpenAI", "OpenAI")]
    [InlineData("AI Agent – Local Execution", "Local Execution")]
    [InlineData("AI Agent — MCP", "MCP")]
    [InlineData("ai agent:  Skills  ", "Skills")]
    [InlineData("AI Agent -", "AI Agent -")]
    [InlineData("AI Agent", "AI Agent")]
    [InlineData("AI Agents - Shared", "AI Agents - Shared")]
    [InlineData("AI Agent Tools", "AI Agent Tools")]
    [InlineData("Other - OpenAI", "Other - OpenAI")]
    public void ChildLabelsAreContextualWithoutChangingFullNameOrPackageIdentity(string title, string expected)
    {
        var root = new SettingsSectionItemViewModel("agent", "AI Agent", "", true, "agent");
        var child = new SettingsSectionItemViewModel("provider", title, "", true, "provider");
        var sections = new[] { root, child };
        var group = Assert.Single(SettingsPackageNavigationProjector.Project(sections, [Package("agent"), Package("provider", "agent")]));

        Assert.Same(child, Assert.Single(group.Children));
        Assert.Equal(expected, child.NavigationTitle);
        Assert.Equal(title, child.Title);
        Assert.Equal("provider", child.PackageId);
        Assert.Equal("AI Agent", group.Root.NavigationTitle);

        var independent = SettingsPackageNavigationProjector.Project(sections, [Package("agent"), Package("provider")]);
        Assert.Equal(2, independent.Count);
        Assert.Equal(title, child.NavigationTitle);
    }

    private static SessionPackageDescriptor Package(string id, params string[] required)
        => new(id, id, "1.0.0", PackageHostRoles.Runtime, null, true, PackageReadinessState.Ready,
            [], null, null, null, 0) { RequiredPackageIds = required };
}
