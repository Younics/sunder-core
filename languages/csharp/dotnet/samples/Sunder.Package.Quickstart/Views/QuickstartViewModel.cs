using Sunder.Generated.Packages.Docs_Sunder_Quickstart_Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Quickstart.Views;

public sealed class QuickstartViewModel(IPackageContext context, IGreetingService greeting)
{
    public async ValueTask<string> GetDefaultNameAsync(CancellationToken cancellationToken)
    {
        var configured = await context.Settings.GetValueAsync("default-name", cancellationToken);
        return string.IsNullOrWhiteSpace(configured) ? "Sunder" : configured;
    }

    public async ValueTask<string> GreetAsync(string name, CancellationToken cancellationToken)
    {
        var result = await greeting.GreetAsync(name, cancellationToken);
        return $"{result.Message} Invocation {result.InvocationCount}.";
    }
}
