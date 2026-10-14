namespace AutomaticEnvelopes.Api.Features.Knowledge.Services;

public interface IKnowledgeIngestionService
{
    Task<KnowledgeFile?> IngestDocumentAsync(
        string tenantId,
        string extractedText,
        string fileName,
        long bytes,
        string mime,
        CancellationToken ct = default);
}

public class KnowledgeIngestionService(
    IKnowledgeBaseService knowledgeBaseService,
    IKnowledgeCatalog catalog,
    ILogger<KnowledgeIngestionService> logger) : IKnowledgeIngestionService
{
    public async Task<KnowledgeFile?> IngestDocumentAsync(
        string tenantId,
        string extractedText,
        string fileName,
        long bytes,
        string mime,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(extractedText))
        {
            logger.LogWarning("Attempted to ingest empty text for tenant {TenantId}, file {FileName}", tenantId, fileName);
            return null;
        }

        var chunks = ChunkText(extractedText, 1000, 200);
        var existing = await catalog.FindByFilenameAsync(tenantId, fileName, ct);
        var documentId = existing?.Id ?? Guid.NewGuid();
        if (existing is not null)
        {
            await knowledgeBaseService.DeleteChunksForDocumentAsync(tenantId, documentId, ct);
        }

        await knowledgeBaseService.DeleteLegacyChunksAsync(tenantId, fileName, ct);
        await knowledgeBaseService.IngestChunksAsync(tenantId, chunks, fileName, documentId, ct);

        var file = new KnowledgeFile
        {
            Id = documentId,
            Filename = fileName,
            Mime = mime,
            Bytes = bytes,
            UploadedAt = DateTimeOffset.UtcNow,
            ChunkCount = chunks.Count,
            Status = "indexed"
        };
        await catalog.UpsertAsync(tenantId, file, ct);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Successfully ingested {ChunkCount} chunks for tenant {TenantId} from {FileName}",
                chunks.Count,
                tenantId,
                fileName);
        }

        return file;
    }

    public static string? MimeFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".md" or ".markdown" => "text/markdown",
        ".txt" => "text/plain",
        _ => null
    };

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
