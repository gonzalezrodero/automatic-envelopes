namespace AutomaticEnvelopes.Api.Features.Chat;

public static class AnsweredKnowledge
{
    public const string FileName = "respuestas.md";

    public static string Render(IEnumerable<UnansweredQuestion> questions)
    {
        var blocks = questions
            .Where(question => !string.IsNullOrWhiteSpace(question.Answer))
            .GroupBy(question => question.Question.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(question => question.AnsweredAt).First())
            .OrderBy(question => question.AnsweredAt)
            .Select(question => $"Pregunta: {question.Question.Trim()}\nRespuesta: {question.Answer!.Trim()}");

        return string.Join("\n\n", blocks);
    }
}
