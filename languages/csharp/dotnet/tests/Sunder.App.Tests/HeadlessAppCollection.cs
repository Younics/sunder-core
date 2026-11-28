using Avalonia;
using Avalonia.Headless;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Sunder.App.Tests.HeadlessTestApplication))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerTest)]

namespace Sunder.App.Tests;

// The Avalonia locator is process-wide. Keep temporary locator replacements and UI dispatches serialized.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HeadlessAppCollection
{
    public const string Name = "App headless lifecycle";

    // Text decorations retain their UI thread and typefaces retain their renderer.
    // Reuse Avalonia's assembly session; each Dispatch still creates and disposes its own application scope.
    internal static HeadlessUnitTestSession Session =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessAppCollection).Assembly);
}

[Collection(HeadlessAppCollection.Name)]
public sealed class HeadlessAppLifecycleTests
{
    [Fact]
    public async Task DispatchRestoresAmbientApplicationAfterSuccessAndFailure()
    {
        Assert.Null(Application.Current);
        var first = await HeadlessAppCollection.Session.Dispatch(() => Assert.IsType<HeadlessTestApplication>(Application.Current), default);
        Assert.Null(Application.Current);
        await Assert.ThrowsAsync<InvalidOperationException>(() => HeadlessAppCollection.Session.Dispatch(() =>
        {
            Assert.NotNull(Application.Current);
            throw new InvalidOperationException("Synthetic dispatch failure.");
        }, default));
        Assert.Null(Application.Current);
        await HeadlessAppCollection.Session.Dispatch(() =>
        {
            Assert.NotNull(Application.Current);
            Assert.NotSame(first, Application.Current);
            Assert.True(first.CheckAccess());
        }, default);
        Assert.Null(Application.Current);
    }
}
