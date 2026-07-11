namespace Sunder.Runtime.Contracts;

public sealed record PackageDataValueResponse(bool Found, string? Value);

public sealed record PackageDataKeysResponse(IReadOnlyList<string> Keys);

public sealed record SetPackageDataValueRequest(string Value);

public sealed record PackageFileEntry(string RelativePath, long Length);

public sealed record PackageFileListResponse(IReadOnlyList<PackageFileEntry> Files);
