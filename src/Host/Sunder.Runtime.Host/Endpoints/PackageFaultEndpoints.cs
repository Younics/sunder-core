using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class PackageFaultEndpoints
{
    public static IEndpointRouteBuilder MapPackageFaultEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/packages");
        group.MapPost(
            "{packageId}/fault",
            (string packageId, ReportPackageFaultRequest request, PackageFaultService faults) =>
            {
                if (request.Origin is PackageFailureOrigin.AppActivation
                    or PackageFailureOrigin.AppHostedView
                    or PackageFailureOrigin.AppUnhandledUi)
                {
                    throw new RuntimeValidationException("App presentation faults are client-local and cannot fault the shared Runtime package session.");
                }
                if (!faults.Report(packageId, request))
                {
                    throw new RuntimeStaleGenerationException("The package fault refers to a package or Runtime generation that is no longer active.");
                }
                return Results.NoContent();
            });

        return endpoints;
    }
}
