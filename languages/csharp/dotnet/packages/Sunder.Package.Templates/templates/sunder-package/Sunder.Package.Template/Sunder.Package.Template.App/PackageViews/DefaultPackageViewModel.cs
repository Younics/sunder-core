using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Template.App.PackageViews;

public sealed class DefaultPackageViewModel(IPackageContext packageContext)
{
    public string PackageId { get; } = packageContext.PackageId;
}
