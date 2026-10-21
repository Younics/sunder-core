using System.Threading.Tasks;
using Sunder.Sdk.Services;

namespace Example.Collision;

[SunderService(SunderServiceExposure.Public)]
public interface ReservedService
{
    [SunderMethod]
    Task<ReservedResult> RunAsync(string @await, string _await);
}

[SunderValue("ReservedResult")]
public sealed record ReservedResult
{
    public required bool Ok { get; init; }
}
