using System.Net.Http.Json;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed partial class RuntimeManagementClient
{
    public async Task<RuntimeDevPackageImportsPrepared> PrepareDevPackageImportsAsync(RuntimePrepareDevPackageImportsRequest request, CancellationToken token = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, CreateUri("dev-packages/prepare-imports")) { Content = JsonContent.Create(request) };
        message.Options.Set(RuntimeAuthenticatedHttpMessageHandler.RequiredFeaturesKey, new[] { RuntimeProtocolFeatures.DevPackageImportsV1 });
        using var response = await _transport.SendAsync(message, cancellationToken: token).ConfigureAwait(false);
        return await _responses.ReadRequiredJsonAsync<RuntimeDevPackageImportsPrepared>(response, token).ConfigureAwait(false);
    }
}
