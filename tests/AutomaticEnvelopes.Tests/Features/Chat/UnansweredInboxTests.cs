using AutomaticEnvelopes.Api.Features.Chat;
using AutomaticEnvelopes.Api.Features.Knowledge;
using AutomaticEnvelopes.Api.Features.Knowledge.Services;
using AwesomeAssertions;
using Moq;

namespace AutomaticEnvelopes.Tests.Features.Chat;

public class UnansweredInboxTests
{
    [Fact]
    public async Task Record_StoresTheTrimmedQuestionAndSkipsABlankOne()
    {
        var harness = new InboxHarness();
        var askedAt = new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

        await harness.Inbox.RecordAsync("club-a", "conversation-1", "  ¿A qué hora abre?  ", askedAt, CancellationToken.None);
        await harness.Inbox.RecordAsync("club-a", "conversation-1", "   ", askedAt, CancellationToken.None);
        await harness.Inbox.RecordAsync("club-a", "", "¿Dónde es?", askedAt, CancellationToken.None);

        var open = await harness.Inbox.ListOpenAsync("club-a", CancellationToken.None);
        open.Should().ContainSingle();
        open[0].Question.Should().Be("¿A qué hora abre?");
        open[0].ConversationId.Should().Be("conversation-1");
        open[0].AskedAt.Should().Be(askedAt);
    }

    [Fact]
    public async Task Record_KeepsOnlyTheFirstFourThousandCharacters()
    {
        var harness = new InboxHarness();

        await harness.Inbox.RecordAsync("club-a", "one", new string('a', 4_005), DateTimeOffset.UtcNow, CancellationToken.None);

        var open = await harness.Inbox.ListOpenAsync("club-a", CancellationToken.None);
        open.Should().ContainSingle();
        open[0].Question.Should().HaveLength(4_000);
    }

    [Fact]
    public async Task List_ReturnsOpenQuestionsNewestFirst()
    {
        var harness = new InboxHarness();
        var first = new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
        await harness.Inbox.RecordAsync("club-a", "one", "Primera", first, CancellationToken.None);
        await harness.Inbox.RecordAsync("club-a", "two", "Segunda", first.AddMinutes(1), CancellationToken.None);

        var open = await harness.Inbox.ListOpenAsync("club-a", CancellationToken.None);

        open.Select(question => question.Question).Should().Equal("Segunda", "Primera");
    }

