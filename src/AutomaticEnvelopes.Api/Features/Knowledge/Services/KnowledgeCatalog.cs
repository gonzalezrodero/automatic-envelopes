using AutomaticEnvelopes.Api.Core.Entities;
using Marten;

namespace AutomaticEnvelopes.Api.Features.Knowledge.Services;

public interface IKnowledgeCatalog
{
    Task UpsertAsync(string tenantId, KnowledgeFile file, CancellationToken ct);

    Task<KnowledgeFile?> FindByFilenameAsync(string tenantId, string filename, CancellationToken ct);

    Task<KnowledgeFile?> FindAsync(string tenantId, Guid id, CancellationToken ct);

    Task<IReadOnlyList<KnowledgeFile>> ListAsync(string tenantId, CancellationToken ct);

    Task DeleteAsync(string tenantId, Guid id, CancellationToken ct);
}

public class KnowledgeCatalog(IDocumentStore store) : IKnowledgeCatalog
{
    public async Task UpsertAsync(string tenantId, KnowledgeFile file, CancellationToken ct)
    {
        using var session = store.LightweightSession(tenantId);
        session.Store(file);
        await session.SaveChangesAsync(ct);
    }

    public async Task<KnowledgeFile?> FindByFilenameAsync(string tenantId, string filename, CancellationToken ct)
    {
        using var session = store.LightweightSession(tenantId);
        return await session.Query<KnowledgeFile>()
            .FirstOrDefaultAsync(file => file.Filename == filename, ct);
    }

    public async Task<KnowledgeFile?> FindAsync(string tenantId, Guid id, CancellationToken ct)
    {
        using var session = store.LightweightSession(tenantId);
        return await session.LoadAsync<KnowledgeFile>(id, ct);
    }

    public async Task<IReadOnlyList<KnowledgeFile>> ListAsync(string tenantId, CancellationToken ct)
    {
        using var session = store.LightweightSession(tenantId);
        var files = await session.Query<KnowledgeFile>().ToListAsync(ct);
        var chunks = await session.Query<DocumentChunk>().ToListAsync(ct);
        var (listed, created) = IncludeLegacySources(files, chunks);
        if (created.Count > 0)
        {
            session.Store(created.ToArray());
            await session.SaveChangesAsync(ct);
        }

        return listed;
    }

    /// <summary>
    /// Club files indexed before the catalog existed live only as chunks.
    /// One catalog row per source lets the portal list them and delete them later.
    /// </summary>
    public static (IReadOnlyList<KnowledgeFile> Files, IReadOnlyList<KnowledgeFile> Created) IncludeLegacySources(
        IReadOnlyList<KnowledgeFile> files,
        IEnumerable<DocumentChunk> chunks)
    {
        var known = new HashSet<string>(files.Select(file => file.Filename), StringComparer.Ordinal);
        var created = chunks
            .Where(chunk => !string.IsNullOrWhiteSpace(chunk.SourceDocument))
            .GroupBy(chunk => chunk.SourceDocument, StringComparer.Ordinal)
            .Where(group => !known.Contains(group.Key))
            .Select(ToLegacyFile)
            .ToArray();

        IReadOnlyList<KnowledgeFile> listed = created.Length == 0
            ? files
            : files.Concat(created).ToArray();
        return (listed, created);
    }

    private static KnowledgeFile ToLegacyFile(IGrouping<string, DocumentChunk> group)
    {
        var documentIds = group.Select(chunk => chunk.DocumentId).Distinct().ToArray();
        var id = documentIds.Length == 1 && documentIds[0] != Guid.Empty
            ? documentIds[0]
            : Guid.NewGuid();

        return new KnowledgeFile
        {
            Id = id,
            Filename = group.Key,
            Mime = MimeFor(group.Key),
            Bytes = 0,
            UploadedAt = group.Min(chunk => chunk.CreatedAt),
            ChunkCount = group.Count(),
            Status = "indexed"
        };
    }

    private static string MimeFor(string filename) => Path.GetExtension(filename).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".md" or ".markdown" => "text/markdown",
        _ => "text/plain"
    };

    public async Task DeleteAsync(string tenantId, Guid id, CancellationToken ct)
    {
        using var session = store.LightweightSession(tenantId);
        session.Delete<KnowledgeFile>(id);
        await session.SaveChangesAsync(ct);
    }
}
