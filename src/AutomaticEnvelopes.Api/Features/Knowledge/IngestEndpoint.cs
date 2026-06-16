using AutomaticEnvelopes.Api.Features.Knowledge.Extractors;
using AutomaticEnvelopes.Api.Features.Knowledge.Services;
using AutomaticEnvelopes.Api.Features.Tenancy;
using Marten;
using Wolverine.Http;

namespace AutomaticEnvelopes.Api.Features.Knowledge;

public class IngestEndpoint
{
    [WolverinePost("/api/admin/ingest/{tenantId}")]
    public async Task<IResult> Ingest(
        string tenantId,
        IFormFile file,
        IDocumentSession session,
        IEnumerable<IDocumentExtractor> extractors,
        IKnowledgeIngestionService ingestionService,
        CancellationToken ct)
    {
        if (file == null || file.Length == 0)
        {
            return Results.BadRequest(new { Error = "No file uploaded." });
        }

        var tenant = await session.LoadAsync<TenantProfile>(tenantId, ct);
        if (tenant == null)
        {
            return Results.BadRequest(new { Error = $"Tenant '{tenantId}' is not registered." });
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var extractor = extractors.FirstOrDefault(e => e.SupportedExtensions.Contains(extension));

        if (extractor == null)
        {
            return Results.BadRequest(new { Error = $"File type '{extension}' is not supported. Please upload .pdf, .md, or .txt." });
        }

        try
        {
            using var stream = file.OpenReadStream();
            string extractedText = await extractor.ExtractTextAsync(stream);

            if (string.IsNullOrWhiteSpace(extractedText))
            {
                return Results.BadRequest(new { Error = "The uploaded file contains no readable text." });
            }

            await ingestionService.IngestDocumentAsync(tenantId, extractedText, file.FileName, ct);

            return Results.Ok(new { Message = $"Successfully ingested {file.FileName} into the vector database." });
        }
        catch (Exception ex)
        {
            return Results.Problem(detail: ex.Message, title: "Ingestion failed");
        }
    }
}