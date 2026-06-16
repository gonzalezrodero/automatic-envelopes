using AutomaticEnvelopes.Api.Core.Entities;
using Marten;
using System.Collections.Concurrent;

namespace AutomaticEnvelopes.Api.Features.Knowledge.Services;

public interface IKnowledgeBaseService
{
    Task<IReadOnlyList<DocumentChunk>> SearchAsync(string tenantId, string query, int limit = 3, CancellationToken ct = default);
    Task IngestChunksAsync(string tenantId, IEnumerable<string> contents, string source, CancellationToken ct = default);
    Task ClearTenantChunksAsync(string tenantId, CancellationToken ct = default);
}

public class KnowledgeBaseService(
    IDocumentStore store,
    IEmbeddingService embeddingService)
    : IKnowledgeBaseService
{
    public async Task<IReadOnlyList<DocumentChunk>> SearchAsync(string tenantId, string query, int limit = 3, CancellationToken ct = default)
    { 
        using var session = store.LightweightSession(tenantId);
        var searchVector = await embeddingService.GenerateEmbeddingAsync(query, ct);

        var sql = @"
            SELECT data FROM mt_doc_documentchunk 
            WHERE tenant_id = ?
            ORDER BY public.extract_embedding(data) <=> CAST(? AS vector) 
            LIMIT ?";

        return [.. await session.QueryAsync<DocumentChunk>(sql, ct, tenantId, searchVector, limit)];
    }

    public async Task IngestChunksAsync(string tenantId, IEnumerable<string> contents, string source, CancellationToken ct = default)
    {
        var validChunks = contents.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        if (validChunks.Count == 0) return;

        var chunksToStore = new ConcurrentBag<DocumentChunk>();

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = 5,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(validChunks, options, async (chunkText, token) =>
        {
            var embedding = await embeddingService.GenerateEmbeddingAsync(chunkText, token);

            var chunk = new DocumentChunk(
                Guid.NewGuid(),
                chunkText,
                source,
                embedding,
                DateTimeOffset.UtcNow);

            chunksToStore.Add(chunk);
        });

        using var session = store.LightweightSession(tenantId);
        session.Store(chunksToStore.ToArray());
        await session.SaveChangesAsync(ct);
    }

    public async Task ClearTenantChunksAsync(string tenantId, CancellationToken ct = default)
    {
        using var session = store.LightweightSession(tenantId);
        session.DeleteWhere<DocumentChunk>(x => true);
        await session.SaveChangesAsync(ct);
    }
}