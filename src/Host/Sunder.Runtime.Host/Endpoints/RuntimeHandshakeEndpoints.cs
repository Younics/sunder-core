using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class RuntimeHandshakeEndpoints
{
    public static IEndpointRouteBuilder MapRuntimeHandshakeEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/handshake",
            (RuntimeProtocolDescriptor protocol) => Results.Ok(protocol.Handshake));
        return endpoints;
    }
}
