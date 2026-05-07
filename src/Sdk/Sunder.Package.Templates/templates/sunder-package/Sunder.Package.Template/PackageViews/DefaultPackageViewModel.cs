using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Template.PackageViews;

public sealed class DefaultPackageViewModel(IPackageContext packageContext)
{
    public string PackageId { get; } = packageContext.PackageId;
}
