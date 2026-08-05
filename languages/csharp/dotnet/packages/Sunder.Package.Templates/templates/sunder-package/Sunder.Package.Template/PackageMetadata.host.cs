using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "SUNDER_PACKAGE_ID_CSHARP",
    Name = "SUNDER_PACKAGE_NAME_CSHARP",
    Summary = "Adds a custom Sunder package extension.",
    Icon = "assets/icon.png"
)]

[assembly: SunderPackageDependency(PackageId = "SUNDER_HOST_PACKAGE_ID_CSHARP", VersionRange = "SUNDER_HOST_PACKAGE_VERSION_RANGE_CSHARP")]
