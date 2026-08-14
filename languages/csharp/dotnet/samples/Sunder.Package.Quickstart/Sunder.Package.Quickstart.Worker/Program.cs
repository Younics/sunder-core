using Sunder.Package.Quickstart.Protocol;
using Sunder.Package.Quickstart.Protocol.Generated;
using Sunder.Sdk.Settings;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Quickstart.Worker;

internal static class Program
{
    public static Task Main()
        => SunderWorker.RunAsync(context =>
        {
            var descriptor = QuickstartProtocol.Greeting;
            var provider = new GreetingProvider(context);
            var handler = new DocsSunderQuickstartGreetingRpcHandler(provider);
            return new SunderWorkerOptions(
            [
                new SunderWorkerProviderRegistration(
                    "docs.sunder.quickstart.greeting",
                    descriptor.ContractId,
                    descriptor.Version,
                    descriptor.Sha256,
                    descriptor.AbiSha256
                        ?? throw new InvalidDataException("The greeting descriptor is missing its ABI hash."),
                    handler),
            ])
            {
                SettingsSchema = CreateSettingsSchema(),
                OnShutdown = (_, _) =>
                {
                    provider.Dispose();
                    return ValueTask.CompletedTask;
                },
            };
        });

    private static PackageSettingsSchema CreateSettingsSchema()
        => new(
            summary: "Quickstart settings.",
            sections:
            [
                new PackageSettingsSection(
                    sectionId: "greeting",
                    title: "Greeting",
                    description: "Defaults used by the quickstart view.",
                    fields:
                    [
                        new PackageSettingsField(
                            key: "default-name",
                            label: "Default name",
                            kind: PackageSettingsFieldKind.Text,
                            defaultValue: "Sunder"),
                    ]),
            ]);
}
