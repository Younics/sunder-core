using Sunder.Sdk.Rpc;
using Sunder.Sdk.Settings;

namespace Sunder.Sdk.Worker.Internal;

internal sealed record WorkerRuntimeConfiguration(
    IReadOnlyList<SunderWorkerProviderRegistration> Providers,
    PackageSettingsSchema? SettingsSchema,
    Func<CancellationToken, ValueTask>? OnCandidateStarted,
    Func<SunderWorkerGenerationContext, CancellationToken, ValueTask>? OnGenerationCommitted,
    Func<ISunderRpcClient, CancellationToken, ValueTask>? OnActivated,
    Func<SunderWorkerShutdownContext, CancellationToken, ValueTask>? OnShutdown)
{
    public static WorkerRuntimeConfiguration FromV1(SunderWorkerOptions options)
        => new(
            options.Providers,
            SettingsSchema: null,
            OnCandidateStarted: null,
            OnGenerationCommitted: null,
            options.OnActivated,
            options.OnShutdown);

    public static WorkerRuntimeConfiguration FromV2(
        SunderWorkerV2Options options,
        SunderWorkerContext context)
        => new(
            options.Providers,
            options.SettingsSchema,
            options.OnCandidateStarted,
            options.OnGenerationCommitted,
            options.OnActivated is null
                ? null
                : (_, cancellationToken) => options.OnActivated(context, cancellationToken),
            options.OnShutdown);
}
