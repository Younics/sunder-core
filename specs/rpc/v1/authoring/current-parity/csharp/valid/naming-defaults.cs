using System;
using System.Threading.Tasks;
using Sunder.Sdk.Services;

namespace Example.Naming;

[SunderService(SunderServiceExposure.Package)]
public sealed partial class URLLookupService
{
    public Task<NamingResult> GetURLAsync(NamingRequest request) => throw new NotImplementedException();
}

[SunderService(SunderServiceExposure.Package)]
public sealed partial class OAuth2Service
{
    public ValueTask<OAuth2Result> RunAsync() => throw new NotImplementedException();
}

[SunderValue("NamingRequest")]
public sealed record NamingRequest
{
    public required string URLValue { get; init; }
    public required string OAuthToken { get; init; }
}

[SunderValue("NamingResult")]
public sealed record NamingResult
{
    public required string URLValue { get; init; }
}

[SunderValue("OAuth2Result")]
public sealed record OAuth2Result
{
    public required bool Ok { get; init; }
}
