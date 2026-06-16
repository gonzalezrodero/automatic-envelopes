namespace AutomaticEnvelopes.Api.Features.Knowledge.Services;

public interface IKnowledgeIngestionService
{
    Task IngestDocumentAsync(string tenantId, string extractedText, string fileName, CancellationToken ct = default);
}

public class KnowledgeIngestionService(
    IKnowledgeBaseService knowledgeBaseService,
    ILogger<KnowledgeIngestionService> logger) : IKnowledgeIngestionService
{
    public async Task IngestDocumentAsync(string tenantId, string extractedText, string fileName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(extractedText))
        {
            logger.LogWarning("Attempted to ingest empty text for tenant {TenantId}, file {FileName}", tenantId, fileName);
            return;
        }

        var chunks = ChunkText(extractedText, 1000, 200);
        await knowledgeBaseService.ClearTenantChunksAsync(tenantId, ct);
        await knowledgeBaseService.IngestChunksAsync(tenantId, chunks, fileName, ct);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Successfully ingested {ChunkCount} chunks for tenant {TenantId} from {FileName}", chunks.Count, tenantId, fileName);
        }
    }

    private static List<string> ChunkText(string text, int chunkSize, int overlap)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var chunks = new List<string>();
        var position = 0;

        while (position < text.Length)
        {
            var length = Math.Min(chunkSize, text.Length - position);
            chunks.Add(text.Substring(position, length));
            position += chunkSize - overlap;
        }

        return chunks;
    }
}