using System.ComponentModel;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Rpc;

/// <summary>Provides exact pre-dispatch checks and cancellation translation for generated ordinary service clients.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public static class SunderRpcServiceCall
{
    /// <summary>Revalidates all eleven provider identity fields through the actual operation scope before dispatch.</summary>
    /// <remarks>Call after request preparation and before InvokeAsync or the first SubscribeAsync move; global discovery is not a substitute.</remarks>
    public static async ValueTask EnsureCurrentAsync(ISunderRpcCallScope scope, SunderRpcProviderSnapshot provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(provider);
        cancellationToken.ThrowIfCancellationRequested();
        if (provider.State != SunderRpcProviderState.Active) throw SunderRpcServiceLocator.Stale();
        SunderRpcServiceLocator.Validate(provider);
        await SunderRpcServiceLocator.ResolveAsync(scope, provider, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rejects a retired generated proxy with a structured stale error, rather than pretending its caller cancelled.</summary>
    public static void ThrowIfRetired(CancellationToken retirementToken)
    {
        if (retirementToken.IsCancellationRequested) throw SunderRpcServiceLocator.Stale();
    }

    /// <summary>Translates cancellation only when an original caller token or an explicit retirement signal identifies its source.</summary>
    /// <remarks>
    /// Pass original method/enumeration tokens, never temporary linked tokens. The method caller wins if both originals cancel.
    /// Remote Cancelled errors remain the inner exception of caller cancellation, preserving structured dispatch/retry metadata.
    /// Non-cancellation failures are returned unchanged. Generated catch blocks rethrow unchanged exceptions with a bare throw.
    /// </remarks>
    public static Exception TranslateCancellation(Exception exception, CancellationToken callerCancellationToken,
        CancellationToken enumerationCancellationToken = default, CancellationToken retirementToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is not OperationCanceledException
            && exception is not SunderRpcException { Error.Kind: SunderRpcErrorKind.Cancelled }) return exception;

        var original = callerCancellationToken.IsCancellationRequested ? callerCancellationToken
            : enumerationCancellationToken.IsCancellationRequested ? enumerationCancellationToken : default;
        if (original.IsCancellationRequested)
        {
            if (exception is OperationCanceledException cancelled && cancelled.CancellationToken == original) return exception;
            return new OperationCanceledException(exception.Message, exception, original);
        }
        if (!retirementToken.IsCancellationRequested) return exception;
        var rpc = exception as SunderRpcException;
        return new SunderRpcException(new(SunderRpcErrorKind.Unavailable, "rpc.binding.retired",
            "The selected service lifetime ended.",
            rpc?.Error.DispatchState ?? SunderRpcDispatchState.PossiblyDispatched,
            rpc?.Error.RetrySafety ?? SunderRpcRetrySafety.Unsafe));
    }
}
