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
    string ExceptionFingerprint);

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
            CreateExceptionFingerprint(exceptionType)));
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
        string? error,
        RuntimeRpcValueMeasurement measurement)
        => new(Classify(error), NormalizePath(error), measurement);

    private static string Classify(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "schema-invalid";
        if (error.Contains("must be of type", StringComparison.Ordinal)) return "type-mismatch";
        if (error.Contains("must contain Unicode scalar values", StringComparison.Ordinal)) return "invalid-unicode";
        if (error.Contains("const value", StringComparison.Ordinal)) return "const-mismatch";
        if (error.Contains("enum value", StringComparison.Ordinal)) return "enum-mismatch";
        if (error.Contains("missing required property", StringComparison.Ordinal)) return "required-property";
        if (error.Contains("undeclared property", StringComparison.Ordinal)) return "undeclared-property";
        if (error.Contains("invalid, duplicate, or case-colliding property", StringComparison.Ordinal)) return "property-name-invalid";
        if (error.Contains(" characters; expected ", StringComparison.Ordinal)) return "string-length";
        if (error.Contains("outside its declared length bounds", StringComparison.Ordinal)) return "string-length";
        if (error.Contains(" properties; expected ", StringComparison.Ordinal)
            || error.Contains(" properties; expected at most ", StringComparison.Ordinal)) return "object-size";
        if (error.Contains("outside its declared property-count bounds", StringComparison.Ordinal)) return "object-size";
        if (error.Contains(" items; expected ", StringComparison.Ordinal)) return "array-size";
        if (error.Contains("outside its declared item-count bounds", StringComparison.Ordinal)) return "array-size";
        if (error.Contains("base64", StringComparison.Ordinal)) return "base64-invalid";
        if (error.Contains("content-reference", StringComparison.Ordinal)) return "content-reference-invalid";
        if (error.Contains("format", StringComparison.Ordinal)
            || error.Contains("canonical UTC date-time", StringComparison.Ordinal)) return "format-invalid";
        if (error.Contains("integer", StringComparison.Ordinal)) return "integer-invalid";
        if (error.Contains("numeric", StringComparison.Ordinal)
            || error.Contains("number", StringComparison.Ordinal)
            || error.Contains("floating-point", StringComparison.Ordinal)) return "number-invalid";
        if (error.Contains("union variant", StringComparison.Ordinal)) return "union-mismatch";
        return "schema-invalid";
    }

    private static string NormalizePath(string? error)
    {
        if (string.IsNullOrEmpty(error) || error[0] != '$') return "$";

        var normalized = new StringBuilder("$");
        var index = 1;
        while (index < error.Length && normalized.Length < 128)
        {
            if (char.IsWhiteSpace(error[index])) break;
            if (error[index] == '.')
            {
                normalized.Append(".*");
                index++;
                while (index < error.Length
                       && !char.IsWhiteSpace(error[index])
                       && error[index] is not ('.' or '['))
                {
                    index++;
                }
                continue;
            }
            if (error[index] == '[')
            {
                normalized.Append("[]");
                index++;
                while (index < error.Length && error[index] != ']') index++;
                if (index < error.Length) index++;
                continue;
            }
            index++;
        }
        return normalized.Length <= 128
            ? normalized.ToString()
            : normalized.ToString(0, 128);
    }
}
