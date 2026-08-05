using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppSingleInstanceCoordinatorTests
{
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