    [Fact]
    public async Task Answer_WritesTheKnowledgeFileAndClosesTheSameWording()
    {
        var harness = new InboxHarness();
        var askedAt = new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
        await harness.Inbox.RecordAsync("club-a", "one", "¿A qué hora abre?", askedAt, CancellationToken.None);
        await harness.Inbox.RecordAsync("club-a", "two", "¿a qué hora abre?", askedAt.AddMinutes(5), CancellationToken.None);
        await harness.Inbox.RecordAsync("club-a", "three", "¿Dónde es?", askedAt.AddMinutes(6), CancellationToken.None);
        var target = harness.Rows.Single(question => question.ConversationId == "one");

        string? markdown = null;
        harness.Ingestion
            .Setup(item => item.IngestDocumentAsync(
                "club-a",
                It.IsAny<string>(),
                AnsweredKnowledge.FileName,
                It.IsAny<long>(),
                "text/markdown",
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, long, string, CancellationToken>((_, text, _, _, _, _) => markdown = text)
            .ReturnsAsync(new KnowledgeFile { Id = Guid.NewGuid(), Filename = AnsweredKnowledge.FileName });

        var saved = await harness.Inbox.AnswerAsync("club-a", target.Id, "A las 9.", CancellationToken.None);

        saved.Should().NotBeNull();
        saved!.Answer.Should().Be("A las 9.");
        markdown.Should().Be("Pregunta: ¿A qué hora abre?\nRespuesta: A las 9.");
        var open = await harness.Inbox.ListOpenAsync("club-a", CancellationToken.None);
        open.Should().ContainSingle(question => question.Question == "¿Dónde es?");
    }

    [Fact]
    public async Task Answer_ReturnsNullWhenTheQuestionIsMissing()
    {
        var harness = new InboxHarness();

        var saved = await harness.Inbox.AnswerAsync("club-a", Guid.NewGuid(), "A las 9.", CancellationToken.None);

        saved.Should().BeNull();
        harness.Ingestion.Verify(item => item.IngestDocumentAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<long>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Forget_KeepsTheAnswerAndDropsTheConversationLink()
    {
        var harness = new InboxHarness();
        await harness.Inbox.RecordAsync("club-a", "one", "Horario", DateTimeOffset.UtcNow, CancellationToken.None);
        var target = harness.Rows.Single();
        harness.Ingestion
            .Setup(item => item.IngestDocumentAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<long>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KnowledgeFile { Filename = AnsweredKnowledge.FileName });
        await harness.Inbox.AnswerAsync("club-a", target.Id, "A las 9.", CancellationToken.None);

        await harness.Inbox.ForgetConversationAsync("club-a", "one", CancellationToken.None);

        var answered = await harness.Inbox.ListAnsweredAsync("club-a", CancellationToken.None);
        answered.Should().ContainSingle(question => question.Question == "Horario" && question.Answer == "A las 9.");
        answered[0].ConversationId.Should().BeEmpty();
        harness.Catalog.Verify(item => item.DeleteAsync(
            It.IsAny<string>(),
            It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Forget_KeepsAnOpenQuestionAndDropsTheLink()
    {
        var harness = new InboxHarness();
        await harness.Inbox.RecordAsync("club-a", "one", "Horario", DateTimeOffset.UtcNow, CancellationToken.None);

        await harness.Inbox.ForgetConversationAsync("club-a", "one", CancellationToken.None);

        var open = await harness.Inbox.ListOpenAsync("club-a", CancellationToken.None);
        open.Should().ContainSingle(question => question.Question == "Horario");
        open[0].ConversationId.Should().BeEmpty();
        harness.Catalog.Verify(item => item.FindByFilenameAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Forget_DropsAQuestionThatIsOnlyPersonalData()
    {
        var harness = new InboxHarness();
        harness.Filter.Clean = text => text.Contains("Lucía", StringComparison.Ordinal) ? "[NAME]" : text;
        await harness.Inbox.RecordAsync("club-a", "one", "Lucía", DateTimeOffset.UtcNow, CancellationToken.None);
        var target = harness.Rows.Single();
        harness.Ingestion
            .Setup(item => item.IngestDocumentAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<long>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KnowledgeFile { Filename = AnsweredKnowledge.FileName });
        await harness.Inbox.AnswerAsync("club-a", target.Id, "A las 9.", CancellationToken.None);
        var fileId = Guid.NewGuid();
        harness.Catalog
            .Setup(item => item.FindByFilenameAsync("club-a", AnsweredKnowledge.FileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KnowledgeFile { Id = fileId, Filename = AnsweredKnowledge.FileName });

        await harness.Inbox.ForgetConversationAsync("club-a", "one", CancellationToken.None);

        harness.Rows.Should().BeEmpty();
        harness.Knowledge.Verify(item => item.DeleteChunksForDocumentAsync("club-a", fileId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Forget_CleansTheCopiedAnswerAndKeepsTheOtherConversation()
    {
        var harness = new InboxHarness();
        harness.Filter.Clean = text => text.Replace("Lucía", "[NAME]", StringComparison.Ordinal);
        var askedAt = DateTimeOffset.UtcNow;
        await harness.Inbox.RecordAsync("club-a", "one", "¿Viene Lucía el martes?", askedAt, CancellationToken.None);
        await harness.Inbox.RecordAsync("club-a", "two", "¿Viene Lucía el martes?", askedAt, CancellationToken.None);
        var first = harness.Rows.Single(question => question.ConversationId == "one");
        harness.Ingestion
            .Setup(item => item.IngestDocumentAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<long>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KnowledgeFile { Filename = AnsweredKnowledge.FileName });
        await harness.Inbox.AnswerAsync("club-a", first.Id, "Sí, Lucía a las 9.", CancellationToken.None);

        string? markdown = null;
        harness.Ingestion
            .Setup(item => item.IngestDocumentAsync(
                "club-a",
                It.IsAny<string>(),
                AnsweredKnowledge.FileName,
                It.IsAny<long>(),
                "text/markdown",
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, long, string, CancellationToken>((_, text, _, _, _, _) => markdown = text)
            .ReturnsAsync(new KnowledgeFile { Filename = AnsweredKnowledge.FileName });

        await harness.Inbox.ForgetConversationAsync("club-a", "one", CancellationToken.None);

        markdown.Should().Be("Pregunta: ¿Viene [NAME] el martes?\nRespuesta: Sí, [NAME] a las 9.");
        var kept = harness.Rows.Single(question => question.ConversationId == "two");
        kept.Question.Should().Be("¿Viene [NAME] el martes?");
        harness.Rows.Should().Contain(question => question.ConversationId.Length == 0 && question.Question == "¿Viene [NAME] el martes?");
    }

    [Fact]
    public async Task Forget_ClearsAnAnswerThatIsOnlyANameAndLeavesTheQuestion()
    {
        var harness = new InboxHarness();
        harness.Filter.Clean = text => text == "Lucía" ? "[NAME]" : text;
        await harness.Inbox.RecordAsync("club-a", "one", "¿A qué hora abre?", DateTimeOffset.UtcNow, CancellationToken.None);
        var target = harness.Rows.Single();
        harness.Ingestion
            .Setup(item => item.IngestDocumentAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<long>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KnowledgeFile { Filename = AnsweredKnowledge.FileName });
        await harness.Inbox.AnswerAsync("club-a", target.Id, "Lucía", CancellationToken.None);
        var fileId = Guid.NewGuid();
        harness.Catalog
            .Setup(item => item.FindByFilenameAsync("club-a", AnsweredKnowledge.FileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KnowledgeFile { Id = fileId, Filename = AnsweredKnowledge.FileName });

        await harness.Inbox.ForgetConversationAsync("club-a", "one", CancellationToken.None);

        var open = await harness.Inbox.ListOpenAsync("club-a", CancellationToken.None);
        open.Should().ContainSingle(question => question.Question == "¿A qué hora abre?" && question.ConversationId.Length == 0);
        (await harness.Inbox.ListAnsweredAsync("club-a", CancellationToken.None)).Should().BeEmpty();
        harness.Knowledge.Verify(item => item.DeleteChunksForDocumentAsync("club-a", fileId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Revise_RewritesTheKnowledgeFile()
    {
        var harness = new InboxHarness();
        await harness.Inbox.RecordAsync("club-a", "one", "Horario", DateTimeOffset.UtcNow, CancellationToken.None);
        var target = harness.Rows.Single();
        string? markdown = null;
        harness.Ingestion
            .Setup(item => item.IngestDocumentAsync(
                "club-a",
                It.IsAny<string>(),
                AnsweredKnowledge.FileName,
                It.IsAny<long>(),
                "text/markdown",
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, long, string, CancellationToken>((_, text, _, _, _, _) => markdown = text)
            .ReturnsAsync(new KnowledgeFile { Filename = AnsweredKnowledge.FileName });
        await harness.Inbox.AnswerAsync("club-a", target.Id, "A las 9.", CancellationToken.None);

        var saved = await harness.Inbox.ReviseAsync("club-a", target.Id, "A las 10.", CancellationToken.None);

        saved!.Answer.Should().Be("A las 10.");
        markdown.Should().Be("Pregunta: Horario\nRespuesta: A las 10.");
    }

    [Fact]
    public async Task Withdraw_RemovesTheAnswerAndKeepsADifferentOne()
    {
        var harness = new InboxHarness();
        var askedAt = DateTimeOffset.UtcNow;
        await harness.Inbox.RecordAsync("club-a", "one", "Horario", askedAt, CancellationToken.None);
        await harness.Inbox.RecordAsync("club-a", "two", "Lugar", askedAt.AddMinutes(1), CancellationToken.None);
        var first = harness.Rows.Single(question => question.Question == "Horario");
        var second = harness.Rows.Single(question => question.Question == "Lugar");
        string? markdown = null;
        harness.Ingestion
            .Setup(item => item.IngestDocumentAsync(
                "club-a",
                It.IsAny<string>(),
                AnsweredKnowledge.FileName,
                It.IsAny<long>(),
                "text/markdown",
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, long, string, CancellationToken>((_, text, _, _, _, _) => markdown = text)
            .ReturnsAsync(new KnowledgeFile { Filename = AnsweredKnowledge.FileName });
        await harness.Inbox.AnswerAsync("club-a", first.Id, "A las 9.", CancellationToken.None);
        await harness.Inbox.AnswerAsync("club-a", second.Id, "En la pista.", CancellationToken.None);

        var removed = await harness.Inbox.WithdrawAsync("club-a", first.Id, CancellationToken.None);

        removed.Should().BeTrue();
        markdown.Should().Be("Pregunta: Lugar\nRespuesta: En la pista.");
        (await harness.Inbox.ListAnsweredAsync("club-a", CancellationToken.None))
            .Should().ContainSingle(question => question.Question == "Lugar");
    }

    [Fact]
    public async Task Discard_RemovesTheOpenQuestionAndLeavesTheAnswer()
    {
        var harness = new InboxHarness();
        var askedAt = DateTimeOffset.UtcNow;
        await harness.Inbox.RecordAsync("club-a", "one", "Horario", askedAt, CancellationToken.None);
        var answered = harness.Rows.Single();
        harness.Ingestion
            .Setup(item => item.IngestDocumentAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<long>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KnowledgeFile { Filename = AnsweredKnowledge.FileName });
        await harness.Inbox.AnswerAsync("club-a", answered.Id, "A las 9.", CancellationToken.None);
        await harness.Inbox.RecordAsync("club-a", "two", " horario ", askedAt.AddMinutes(1), CancellationToken.None);
        await harness.Inbox.RecordAsync("club-a", "three", "Lugar", askedAt.AddMinutes(2), CancellationToken.None);
        var pending = harness.Rows.Single(question => question.Answer is null && question.Question.Contains("horario", StringComparison.OrdinalIgnoreCase));

        var removed = await harness.Inbox.DiscardAsync("club-a", pending.Id, CancellationToken.None);

        removed.Should().BeTrue();
        (await harness.Inbox.DiscardAsync("club-a", answered.Id, CancellationToken.None)).Should().BeFalse();
        (await harness.Inbox.ListOpenAsync("club-a", CancellationToken.None))
            .Should().ContainSingle(question => question.Question == "Lugar");
        (await harness.Inbox.ListAnsweredAsync("club-a", CancellationToken.None))
            .Should().ContainSingle(question => question.Question == "Horario");
        harness.Ingestion.Verify(
            item => item.IngestDocumentAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<long>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Forget_RetriesTheCleanedFileWhenPublishingFails()
    {
        var harness = new InboxHarness();
        harness.Filter.Clean = text => text.Replace("Lucía", "[NAME]", StringComparison.Ordinal);
        await harness.Inbox.RecordAsync("club-a", "one", "¿Viene Lucía?", DateTimeOffset.UtcNow, CancellationToken.None);
        var target = harness.Rows.Single();
        harness.Ingestion
            .Setup(item => item.IngestDocumentAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<long>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KnowledgeFile { Filename = AnsweredKnowledge.FileName });
        await harness.Inbox.AnswerAsync("club-a", target.Id, "A las 9.", CancellationToken.None);

        harness.Ingestion
            .Setup(item => item.IngestDocumentAsync(
                "club-a",
                It.IsAny<string>(),
                AnsweredKnowledge.FileName,
                It.IsAny<long>(),
                "text/markdown",
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("storage down"));

        var failed = () => harness.Inbox.ForgetConversationAsync("club-a", "one", CancellationToken.None);
        await failed.Should().ThrowAsync<InvalidOperationException>();
        harness.Rows.Single().ConversationId.Should().Be("one");
        harness.Rows.Single().Question.Should().Be("¿Viene Lucía?");

        string? markdown = null;
        harness.Ingestion
            .Setup(item => item.IngestDocumentAsync(
                "club-a",
                It.IsAny<string>(),
                AnsweredKnowledge.FileName,
                It.IsAny<long>(),
                "text/markdown",
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, long, string, CancellationToken>((_, text, _, _, _, _) => markdown = text)
            .ReturnsAsync(new KnowledgeFile { Filename = AnsweredKnowledge.FileName });

        await harness.Inbox.ForgetConversationAsync("club-a", "one", CancellationToken.None);

        markdown.Should().Be("Pregunta: ¿Viene [NAME]?\nRespuesta: A las 9.");
        harness.Rows.Single().ConversationId.Should().BeEmpty();
    }

    private sealed class InboxHarness
    {
        private readonly MemoryDocuments documents = new();
        public List<UnansweredQuestion> Rows => documents.Rows;
        public Mock<IKnowledgeIngestionService> Ingestion { get; } = new();
        public Mock<IKnowledgeCatalog> Catalog { get; } = new();
        public Mock<IKnowledgeBaseService> Knowledge { get; } = new();
        public UnansweredInbox Inbox { get; }

        public InboxHarness() =>
            Inbox = new UnansweredInbox(documents, Filter, Catalog.Object, Knowledge.Object, Ingestion.Object);

        public MapFilter Filter { get; } = new();
    }

    private sealed class MemoryDocuments : IUnansweredQuestionDocuments
    {
        public List<UnansweredQuestion> Rows { get; } = [];

        public Task<IReadOnlyList<UnansweredQuestion>> ListAsync(string tenantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<UnansweredQuestion>>(Rows.Select(Copy).ToList());

        public Task SaveAsync(string tenantId, IReadOnlyList<UnansweredQuestion> questions, CancellationToken ct)
        {
            foreach (var question in questions)
            {
                var index = Rows.FindIndex(row => row.Id == question.Id);
                if (index < 0) Rows.Add(question);
                else Rows[index] = question;
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string tenantId, IReadOnlyList<UnansweredQuestion> questions, CancellationToken ct)
        {
            foreach (var question in questions)
            {
                Rows.RemoveAll(row => row.Id == question.Id);
            }

            return Task.CompletedTask;
        }

        private static UnansweredQuestion Copy(UnansweredQuestion question) => new()
        {
            Id = question.Id,
            ConversationId = question.ConversationId,
            Question = question.Question,
            AskedAt = question.AskedAt,
            Answer = question.Answer,
            AnsweredAt = question.AnsweredAt
        };
    }

    private sealed class MapFilter : IPersonalDataFilter
    {
        public Func<string, string> Clean { get; set; } = text => text;

        public Task<string> CleanAsync(string text, CancellationToken ct) => Task.FromResult(Clean(text));
    }
}
