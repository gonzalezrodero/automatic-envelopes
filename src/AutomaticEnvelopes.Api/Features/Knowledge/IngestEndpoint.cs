using System.Security.Claims;
using AutomaticEnvelopes.Api.Features.Knowledge.Extractors;
using AutomaticEnvelopes.Api.Features.Knowledge.Services;
using AutomaticEnvelopes.Api.Features.Tenancy;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Wolverine.Http;

namespace AutomaticEnvelopes.Api.Features.Knowledge;

public class IngestEndpoint(ILogger<IngestEndpoint> logger)
{
    [Authorize(Policy = "TenantAdmin")]
    [WolverinePost("/api/admin/ingest/{tenantId}")]
    [EnableRateLimiting("AdminPolicy")]
    // Kestrel only. Amazon.Lambda.AspNetCoreServer does not set IHttpMaxRequestBodySizeFeature.
    [RequestSizeLimit(IngestLimits.MaxUploadBytes)]
    public async Task<IResult> Ingest(
        string tenantId,
        ClaimsPrincipal user,
        IFormFile file,
        IDocumentSession session,
        IEnumerable<IDocumentExtractor> extractors,
        IKnowledgeIngestionService ingestionService,
        CancellationToken ct)
    {
        if (TenantRequest.Reject(user, tenantId) is { } rejected)
        {
            return rejected;
        }

        if (file == null || file.Length == 0)
        {
            return Results.BadRequest(new { Error = IngestLimits.EmptyFile });
        }

        if (IngestLimits.RejectFile(file) is { } tooLarge)
        {
            return tooLarge;
        }

        if (IngestLimits.RejectReservedName(file.FileName) is { } reserved)
        {
            return reserved;
        }

        var tenant = await session.LoadAsync<TenantProfile>(tenantId, ct);
        if (tenant == null)
        {
            return Results.BadRequest(new { Error = TenantRequest.OrganizationNotFound });
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var extractor = extractors.FirstOrDefault(e => e.SupportedExtensions.Contains(extension));

        if (extractor == null)
        {
            return Results.BadRequest(new { Error = IngestLimits.UnsupportedType });
        }

        var readingPdf = false;
        try
        {
            using var stream = file.OpenReadStream();
            readingPdf = extractor is PdfDocumentExtractor;
            if (IngestLimits.RejectPdf(stream, extractor) is { } tooManyPages)
            {
                return tooManyPages;
            }

            string extractedText = await extractor.ExtractTextAsync(stream);
            readingPdf = false;

            if (string.IsNullOrWhiteSpace(extractedText))
            {
                return Results.BadRequest(new { Error = IngestLimits.NoReadableText });
            }

            if (IngestLimits.RejectText(extractedText) is { } tooLong)
            {
                return tooLong;
            }

            var fileName = Path.GetFileName(file.FileName);
            var mime = KnowledgeIngestionService.MimeFor(fileName);
            if (mime is null)
            {
                return Results.BadRequest(new { Error = IngestLimits.UnsupportedType });
            }

            await ingestionService.IngestDocumentAsync(tenantId, extractedText, fileName, file.Length, mime, ct);

            return Results.Ok(new { Message = "Ya hemos guardado el documento. El asistente ya puede usarlo." });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (readingPdf)
            {
                return IngestLimits.RejectPdfRead(ex, logger, tenantId);
            }

            return IngestLimits.IngestionFailed(ex, logger, tenantId);
        }
    }
}