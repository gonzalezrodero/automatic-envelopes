using System.Text;
using AutomaticEnvelopes.Api.Features.Knowledge.Services;

namespace AutomaticEnvelopes.Api.Features.Chat;

public interface IUnansweredInbox
{
    Task RecordAsync(string tenantId, string conversationId, string question, DateTimeOffset askedAt, CancellationToken ct);

    Task<IReadOnlyList<UnansweredQuestion>> ListOpenAsync(string tenantId, CancellationToken ct);

    Task<UnansweredQuestion?> AnswerAsync(string tenantId, Guid id, string answer, CancellationToken ct);

    Task<IReadOnlyList<UnansweredQuestion>> ListAnsweredAsync(string tenantId, CancellationToken ct);

    Task<UnansweredQuestion?> ReviseAsync(string tenantId, Guid id, string answer, CancellationToken ct);

    Task<bool> WithdrawAsync(string tenantId, Guid id, CancellationToken ct);

    Task<bool> DiscardAsync(string tenantId, Guid id, CancellationToken ct);

    Task ForgetConversationAsync(string tenantId, string conversationId, CancellationToken ct);
}

public class UnansweredInbox(
    IUnansweredQuestionDocuments questions,
    IPersonalDataFilter personalData,
    IKnowledgeCatalog catalog,
    IKnowledgeBaseService knowledge,
    IKnowledgeIngestionService ingestion) : IUnansweredInbox
{
    public async Task RecordAsync(string tenantId, string conversationId, string question, DateTimeOffset askedAt, CancellationToken ct)
    {
        var text = UnansweredInboxRules.TrimQuestion(question);
        if (text.Length == 0 || conversationId.Length == 0)
        {
            return;
        }

        await questions.SaveAsync(tenantId, [new UnansweredQuestion
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Question = text,
            AskedAt = askedAt
        }], ct);
    }

    public async Task<IReadOnlyList<UnansweredQuestion>> ListOpenAsync(string tenantId, CancellationToken ct)
    {
        var stored = await questions.ListAsync(tenantId, ct);
        return stored
            .Where(question => question.Answer is null)
            .OrderByDescending(question => question.AskedAt)
            .ToArray();
    }

    public async Task<UnansweredQuestion?> AnswerAsync(string tenantId, Guid id, string answer, CancellationToken ct)
    {
        var stored = (await questions.ListAsync(tenantId, ct)).ToList();
        if (!UnansweredInboxRules.TryApply(stored, id, answer, DateTimeOffset.UtcNow, out var changed))
        {
            return null;
        }

        await questions.SaveAsync(tenantId, changed, ct);
        await PublishAsync(tenantId, ct);
        return changed.First(question => question.Id == id);
    }

    public async Task<IReadOnlyList<UnansweredQuestion>> ListAnsweredAsync(string tenantId, CancellationToken ct)
    {
        var stored = await questions.ListAsync(tenantId, ct);
        return UnansweredInboxRules.LatestAnswered(stored);
    }

    public async Task<UnansweredQuestion?> ReviseAsync(string tenantId, Guid id, string answer, CancellationToken ct)
    {
        var stored = (await questions.ListAsync(tenantId, ct)).ToList();
        if (!UnansweredInboxRules.TryRevise(stored, id, answer, DateTimeOffset.UtcNow, out var changed))
        {
            return null;
        }

        await questions.SaveAsync(tenantId, changed, ct);
        await PublishAsync(tenantId, ct);
        return changed.First(question => question.Id == id);
    }

    public async Task<bool> WithdrawAsync(string tenantId, Guid id, CancellationToken ct)
    {
        var stored = (await questions.ListAsync(tenantId, ct)).ToList();
        if (!UnansweredInboxRules.TryWithdraw(stored, id, out var removed))
        {
            return false;
        }

        await questions.DeleteAsync(tenantId, removed, ct);
        await PublishAsync(tenantId, ct);
        return true;
    }

    public async Task<bool> DiscardAsync(string tenantId, Guid id, CancellationToken ct)
    {
        var stored = (await questions.ListAsync(tenantId, ct)).ToList();
        if (!UnansweredInboxRules.TryDiscard(stored, id, out var removed))
        {
            return false;
        }

        await questions.DeleteAsync(tenantId, removed, ct);
        return true;
    }

    public async Task ForgetConversationAsync(string tenantId, string conversationId, CancellationToken ct)
    {
        var stored = (await questions.ListAsync(tenantId, ct)).ToList();
        var mine = stored.Where(question => question.ConversationId == conversationId).ToArray();
        if (mine.Length == 0)
        {
            return;
        }

        var samples = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var question in mine)
        {
            samples.Add(question.Question);
            if (!string.IsNullOrWhiteSpace(question.Answer))
            {
                samples.Add(question.Answer);
            }
        }

        var cleaned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in samples)
        {
            cleaned[sample] = await personalData.CleanAsync(sample, ct);
        }

        var erasure = UnansweredInboxRules.ApplyErasure(stored, conversationId, cleaned);
        var remaining = stored.Where(question => !erasure.Removed.Contains(question)).ToArray();
        if (erasure.KnowledgeChanged)
        {
            await PublishAsync(tenantId, remaining, ct);
        }

        await questions.SaveAsync(tenantId, erasure.Changed, ct);
        await questions.DeleteAsync(tenantId, erasure.Removed, ct);
    }

    private async Task PublishAsync(string tenantId, CancellationToken ct)
    {
        var stored = await questions.ListAsync(tenantId, ct);
        await PublishAsync(tenantId, stored, ct);
    }

    private async Task PublishAsync(string tenantId, IReadOnlyList<UnansweredQuestion> stored, CancellationToken ct)
    {
        var markdown = AnsweredKnowledge.Render(stored);
        if (markdown.Length == 0)
        {
            await RemoveKnowledgeFileAsync(tenantId, ct);
            return;
        }

        var bytes = Encoding.UTF8.GetByteCount(markdown);
        await ingestion.IngestDocumentAsync(
            tenantId,
            markdown,
            AnsweredKnowledge.FileName,
            bytes,
            "text/markdown",
            ct);
    }

    private async Task RemoveKnowledgeFileAsync(string tenantId, CancellationToken ct)
    {
        var existing = await catalog.FindByFilenameAsync(tenantId, AnsweredKnowledge.FileName, ct);
        if (existing is null)
        {
            return;
        }

        await knowledge.DeleteChunksForDocumentAsync(tenantId, existing.Id, ct);
        await knowledge.DeleteLegacyChunksAsync(tenantId, existing.Filename, ct);
        await catalog.DeleteAsync(tenantId, existing.Id, ct);
    }
}
