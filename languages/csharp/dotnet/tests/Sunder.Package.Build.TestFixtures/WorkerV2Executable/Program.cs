using Sunder.Sdk.Packaging;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Settings;
using Sunder.Sdk.Worker;

[assembly: SunderPackage(Id = "test.worker.v2.executable", Name = "Worker V2 Executable Fixture")]

await SunderWorkerV2.RunAsync(context => new SunderWorkerV2Options([])
{
    SettingsSchema = new PackageSettingsSchema(
        "Fixture settings.",
        [
            new PackageSettingsSection(
                "general",
                "General",
                null,
                [
                    new PackageSettingsField(
                        "enabled",
                        "Enabled",
                        PackageSettingsFieldKind.Boolean,
                        defaultValue: "true"),
                ]),
        ]),
    OnActivated = async (activationContext, cancellationToken) =>
    {
        await activationContext.Settings.GetValueAsync("enabled", cancellationToken);
        await activationContext.State.ContainsKeyAsync("initialized", cancellationToken);
        await activationContext.Files.ReadAsync("fixture.txt", cancellationToken);
        await activationContext.Secrets.GetSecretAsync("fixture.token", cancellationToken);
        await activationContext.Logging.Events.WriteAsync(
            PackageLogLevel.Information,
            "fixture.activated",
            "Worker fixture activated.",
            cancellationToken: cancellationToken);
    },
});
