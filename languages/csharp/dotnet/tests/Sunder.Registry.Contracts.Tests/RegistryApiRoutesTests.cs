using System.Reflection;
using Sunder.Registry.Contracts;
using Xunit;

namespace Sunder.Registry.Contracts.Tests;

public sealed class RegistryApiRoutesTests
{
    private static readonly Guid MediaId = Guid.Parse("00112233-4455-6677-8899-AABBCCDDEEFF");
    private static readonly Guid TokenId = Guid.Parse("FFEEDDCC-BBAA-9988-7766-554433221100");

    [Fact]
    public void Constants_MatchLiveRegistryV1Routes()
    {
        Assert.Equal("/api/v1", RegistryApiRoutes.Root);
        Assert.Equal("/api/v1/contracts", RegistryApiRoutes.Contracts);
        Assert.Equal("/api/v1/cli-auth", RegistryApiRoutes.CliAuth);
        Assert.Equal("/api/v1/cli-auth/authorize", RegistryApiRoutes.CliAuthAuthorize);
        Assert.Equal("/api/v1/cli-auth/token", RegistryApiRoutes.CliAuthToken);
        Assert.Equal("/api/v1/me", RegistryApiRoutes.CurrentUser);
        Assert.Equal("/api/v1/me/profile", RegistryApiRoutes.CurrentUserProfile);
        Assert.Equal("/api/v1/me/packages", RegistryApiRoutes.CurrentUserPackages);
        Assert.Equal("/api/v1/me/stacks", RegistryApiRoutes.CurrentUserStacks);
        Assert.Equal("/api/v1/me/users", RegistryApiRoutes.CurrentUserUsers);
        Assert.Equal("/api/v1/me/tokens", RegistryApiRoutes.CurrentUserCliTokens);
        Assert.Equal("/api/v1/me/publish-tokens", RegistryApiRoutes.CurrentUserPublishTokens);
        Assert.Equal("/api/v1/packages", RegistryApiRoutes.Packages);
        Assert.Equal("/api/v1/packages/resolve-updates", RegistryApiRoutes.PackageResolveUpdates);
        Assert.Equal("/api/v1/packages/resolve-install-plan", RegistryApiRoutes.PackageResolveInstallPlan);
        Assert.Equal("/api/v1/packages/resolve-package-changes", RegistryApiRoutes.PackageResolvePackageChanges);
        Assert.Equal("/api/v1/packages/publish", RegistryApiRoutes.PackagePublish);
        Assert.Equal("/api/v1/execution-runtimes", RegistryApiRoutes.ExecutionRuntimes);
        Assert.Equal("/api/v1/execution-runtimes/resolve", RegistryApiRoutes.ExecutionRuntimeResolve);
        Assert.Equal("/api/v1/admin/execution-runtimes", RegistryApiRoutes.AdministratorExecutionRuntimes);
        Assert.Equal("/api/v1/admin/execution-runtimes/stages", RegistryApiRoutes.ExecutionRuntimeStages);
        Assert.Equal("/api/v1/admin/execution-runtimes/stages/lookup", RegistryApiRoutes.ExecutionRuntimeStageLookup);
        Assert.Equal("/api/v1/stacks", RegistryApiRoutes.Stacks);
        Assert.Equal("/api/v1/stacks/publish", RegistryApiRoutes.StackPublish);
        Assert.Equal("/api/v1/dev/packages", RegistryApiRoutes.DevelopmentPackages);
        Assert.Equal("/api/v1/dev/packages/publish/local", RegistryApiRoutes.DevelopmentPackagePublishLocal);
        Assert.Equal("/api/v1/dev/execution-runtimes", RegistryApiRoutes.DevelopmentExecutionRuntimes);
        Assert.Equal("/api/v1/dev/execution-runtimes/stages", RegistryApiRoutes.DevelopmentExecutionRuntimeStages);
        Assert.Equal("/api/v1/dev/execution-runtimes/stages/lookup", RegistryApiRoutes.DevelopmentExecutionRuntimeStageLookup);
        Assert.Equal("/api/v1/dev/stacks", RegistryApiRoutes.DevelopmentStacks);
        Assert.Equal("/api/v1/dev/stacks/publish/local", RegistryApiRoutes.DevelopmentStackPublishLocal);
    }

