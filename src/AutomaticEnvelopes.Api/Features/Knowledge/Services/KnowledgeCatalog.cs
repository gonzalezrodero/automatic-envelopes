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
        return await session.Query<KnowledgeFile>().ToListAsync(ct);
    }

    public async Task DeleteAsync(string tenantId, Guid id, CancellationToken ct)
    {
        using var session = store.LightweightSession(tenantId);
        session.Delete<KnowledgeFile>(id);
        await session.SaveChangesAsync(ct);
    }
}
