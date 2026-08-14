namespace Sunder.Registry.Contracts;

public sealed record RegistryContractDescriptorMetadata(
    string ContractId,
    string Version,
    string DescriptorSha256,
    string AbiSha256,
    string Exposure,
    long Size,
    string DescriptorDownloadUrl,
    string FirstPublisherPackageId,
    string FirstPublisherPackageVersion,
    DateTimeOffset PublishedAtUtc);
