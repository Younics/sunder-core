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
        var response = await greeting.CreateAsync(new GreetRequest { Name = new GreetingName { Value = name } }, cancellationToken);
        return $"{response.Message.Value} Invocation {response.InvocationCount.Value}.";
    }
}
