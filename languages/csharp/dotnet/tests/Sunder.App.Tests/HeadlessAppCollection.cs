using Xunit;

namespace Sunder.App.Tests;

// Avalonia's locator and UI dispatcher are process-global, not per xUnit class.
// Serialize both headless sessions and tests that temporarily replace the locator.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HeadlessAppCollection
{
    public const string Name = "App headless lifecycle";
}

[Collection(HeadlessAppCollection.Name)]
public sealed class HeadlessAppLifecycleTests
{
    [Fact]
    public async Task DispatchRestoresAmbientApplicationAfterSuccessAndFailure()
    {
        Assert.Null(Avalonia.Application.Current);
        // Dispatch can complete its awaiting test inline on the session thread.
        // Async disposal lets that callback exit before waiting for its loop.
        await using (var ui = Avalonia.Headless.HeadlessUnitTestSession.StartNew(
                   typeof(PackageViewHostServiceTests.DevelopmentViewTestApplication)))
        {
            await ui.Dispatch(() => Assert.NotNull(Avalonia.Application.Current), default);
            Assert.Null(Avalonia.Application.Current);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ui.Dispatch(() =>
            {
                Assert.NotNull(Avalonia.Application.Current);
                throw new InvalidOperationException("Synthetic dispatch failure.");
            }, default));
            Assert.Null(Avalonia.Application.Current);
            await ui.Dispatch(() => Assert.NotNull(Avalonia.Application.Current), default);
            Assert.Null(Avalonia.Application.Current);
        }
        Assert.Null(Avalonia.Application.Current);
    }
}
