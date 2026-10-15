using Marten;

namespace AutomaticEnvelopes.Api.Features.Chat;

public interface IUnansweredQuestionDocuments
{
    Task<IReadOnlyList<UnansweredQuestion>> ListAsync(string tenantId, CancellationToken ct);

    Task SaveAsync(string tenantId, IReadOnlyList<UnansweredQuestion> questions, CancellationToken ct);

    Task DeleteAsync(string tenantId, IReadOnlyList<UnansweredQuestion> questions, CancellationToken ct);
}

public sealed class MartenUnansweredQuestionDocuments(IDocumentStore store) : IUnansweredQuestionDocuments
{
    public async Task<IReadOnlyList<UnansweredQuestion>> ListAsync(string tenantId, CancellationToken ct)
    {
        using var session = store.LightweightSession(tenantId);
        return await session.Query<UnansweredQuestion>().ToListAsync(ct);
    }

    public async Task SaveAsync(string tenantId, IReadOnlyList<UnansweredQuestion> questions, CancellationToken ct)
    {
        if (questions.Count == 0)
        {
            return;
        }

        using var session = store.LightweightSession(tenantId);
        session.Store(questions.ToArray());
        await session.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string tenantId, IReadOnlyList<UnansweredQuestion> questions, CancellationToken ct)
    {
        if (questions.Count == 0)
        {
            return;
        }

        using var session = store.LightweightSession(tenantId);
        foreach (var question in questions)
        {
            session.Delete(question);
        }

        await session.SaveChangesAsync(ct);
    }
}
