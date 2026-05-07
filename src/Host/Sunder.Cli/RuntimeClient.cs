using System.Net.Http.Json;
using Sunder.Protocol;

namespace Sunder.Cli;

internal sealed class RuntimeClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public RuntimeClient(Uri runtimeUrl)
    {
        _httpClient = new HttpClient { BaseAddress = runtimeUrl };
    }

    public async Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken cancellationToken)
        => await _httpClient.GetFromJsonAsync<IReadOnlyList<InstalledPackageDescriptor>>("api/packages/installed", cancellationToken) ?? [];

    public Task<PackageOperationResult> InstallPackageFromPathAsync(string packagePath, CancellationToken cancellationToken)
        => SendPackageOperationAsync(
            () => _httpClient.PostAsJsonAsync("api/packages/install/local", new PackageInstallFromPathRequest(packagePath), cancellationToken),
            cancellationToken);

    public Task<PackageOperationResult> UpgradePackageFromPathAsync(
        string packageId,
        string packagePath,
        bool allowDowngrade,
        bool reinstall,
        CancellationToken cancellationToken)
        => SendPackageOperationAsync(
            () => _httpClient.PostAsJsonAsync(
                $"api/packages/{Uri.EscapeDataString(packageId)}/upgrade/local",
                new PackageUpgradeFromPathRequest(packagePath, allowDowngrade, reinstall),
                cancellationToken),
            cancellationToken);

    private async Task<PackageOperationResult> SendPackageOperationAsync(
        Func<Task<HttpResponseMessage>> sendAsync,
        CancellationToken cancellationToken)
    {
        using var response = await sendAsync();
        var result = await response.Content.ReadFromJsonAsync<PackageOperationResult>(cancellationToken: cancellationToken);
        if (result is not null)
        {
            return result;
        }

        return response.IsSuccessStatusCode
            ? new PackageOperationResult(true, null, RequiresAppRestart: true, [], [])
            : new PackageOperationResult(false, response.ReasonPhrase, RequiresAppRestart: false, [], [response.ReasonPhrase ?? "Package operation failed."]);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
