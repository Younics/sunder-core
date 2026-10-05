using System;
using System.Threading.Tasks;
using Sunder.Sdk.Rpc;

#nullable enable

namespace Example.Naming;

[SunderRpcService(SunderRpcExposure.Package)]
public sealed partial class URLLookupService
{
    [SunderRpcMethod]
    public Task<NamingResult> GetURLAsync(NamingRequest request) => throw new NotImplementedException();
}

[SunderRpcService(SunderRpcExposure.Package)]
public sealed partial class OAuth2Service
{
    [SunderRpcMethod]
    public ValueTask<OAuth2Result> RunAsync() => throw new NotImplementedException();
}

[SunderRpcDefinition("NamingRequest")]
public sealed record NamingRequest
{
    public required string URLValue { get; init; }
    public required string OAuthToken { get; init; }
}

[SunderRpcDefinition("NamingResult")]
public sealed record NamingResult
{
    public required string URLValue { get; init; }
}

[SunderRpcDefinition("OAuth2Result")]
public sealed record OAuth2Result
{
    public required bool Ok { get; init; }
}
