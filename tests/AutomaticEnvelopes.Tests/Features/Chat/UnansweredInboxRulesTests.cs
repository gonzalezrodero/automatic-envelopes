using AutomaticEnvelopes.Api.Features.Chat;
using AwesomeAssertions;

namespace AutomaticEnvelopes.Tests.Features.Chat;

public class UnansweredInboxRulesTests
{
    [Fact]
    public void TryStrip_RemovesOnlyATrailingMarker()
    {
        UnknownReply.TryStrip("No tengo ese dato.\n[[UNKNOWN]]", out var visible).Should().BeTrue();
        visible.Should().Be("No tengo ese dato.");

        UnknownReply.TryStrip("El horario es de 9 a 13.", out var kept).Should().BeFalse();
        kept.Should().Be("El horario es de 9 a 13.");

        UnknownReply.TryStrip("[[UNKNOWN]]", out var empty).Should().BeTrue();
        empty.Should().BeEmpty();
    }

    [Fact]
    public void TryApply_ClosesEveryOpenQuestionWithTheSameWording()
    {
        var now = new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
        var first = Question("¿A qué hora abre?");
        var second = Question("¿a qué hora abre?");
        var other = Question("¿Dónde es el partido?");
        var answered = Question("Ya consta");
        answered.Answer = "Sí";

        var applied = UnansweredInboxRules.TryApply([first, second, other, answered], first.Id, "A las 9.", now, out var changed);

        applied.Should().BeTrue();
        changed.Should().Equal(first, second);
        first.Answer.Should().Be("A las 9.");
        second.AnsweredAt.Should().Be(now);
        other.Answer.Should().BeNull();
        answered.Answer.Should().Be("Sí");
    }

    [Fact]
    public void TryApply_RejectsAQuestionThatIsAlreadyAnswered()
    {
        var done = Question("Horario");
        done.Answer = "A las 9.";

        UnansweredInboxRules.TryApply([done], done.Id, "Otra", DateTimeOffset.UtcNow, out _).Should().BeFalse();
        UnansweredInboxRules.TryNormalizeAnswer("  ", out _, out var error).Should().BeFalse();
        error.Should().Be(UnansweredInboxRules.AnswerRequired);
        UnansweredInboxRules.TryNormalizeAnswer(new string('a', 2001), out _, out error).Should().BeFalse();
        error.Should().Be(UnansweredInboxRules.AnswerTooLong);
    }

    [Fact]
    public void Render_KeepsTheLatestAnswerForEachQuestion()
    {
        var older = Question("Horario");
        older.Answer = "A las 8.";
        older.AnsweredAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var newer = Question(" horario ");
        newer.Answer = "A las 9.";
        newer.AnsweredAt = older.AnsweredAt.Value.AddDays(1);
        var place = Question("Sitio");
        place.Answer = "Pabellón.";
        place.AnsweredAt = newer.AnsweredAt.Value.AddHours(1);

        var markdown = AnsweredKnowledge.Render([place, older, newer, Question("Pendiente")]);

        markdown.Should().Be("Pregunta: horario\nRespuesta: A las 9.\n\nPregunta: Sitio\nRespuesta: Pabellón.");
        BotPrompts.SystemPromptTemplate.Should().Contain(UnknownReply.Marker);
    }

    [Fact]
    public void HasSubstance_KeepsAQuestionThatStillAsksSomething()
    {
        UnansweredInboxRules.HasSubstance("[NAME]").Should().BeFalse();
        UnansweredInboxRules.HasSubstance("  [PHONE] ").Should().BeFalse();
        UnansweredInboxRules.HasSubstance("¿Viene [NAME] el martes?").Should().BeTrue();
    }

    [Fact]
    public void TryRevise_UpdatesEveryClosedCopyAndLeavesAnOpenQuestion()
    {
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var first = Question("Horario");
        first.Answer = "A las 9.";
        var second = Question(" horario ");
        second.Answer = "A las 9.";
        var open = Question("Horario");

        var revised = UnansweredInboxRules.TryRevise([first, second, open], first.Id, "A las 10.", now, out var changed);

        revised.Should().BeTrue();
        changed.Should().Equal(first, second);
        first.Answer.Should().Be("A las 10.");
        second.AnsweredAt.Should().Be(now);
        open.Answer.Should().BeNull();
        UnansweredInboxRules.TryRevise([open], open.Id, "A las 10.", now, out _).Should().BeFalse();
    }

    [Fact]
    public void TryWithdraw_DropsOnlyTheClosedCopies()
    {
        var done = Question("Horario");
        done.Answer = "A las 9.";
        var copy = Question("horario");
        copy.Answer = "A las 8.";
        var open = Question("Horario");

        UnansweredInboxRules.TryWithdraw([done, copy, open], done.Id, out var removed).Should().BeTrue();

        removed.Should().Equal(done, copy);
        UnansweredInboxRules.TryWithdraw([open], open.Id, out _).Should().BeFalse();
    }

    [Fact]
    public void ApplyErasure_CutsTheLinkAndCleansTheCopiedAnswer()
    {
        var owned = Question("¿Viene Lucía?");
        owned.ConversationId = "one";
        owned.Answer = "Llama a Lucía";
        var copy = Question("¿Viene Lucía?");
        copy.ConversationId = "two";
        copy.Answer = "Llama a Lucía";
        var other = Question("Horario");
        other.ConversationId = "two";
        other.Answer = "A las 9.";
        var cleaned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["¿Viene Lucía?"] = "¿Viene [NAME]?",
            ["Llama a Lucía"] = "Llama a [NAME]"
        };

        var result = UnansweredInboxRules.ApplyErasure([owned, copy, other], "one", cleaned);

        result.KnowledgeChanged.Should().BeTrue();
        result.Removed.Should().BeEmpty();
        owned.ConversationId.Should().BeEmpty();
        owned.Question.Should().Be("¿Viene [NAME]?");
        copy.ConversationId.Should().Be("two");
        copy.Answer.Should().Be("Llama a [NAME]");
        other.Answer.Should().Be("A las 9.");
    }

    [Fact]
    public void ApplyErasure_DropsAQuestionThatIsOnlyAName()
    {
        var owned = Question("Lucía");
        owned.ConversationId = "one";
        var cleaned = new Dictionary<string, string> { ["Lucía"] = "[NAME]" };

        var result = UnansweredInboxRules.ApplyErasure([owned], "one", cleaned);

        result.Removed.Should().ContainSingle().Which.Should().BeSameAs(owned);
        result.KnowledgeChanged.Should().BeFalse();
    }

    private static UnansweredQuestion Question(string text) => new()
    {
        Id = Guid.NewGuid(),
        ConversationId = "abc",
        Question = text,
        AskedAt = DateTimeOffset.UtcNow
    };
}
