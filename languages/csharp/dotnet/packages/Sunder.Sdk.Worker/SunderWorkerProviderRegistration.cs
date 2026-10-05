using System.Collections.ObjectModel;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.Worker;

/// <summary>Binds one manifest-declared provider identity to its activation-scoped RPC handler.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderWorkerProviderRegistration
{
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> _declaredErrors;

    /// <summary>Creates a provider registration.</summary>
    public SunderWorkerProviderRegistration(
        string providerId,
        string contractId,
        string contractVersion,
        string descriptorSha256,
        string abiSha256,
        ISunderRpcServiceHandler handler)
    {
        ArgumentNullException.ThrowIfNull(contractVersion);
        ArgumentNullException.ThrowIfNull(descriptorSha256);
        ArgumentNullException.ThrowIfNull(abiSha256);
        if (!PackageId.TryParse(providerId, out _))
        {
            throw new ArgumentException("The provider id must be a canonical package-style identifier.", nameof(providerId));
        }
        if (!PackageId.TryParse(contractId, out _))
        {
            throw new ArgumentException("The contract id must be a canonical package-style identifier.", nameof(contractId));
        }
        if (contractVersion.Length > SemanticVersion.MaximumLength || !SemanticVersion.TryParse(contractVersion, out _))
        {
            throw new ArgumentException("The contract version must be a bounded strict semantic version.", nameof(contractVersion));
        }
        if (!IsSha256(descriptorSha256))
        {
            throw new ArgumentException(
                "The descriptor SHA-256 must be 64 lowercase hexadecimal characters.",
                nameof(descriptorSha256));
        }
        if (!IsSha256(abiSha256))
        {
            throw new ArgumentException(
                "The ABI SHA-256 must be 64 lowercase hexadecimal characters.",
                nameof(abiSha256));
        }

        ArgumentNullException.ThrowIfNull(handler);
        ProviderId = providerId;
        ContractId = contractId;
        ContractVersion = contractVersion;
        DescriptorSha256 = descriptorSha256;
        AbiSha256 = abiSha256;
        Handler = handler;
        _declaredErrors = SnapshotDeclaredErrors(
            (handler as ISunderRpcDeclaredErrorMetadata)?.DeclaredErrors);
    }

    /// <summary>Gets the manifest-declared provider identifier.</summary>
    public string ProviderId { get; }

    /// <summary>Gets the bundled contract identifier.</summary>
    public string ContractId { get; }

    /// <summary>Gets the exact bundled contract semantic version.</summary>
    public string ContractVersion { get; }

    /// <summary>Gets the lowercase canonical descriptor SHA-256.</summary>
    public string DescriptorSha256 { get; }

    /// <summary>Gets the lowercase domain-separated structural ABI SHA-256.</summary>
    public string AbiSha256 { get; }

    /// <summary>Gets the activation-scoped provider handler.</summary>
    public ISunderRpcServiceHandler Handler { get; }

    /// <summary>Gets the immutable descriptor-declared domain errors keyed by service and method id.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> DeclaredErrors
        => _declaredErrors;

    internal bool IsDeclaredDomainError(string serviceId, string methodId, string errorCode)
        => _declaredErrors.TryGetValue(serviceId, out var methods)
           && methods.TryGetValue(methodId, out var errors)
           && errors.Contains(errorCode, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> SnapshotDeclaredErrors(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>? declaredErrors)
    {
        if (declaredErrors is null)
        {
            return new ReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(
                new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.Ordinal));
        }

        var services = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.Ordinal);
        foreach (var (serviceId, declaredMethods) in declaredErrors)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
            ArgumentNullException.ThrowIfNull(declaredMethods);
            var methods = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var (methodId, declaredCodes) in declaredMethods)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(methodId);
                ArgumentNullException.ThrowIfNull(declaredCodes);
                var codes = declaredCodes.ToArray();
                if (codes.Any(static code => string.IsNullOrWhiteSpace(code)
                                             || code.Length > 128
                                             || code.Any(static character =>
                                                 !(char.IsAsciiLetterOrDigit(character)
                                                   || character is '.' or '-' or '_')))
                    || codes.Distinct(StringComparer.Ordinal).Count() != codes.Length)
                {
                    throw new ArgumentException(
                        $"Worker provider method '{serviceId}/{methodId}' has invalid declared domain errors.",
                        nameof(declaredErrors));
                }
                methods.Add(methodId, Array.AsReadOnly(codes));
            }
            services.Add(
                serviceId,
                new ReadOnlyDictionary<string, IReadOnlyList<string>>(methods));
        }
        return new ReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(services);
    }

    private static bool IsSha256(string value)
        => value.Length == 64
           && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