    [Fact]
    public void ContractAndCurrentUserBuilders_MatchLiveRegistryV1Routes()
    {
        Assert.Equal("/api/v1/contracts/sample.contract/versions", RegistryApiRoutes.ContractVersions("sample.contract"));
        Assert.Equal("/api/v1/contracts/sample.contract/versions/1.2.3", RegistryApiRoutes.ContractVersion("sample.contract", "1.2.3"));
        Assert.Equal("/api/v1/contracts/sample.contract/versions/1.2.3/descriptor", RegistryApiRoutes.ContractVersionDescriptor("sample.contract", "1.2.3"));
        Assert.Equal("/api/v1/contracts/sample.contract/resolve", RegistryApiRoutes.ContractResolve("sample.contract"));
        Assert.Equal("/api/v1/me/tokens/ffeeddcc-bbaa-9988-7766-554433221100", RegistryApiRoutes.CurrentUserCliToken(TokenId));
        Assert.Equal("/api/v1/me/publish-tokens/ffeeddcc-bbaa-9988-7766-554433221100", RegistryApiRoutes.CurrentUserPublishToken(TokenId));
        Assert.Equal("/api/v1/me/packages/sample.package", RegistryApiRoutes.CurrentUserPackage("sample.package"));
        Assert.Equal("/api/v1/me/packages/sample.package/profile", RegistryApiRoutes.CurrentUserPackageProfile("sample.package"));
        Assert.Equal("/api/v1/me/packages/sample.package/media", RegistryApiRoutes.CurrentUserPackageMedia("sample.package"));
        Assert.Equal("/api/v1/me/packages/sample.package/media/00112233-4455-6677-8899-aabbccddeeff", RegistryApiRoutes.CurrentUserPackageMedia("sample.package", MediaId));
        Assert.Equal("/api/v1/me/stacks/sample.stack", RegistryApiRoutes.CurrentUserStack("sample.stack"));
        Assert.Equal("/api/v1/me/stacks/sample.stack/profile", RegistryApiRoutes.CurrentUserStackProfile("sample.stack"));
        Assert.Equal("/api/v1/me/stacks/sample.stack/media", RegistryApiRoutes.CurrentUserStackMedia("sample.stack"));
        Assert.Equal("/api/v1/me/stacks/sample.stack/media/00112233-4455-6677-8899-aabbccddeeff", RegistryApiRoutes.CurrentUserStackMedia("sample.stack", MediaId));
    }

