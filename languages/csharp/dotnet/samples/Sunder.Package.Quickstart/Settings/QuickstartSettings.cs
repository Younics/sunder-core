using Sunder.Sdk.Settings;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Quickstart;

public sealed class QuickstartSettings : ISunderWorkerSettingsSchemaProvider
{
    public PackageSettingsSchema SettingsSchema { get; } = new(
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
