using System.Runtime.ExceptionServices;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Settings;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Storage;

namespace Sunder.Sdk.Worker.Internal;

internal sealed record WorkerRuntimeConfiguration(
    IReadOnlyList<SunderWorkerProviderRegistration> Providers,
    PackageSettingsSchema? SettingsSchema,
    IReadOnlyList<IPackageCallbackHandler> CallbackHandlers,
    IPackageAuthHandler? AuthHandler,
    SunderWorkerComposition? Composition,
    IReadOnlyList<PackageStorageKeyMigration> StateMigrations,
    IReadOnlyList<PackageStorageKeyMigration> SecretMigrations,
    Func<CancellationToken, ValueTask>? OnCandidateStarted,
    Func<SunderWorkerGenerationContext, CancellationToken, ValueTask>? OnGenerationCommitted,
    Func<SunderWorkerMigrationPreparationContext, CancellationToken, ValueTask>? OnBeforeStorageMigrations,
    Func<ISunderRpcClient, CancellationToken, ValueTask>? OnActivated,
    Func<SunderWorkerShutdownContext, CancellationToken, ValueTask>? OnShutdown)
{
    public static WorkerRuntimeConfiguration FromOptions(
        SunderWorkerOptions options,
        SunderWorkerContext context)
    {
        options.Composition?.Bind();
        return new(
            options.Providers,
            options.SettingsSchema,
            options.CallbackHandlers,
            options.AuthHandler,
            options.Composition,
            options.StateMigrations,
            options.SecretMigrations,
            CreateCandidateStart(options),
            CreateGenerationCommit(options),
            CreateMigrationPreparation(options),
            CreateActivation(options, context),
            CreateShutdown(options));
    }

    private static Func<CancellationToken, ValueTask>? CreateCandidateStart(
        SunderWorkerOptions options)
    {
        if (options.Composition is null && options.OnCandidateStarted is null) return null;
        return async cancellationToken =>
        {
            try
            {
                if (options.Composition is not null)
                {
                    await options.Composition.PrepareCandidateAsync(cancellationToken).ConfigureAwait(false);
                }
                if (options.OnCandidateStarted is not null)
                {
                    await options.OnCandidateStarted(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                await ThrowAfterCompositionCleanupAsync(options.Composition, exception).ConfigureAwait(false);
            }
        };
    }

    private static Func<SunderWorkerGenerationContext, CancellationToken, ValueTask>?
        CreateGenerationCommit(SunderWorkerOptions options)
    {
        if (options.Composition is null && options.OnGenerationCommitted is null) return null;
        return async (generation, cancellationToken) =>
        {
            try
            {
                if (options.Composition is not null)
                {
                    await options.Composition.CommitGenerationAsync(generation, cancellationToken).ConfigureAwait(false);
                }
                if (options.OnGenerationCommitted is not null)
                {
                    await options.OnGenerationCommitted(generation, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                await ThrowAfterCompositionCleanupAsync(options.Composition, exception).ConfigureAwait(false);
            }
        };
    }

    private static Func<SunderWorkerMigrationPreparationContext, CancellationToken, ValueTask>?
        CreateMigrationPreparation(SunderWorkerOptions options)
    {
        if (options.OnBeforeStorageMigrations is null) return null;
        return async (context, cancellationToken) =>
        {
            try
            {
                await options.OnBeforeStorageMigrations(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await ThrowAfterCompositionCleanupAsync(options.Composition, exception).ConfigureAwait(false);
            }
        };
    }

    private static Func<ISunderRpcClient, CancellationToken, ValueTask>? CreateActivation(
        SunderWorkerOptions options,
        SunderWorkerContext context)
    {
        if (options.StateMigrations.Count == 0
            && options.SecretMigrations.Count == 0
            && options.Composition is null
            && options.OnActivated is null)
        {
            return null;
        }
        return async (_, cancellationToken) =>
        {
            try
            {
                if (options.StateMigrations.Count > 0)
                {
                    await ((IPackageStorageKeyMigrator)context.Storage.State).MigrateKeysAsync(
                        options.StateMigrations,
                        cancellationToken).ConfigureAwait(false);
                }
                if (options.SecretMigrations.Count > 0)
                {
                    await ((IPackageStorageKeyMigrator)context.Secrets).MigrateKeysAsync(
                        options.SecretMigrations,
                        cancellationToken).ConfigureAwait(false);
                }
                if (options.Composition is not null)
                {
                    await options.Composition.ActivateAsync(context, cancellationToken).ConfigureAwait(false);
                }
                if (options.OnActivated is not null)
                {
                    await options.OnActivated(context, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                await ThrowAfterCompositionCleanupAsync(options.Composition, exception).ConfigureAwait(false);
            }
        };
    }

    private static Func<SunderWorkerShutdownContext, CancellationToken, ValueTask>? CreateShutdown(
        SunderWorkerOptions options)
    {
        if (options.Composition is null && options.OnShutdown is null) return null;
        return async (context, cancellationToken) =>
        {
            Exception? callbackFailure = null;
            try
            {
                if (options.OnShutdown is not null)
                {
                    await options.OnShutdown(context, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                callbackFailure = exception;
            }
            try
            {
                if (options.Composition is not null)
                {
                    await options.Composition.ShutdownAsync(context, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                throw callbackFailure is null
                    ? exception
                    : new AggregateException(
                        "Worker shutdown callback and composition cleanup both failed.",
                        callbackFailure,
                        exception);
            }
            if (callbackFailure is not null)
            {
                ExceptionDispatchInfo.Capture(callbackFailure).Throw();
            }
        };
    }

    private static async ValueTask ThrowAfterCompositionCleanupAsync(
        SunderWorkerComposition? composition,
        Exception failure)
    {
        try
        {
            if (composition is not null)
            {
                await composition.ShutdownAsync(
                    new SunderWorkerShutdownContext("lifecycle-failed"),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception cleanupFailure)
        {
            throw new AggregateException(
                "Worker lifecycle callback and composition cleanup both failed.",
                failure,
                cleanupFailure);
        }
        ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
