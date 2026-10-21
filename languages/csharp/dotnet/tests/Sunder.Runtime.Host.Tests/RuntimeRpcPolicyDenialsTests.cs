using Sunder.Registry.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeRpcPolicyDenialsTests
{
    public static IEnumerable<object[]> CatalogCases()
        => PolicyErrorCatalog.All.Select(value => new object[]
        {
            value.Code, value.Retryable ? SunderRpcErrorKind.Unavailable
                : value.Code == PackagePolicyErrorCodes.PolicyConflict ? SunderRpcErrorKind.Protocol
                : SunderRpcErrorKind.PermissionDenied,
        });

    [Theory]
    [MemberData(nameof(CatalogCases))]
    public void CatalogRetryabilityIsPreservedWithoutBroadeningTerminalDecisions(string code, SunderRpcErrorKind expected)
    {
        var result = RuntimeRpcPolicyDenials.ToException(new(code));
        Assert.Equal(expected, result.Error.Kind);
        Assert.Equal(code, result.Error.Code);
    }

    [Fact]
    public void UnknownSafePolicyCodeIsPreservedButNeverMadeRetryable()
    {
        const string code = "package.future-policy-denial";
        var result = RuntimeRpcPolicyDenials.ToException(new(code));
        Assert.Equal(SunderRpcErrorKind.Protocol, result.Error.Kind);
        Assert.Equal(code, result.Error.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a wire code\n")]
    public void UnsafeUnknownPolicyCodeFailsClosedWithoutEchoingIt(string? code)
    {
        var result = RuntimeRpcPolicyDenials.ToException(new(code!));
        Assert.Equal(SunderRpcErrorKind.Protocol, result.Error.Kind);
        Assert.Equal("rpc.policy.invalid-denial", result.Error.Code);
    }
}