    [Fact]
    public void PackageBuilders_MatchLiveRegistryV1Routes()
    {
        Assert.Equal("/api/v1/packages/sample.package", RegistryApiRoutes.Package("sample.package"));
        Assert.Equal("/api/v1/packages/sample.package/versions", RegistryApiRoutes.PackageVersions("sample.package"));
        Assert.Equal("/api/v1/packages/sample.package/versions/1.2.3", RegistryApiRoutes.PackageVersion("sample.package", "1.2.3"));
        Assert.Equal("/api/v1/packages/sample.package/versions/1.2.3/icon", RegistryApiRoutes.PackageVersionIcon("sample.package", "1.2.3"));
        Assert.Equal("/api/v1/packages/sample.package/versions/1.2.3/canonical", RegistryApiRoutes.PackageVersionCanonical("sample.package", "1.2.3"));
        Assert.Equal("/api/v1/packages/sample.package/versions/1.2.3/projections/shared/download", RegistryApiRoutes.PackageVersionSharedProjectionDownload("sample.package", "1.2.3"));
        Assert.Equal("/api/v1/packages/sample.package/versions/1.2.3/projections/runtime/win-x64/download", RegistryApiRoutes.PackageVersionTargetProjectionDownload("sample.package", "1.2.3", "runtime", "win-x64"));
        Assert.Equal("/api/v1/packages/sample.package/view", RegistryApiRoutes.PackageView("sample.package"));
        Assert.Equal("/api/v1/packages/sample.package/star", RegistryApiRoutes.PackageStar("sample.package"));
        Assert.Equal("/api/v1/packages/sample.package/resolve", RegistryApiRoutes.PackageResolve("sample.package"));
        Assert.Equal("/api/v1/packages/sample.package/dist-tags", RegistryApiRoutes.PackageDistTags("sample.package"));
        Assert.Equal("/api/v1/packages/sample.package/dist-tags/latest", RegistryApiRoutes.PackageDistTag("sample.package", "latest"));
        Assert.Equal("/api/v1/packages/sample.package/versions/1.2.3/yank", RegistryApiRoutes.PackageVersionYank("sample.package", "1.2.3"));
        Assert.Equal("/api/v1/packages/sample.package/versions/1.2.3/deprecation", RegistryApiRoutes.PackageVersionDeprecation("sample.package", "1.2.3"));
        Assert.Equal("/api/v1/packages/sample.package/profile", RegistryApiRoutes.PackageProfile("sample.package"));
        Assert.Equal("/api/v1/packages/sample.package/media/00112233-4455-6677-8899-aabbccddeeff", RegistryApiRoutes.PackageMedia("sample.package", MediaId));
        Assert.Equal("/api/v1/packages/sample.package/maintainers", RegistryApiRoutes.PackageMaintainers("sample.package"));
        Assert.Equal("/api/v1/packages/sample.package/maintainers/user-1", RegistryApiRoutes.PackageMaintainer("sample.package", "user-1"));
    }

    [Fact]
    public void StackBuilders_MatchLiveRegistryV1Routes()
    {
        Assert.Equal("/api/v1/stacks/sample.stack", RegistryApiRoutes.Stack("sample.stack"));
        Assert.Equal("/api/v1/stacks/sample.stack/view", RegistryApiRoutes.StackView("sample.stack"));
        Assert.Equal("/api/v1/stacks/sample.stack/download", RegistryApiRoutes.StackDownload("sample.stack"));
        Assert.Equal("/api/v1/stacks/sample.stack/star", RegistryApiRoutes.StackStar("sample.stack"));
        Assert.Equal("/api/v1/stacks/sample.stack/profile", RegistryApiRoutes.StackProfile("sample.stack"));
        Assert.Equal("/api/v1/stacks/sample.stack/media/00112233-4455-6677-8899-aabbccddeeff", RegistryApiRoutes.StackMedia("sample.stack", MediaId));
        Assert.Equal("/api/v1/stacks/sample.stack/maintainers", RegistryApiRoutes.StackMaintainers("sample.stack"));
        Assert.Equal("/api/v1/stacks/sample.stack/maintainers/user-1", RegistryApiRoutes.StackMaintainer("sample.stack", "user-1"));
    }

