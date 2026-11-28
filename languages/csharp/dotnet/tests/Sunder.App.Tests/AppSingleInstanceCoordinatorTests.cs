using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppSingleInstanceCoordinatorTests
{
    [Fact]
    public void ExplicitAppProfilesHaveSeparateNormalizedLaunchScopesWithoutChangingDefaultScope()
    {
        var normal = AppSingleInstanceCoordinator.BuildCurrentUserScope(null);
        Assert.Equal(string.Join('\n', Environment.UserName,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)), normal);
        Assert.Equal(normal, AppSingleInstanceCoordinator.BuildCurrentUserScope(" "));
        Assert.Equal(normal, AppSingleInstanceCoordinator.BuildCurrentUserScope(AppLocalState.ResolveRootPath(null)));
        var first = Path.Combine(Path.GetTempPath(), "sunder-profile-one");
        var second = Path.Combine(Path.GetTempPath(), "sunder-profile-two");
        var scope = AppSingleInstanceCoordinator.BuildCurrentUserScope(first);
        Assert.NotEqual(normal, scope);
        Assert.NotEqual(scope, AppSingleInstanceCoordinator.BuildCurrentUserScope(second));
        Assert.Equal(scope, AppSingleInstanceCoordinator.BuildCurrentUserScope(first + Path.DirectorySeparatorChar));
        Assert.Equal(scope, AppSingleInstanceCoordinator.BuildCurrentUserScope(Path.Combine(first, ".")));
        if (OperatingSystem.IsWindows())
            Assert.Equal(scope, AppSingleInstanceCoordinator.BuildCurrentUserScope(first.ToUpperInvariant()));
    }

    [Fact]
    public async Task EarlyProtocolActivation_ReachesTheShellWhenItIsReady()
    {
        using var primary = AppSingleInstanceCoordinator.Create($"Sunder.App.Tests.{Guid.NewGuid():N}");
        var request = AppLaunchRequestParser.Parse("sunder://packages/demo/install?version=1.0.0&registry=https://hub.example/");
        primary.DispatchLaunchRequest(request);
        var received = new TaskCompletionSource<AppLaunchRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.SetLaunchRequestHandler((pending, _) =>
        {
            received.SetResult(pending);
            return Task.CompletedTask;
        });
        Assert.Equal(request, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task TryForwardLaunchArgumentsAsync_WhenPrimaryIsListening_ForwardsParsedRequest()
    {
        var scope = $"Sunder.App.Tests.{Guid.NewGuid():N}";
        using var primary = AppSingleInstanceCoordinator.Create(scope);
        Assert.True(primary.IsPrimary);

        var received = new TaskCompletionSource<AppLaunchRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.SetLaunchRequestHandler((request, _) =>
        {
            received.TrySetResult(request);
            return Task.CompletedTask;
        });
        primary.StartListening();

        using var secondary = AppSingleInstanceCoordinator.Create(scope);
        Assert.False(secondary.IsPrimary);

        var forwarded = await secondary.TryForwardLaunchArgumentsAsync(
            ["sunder://stacks/sunder.stack.demo"],
            TimeSpan.FromSeconds(5));

        Assert.True(forwarded);
        var request = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AppLaunchRequestKind.StackDetails, request.Kind);
        Assert.Equal("sunder.stack.demo", request.StackId);
    }

    [Fact]
    public async Task TryForwardLaunchArgumentsAsync_RejectsOversizedPayloadBeforeConnecting()
    {
        var scope = $"Sunder.App.Tests.{Guid.NewGuid():N}";
        using var primary = AppSingleInstanceCoordinator.Create(scope);
        using var secondary = AppSingleInstanceCoordinator.Create(scope);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            secondary.TryForwardLaunchArgumentsAsync([new string('x', AppSingleInstanceCoordinator.MaxLaunchPayloadCharacters)]));

        Assert.Contains("payload limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
