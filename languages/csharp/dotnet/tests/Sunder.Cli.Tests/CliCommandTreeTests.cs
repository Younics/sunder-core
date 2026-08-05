using Sunder.Cli;

namespace Sunder.Cli.Tests;

public sealed class CliCommandTreeTests
{
    public static TheoryData<string[], Type, string> CanonicalCommands => new()
    {
        { ["runtime", "status"], typeof(RuntimeStatusCommand), "runtime status" },
        { ["runtime", "start"], typeof(RuntimeLifecycleCommand), "runtime start" },
        { ["runtime", "stop"], typeof(RuntimeLifecycleCommand), "runtime stop" },
        { ["runtime", "restart"], typeof(RuntimeLifecycleCommand), "runtime restart" },
        { ["runtime", "reset", "--yes"], typeof(RuntimeResetCommand), "runtime reset" },
        { ["package", "search", "agent"], typeof(SearchPackagesCommand), "package search" },
        { ["package", "info", "demo"], typeof(PackageInfoCommand), "package info" },
        { ["package", "list"], typeof(ListInstalledCommand), "package list" },
        { ["package", "status", "demo"], typeof(PackageStatusCommand), "package status" },
        { ["package", "install", "demo"], typeof(InstallRegistryPackageCommand), "package install" },
        { ["package", "install", "--file", "demo.sunderpkg"], typeof(InstallLocalPackageCommand), "package install" },
        { ["package", "update", "demo"], typeof(UpdatePackagesCommand), "package update" },
        { ["package", "update", "--all"], typeof(UpdatePackagesCommand), "package update" },
        { ["package", "source", "adopt", "demo", "--registry-origin", "https://registry.test/", "--tag", "latest", "--dry-run"], typeof(AdoptPackageSourceCommand), "package source adopt" },
        { ["package", "enable", "demo"], typeof(SetPackageEnabledCommand), "package enable" },
        { ["package", "disable", "demo"], typeof(SetPackageEnabledCommand), "package disable" },
        { ["package", "uninstall", "demo", "--dry-run"], typeof(UninstallPackageCommand), "package uninstall" },
        { ["package", "uninstall", "demo", "--yes", "--cascade"], typeof(UninstallPackageCommand), "package uninstall" },
        { ["package", "config", "schema", "demo"], typeof(ShowPackageConfigSchemaCommand), "package config schema" },
        { ["package", "config", "list", "demo"], typeof(ListPackageConfigCommand), "package config list" },
        { ["package", "config", "get", "demo", "mode"], typeof(GetPackageConfigCommand), "package config get" },
        { ["package", "config", "set", "demo", "mode", "fast"], typeof(SetPackageConfigCommand), "package config set" },
        { ["package", "config", "unset", "demo", "mode"], typeof(UnsetPackageConfigCommand), "package config unset" },
        { ["package", "secret", "status", "demo"], typeof(PackageSecretStatusCommand), "package secret status" },
        { ["package", "secret", "set", "demo", "api-key", "--from-env", "API_KEY"], typeof(SetPackageSecretCommand), "package secret set" },
        { ["package", "secret", "unset", "demo", "api-key"], typeof(UnsetPackageSecretCommand), "package secret unset" },
        { ["package", "auth", "status", "demo"], typeof(PackageAuthStatusCommand), "package auth status" },
        { ["package", "auth", "login", "demo"], typeof(PackageAuthLoginCommand), "package auth login" },
        { ["package", "auth", "logout", "demo"], typeof(PackageAuthLogoutCommand), "package auth logout" },
        { ["stack", "search", "agent"], typeof(SearchStacksCommand), "stack search" },
        { ["stack", "info", "demo"], typeof(StackInfoCommand), "stack info" },
        { ["stack", "download", "demo", "-o", "demo.sunderstack"], typeof(DownloadStackCommand), "stack download" },
        { ["stack", "open", "demo"], typeof(OpenStackCommand), "stack open" },
        { ["stack", "import", "--file", "demo.sunderstack", "--dry-run"], typeof(ImportStackCommand), "stack import" },
        { ["stack", "export", "demo", "--output", "demo.sunderstack"], typeof(ExportStackCommand), "stack export" },
        { ["registry", "auth", "login"], typeof(AuthLoginCommand), "registry auth login" },
        { ["registry", "auth", "status"], typeof(AuthStatusCommand), "registry auth status" },
        { ["registry", "auth", "logout"], typeof(AuthLogoutCommand), "registry auth logout" },
        { ["registry", "package", "publish", "--file", "demo.sunderpkg"], typeof(PublishPackageCommand), "registry package publish" },
        { ["registry", "package", "yank", "demo", "1.0.0"], typeof(SetYankCommand), "registry package yank" },
        { ["registry", "package", "unyank", "demo", "1.0.0"], typeof(SetYankCommand), "registry package unyank" },
        { ["registry", "package", "deprecate", "demo", "1.0.0", "--message", "old"], typeof(SetDeprecationCommand), "registry package deprecate" },
        { ["registry", "package", "undeprecate", "demo", "1.0.0"], typeof(SetDeprecationCommand), "registry package undeprecate" },
        { ["registry", "package", "tag", "list", "demo"], typeof(ListDistTagsCommand), "registry package tag list" },
        { ["registry", "package", "tag", "set", "demo", "latest", "1.0.0"], typeof(SetDistTagCommand), "registry package tag set" },
        { ["registry", "package", "tag", "delete", "demo", "beta"], typeof(SetDistTagCommand), "registry package tag delete" },
        { ["registry", "stack", "publish", "--file", "demo.sunderstack"], typeof(PublishStackCommand), "registry stack publish" },
        { ["registry", "stack", "delete", "demo"], typeof(DeleteStackCommand), "registry stack delete" },
        { ["dev", "package", "validate", "demo.sunderpkg"], typeof(ValidatePackageCommand), "dev package validate" },
        { ["dev", "stack", "inspect", "demo.sunderstack"], typeof(ValidateStackCommand), "dev stack inspect" },
        { ["dev", "registry", "package", "publish-local", "--file", "demo.sunderpkg"], typeof(PublishPackageCommand), "dev registry package publish-local" },
        { ["dev", "registry", "stack", "publish-local", "--file", "demo.sunderstack"], typeof(PublishStackCommand), "dev registry stack publish-local" },
        { ["config", "show"], typeof(ShowConfigCommand), "config show" },
        { ["version"], typeof(VersionCommand), "version" },
    };