    [Fact]
    public void ExecutionRuntimeBuilders_MatchRegistryV1Routes()
    {
        Assert.Equal("/api/v1/execution-runtimes/sunder.runtime.dotnet", RegistryApiRoutes.ExecutionRuntime("sunder.runtime.dotnet"));
        Assert.Equal("/api/v1/execution-runtimes/sunder.runtime.dotnet/releases", RegistryApiRoutes.ExecutionRuntimeReleases("sunder.runtime.dotnet"));
        Assert.Equal("/api/v1/execution-runtimes/sunder.runtime.dotnet/releases/10.0.1/revisions/2", RegistryApiRoutes.ExecutionRuntimeRelease("sunder.runtime.dotnet", "10.0.1", 2));
        Assert.Equal("/api/v1/execution-runtimes/sunder.runtime.dotnet/releases/10.0.1/revisions/2/artifacts/win-x64", RegistryApiRoutes.ExecutionRuntimeArtifact("sunder.runtime.dotnet", "10.0.1", 2, "win-x64"));
        Assert.Equal("/api/v1/execution-runtimes/sunder.runtime.dotnet/releases/10.0.1/revisions/2/artifacts/win-x64/download", RegistryApiRoutes.ExecutionRuntimeArtifactDownload("sunder.runtime.dotnet", "10.0.1", 2, "win-x64"));
        Assert.Equal("/api/v1/execution-runtimes/sunder.runtime.dotnet/releases/10.0.1/revisions/2/lifecycle", RegistryApiRoutes.ExecutionRuntimeReleaseLifecycle("sunder.runtime.dotnet", "10.0.1", 2));
        Assert.Equal("/api/v1/admin/execution-runtimes/stages/stage-1", RegistryApiRoutes.ExecutionRuntimeStage("stage-1"));
        Assert.Equal("/api/v1/admin/execution-runtimes/stages/stage-1/artifacts/win-x64", RegistryApiRoutes.ExecutionRuntimeStageArtifact("stage-1", "win-x64"));
        Assert.Equal("/api/v1/admin/execution-runtimes/stages/stage-1/promotion", RegistryApiRoutes.ExecutionRuntimeStagePromotion("stage-1"));
        Assert.Equal("/api/v1/dev/execution-runtimes/stages/stage-1", RegistryApiRoutes.DevelopmentExecutionRuntimeStage("stage-1"));
        Assert.Equal("/api/v1/dev/execution-runtimes/stages/stage-1/artifacts/win-x64/local", RegistryApiRoutes.DevelopmentExecutionRuntimeStageArtifactLocal("stage-1", "win-x64"));
        Assert.Equal("/api/v1/dev/execution-runtimes/stages/stage-1/promotion", RegistryApiRoutes.DevelopmentExecutionRuntimeStagePromotion("stage-1"));
        Assert.Throws<ArgumentOutOfRangeException>(() => RegistryApiRoutes.ExecutionRuntimeRelease("sunder.runtime.dotnet", "10.0.1", 0));
    }

    [Fact]
    public void ExecutionRuntimeOperationNames_AreStable()
    {
        Assert.Equal("BrowseExecutionRuntimes", RegistryApiOperationNames.BrowseExecutionRuntimes);
        Assert.Equal("GetExecutionRuntime", RegistryApiOperationNames.GetExecutionRuntime);
        Assert.Equal("GetExecutionRuntimeRelease", RegistryApiOperationNames.GetExecutionRuntimeRelease);
        Assert.Equal("DownloadExecutionRuntimeArtifact", RegistryApiOperationNames.DownloadExecutionRuntimeArtifact);
        Assert.Equal("ResolveExecutionRuntime", RegistryApiOperationNames.ResolveExecutionRuntime);
        Assert.Equal("LookupExecutionRuntimePublication", RegistryApiOperationNames.LookupExecutionRuntimePublication);
        Assert.Equal("StageExecutionRuntimeRelease", RegistryApiOperationNames.StageExecutionRuntimeRelease);
        Assert.Equal("PublishExecutionRuntimeArtifact", RegistryApiOperationNames.PublishExecutionRuntimeArtifact);
        Assert.Equal("PromoteExecutionRuntimeRelease", RegistryApiOperationNames.PromoteExecutionRuntimeRelease);
        Assert.Equal("TransitionExecutionRuntimeLifecycle", RegistryApiOperationNames.TransitionExecutionRuntimeLifecycle);
    }

    [Fact]
    public void Builders_StrictlyEscapeEveryRawStringSegment()
    {
        var routes = typeof(RegistryApiRoutes)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => method.Name != nameof(RegistryApiRoutes.ToHttpClientRelativePath))
            .ToArray();

        Assert.Equal(73, routes.Length);
        foreach (var route in routes)
        {
            var parameters = route.GetParameters();
            var arguments = parameters
                .Select(static parameter => parameter.ParameterType == typeof(string)
                    ? (object)"raw/value"
                    : ValidNonStringArgument(parameter))
                .ToArray();
            var path = Assert.IsType<string>(route.Invoke(null, arguments));
            Assert.Equal(parameters.Count(static parameter => parameter.ParameterType == typeof(string)), Count(path, "%2F"));
        }

