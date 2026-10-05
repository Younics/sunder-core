using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Template.Views;

public sealed class DefaultPackageViewModel(IPackageContext packageContext, ISampleService sample)
{
    public string PackageId { get; } = packageContext.PackageId;

    public async Task<string> GreetAsync(string name, CancellationToken cancellationToken)
    {
        var response = await sample.GreetAsync(new SampleRequest { Name = new SampleText { Value = name } }, cancellationToken);
        return response.Message.Value;
    }
}
