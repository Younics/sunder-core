using Sunder.Sdk.Packaging;
using Sunder.Registry.Contracts;

namespace Sunder.Cli;

internal sealed class DeveloperPublishCommandHandler(
    ICliRuntimePublishClient? runtime,
    IRegistryManageClient? registry,
    ArchiveValidationService archives,
    CliOutput output,
    ICliProgress progress,
    CliOptions options,
    IRegistryPublishCredentialReader credentials)
{
    public async Task<int> ExecuteAsync(PublishPackageCommand command, CancellationToken token)
    {
        var fullPath = Path.GetFullPath(command.File);
        var validation = await archives.ValidatePackageAsync(fullPath, token).ConfigureAwait(false);
        if (!validation.Success || validation.Manifest?.Id is null || validation.Manifest.Version is null)
            return CliRenderers.PackageValidation(output, validation);

        var setLatest = ResolveSetLatest(validation.Manifest.Version, command.SetLatest);
        progress.Report(command.DevLocal
            ? $"Publishing {validation.Manifest.Id} {validation.Manifest.Version} to the development Registry..."
            : command.CredentialSource == RegistryCredentialSource.Runtime
                ? $"Publishing {validation.Manifest.Id} {validation.Manifest.Version} through the Runtime..."
                : $"Publishing {validation.Manifest.Id} {validation.Manifest.Version} with a scoped automation credential...");
        RegistryPublishPackageResponse result;
        if (command.DevLocal)
        {
            result = await RequireRegistry().PublishLocalPackageAsync(fullPath, setLatest, token).ConfigureAwait(false);
        }
        else if (command.CredentialSource == RegistryCredentialSource.Runtime)
        {
            result = await RequireRuntime().PublishRegistryPackageAsync(
                options.RequireRegistryApiUrl().AbsoluteUri, fullPath, setLatest, token).ConfigureAwait(false);
        }
        else
        {
            using var credential = await credentials.ReadAsync(command.CredentialSource, token).ConfigureAwait(false);
            result = await RequireRegistry().PublishPackageAsync(fullPath, setLatest, credential, token)
                .ConfigureAwait(false);
        }
        return CliRenderers.PackagePublish(output, result);
    }

    internal static bool ResolveSetLatest(string version, bool? explicitValue)
        => explicitValue
           ?? (SemanticVersion.TryParse(version, out var parsed) && parsed.Prerelease is null);

    private ICliRuntimePublishClient RequireRuntime()
        => runtime ?? throw new InvalidOperationException("Authenticated publication requires a Runtime client.");

    private IRegistryManageClient RequireRegistry()
        => registry ?? throw new InvalidOperationException("Development publication requires a Registry client.");
}
