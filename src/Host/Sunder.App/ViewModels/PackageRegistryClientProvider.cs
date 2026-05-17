using Sunder.App.Services;

namespace Sunder.App.ViewModels;

internal sealed class PackageRegistryClientProvider(
    Func<string> getRegistryUrlText,
    Func<Uri, IRegistryApiClient> createRegistryClient)
{
    public bool TryCreate(out IRegistryApiClient registryClient, out string? errorMessage)
    {
        registryClient = null!;
        if (!TryResolve(out var registryUrl, out errorMessage) || registryUrl is null)
        {
            return false;
        }

        registryClient = createRegistryClient(registryUrl);
        return true;
    }

    public bool TryResolve(out Uri? registryUrl, out string? errorMessage)
    {
        if (!RegistryUrlHelper.TryParse(getRegistryUrlText(), out registryUrl) || registryUrl is null)
        {
            errorMessage = "Enter a valid HTTP or HTTPS registry URL.";
            return false;
        }

        errorMessage = null;
        return true;
    }
}
