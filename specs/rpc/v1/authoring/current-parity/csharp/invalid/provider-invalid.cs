using System;
using System.Threading.Tasks;
using Sunder.Sdk.Services;

namespace Example.Invalid;

[SunderService(SunderServiceExposure.Package)]
[SunderServiceProvider(Audience = SunderServiceAudience.Public)]
public sealed partial class PrivateProviderService
{
    [SunderMethod]
    public ValueTask<ProviderResult> RunAsync() => throw new NotImplementedException();
}

public sealed record ProviderResult
{
    public required bool Ok { get; init; }
}
