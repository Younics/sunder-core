using Microsoft.AspNetCore.Http;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeBearerAuthenticationMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_WhenAuthorizationIsMissing_ReturnsUnauthorized()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(() => nextCalled = true);
        var context = new DefaultHttpContext();

        await Assert.ThrowsAsync<RuntimeAuthenticationException>(() => middleware.InvokeAsync(context));
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_WhenBearerTokenIsWrong_ReturnsUnauthorized()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(() => nextCalled = true);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer wrong-token";

        await Assert.ThrowsAsync<RuntimeAuthenticationException>(() => middleware.InvokeAsync(context));
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_WhenAuthorizationHeaderExceedsLimit_ReturnsUnauthorized()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(() => nextCalled = true);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer " + new string('x', RuntimeBearerTokenValidator.MaxAuthorizationHeaderLength);

        await Assert.ThrowsAsync<RuntimeAuthenticationException>(() => middleware.InvokeAsync(context));

        Assert.False(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_WhenBearerTokenIsCorrect_ContinuesRequest()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(() => nextCalled = true);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer correct-token";

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    private static RuntimeBearerAuthenticationMiddleware CreateMiddleware(Action onNext)
        => new(
            _ =>
            {
                onNext();
                return Task.CompletedTask;
            },
            new RuntimeBearerTokenValidator("correct-token"));
}
