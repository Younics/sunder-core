using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal sealed record RuntimeRpcFailureContext(
    string CallerPackageId,
    string CallerPackageVersion,
    Guid? CallerActivationId,
    string ProviderPackageId,
    string ProviderPackageVersion,
    string ProviderId,
    string ContractId,
    Guid ProviderActivationId,
    string ServiceId,
    string MethodId,
    string CallKind,
    SunderRpcErrorKind ErrorKind,
    string ErrorCode,
    string? ValidationCode,
    string? ValidationPath,
    int? ValueUtf8Bytes,
    int? ValueMaximumDepth,
    int? ValueTotalNodes,
    int? ValueMaximumObjectMembers,
    long? ValueInlineDecodedBytes,
    string ExceptionType,
    string ExceptionFingerprint,
    WorkerTransportFailureKind? TransportFailureKind = null,
    long? TransportObserved = null,
    long? TransportLimit = null);

internal static class RuntimeRpcFailureContextStore
{
    internal const int MaximumExceptionTypeCharacters = 256;
    internal const int MaximumExceptionFingerprintCharacters = 64;
    private static readonly ConditionalWeakTable<SunderRpcException, RuntimeRpcFailureContext> Contexts = new();

    public static SunderRpcException AttachProviderFault(
        SunderRpcException exception,
        string callerPackageId,
        string callerPackageVersion,
        Guid? callerActivationId,
        SunderRpcProviderSnapshot provider,
        string serviceId,
        string methodId,
        string callKind,
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
        exceptionType = NormalizeExceptionType(exceptionType);
        var semanticValidation = providerException as RuntimeRpcSemanticValidationException;
        var measurement = semanticValidation?.Measurement;
        var transport = providerException as WorkerTransportResourceException;
        Contexts.Add(exception, new RuntimeRpcFailureContext(
            callerPackageId,
            callerPackageVersion,
            callerActivationId,
            provider.PackageId,
            provider.PackageVersion,
            provider.ProviderId,
            provider.ContractId,
            provider.ActivationId,
            serviceId,
            methodId,
            callKind,
            exception.Error.Kind,
            exception.Error.Code,
            semanticValidation?.ValidationCode,
            semanticValidation?.ValidationPath,
            measurement?.Utf8Bytes,
            measurement?.MaximumDepth,
            measurement?.TotalNodes,
            measurement?.MaximumObjectMembers,
            measurement?.InlineDecodedBytes,
            exceptionType,
            CreateExceptionFingerprint(exceptionType), transport?.Kind, transport?.Observed, transport?.Limit));
        return exception;
    }

    public static string CreateExceptionFingerprint(string exceptionType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exceptionType);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exceptionType)))
            .ToLowerInvariant();
    }

    private static string NormalizeExceptionType(string value)
    {
        var length = Math.Min(value.Length, MaximumExceptionTypeCharacters);
        var normalized = new char[length];
        for (var index = 0; index < length; index++)
        {
            var character = value[index];
            normalized[index] = char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '+' or '`'
                ? character
                : '_';
        }
        return normalized.Length == 0 ? "System.Exception" : new string(normalized);
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

internal sealed class RuntimeRpcSemanticValidationException : Exception
{
    private RuntimeRpcSemanticValidationException(
        string validationCode,
        string validationPath,
        RuntimeRpcValueMeasurement? measurement)
        : base("The provider output failed semantic RPC validation.")
    {
        ValidationCode = validationCode;
        ValidationPath = validationPath;
        Measurement = measurement;
    }

    public string ValidationCode { get; }

    public string ValidationPath { get; }

    public RuntimeRpcValueMeasurement? Measurement { get; }

    public static RuntimeRpcSemanticValidationException InvalidJson()
        => new("invalid-json", "$", null);

    public static RuntimeRpcSemanticValidationException Undefined()
        => new("undefined", "$", null);

    public static RuntimeRpcSemanticValidationException Schema(
        SunderRpcValidationFailure? failure,
        RuntimeRpcValueMeasurement measurement)
        => new(
            failure?.Code ?? "schema-invalid",
            NormalizePath(failure?.Path),
            measurement);

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '$') return "$";

        var normalized = new StringBuilder("$");
        var index = 1;
        while (index < path.Length && normalized.Length < 128)
        {
            if (path[index] == '.')
            {
                normalized.Append(".*");
                index++;
                while (index < path.Length && path[index] is not ('.' or '['))
                {
                    index++;
                }
                continue;
            }
            if (path[index] == '[')
            {
                normalized.Append("[]");
                index++;
                while (index < path.Length && path[index] != ']') index++;
                if (index < path.Length) index++;
                continue;
            }
            index++;
        }
        return normalized.Length <= 128
            ? normalized.ToString()
            : normalized.ToString(0, 128);
    }
}
