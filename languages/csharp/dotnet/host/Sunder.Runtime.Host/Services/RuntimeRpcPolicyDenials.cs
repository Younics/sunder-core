using Sunder.Registry.Contracts;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal static class RuntimeRpcPolicyDenials
{
    internal static SunderRpcException ToException(PackagePolicyDeniedException denial)
    {
        var descriptor = PolicyErrorCatalog.All.FirstOrDefault(value => string.Equals(value.Code, denial.Code, StringComparison.Ordinal));
        var kind = descriptor?.Retryable == true ? SunderRpcErrorKind.Unavailable
            : descriptor is null || denial.Code == PackagePolicyErrorCodes.PolicyConflict ? SunderRpcErrorKind.Protocol
            : SunderRpcErrorKind.PermissionDenied;
        // Preserve known and safe future codes without turning an unknown policy
        // decision into a retry. Exception messages are never wire diagnostics.
        var code = denial.Code is { Length: > 0 and <= 128 }
            && denial.Code.All(static value => char.IsAsciiLetterOrDigit(value) || value is '.' or '-' or '_')
                ? denial.Code : "rpc.policy.invalid-denial";
        return SunderRpcException.Infrastructure(kind, code,
            descriptor?.SafeMessage ?? "The Runtime could not validate the package policy denial.");
    }
}
