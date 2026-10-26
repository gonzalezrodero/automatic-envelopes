using System.Text.RegularExpressions;

namespace AutomaticEnvelopes.Api.Features.Chat;

public static partial class UnansweredInboxRules
{
    public const int MaxAnswerLength = 2_000;
    public const int MaxQuestionLength = 4_000;
    public const string AnswerRequired = "Falta la respuesta.";
    public const string AnswerTooLong = "La respuesta es demasiado larga.";

    public static bool TryNormalizeAnswer(string? answer, out string normalized, out string error)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(answer))
        {
            error = AnswerRequired;
            return false;
        }

        normalized = answer.Trim();
        if (normalized.Length > MaxAnswerLength)
        {
            error = AnswerTooLong;
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static string TrimQuestion(string question)
    {
        var trimmed = question.Trim();
        return trimmed.Length <= MaxQuestionLength ? trimmed : trimmed[..MaxQuestionLength];
    }

    /// <summary>
    /// Closes the chosen question and every other open question with the same wording.
    /// </summary>
    public static bool TryApply(
        IReadOnlyList<UnansweredQuestion> questions,
        Guid id,
        string answer,
        DateTimeOffset now,
        out IReadOnlyList<UnansweredQuestion> changed)
    {
        changed = [];
        var target = questions.FirstOrDefault(question => question.Id == id && question.Answer is null);
        if (target is null)
        {
            return false;
        }

        var key = target.Question.Trim();
        changed = questions
            .Where(question => question.Answer is null && string.Equals(question.Question.Trim(), key, StringComparison.OrdinalIgnoreCase))
            .Select(question =>
            {
                question.Answer = answer;
                question.AnsweredAt = now;
                return question;
            })
            .ToArray();
        return changed.Count > 0;
    }

    /// <summary>
    /// Replaces the answer on an already closed question and on every other closed question with the same wording.
    /// </summary>
    public static bool TryRevise(
        IReadOnlyList<UnansweredQuestion> questions,
        Guid id,
        string answer,
        DateTimeOffset now,
        out IReadOnlyList<UnansweredQuestion> changed)
    {
        changed = [];
        var target = questions.FirstOrDefault(question => question.Id == id && question.Answer is not null);
        if (target is null)
        {
            return false;
        }

        changed = ClosedWithWording(questions, target.Question)
            .Select(question =>
            {
                question.Answer = answer;
                question.AnsweredAt = now;
                return question;
            })
            .ToArray();
        return changed.Count > 0;
    }

    /// <summary>
    /// Drops every closed question with the same wording as the chosen one.
    /// </summary>
    public static bool TryWithdraw(
        IReadOnlyList<UnansweredQuestion> questions,
        Guid id,
        out IReadOnlyList<UnansweredQuestion> removed)
    {
        removed = [];
        var target = questions.FirstOrDefault(question => question.Id == id && question.Answer is not null);
        if (target is null)
        {
            return false;
        }

        removed = ClosedWithWording(questions, target.Question).ToArray();
        return removed.Count > 0;
    }

    /// <summary>
    /// Drops every still-open question with the same wording. Answered copies stay in the knowledge.
    /// </summary>
    public static bool TryDiscard(
        IReadOnlyList<UnansweredQuestion> questions,
        Guid id,
        out IReadOnlyList<UnansweredQuestion> removed)
    {
        removed = [];
        var target = questions.FirstOrDefault(question => question.Id == id && question.Answer is null);
        if (target is null)
        {
            return false;
        }

        removed = OpenWithWording(questions, target.Question).ToArray();
        return removed.Count > 0;
    }

    public static IReadOnlyList<UnansweredQuestion> LatestAnswered(IEnumerable<UnansweredQuestion> questions) =>
        questions
            .Where(question => !string.IsNullOrWhiteSpace(question.Answer))
            .GroupBy(question => question.Question.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(question => question.AnsweredAt).First())
            .OrderByDescending(question => question.AnsweredAt)
            .ToArray();

    /// <summary>
    /// True when the text still asks something after personal-data placeholders are removed.
    /// </summary>
    public static bool HasSubstance(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var stripped = PersonalDataPlaceholder().Replace(text, " ");
        return stripped.Any(char.IsLetterOrDigit);
    }

    /// <summary>
    /// Keeps questions from an erased conversation, removes personal data, and cuts that conversation's link.
    /// The same cleaned text is applied to every copy of that question.
    /// </summary>
    public static ErasureResult ApplyErasure(
        IReadOnlyList<UnansweredQuestion> questions,
        string conversationId,
        IReadOnlyDictionary<string, string> cleaned)
    {
        var mine = questions.Where(question => question.ConversationId == conversationId).ToArray();
        if (mine.Length == 0)
        {
            return new ErasureResult([], [], false);
        }

        var keys = mine.Select(question => question.Question.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = new List<UnansweredQuestion>();
        var removed = new List<UnansweredQuestion>();
        var knowledgeChanged = false;

        foreach (var question in questions)
        {
            if (!TouchesErasure(question, conversationId, keys))
            {
                continue;
            }

            var outcome = CleanErased(question, conversationId, cleaned);
            if (outcome.Removed)
            {
                removed.Add(question);
            }
            else if (outcome.Changed)
            {
                changed.Add(question);
            }

            knowledgeChanged |= outcome.KnowledgeChanged;
        }

        return new ErasureResult(changed, removed, knowledgeChanged);
    }

    private static bool TouchesErasure(UnansweredQuestion question, string conversationId, HashSet<string> keys) =>
        question.ConversationId == conversationId || keys.Contains(question.Question.Trim());

    private static CleanOutcome CleanErased(
        UnansweredQuestion question,
        string conversationId,
        IReadOnlyDictionary<string, string> cleaned)
    {
        var originalQuestion = question.Question;
        var originalAnswer = question.Answer;
        var hadAnswer = !string.IsNullOrWhiteSpace(originalAnswer);
        var owned = question.ConversationId == conversationId;

        ApplyCleanedQuestion(question, cleaned);
        ApplyCleanedAnswer(question, originalAnswer, hadAnswer, cleaned);
        if (owned)
        {
            question.ConversationId = string.Empty;
        }

        if (!HasSubstance(question.Question))
        {
            return new CleanOutcome(true, false, hadAnswer);
        }

        var knowledgeChanged = NoteKnowledgeChange(question, hadAnswer, originalQuestion, originalAnswer);
        var changed = question.Question != originalQuestion
            || question.Answer != originalAnswer
            || (owned && question.ConversationId.Length == 0);
        return new CleanOutcome(false, changed, knowledgeChanged);
    }

    private static void ApplyCleanedQuestion(UnansweredQuestion question, IReadOnlyDictionary<string, string> cleaned)
    {
        if (!cleaned.TryGetValue(question.Question, out var next))
        {
            return;
        }

        var trimmed = TrimQuestion(next);
        if (ReplaceWith(question.Question, trimmed))
        {
            question.Question = trimmed;
        }
    }

    private static void ApplyCleanedAnswer(
        UnansweredQuestion question,
        string? originalAnswer,
        bool hadAnswer,
        IReadOnlyDictionary<string, string> cleaned)
    {
        if (!hadAnswer || originalAnswer is null || !cleaned.TryGetValue(originalAnswer, out var next))
        {
            return;
        }

        var trimmed = next.Trim();
        if (ReplaceWith(originalAnswer, trimmed))
        {
            question.Answer = trimmed;
        }
    }

    private static bool ReplaceWith(string current, string cleaned) =>
        !string.Equals(cleaned, current.Trim(), StringComparison.OrdinalIgnoreCase) || !HasSubstance(cleaned);

    private static bool NoteKnowledgeChange(
        UnansweredQuestion question,
        bool hadAnswer,
        string originalQuestion,
        string? originalAnswer)
    {
        if (!hadAnswer)
        {
            return false;
        }

        if (!HasSubstance(question.Answer))
        {
            question.Answer = null;
            question.AnsweredAt = null;
            return true;
        }

        return !string.Equals(question.Question.Trim(), originalQuestion.Trim(), StringComparison.Ordinal)
            || !string.Equals(question.Answer?.Trim(), originalAnswer?.Trim(), StringComparison.Ordinal);
    }

    private readonly record struct CleanOutcome(bool Removed, bool Changed, bool KnowledgeChanged);

    private static IEnumerable<UnansweredQuestion> ClosedWithWording(IReadOnlyList<UnansweredQuestion> questions, string question)
    {
        var key = question.Trim();
        return questions.Where(item =>
            item.Answer is not null &&
            string.Equals(item.Question.Trim(), key, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<UnansweredQuestion> OpenWithWording(IReadOnlyList<UnansweredQuestion> questions, string question)
    {
        var key = question.Trim();
        return questions.Where(item =>
            item.Answer is null &&
            string.Equals(item.Question.Trim(), key, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"\[(NAME|PHONE|EMAIL|ID)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PersonalDataPlaceholder();
}

public readonly record struct ErasureResult(
    IReadOnlyList<UnansweredQuestion> Changed,
    IReadOnlyList<UnansweredQuestion> Removed,
    bool KnowledgeChanged);
