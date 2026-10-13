using FundLedger.Api.Hosting;
using FundLedger.Application.Attachments;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace FundLedger.Api.Endpoints;

/// <summary>Files attached to entries (PRD §18, TRD §9).</summary>
internal static class AttachmentEndpoints
{
    /// <summary>The largest upload any organization may allow (20 MB) plus a little for the form envelope.</summary>
    private const long RequestLimit = 21L * 1024 * 1024;

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1").WithTags("Attachments").RequireAuthorization(Policies.User).AddEndpointFilter<TenantTransactionFilter>();

        g.MapPost("/transactions/{id:guid}/attachments", async Task<Created<AttachmentDto>> (Guid id, [FromForm] IFormFile file, AttachmentService s, HttpContext h) =>
            {
                await using var stream = file.OpenReadStream();
                var dto = await s.UploadAsync(id, file.FileName, stream, h.RequestAborted).ConfigureAwait(false);
                return TypedResults.Created($"/api/v1/attachments/{dto.Id}", dto);
            })
            .DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(RequestLimit))
            .WithName("UploadAttachment").WithSummary("Attach a photo or PDF to an entry (multipart, field \"file\"). Admin or the entry's own recorder.")
            .Accepts<IFormFile>("multipart/form-data")
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(413).ProducesProblem(415).ProducesProblem(503);

        g.MapGet("/transactions/{id:guid}/attachments", async Task<Ok<IReadOnlyList<AttachmentDto>>> (Guid id, AttachmentService s, HttpContext h) =>
                TypedResults.Ok(await s.ListAsync(id, h.RequestAborted).ConfigureAwait(false)))
            .WithName("ListAttachments").WithSummary("The files attached to an entry (hidden ones only for Admins).").ProducesProblem(404);

        g.MapGet("/attachments/{id:guid}", async Task<IResult> (Guid id, AttachmentService s, HttpContext h) =>
            {
                var file = await s.DownloadAsync(id, h.RequestAborted).ConfigureAwait(false);
                h.Response.Headers.CacheControl = "private, no-store";
                if (file.Url is { } url)
                {
                    return TypedResults.Redirect(url.ToString());           // 302 to a link that works for 60 seconds
                }

                h.Response.Headers.ContentDisposition = $"inline; filename=\"{file.FileName.Replace("\"", string.Empty, StringComparison.Ordinal)}\"";
                return TypedResults.Stream(file.Content!, file.ContentType);
            })
            .WithName("GetAttachment").WithSummary("The file, after checking access: a 302 to a 60-second link, or the bytes themselves.").ProducesProblem(404);

        g.MapPost("/attachments/{id:guid}/hide", async Task<Ok<AttachmentDto>> (Guid id, HideAttachmentRequest b, AttachmentService s, HttpContext h) =>
                TypedResults.Ok(await s.HideAsync(id, b, h.RequestAborted).ConfigureAwait(false)))
            .WithName("HideAttachment").WithSummary("Hide a file with a reason (Admin). It is kept, never deleted.").ProducesProblem(400).ProducesProblem(403).ProducesProblem(404);
    }
}