        Assert.Equal("/api/v1/packages/%252F", RegistryApiRoutes.Package("%2F"));
        Assert.Equal("/api/v1/packages/a%20b%3Fc%23d%25e%C3%A9", RegistryApiRoutes.Package("a b?c#d%e\u00E9"));
        Assert.Equal("/api/v1/packages/%20value%20", RegistryApiRoutes.Package(" value "));
    }

    [Fact]
    public void Builders_RejectNullEmptyAndDotSegmentsForEveryStringParameter()
    {
        var routes = typeof(RegistryApiRoutes)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => method.Name != nameof(RegistryApiRoutes.ToHttpClientRelativePath))
            .ToArray();
        object?[] invalidSegments = [null, string.Empty, ".", ".."];

        foreach (var route in routes)
        {
            var parameters = route.GetParameters();
            for (var parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
            {
                if (parameters[parameterIndex].ParameterType != typeof(string)) continue;
                foreach (var invalidSegment in invalidSegments)
                {
                    object?[] arguments = parameters
                        .Select(static parameter => parameter.ParameterType == typeof(string)
                            ? (object?)"valid"
                            : ValidNonStringArgument(parameter))
                        .ToArray();
                    arguments[parameterIndex] = invalidSegment;
                    var exception = Assert.Throws<TargetInvocationException>(() => route.Invoke(null, arguments));
                    Assert.IsAssignableFrom<ArgumentException>(exception.InnerException!);
                }
            }
        }
    }

    [Fact]
    public void ToHttpClientRelativePath_StripsOneSlashAndPreservesBasePathResolution()
    {
        Assert.Equal("api/v1", RegistryApiRoutes.ToHttpClientRelativePath("/api/v1"));
        Assert.Equal(
            "api/v1/packages/raw%2Fvalue",
            RegistryApiRoutes.ToHttpClientRelativePath(RegistryApiRoutes.Package("raw/value")));
        Assert.Equal(
            new Uri("https://registry.example/base/api/v1/packages"),
            new Uri(
                new Uri("https://registry.example/base/"),
                RegistryApiRoutes.ToHttpClientRelativePath(RegistryApiRoutes.Packages)));
    }

    [Fact]
    public void ToHttpClientRelativePath_RejectsNull()
        => Assert.Throws<ArgumentNullException>(() => RegistryApiRoutes.ToHttpClientRelativePath(null!));

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("api/v1")]
    [InlineData("https://registry.example/api/v1")]
    [InlineData("file:///api/v1")]
    [InlineData("/urn:registry-route")]
    [InlineData("//registry.example/api/v1")]
    [InlineData("/api//v1")]
    [InlineData("/api/v1/")]
    [InlineData("/api/./v1")]
    [InlineData("/api/../v1")]
    [InlineData("/api/%2e/v1")]
    [InlineData("/api/%2E%2E/v1")]
    [InlineData("/api/v1?take=1")]
    [InlineData("/api/v1#fragment")]
    [InlineData("/api\\v1")]
    [InlineData("/api/v 1")]
    [InlineData("/api/%")]
    [InlineData("/api/%2")]
    [InlineData("/api/%GG")]
    public void ToHttpClientRelativePath_RejectsInvalidOrAbsoluteInputs(string path)
        => Assert.Throws<ArgumentException>(() => RegistryApiRoutes.ToHttpClientRelativePath(path));

    private static int Count(string value, string fragment)
        => value.Split(fragment, StringSplitOptions.None).Length - 1;

    private static object ValidNonStringArgument(ParameterInfo parameter)
        => parameter.ParameterType == typeof(int) ? 1
            : parameter.ParameterType == typeof(long) ? 1L
            : parameter.ParameterType == typeof(Guid) ? MediaId
            : throw new InvalidOperationException($"Route parameter type requires an explicit test value: {parameter.ParameterType}.");
}
