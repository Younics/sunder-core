using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class ContentTransferEndpoints
{
    public static IEndpointRouteBuilder MapContentTransferEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var uploads = endpoints.MapGroup("/uploads");
        uploads.MapPost("packages", (HttpRequest request, RuntimeContentTransferService transfers, CancellationToken cancellationToken)
                => UploadAsync(request, transfers, RuntimeUploadKind.Package, cancellationToken))
            .WithMetadata(new RequestSizeLimitAttribute(RuntimeContentTransferStore.MaxPackageUploadBytes));
        uploads.MapPost("stacks", (HttpRequest request, RuntimeContentTransferService transfers, CancellationToken cancellationToken)
                => UploadAsync(request, transfers, RuntimeUploadKind.Stack, cancellationToken))
            .WithMetadata(new RequestSizeLimitAttribute(RuntimeContentTransferStore.MaxStackUploadBytes));
        uploads.MapPost("stack-media", (HttpRequest request, RuntimeContentTransferService transfers, CancellationToken cancellationToken)
                => UploadAsync(request, transfers, RuntimeUploadKind.StackMedia, cancellationToken))
            .WithMetadata(new RequestSizeLimitAttribute(RuntimeContentTransferStore.MaxMediaUploadBytes));

        endpoints.MapGet("/downloads/{downloadId}", async (
            string downloadId,
            HttpResponse response,
            RuntimeContentTransferService transfers,
            CancellationToken cancellationToken) =>
        {
            var lease = transfers.AcquireDownload(downloadId);

            response.ContentType = lease.ContentType;
            response.ContentLength = lease.Length;
            response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileNameStar = lease.FileName,
            }.ToString();
            response.Headers["X-Content-SHA256"] = lease.ContentHash;
            try
            {
                await using var stream = new FileStream(lease.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await stream.CopyToAsync(response.Body, cancellationToken);
            }
            finally
            {
                transfers.Release(lease);
            }
        });

        return endpoints;
    }

    private static async Task<IResult> UploadAsync(
        HttpRequest request,
        RuntimeContentTransferService transfers,
        RuntimeUploadKind kind,
        CancellationToken cancellationToken)
    {
        var fileName = ContentDispositionHeaderValue.TryParse(request.Headers.ContentDisposition, out var disposition)
            ? disposition.FileNameStar ?? disposition.FileName
            : null;
        var descriptor = await transfers.CreateUploadAsync(
            kind,
            request.Body,
            request.ContentLength,
            request.Headers["X-Content-SHA256"].FirstOrDefault(),
            fileName,
            request.ContentType,
            cancellationToken);
        return Results.Ok(descriptor);
    }
}
