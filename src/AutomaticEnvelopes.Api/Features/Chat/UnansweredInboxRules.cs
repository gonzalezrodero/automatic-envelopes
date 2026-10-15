using System.Text.RegularExpressions;

namespace AutomaticEnvelopes.Api.Features.Chat;

public static partial class UnansweredInboxRules
{
    public const int MaxAnswerLength = 2_000;
    public const int MaxQuestionLength = 4_000;
    public const string AnswerRequired = "answer is required.";
    public const string AnswerTooLong = "answer is too long.";

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
            var owned = question.ConversationId == conversationId;
            if (!owned && !keys.Contains(question.Question.Trim()))
            {
                continue;
            }

            var originalQuestion = question.Question;
            var originalAnswer = question.Answer;
            var hadAnswer = !string.IsNullOrWhiteSpace(originalAnswer);

            if (cleaned.TryGetValue(question.Question, out var nextQuestion))
            {
                var trimmed = TrimQuestion(nextQuestion);
                if (!string.Equals(trimmed, question.Question.Trim(), StringComparison.OrdinalIgnoreCase) || !HasSubstance(trimmed))
                {
                    question.Question = trimmed;
                }
            }

            if (hadAnswer && cleaned.TryGetValue(originalAnswer!, out var nextAnswer))
            {
                var trimmed = nextAnswer.Trim();
                if (!string.Equals(trimmed, originalAnswer!.Trim(), StringComparison.OrdinalIgnoreCase) || !HasSubstance(trimmed))
                {
                    question.Answer = trimmed;
                }
            }

            if (owned)
            {
                question.ConversationId = string.Empty;
            }

            if (!HasSubstance(question.Question))
            {
                removed.Add(question);
                if (hadAnswer)
                {
                    knowledgeChanged = true;
                }

                continue;
            }

            if (hadAnswer && !HasSubstance(question.Answer))
            {
                question.Answer = null;
                question.AnsweredAt = null;
                knowledgeChanged = true;
            }
            else if (hadAnswer && (
                !string.Equals(question.Question.Trim(), originalQuestion.Trim(), StringComparison.Ordinal) ||
                !string.Equals(question.Answer?.Trim(), originalAnswer?.Trim(), StringComparison.Ordinal)))
            {
                knowledgeChanged = true;
            }

            if (question.Question != originalQuestion ||
                question.Answer != originalAnswer ||
                question.ConversationId != conversationId && owned)
            {
                changed.Add(question);
            }
            else if (owned && question.ConversationId.Length == 0)
            {
                changed.Add(question);
            }
        }

        return new ErasureResult(changed, removed, knowledgeChanged);
    }

    private static IEnumerable<UnansweredQuestion> ClosedWithWording(IReadOnlyList<UnansweredQuestion> questions, string question)
    {
        var key = question.Trim();
        return questions.Where(item =>
            item.Answer is not null &&
            string.Equals(item.Question.Trim(), key, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"\[(NAME|PHONE|EMAIL|ID)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PersonalDataPlaceholder();
}

public readonly record struct ErasureResult(
    IReadOnlyList<UnansweredQuestion> Changed,
    IReadOnlyList<UnansweredQuestion> Removed,
    bool KnowledgeChanged);
