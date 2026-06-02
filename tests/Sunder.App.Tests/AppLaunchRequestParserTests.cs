using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppLaunchRequestParserTests
{
    [Fact]
    public void Parse_WhenPackageDetailsLink_ReturnsPackageDetailsRequest()
    {
        var request = AppLaunchRequestParser.Parse("sunder://packages/sunder.package.agent?registry=https%3A%2F%2Fregistry.example.test%2F");

        Assert.Equal(AppLaunchRequestKind.PackageDetails, request.Kind);
        Assert.Equal("sunder.package.agent", request.PackageId);
        Assert.Equal("https://registry.example.test/", request.RegistryUrl?.ToString());
    }

    [Fact]
    public void Parse_WhenPackageInstallLink_ReturnsPackageInstallRequest()
    {
        var request = AppLaunchRequestParser.Parse("sunder://packages/sunder.package.agent/install");

        Assert.Equal(AppLaunchRequestKind.PackageInstall, request.Kind);
        Assert.Equal("sunder.package.agent", request.PackageId);
    }

    [Fact]
    public void Parse_WhenStackUseLink_ReturnsStackUseRequest()
    {
        var request = AppLaunchRequestParser.Parse("sunder://stacks/sunder.stack.agent.fullstack-dev/use");

        Assert.Equal(AppLaunchRequestKind.StackUse, request.Kind);
        Assert.Equal("sunder.stack.agent.fullstack-dev", request.StackId);
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
