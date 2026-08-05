using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal sealed record RuntimeRpcFailureContext(
    string CallerPackageId,
    string ProviderPackageId,
    string ProviderPackageVersion,
    string ProviderId,
    string ContractId,
    Guid ProviderActivationId,
    string ServiceId,
    string MethodId,
    SunderRpcErrorKind ErrorKind,
    string ErrorCode,
    string ExceptionType,
    string ExceptionFingerprint);

internal static class RuntimeRpcFailureContextStore
{
    internal const int MaximumExceptionTypeCharacters = 256;
    internal const int MaximumExceptionFingerprintCharacters = 64;
    private static readonly ConditionalWeakTable<SunderRpcException, RuntimeRpcFailureContext> Contexts = new();

    public static SunderRpcException AttachProviderFault(
        SunderRpcException exception,
        string callerPackageId,
        SunderRpcProviderSnapshot provider,
        string serviceId,
        string methodId,
        Exception providerException)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(providerException);
        if (!exception.IsHostAuthenticated)
        {
            throw new InvalidOperationException("RPC failure context requires a Host-authenticated exception.");
        }

        var exceptionType = providerException is ProcessRpcProviderFaultException
            {
                ProviderExceptionType: { } processExceptionType,
            }
            ? processExceptionType
            : providerException.GetType().FullName ?? providerException.GetType().Name;
        exceptionType = exceptionType[..Math.Min(exceptionType.Length, MaximumExceptionTypeCharacters)];
        Contexts.Add(exception, new RuntimeRpcFailureContext(
            callerPackageId,
            provider.PackageId,
            provider.PackageVersion,
            provider.ProviderId,
            provider.ContractId,
            provider.ActivationId,
            serviceId,
            methodId,
            exception.Error.Kind,
            exception.Error.Code,
            exceptionType,
            CreateExceptionFingerprint(exceptionType)));
        return exception;
    }

    public static string CreateExceptionFingerprint(string exceptionType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exceptionType);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exceptionType)))
            .ToLowerInvariant();
    }

    public static SunderRpcException Copy(
        SunderRpcException source,
        SunderRpcException target)
    {
        if (source.IsHostAuthenticated
            && target.IsHostAuthenticated
            && Contexts.TryGetValue(source, out var context))
        {
            Contexts.Add(target, context);
        }
        return target;
    }

    public static bool TryGet(
        SunderRpcException exception,
        [NotNullWhen(true)] out RuntimeRpcFailureContext? context)
    {
        context = null;
        return exception.IsHostAuthenticated
               && Contexts.TryGetValue(exception, out context);
    }
}
