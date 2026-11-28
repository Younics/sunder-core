using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppLaunchRequestParserTests
{
    [Fact]
    public void Parse_WhenPackageDetailsLink_ReturnsPackageDetailsRequest()
    {
        var request = AppLaunchRequestParser.Parse("sunder://packages/younics.ai.agent?registry=https%3A%2F%2Fregistry.example.test%2F");

        Assert.Equal(AppLaunchRequestKind.PackageDetails, request.Kind);
        Assert.Equal("younics.ai.agent", request.PackageId);
        Assert.Equal("https://registry.example.test/", request.RegistryUrl?.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("?version=1.2.3")]
    [InlineData("?registry=https://hub.example/")]
    [InlineData("?registry=https://hub.example/&version=latest")]
    [InlineData("?registry=https://hub.example/&version=1.0.0&version=2.0.0")]
    [InlineData("?registry=https://hub.example/&registry=https://other.example/&version=1.0.0")]
    [InlineData("?registry=http://hub.example/&version=1.0.0")]
    public void Parse_InstallRequiresAnExactVersionAndUnambiguousOrigin(string query)
    {
        var request = AppLaunchRequestParser.Parse("sunder://packages/younics.ai.agent/install" + query);

        Assert.Equal(AppLaunchRequestKind.Invalid, request.Kind);
    }

    [Fact]
    public void Parse_InstallPreservesExactVersionAndHub()
    {
        var request = AppLaunchRequestParser.Parse("sunder://packages/younics.ai.agent/install?version=2.0.0-rc.1%2Bbuild.2&registry=https%3A%2F%2Fhub.example%2F");
        Assert.Equal(AppLaunchRequestKind.PackageInstall, request.Kind);
        Assert.Equal("2.0.0-rc.1+build.2", request.PackageVersion);
        Assert.Equal("https://hub.example/", request.RegistryUrl?.AbsoluteUri);
    }

    [Fact]
    public void Parse_WhenStackDetailsLink_ReturnsStackDetailsRequest()
    {
        var request = AppLaunchRequestParser.Parse("sunder://stacks/sunder.stack.agent.fullstack-dev?registry=https%3A%2F%2Fregistry.example.test%2F");

        Assert.Equal(AppLaunchRequestKind.StackDetails, request.Kind);
        Assert.Equal("sunder.stack.agent.fullstack-dev", request.StackId);
        Assert.Equal("https://registry.example.test/", request.RegistryUrl?.ToString());
    }

    [Fact]
    public void Parse_StackUseRequiresAndPreservesTheHub()
    {
        var request = AppLaunchRequestParser.Parse("sunder://stacks/sunder.stack.agent.fullstack-dev/use");

        Assert.Equal(AppLaunchRequestKind.Invalid, request.Kind);
        var valid = AppLaunchRequestParser.Parse("sunder://stacks/sunder.stack.agent.fullstack-dev/use?registry=https%3A%2F%2Fhub.example%2F");
        Assert.Equal(AppLaunchRequestKind.StackUse, valid.Kind);
        Assert.Equal("sunder.stack.agent.fullstack-dev", valid.StackId);
        Assert.Equal("https://hub.example/", valid.RegistryUrl?.AbsoluteUri);
    }

    [Fact]
    public void Parse_WhenStackFilePath_ReturnsStackFileRequest()
    {
        var request = AppLaunchRequestParser.Parse("my-stack.sunderstack");

        Assert.Equal(AppLaunchRequestKind.StackFile, request.Kind);
        Assert.EndsWith("my-stack.sunderstack", request.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_WhenPackageIdIsInvalid_ReturnsInvalidRequest()
    {
        var request = AppLaunchRequestParser.Parse("sunder://packages/Sunder.Package.Agent/install");

        Assert.Equal(AppLaunchRequestKind.Invalid, request.Kind);
        Assert.Contains("invalid package id", request.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_WhenValueIsUnrelated_ReturnsNone()
    {
        var request = AppLaunchRequestParser.Parse("--some-option");

        Assert.Equal(AppLaunchRequestKind.None, request.Kind);
    }
}
