using System.Runtime.InteropServices;
using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPackageUiFaultRouterTests
{
    [Fact]
    public void KnownViewFault_RequiresPositiveAttributionAndAcceptedQuarantine()
    {
        var router = new AppPackageUiFaultRouter();
        var accepted = 0;

        Assert.False(router.TryRouteKnownViewFailure(
            packageId: null,
            "agent.chat",
            new InvalidOperationException(),
            (_, _, _) =>
            {
                accepted++;
                return true;
            }));
        Assert.False(router.TryRouteKnownViewFailure(
            "agent",
            viewId: null,
            new InvalidOperationException(),
            (_, _, _) =>
            {
                accepted++;
                return true;
            }));
        Assert.False(router.TryRouteKnownViewFailure(
            "agent",
            "agent.chat",
            new InvalidOperationException(),
            (_, _, _) =>
            {
                accepted++;
                return false;
            }));
        Assert.True(router.TryRouteKnownViewFailure(
            "agent",
            "agent.chat",
            new InvalidOperationException(),
            (_, _, _) =>
            {
                accepted++;
                return true;
            }));

        Assert.Equal(2, accepted);
    }

    [Fact]
    public void PackageOnlyFault_IsHandledOnlyWhenExclusiveQuarantineIsAccepted()
    {
        var router = new AppPackageUiFaultRouter();
        var exception = new InvalidOperationException("dispatcher fault");

        Assert.False(router.TryRoutePackageFailure(
            exception,
            _ => null,
            (_, _) => true));
        Assert.False(router.TryRoutePackageFailure(
            exception,
            _ => "agent",
            (_, _) => false));
        Assert.True(router.TryRoutePackageFailure(
            exception,
            _ => "agent",
            (packageId, routed) => packageId == "agent" && ReferenceEquals(routed, exception)));
    }

    public static TheoryData<Exception> FatalFaults => new()
    {
        new OutOfMemoryException(),
        new StackOverflowException(),
        new AccessViolationException(),
        new SEHException(),
        new InvalidOperationException("wrapper", new OutOfMemoryException()),
        new AggregateException(
            new InvalidOperationException(),
            new InvalidOperationException("wrapper", new AccessViolationException())),
        new System.Reflection.ReflectionTypeLoadException([], [new SEHException()]),
    };

    [Theory]
    [MemberData(nameof(FatalFaults))]
    public void FatalFaultsAndWrappers_AreNeverRouted(Exception exception)
    {
        var router = new AppPackageUiFaultRouter();
        var callbacks = 0;

        Assert.False(AppPackageUiFaultPolicy.IsRecoverable(exception));
        Assert.False(router.TryRouteKnownViewFailure(
            "agent",
            "agent.chat",
            exception,
            (_, _, _) =>
            {
                callbacks++;
                return true;
            }));
        Assert.False(router.TryRoutePackageFailure(
            exception,
            _ => "agent",
            (_, _) =>
            {
                callbacks++;
                return true;
            }));
        Assert.Equal(0, callbacks);
    }
}
