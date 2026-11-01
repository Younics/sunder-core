using Sunder.Generated.Packages.SUNDER_PACKAGE_NAMESPACE_Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Template.Views;

public sealed class DefaultPackageViewModel(IPackageContext packageContext, ISampleService sample)
{
    public string PackageId { get; } = packageContext.PackageId;

    public Task<string> GreetAsync(string name, CancellationToken cancellationToken)
        => sample.GreetAsync(name, cancellationToken);
}