    [Theory]
    [MemberData(nameof(CanonicalCommands))]
    public void Canonical_leaf_builds_the_expected_invocation(string[] arguments, Type commandType, string path)
    {
        var result = new CliCommandParser().Parse(arguments);

        Assert.Empty(result.Errors);
        Assert.NotNull(result.Invocation);
        Assert.IsType(commandType, result.Invocation.Command);
        Assert.Equal(path, result.Invocation.CommandPath);
    }

    [Theory]
    [InlineData("runtime", "reset", "--force")]
    [InlineData("package", "validate", "demo.sunderpkg")]
    [InlineData("stack", "use", "demo")]
    [InlineData("stack", "publish", "--file", "demo.sunderstack")]
    [InlineData("stack", "validate", "demo.sunderstack")]
    [InlineData("registry", "status")]
    [InlineData("registry", "stack", "update", "demo", "--file", "demo.sunderstack")]
    [InlineData("registry", "package", "dist-tag", "list", "demo")]
    [InlineData("dev", "package", "publish", "--file", "demo.sunderpkg", "--dev-local")]
    public void Legacy_leaf_paths_are_not_bound(params string[] arguments)
    {
        var result = new CliCommandParser().Parse(arguments);

        Assert.Null(result.Invocation);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Publication_targets_use_the_intended_authenticated_or_development_client()
    {
        var parser = new CliCommandParser();
        var remote = Assert.IsType<PublishPackageCommand>(parser.Parse([
            "registry", "package", "publish", "--file", "demo.sunderpkg",
        ]).Invocation?.Command);
        var development = Assert.IsType<PublishPackageCommand>(parser.Parse([
            "dev", "registry", "package", "publish-local", "--file", "demo.sunderpkg",
        ]).Invocation?.Command);

        Assert.False(remote.DevLocal);
        Assert.Equal(RegistryCredentialSource.Runtime, remote.CredentialSource);
        Assert.Equal(CliClientRequirements.Runtime, CliCommandPlan.For(remote).Clients);
        Assert.True(development.DevLocal);
        Assert.Equal(CliClientRequirements.Registry, CliCommandPlan.For(development).Clients);
    }

    [Theory]
    [InlineData("environment", "Environment")]
    [InlineData("stdin", "StandardInput")]
    public void Automation_publication_uses_the_direct_registry_client(
        string sourceName,
        string expected)
    {
        var command = Assert.IsType<PublishPackageCommand>(new CliCommandParser().Parse([
            "registry", "package", "publish", "--file", "demo.sunderpkg",
            "--credential-source", sourceName,
        ]).Invocation?.Command);

        Assert.Equal(expected, command.CredentialSource.ToString());
        Assert.Equal(CliClientRequirements.Registry, CliCommandPlan.For(command).Clients);
        Assert.Equal(CliConfigurationRequirements.RegistryApi, CliCommandPlan.For(command).Configuration);
    }

    [Fact]
    public void Package_uninstall_requires_exactly_one_plan_or_apply_mode()
    {
        var parser = new CliCommandParser();

        Assert.NotEmpty(parser.Parse(["package", "uninstall", "demo"]).Errors);
        Assert.NotEmpty(parser.Parse(["package", "uninstall", "demo", "--yes", "--dry-run"]).Errors);
        Assert.NotEmpty(parser.Parse(["package", "uninstall", "demo", "--dry-run", "--cascade"]).Errors);
    }

    [Fact]
    public void Package_source_adoption_requires_origin_policy_and_exactly_one_safety_mode()
    {
        var parser = new CliCommandParser();

        Assert.NotEmpty(parser.Parse(["package", "source", "adopt", "demo", "--tag", "latest", "--dry-run"]).Errors);
        Assert.NotEmpty(parser.Parse(["package", "source", "adopt", "demo", "--registry-origin", "https://registry.test/", "--dry-run"]).Errors);
        Assert.NotEmpty(parser.Parse(["package", "source", "adopt", "demo", "--registry-origin", "https://registry.test/", "--tag", "latest", "--version", "1.0.0", "--dry-run"]).Errors);
        Assert.NotEmpty(parser.Parse(["package", "source", "adopt", "demo", "--registry-origin", "https://registry.test/", "--version", "1.0.0", "--include-prerelease", "--dry-run"]).Errors);
        Assert.NotEmpty(parser.Parse(["package", "source", "adopt", "demo", "--registry-origin", "https://registry.test/", "--tag", "latest"]).Errors);
        Assert.NotEmpty(parser.Parse(["package", "source", "adopt", "demo", "--registry-origin", "https://registry.test/", "--tag", "latest", "--yes", "--dry-run"]).Errors);
    }

    [Fact]
    public void Safe_input_commands_require_an_explicit_noninteractive_mode()
    {
        var parser = new CliCommandParser();

        Assert.NotEmpty(parser.Parse(["package", "secret", "set", "demo", "api-key"]).Errors);
        Assert.NotEmpty(parser.Parse(["package", "secret", "set", "demo", "api-key", "--stdin", "--from-env", "API_KEY"]).Errors);
        Assert.NotEmpty(parser.Parse(["package", "secret", "set", "demo", "api-key", "literal-value"]).Errors);
        Assert.NotEmpty(parser.Parse(["stack", "import", "--file", "demo.sunderstack"]).Errors);
    }

    [Fact]
    public void Ordinary_user_commands_provision_only_their_capability_clients()
    {
        var parser = new CliCommandParser();
        var status = parser.Parse(["runtime", "status"]).Invocation!.Command;
        var lifecycle = parser.Parse(["runtime", "start"]).Invocation!.Command;
        var settings = parser.Parse(["package", "config", "list", "demo"]).Invocation!.Command;
        var stackExport = parser.Parse(["stack", "export", "demo"]).Invocation!.Command;
        var update = parser.Parse(["package", "update", "--all"]).Invocation!.Command;
        var adoption = parser.Parse([
            "package", "source", "adopt", "demo", "--registry-origin", "https://registry.test/", "--tag", "latest", "--dry-run",
        ]).Invocation!.Command;

        Assert.Equal(CliClientRequirements.Host | CliClientRequirements.Runtime, CliCommandPlan.For(status).Clients);
        Assert.Equal(CliClientRequirements.Host, CliCommandPlan.For(lifecycle).Clients);
        Assert.Equal(CliClientRequirements.Runtime, CliCommandPlan.For(settings).Clients);
        Assert.Equal(CliClientRequirements.Runtime, CliCommandPlan.For(stackExport).Clients);
        Assert.Equal(CliConfigurationRequirements.Runtime, CliCommandPlan.For(update).Configuration);
        Assert.Equal(CliConfigurationRequirements.Runtime, CliCommandPlan.For(adoption).Configuration);
    }

    [Theory]
    [InlineData("1.0.0", null, true)]
    [InlineData("1.0.0-beta.1", null, false)]
    [InlineData("1.0.0-beta.1", true, true)]
    [InlineData("1.0.0", false, false)]
    public void Package_publish_uses_prerelease_safe_latest_defaults(
        string version,
        bool? explicitValue,
        bool expected)
        => Assert.Equal(expected, DeveloperPublishCommandHandler.ResolveSetLatest(version, explicitValue));
}
