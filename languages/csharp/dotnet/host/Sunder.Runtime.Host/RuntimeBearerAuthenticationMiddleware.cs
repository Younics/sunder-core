namespace Sunder.Runtime.Host;

internal sealed class RuntimeBearerAuthenticationMiddleware(
    RequestDelegate next,
    RuntimeBearerTokenValidator tokenValidator)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!tokenValidator.IsValid(context.Request.Headers.Authorization.ToString()))
        {
            throw new RuntimeAuthenticationException();
        }

        await next(context);
    }
}
