namespace Sunder.Registry.Contracts;

public sealed record RegistryContractDescriptorMetadata(
    string ContractId,
    string Version,
    string Sha256,
    long Size,
    string DescriptorDownloadUrl,
    string FirstPublisherPackageId,
    string FirstPublisherPackageVersion,
    DateTimeOffset PublishedAtUtc);
